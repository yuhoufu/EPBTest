using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace IO.NI
{
    /// <summary>
    /// Converts the monotonic Stopwatch counter and sample offsets to DateTime ticks.
    /// DateTime.AddSeconds is intentionally avoided because .NET Framework rounds it
    /// to milliseconds, which destroys a 2 kHz (0.5 ms) sample interval.
    /// </summary>
    public sealed class HighResolutionSampleClock
    {
        private DateTime _wallTime;
        private long _startTimestamp;

        public DateTime WallTime => _wallTime;
        public long StartTimestamp => _startTimestamp;

        public void Reset(DateTime wallTime)
        {
            _wallTime = wallTime;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public DateTime Now()
        {
            return FromStopwatchTimestamp(_wallTime, _startTimestamp, Stopwatch.GetTimestamp());
        }

        public static DateTime FromStopwatchTimestamp(
            DateTime wallTime,
            long startTimestamp,
            long currentTimestamp)
        {
            var elapsed = currentTimestamp - startTimestamp;
            var ticks = (long)Math.Round(
                elapsed * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency),
                MidpointRounding.AwayFromZero);
            return wallTime.AddTicks(ticks);
        }

        public static long SampleOffsetTicks(int sampleOffset, double sampleRate)
        {
            if (sampleOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(sampleOffset));
            if (sampleRate <= 0 || double.IsNaN(sampleRate) || double.IsInfinity(sampleRate))
                throw new ArgumentOutOfRangeException(nameof(sampleRate));

            return (long)Math.Round(
                sampleOffset * (TimeSpan.TicksPerSecond / sampleRate),
                MidpointRounding.AwayFromZero);
        }

        public static DateTime AddSamples(DateTime origin, int sampleOffset, double sampleRate)
        {
            return origin.AddTicks(SampleOffsetTicks(sampleOffset, sampleRate));
        }

        public static DateTime[] BuildBatchTimestamps(
            DateTime batchLastSampleUtc,
            int sampleCount,
            double sampleRate)
        {
            if (sampleCount < 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));

            var result = new DateTime[sampleCount];
            if (sampleCount == 0)
                return result;

            var first = batchLastSampleUtc.AddTicks(
                -SampleOffsetTicks(sampleCount - 1, sampleRate));
            for (var i = 0; i < sampleCount; i++)
                result[i] = first.AddTicks(SampleOffsetTicks(i, sampleRate));
            return result;
        }

        public static void FillBatchTimestamps(
            DateTime batchLastSampleUtc,
            DateTime[] destination,
            int sampleCount,
            double sampleRate)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (sampleCount < 0 || sampleCount > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));
            if (sampleCount == 0) return;

            var first = batchLastSampleUtc.AddTicks(
                -SampleOffsetTicks(sampleCount - 1, sampleRate));
            for (var i = 0; i < sampleCount; i++)
                destination[i] = first.AddTicks(SampleOffsetTicks(i, sampleRate));
        }

        /// <summary>
        /// 计算下一批的批尾时间。主机时间可用于向前纠偏，但不得让批尾早于
        /// “上一批尾 + 本批采样时长”，否则向前回推批内时间戳会与上一批重叠。
        /// </summary>
        public static DateTime AdvanceBatchEnd(
            DateTime previousBatchEnd,
            DateTime hostNow,
            int sampleCount,
            double sampleRate,
            double correctionThresholdMs = 5.0)
        {
            var idealBatchEnd = AddSamples(
                previousBatchEnd,
                sampleCount,
                sampleRate);
            var driftMs = (hostNow - idealBatchEnd).TotalMilliseconds;
            if (Math.Abs(driftMs) <= Math.Max(0, correctionThresholdMs))
                return idealBatchEnd;

            return hostNow > idealBatchEnd
                ? hostNow
                : idealBatchEnd;
        }
    }

    /// <summary>
    /// Serializes per-device timestamp advancement with the caller's batch commit.
    /// The commit must stay inside the same gate as timestamp allocation; otherwise
    /// overlapping DAQ callbacks can allocate A/B/C in order but enqueue A/C/B.
    /// </summary>
    public sealed class DeviceBatchTimestampCoordinator
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DateTime> _lastTimestampByDevice =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _origin;

        public void Reset(DateTime origin, params string[] devices)
        {
            lock (_gate)
            {
                _origin = origin;
                _lastTimestampByDevice.Clear();
                if (devices == null) return;
                foreach (var device in devices)
                {
                    if (!string.IsNullOrWhiteSpace(device))
                        _lastTimestampByDevice[device] = origin;
                }
            }
        }

        public void AdvanceAndCommit(
            string device,
            DateTime hostNow,
            int sampleCount,
            double sampleRate,
            Action<DateTime, DateTime, double> commit)
        {
            if (string.IsNullOrWhiteSpace(device))
                throw new ArgumentException("device is required", nameof(device));
            if (commit == null)
                throw new ArgumentNullException(nameof(commit));

            lock (_gate)
            {
                if (!_lastTimestampByDevice.TryGetValue(device, out var previousEnd))
                    previousEnd = _origin;

                var idealEnd = HighResolutionSampleClock.AddSamples(
                    previousEnd,
                    sampleCount,
                    sampleRate);
                var driftMs = (hostNow - idealEnd).TotalMilliseconds;
                var currentEnd = HighResolutionSampleClock.AdvanceBatchEnd(
                    previousEnd,
                    hostNow,
                    sampleCount,
                    sampleRate);

                // Commit before releasing the ordering gate. This is intentionally
                // limited to a non-blocking queue enqueue at the call site.
                commit(previousEnd, currentEnd, driftMs);
                _lastTimestampByDevice[device] = currentEnd;
            }
        }
    }

    public enum ClockState
    {
        WarmingUp,
        Adjusting,
        Locked,
        Invalid
    }

    /// <summary>每台 DAQ 的在线时钟驯服参数。所有阈值均针对 DAQ 时钟与主机单调时钟的相对误差。</summary>
    public sealed class ClockDisciplineOptions
    {
        public ClockDisciplineOptions(
            int estimatorWindowSeconds = 60,
            int warmupSeconds = 30,
            double maxAbsSkewPpm = 250,
            double maxCorrectionPpmPerUpdate = 5,
            double residualHardLimitMs = 100,
            int invalidConfirmations = 10,
            double smoothingAlpha = 0.2,
            double lockTolerancePpm = 2,
            int lockConfirmations = 5)
        {
            EstimatorWindowSeconds = Math.Max(30, Math.Min(600, estimatorWindowSeconds));
            WarmupSeconds = Math.Max(10, Math.Min(EstimatorWindowSeconds, warmupSeconds));
            MaxAbsSkewPpm = Math.Max(50, Math.Min(5000, maxAbsSkewPpm));
            MaxCorrectionPpmPerUpdate = Math.Max(0.1, Math.Min(100, maxCorrectionPpmPerUpdate));
            ResidualHardLimitMs = Math.Max(20, Math.Min(5000, residualHardLimitMs));
            InvalidConfirmations = Math.Max(2, Math.Min(120, invalidConfirmations));
            SmoothingAlpha = Math.Max(0.01, Math.Min(1, smoothingAlpha));
            LockTolerancePpm = Math.Max(0.1, Math.Min(50, lockTolerancePpm));
            LockConfirmations = Math.Max(1, Math.Min(60, lockConfirmations));
        }

        public int EstimatorWindowSeconds { get; }
        public int WarmupSeconds { get; }
        public double MaxAbsSkewPpm { get; }
        public double MaxCorrectionPpmPerUpdate { get; }
        public double ResidualHardLimitMs { get; }
        public int InvalidConfirmations { get; }
        public double SmoothingAlpha { get; }
        public double LockTolerancePpm { get; }
        public int LockConfirmations { get; }
    }

    public readonly struct ClockDisciplinedTimelineResult
    {
        public ClockDisciplinedTimelineResult(
            DateTime previousBatchEndUtc,
            DateTime batchEndUtc,
            long batchEndMonotonicTicks,
            long totalSamples,
            double arrivalDelayMs,
            double sampleLeadMs,
            double effectiveSampleRateHz,
            double estimatedSkewPpm,
            double residualMs,
            double estimatorWindowSeconds,
            double correctionPpm,
            ClockState clockState,
            bool stateChanged)
        {
            PreviousBatchEndUtc = previousBatchEndUtc;
            BatchEndUtc = batchEndUtc;
            BatchEndMonotonicTicks = batchEndMonotonicTicks;
            TotalSamples = totalSamples;
            ArrivalDelayMs = arrivalDelayMs;
            SampleLeadMs = sampleLeadMs;
            EffectiveSampleRateHz = effectiveSampleRateHz;
            EstimatedSkewPpm = estimatedSkewPpm;
            ResidualMs = residualMs;
            EstimatorWindowSeconds = estimatorWindowSeconds;
            CorrectionPpm = correctionPpm;
            ClockState = clockState;
            StateChanged = stateChanged;
        }

        public DateTime PreviousBatchEndUtc { get; }
        public DateTime BatchEndUtc { get; }
        public long BatchEndMonotonicTicks { get; }
        public long TotalSamples { get; }
        public double ArrivalDelayMs { get; }
        public double SampleLeadMs { get; }
        public double EffectiveSampleRateHz { get; }
        public double EstimatedSkewPpm { get; }
        public double ResidualMs { get; }
        public double EstimatorWindowSeconds { get; }
        public double CorrectionPpm { get; }
        public ClockState ClockState { get; }
        public bool StateChanged { get; }
        public bool RequiresRecovery => ClockState == ClockState.Invalid;
    }

    /// <summary>
    /// 使用样本序号与每设备独立的主机单调时钟观测建立受控时间轴。回调调度延迟只参与
    /// 长窗口低延迟包络估计，不会把单次追赶回调误判为控制数据无效。
    /// </summary>
    public sealed class ClockDisciplinedSampleTimeline
    {
        private struct Observation
        {
            public long Bucket;
            public long SampleIndex;
            public long CallbackTicks;
            public long PredictedEndTicks;
        }

        private readonly object _gate = new object();
        private readonly ClockDisciplineOptions _options;
        private readonly Observation[] _observations;
        private DateTime _originUtc;
        private long _originMonotonicTicks;
        private long _totalSamples;
        private double _elapsedMonotonicTicks;
        private double _elapsedWallTicks;
        private double _nominalSampleRateHz;
        private double _effectiveSampleRateHz;
        private DateTime _lastBatchEndUtc;
        private long _lastBatchEndMonotonicTicks;
        private Observation _activeObservation;
        private bool _hasActiveObservation;
        private int _observationCount;
        private int _observationWriteIndex;
        private int _stableFits;
        private int _outOfRangeFits;
        private int _residualViolationFits;
        private double _estimatedSkewPpm;
        private double _residualMs;
        private double _estimatorWindowSeconds;
        private double _lastCorrectionPpm;
        private ClockState _state = ClockState.WarmingUp;

        public ClockDisciplinedSampleTimeline(ClockDisciplineOptions options = null)
        {
            _options = options ?? new ClockDisciplineOptions();
            _observations = new Observation[_options.EstimatorWindowSeconds];
        }

        public void Reset(DateTime originUtc, long originMonotonicTicks, double nominalSampleRateHz)
        {
            if (originMonotonicTicks <= 0)
                throw new ArgumentOutOfRangeException(nameof(originMonotonicTicks));
            if (!IsFinitePositive(nominalSampleRateHz))
                throw new ArgumentOutOfRangeException(nameof(nominalSampleRateHz));

            lock (_gate)
            {
                _originUtc = originUtc.Kind == DateTimeKind.Utc ? originUtc : originUtc.ToUniversalTime();
                _originMonotonicTicks = originMonotonicTicks;
                _totalSamples = 0;
                _elapsedMonotonicTicks = 0;
                _elapsedWallTicks = 0;
                _nominalSampleRateHz = nominalSampleRateHz;
                _effectiveSampleRateHz = nominalSampleRateHz;
                _lastBatchEndUtc = _originUtc;
                _lastBatchEndMonotonicTicks = _originMonotonicTicks;
                _activeObservation = default;
                _hasActiveObservation = false;
                _observationCount = 0;
                _observationWriteIndex = 0;
                _stableFits = 0;
                _outOfRangeFits = 0;
                _residualViolationFits = 0;
                _estimatedSkewPpm = 0;
                _residualMs = 0;
                _estimatorWindowSeconds = 0;
                _lastCorrectionPpm = 0;
                _state = ClockState.WarmingUp;
                Array.Clear(_observations, 0, _observations.Length);
            }
        }

        public ClockDisciplinedTimelineResult Advance(
            int sampleCount,
            DateTime callbackArrivalUtc,
            long callbackMonotonicTicks)
        {
            if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
            if (callbackMonotonicTicks <= 0)
                throw new ArgumentOutOfRangeException(nameof(callbackMonotonicTicks));

            lock (_gate)
            {
                var previousState = _state;
                var previousUtc = _lastBatchEndUtc;
                _lastCorrectionPpm = 0;

                try
                {
                    _totalSamples = checked(_totalSamples + sampleCount);
                    _elapsedMonotonicTicks += sampleCount * (Stopwatch.Frequency / _effectiveSampleRateHz);
                    _elapsedWallTicks += sampleCount * (TimeSpan.TicksPerSecond / _effectiveSampleRateHz);
                    if (!IsFinitePositive(_elapsedMonotonicTicks) || !IsFinitePositive(_elapsedWallTicks))
                        throw new ArithmeticException("DAQ clock elapsed value is not finite.");

                    var endMonotonicTicks = checked(_originMonotonicTicks + RoundTicks(_elapsedMonotonicTicks));
                    var endUtc = _originUtc.AddTicks(RoundTicks(_elapsedWallTicks));
                    if (endMonotonicTicks <= _lastBatchEndMonotonicTicks || endUtc <= _lastBatchEndUtc)
                        throw new ArithmeticException("DAQ clock timeline is not strictly increasing.");

                    _lastBatchEndMonotonicTicks = endMonotonicTicks;
                    _lastBatchEndUtc = endUtc;
                    Observe(_totalSamples, callbackMonotonicTicks, endMonotonicTicks);

                    var sampleLeadMs = (endMonotonicTicks - callbackMonotonicTicks) *
                                       1000.0 / Stopwatch.Frequency;
                    return new ClockDisciplinedTimelineResult(
                        previousUtc,
                        endUtc,
                        endMonotonicTicks,
                        _totalSamples,
                        -sampleLeadMs,
                        sampleLeadMs,
                        _effectiveSampleRateHz,
                        _estimatedSkewPpm,
                        _residualMs,
                        _estimatorWindowSeconds,
                        _lastCorrectionPpm,
                        _state,
                        previousState != _state);
                }
                catch (Exception ex) when (ex is OverflowException || ex is ArgumentOutOfRangeException || ex is ArithmeticException)
                {
                    _state = ClockState.Invalid;
                    // 即使模型自身不可用，已由DAQ成功读取的当前批也必须保持连续时间戳，
                    // 交给上层安全暂停/重建流程；不得再次制造有效数据缺口。
                    try
                    {
                        var fallbackMonotonicDelta = RoundTicks(
                            sampleCount * (Stopwatch.Frequency / _nominalSampleRateHz));
                        var fallbackWallDelta = RoundTicks(
                            sampleCount * (TimeSpan.TicksPerSecond / _nominalSampleRateHz));
                        _lastBatchEndMonotonicTicks = checked(
                            _lastBatchEndMonotonicTicks + Math.Max(1, fallbackMonotonicDelta));
                        _lastBatchEndUtc = _lastBatchEndUtc.AddTicks(Math.Max(1, fallbackWallDelta));
                    }
                    catch
                    {
                        // DateTime/Int64 已到不可表示边界时只能保留最后有效值；上层会立即安全恢复。
                    }
                    return new ClockDisciplinedTimelineResult(
                        previousUtc,
                        _lastBatchEndUtc,
                        _lastBatchEndMonotonicTicks,
                        _totalSamples,
                        0,
                        0,
                        _effectiveSampleRateHz,
                        _estimatedSkewPpm,
                        _residualMs,
                        _estimatorWindowSeconds,
                        0,
                        _state,
                        previousState != _state);
                }
            }
        }

        private void Observe(long sampleIndex, long callbackTicks, long predictedEndTicks)
        {
            var elapsedTicks = callbackTicks - _originMonotonicTicks;
            var bucket = elapsedTicks <= 0 ? 0 : elapsedTicks / Stopwatch.Frequency;
            var candidate = new Observation
            {
                Bucket = bucket,
                SampleIndex = sampleIndex,
                CallbackTicks = callbackTicks,
                PredictedEndTicks = predictedEndTicks
            };

            if (!_hasActiveObservation)
            {
                _activeObservation = candidate;
                _hasActiveObservation = true;
                return;
            }

            if (bucket == _activeObservation.Bucket)
            {
                // 同一秒内保留到达延迟最低的批次，作为驱动批处理抖动的低延迟包络。
                var candidateDelay = callbackTicks - predictedEndTicks;
                var activeDelay = _activeObservation.CallbackTicks - _activeObservation.PredictedEndTicks;
                if (candidateDelay < activeDelay)
                    _activeObservation = candidate;
                return;
            }

            CommitObservation(_activeObservation);
            _activeObservation = candidate;
            FitClock();
        }

        private void CommitObservation(Observation observation)
        {
            _observations[_observationWriteIndex] = observation;
            _observationWriteIndex = (_observationWriteIndex + 1) % _observations.Length;
            if (_observationCount < _observations.Length) _observationCount++;
            _residualMs = (observation.PredictedEndTicks - observation.CallbackTicks) *
                          1000.0 / Stopwatch.Frequency;
        }

        private void FitClock()
        {
            if (_state == ClockState.Invalid || _observationCount < 2) return;

            var oldestIndex = (_observationWriteIndex - _observationCount + _observations.Length) %
                              _observations.Length;
            var first = _observations[oldestIndex];
            var newestIndex = (_observationWriteIndex - 1 + _observations.Length) % _observations.Length;
            var newest = _observations[newestIndex];
            _estimatorWindowSeconds = (newest.CallbackTicks - first.CallbackTicks) /
                                      (double)Stopwatch.Frequency;
            if (_estimatorWindowSeconds < _options.WarmupSeconds)
            {
                _state = ClockState.WarmingUp;
                return;
            }

            double sumX = 0;
            double sumY = 0;
            double sumXx = 0;
            double sumXy = 0;
            for (var i = 0; i < _observationCount; i++)
            {
                var index = (oldestIndex + i) % _observations.Length;
                var observation = _observations[index];
                var x = (double)(observation.SampleIndex - first.SampleIndex);
                var y = (double)(observation.CallbackTicks - first.CallbackTicks);
                sumX += x;
                sumY += y;
                sumXx += x * x;
                sumXy += x * y;
            }

            var count = (double)_observationCount;
            var denominator = sumXx - sumX * sumX / count;
            var numerator = sumXy - sumX * sumY / count;
            if (!(denominator > 0) || !(numerator > 0))
            {
                ConfirmEstimatorInvalid();
                return;
            }

            var ticksPerSample = numerator / denominator;
            var candidateRate = Stopwatch.Frequency / ticksPerSample;
            if (!IsFinitePositive(candidateRate))
            {
                ConfirmEstimatorInvalid();
                return;
            }

            var candidateSkewPpm = (candidateRate / _nominalSampleRateHz - 1.0) * 1_000_000.0;
            if (Math.Abs(candidateSkewPpm) > _options.MaxAbsSkewPpm)
            {
                _stableFits = 0;
                _residualViolationFits = 0;
                if (++_outOfRangeFits >= _options.InvalidConfirmations)
                    _state = ClockState.Invalid;
                else
                    _state = ClockState.Adjusting;
                return;
            }

            _outOfRangeFits = 0;
            var differencePpm = (candidateRate / _effectiveSampleRateHz - 1.0) * 1_000_000.0;
            var correctionPpm = differencePpm * _options.SmoothingAlpha;
            correctionPpm = Math.Max(
                -_options.MaxCorrectionPpmPerUpdate,
                Math.Min(_options.MaxCorrectionPpmPerUpdate, correctionPpm));
            _effectiveSampleRateHz *= 1.0 + correctionPpm / 1_000_000.0;
            _lastCorrectionPpm = correctionPpm;
            _estimatedSkewPpm = (_effectiveSampleRateHz / _nominalSampleRateHz - 1.0) * 1_000_000.0;

            var remainingPpm = Math.Abs((candidateRate / _effectiveSampleRateHz - 1.0) * 1_000_000.0);
            if (remainingPpm <= _options.LockTolerancePpm)
                _stableFits++;
            else
                _stableFits = 0;

            _state = _stableFits >= _options.LockConfirmations
                ? ClockState.Locked
                : ClockState.Adjusting;

            if (_state == ClockState.Locked && _residualMs > _options.ResidualHardLimitMs)
            {
                if (++_residualViolationFits >= _options.InvalidConfirmations)
                    _state = ClockState.Invalid;
            }
            else
            {
                _residualViolationFits = 0;
            }
        }

        private void ConfirmEstimatorInvalid()
        {
            _stableFits = 0;
            _residualViolationFits = 0;
            if (++_outOfRangeFits >= _options.InvalidConfirmations)
                _state = ClockState.Invalid;
            else
                _state = ClockState.Adjusting;
        }

        private static long RoundTicks(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value > long.MaxValue || value < long.MinValue)
                throw new ArithmeticException("DAQ clock tick value is outside Int64 range.");
            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
