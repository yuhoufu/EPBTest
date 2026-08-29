using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    internal enum EmergencyPowerGroupCompletionStatus
    {
        Recovered = 0,
        FailedSafe = 1,
        Superseded = 2
    }

    internal sealed class EmergencyPowerGroupCompletion
    {
        internal EmergencyPowerGroupCompletion(
            EmergencyPowerGroupCompletionStatus status,
            string reason)
        {
            Status = status;
            Reason = reason ?? string.Empty;
        }

        internal EmergencyPowerGroupCompletionStatus Status { get; }
        internal string Reason { get; }
        internal bool Recovered => Status == EmergencyPowerGroupCompletionStatus.Recovered;
    }

    internal readonly struct EmergencyPowerGroupRegistration
    {
        internal EmergencyPowerGroupRegistration(
            Guid correlationId,
            long generation,
            DateTime startedUtc,
            bool isFirst,
            int requestCount,
            bool shouldPublishNonDaqFault,
            Task<EmergencyPowerGroupCompletion> completion)
        {
            CorrelationId = correlationId;
            Generation = generation;
            StartedUtc = startedUtc;
            IsFirst = isFirst;
            RequestCount = requestCount;
            ShouldPublishNonDaqFault = shouldPublishNonDaqFault;
            Completion = completion;
        }

        internal Guid CorrelationId { get; }
        internal long Generation { get; }
        internal DateTime StartedUtc { get; }
        internal bool IsFirst { get; }
        internal int RequestCount { get; }
        internal bool ShouldPublishNonDaqFault { get; }
        internal Task<EmergencyPowerGroupCompletion> Completion { get; }
        internal bool IsValid => CorrelationId != Guid.Empty && Completion != null;
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
            internal long Generation;
            internal DateTime StartedUtc;
            internal int RequestCount;
            internal int NonDaqFaultPublished;
            internal readonly TaskCompletionSource<EmergencyPowerGroupCompletion> Completion =
                new TaskCompletionSource<EmergencyPowerGroupCompletion>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentDictionary<int, Entry> _entries = new();
        private long _generation;

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
                Generation = Interlocked.Increment(ref _generation),
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
                active.Generation,
                active.StartedUtc,
                isFirst,
                requestCount,
                shouldPublishNonDaqFault,
                active.Completion.Task);
        }

        internal bool ContainsKey(int groupId)
        {
            return _entries.ContainsKey(groupId);
        }

        internal bool TryRemove(int groupId)
        {
            if (!_entries.TryRemove(groupId, out var active)) return false;
            active.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                EmergencyPowerGroupCompletionStatus.Recovered,
                "EmergencyPowerGroupRecovered"));
            return true;
        }

        internal bool TryRemove(int groupId, Guid correlationId, long generation = 0)
        {
            if (!_entries.TryGetValue(groupId, out var active) ||
                correlationId == Guid.Empty || active.CorrelationId != correlationId ||
                (generation > 0 && active.Generation != generation))
                return false;
            var removed = ((ICollection<KeyValuePair<int, Entry>>)_entries).Remove(
                new KeyValuePair<int, Entry>(groupId, active));
            if (removed)
                active.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                    EmergencyPowerGroupCompletionStatus.Recovered,
                    "EmergencyPowerGroupRecovered"));
            return removed;
        }

        internal bool TryFail(int groupId, Guid correlationId, string reason)
        {
            if (!_entries.TryGetValue(groupId, out var active) ||
                correlationId == Guid.Empty || active.CorrelationId != correlationId)
                return false;
            return active.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                EmergencyPowerGroupCompletionStatus.FailedSafe,
                reason));
        }

        internal void Clear()
        {
            foreach (var pair in _entries)
            {
                if (((ICollection<KeyValuePair<int, Entry>>)_entries).Remove(pair))
                    pair.Value.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                        EmergencyPowerGroupCompletionStatus.Superseded,
                        "EmergencyPowerGroupCleared"));
            }
        }
    }
}
