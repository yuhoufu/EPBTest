using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Controller
{
    internal readonly struct EmergencyPowerGroupRegistration
    {
        internal EmergencyPowerGroupRegistration(
            Guid correlationId,
            DateTime startedUtc,
            bool isFirst,
            int requestCount,
            bool shouldPublishNonDaqFault)
        {
            CorrelationId = correlationId;
            StartedUtc = startedUtc;
            IsFirst = isFirst;
            RequestCount = requestCount;
            ShouldPublishNonDaqFault = shouldPublishNonDaqFault;
        }

        internal Guid CorrelationId { get; }
        internal DateTime StartedUtc { get; }
        internal bool IsFirst { get; }
        internal int RequestCount { get; }
        internal bool ShouldPublishNonDaqFault { get; }
    }

    /// <summary>
    ///     保存电源组失效安全联锁的活动关联。重复请求复用原关联号用于去重诊断，
    ///     但调用方仍必须重新执行逐路 DO OFF 和程控电源 OFF，不能因已有 latch 静默返回。
    /// </summary>
    internal sealed class EmergencyPowerGroupLatch
    {
        private sealed class Entry
        {
            internal Guid CorrelationId;
            internal DateTime StartedUtc;
            internal int RequestCount;
            internal int NonDaqFaultPublished;
        }

        private readonly ConcurrentDictionary<int, Entry> _entries = new();

        internal EmergencyPowerGroupRegistration Register(
            int groupId,
            Guid requestedCorrelationId,
            DateTime requestedUtc,
            bool nonDaqFault = false)
        {
            if (groupId <= 0) throw new ArgumentOutOfRangeException(nameof(groupId));
            if (requestedCorrelationId == Guid.Empty)
                requestedCorrelationId = Guid.NewGuid();
            if (requestedUtc == default)
                requestedUtc = DateTime.UtcNow;

            var candidate = new Entry
            {
                CorrelationId = requestedCorrelationId,
                StartedUtc = requestedUtc,
                RequestCount = 1
            };
            var active = _entries.GetOrAdd(groupId, candidate);
            var isFirst = ReferenceEquals(active, candidate);
            var requestCount = isFirst
                ? 1
                : Interlocked.Increment(ref active.RequestCount);
            var shouldPublishNonDaqFault = nonDaqFault &&
                Interlocked.CompareExchange(ref active.NonDaqFaultPublished, 1, 0) == 0;
            return new EmergencyPowerGroupRegistration(
                active.CorrelationId,
                active.StartedUtc,
                isFirst,
                requestCount,
                shouldPublishNonDaqFault);
        }

        internal bool ContainsKey(int groupId)
        {
            return _entries.ContainsKey(groupId);
        }

        internal bool TryRemove(int groupId)
        {
            return _entries.TryRemove(groupId, out _);
        }

        internal void Clear()
        {
            _entries.Clear();
        }
    }
}
