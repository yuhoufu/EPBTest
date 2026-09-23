using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// 正式调度只依据同一单调时钟。UTC 映射仅用于日志，不能反向参与放行计算。
    /// </summary>
    internal sealed class FormalScheduleClock
    {
        private readonly Func<long> _nowTimestamp;
        private readonly Func<DateTime> _nowUtc;
        private readonly Func<int, CancellationToken, Task> _delay;

        public FormalScheduleClock()
            : this(Stopwatch.GetTimestamp, Stopwatch.Frequency,
                () => DateTime.UtcNow, (milliseconds, token) => Task.Delay(milliseconds, token))
        {
        }

        internal FormalScheduleClock(
            Func<long> nowTimestamp,
            long frequency,
            Func<DateTime> nowUtc,
            Func<int, CancellationToken, Task> delay)
        {
            _nowTimestamp = nowTimestamp ?? throw new ArgumentNullException(nameof(nowTimestamp));
            _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
            if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
            Frequency = frequency;
        }

        public long Frequency { get; }
        public long NowTimestamp => _nowTimestamp();
        public DateTime NowUtc
        {
            get
            {
                var value = _nowUtc();
                return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            }
        }

        public long AddMilliseconds(long timestamp, double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds))
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            return checked(timestamp + checked((long)Math.Round(
                milliseconds * Frequency / 1000.0, MidpointRounding.AwayFromZero)));
        }

        public double ElapsedMilliseconds(long start, long end) =>
            (end - (double)start) * 1000.0 / Frequency;

        public DateTime ToUtc(long timestamp)
        {
            var nowTimestamp = NowTimestamp;
            var nowUtc = NowUtc;
            return nowUtc.AddMilliseconds(ElapsedMilliseconds(nowTimestamp, timestamp));
        }

        public async Task DelayUntilAsync(long dueTimestamp, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = ElapsedMilliseconds(NowTimestamp, dueTimestamp);
                if (remaining <= 0) return;
                var delayMs = (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(remaining)));
                await _delay(delayMs, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>全批同一逻辑槽共用的不可变调度授权；实际跳槽不改变逻辑安全链。</summary>
    internal sealed class FormalBatchSlotGrant
    {
        internal FormalBatchSlotGrant(
            long logicalSlot,
            long scheduleSlot,
            long releaseTimestamp,
            long anchorTimestamp,
            long periodTicks,
            int periodMs,
            FormalScheduleClock clock)
        {
            LogicalSlot = logicalSlot;
            ScheduleSlot = scheduleSlot;
            ReleaseTimestamp = releaseTimestamp;
            AnchorTimestamp = anchorTimestamp;
            PeriodTicks = periodTicks;
            PeriodMs = periodMs;
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            PlannedUtc = clock.ToUtc(releaseTimestamp);
        }

        public long LogicalSlot { get; }
        public long ScheduleSlot { get; }
        public long ReleaseTimestamp { get; }
        public long AnchorTimestamp { get; }
        public long PeriodTicks { get; }
        public int PeriodMs { get; }
        public DateTime PlannedUtc { get; }
        public FormalScheduleClock Clock { get; }

        public long GetTimestamp(int phaseMs) => Clock.AddMilliseconds(ReleaseTimestamp, phaseMs);

        public long FirstFutureBoundary(long afterTimestamp) =>
            FirstFutureBoundary(AnchorTimestamp, afterTimestamp, PeriodTicks);

        internal static long FirstFutureBoundary(long anchor, long after, long periodTicks)
        {
            if (periodTicks <= 0) throw new ArgumentOutOfRangeException(nameof(periodTicks));
            if (after < anchor) return anchor;
            return checked(anchor + checked((checked(after - anchor) / periodTicks + 1) * periodTicks));
        }
    }
}
