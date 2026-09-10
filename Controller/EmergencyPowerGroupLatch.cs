using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    internal enum EmergencyPowerGroupCompletionStatus
    {
        Recovered = 0,
        FailedSafe = 1,
        Superseded = 2,
        ControlReady = 3
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
            Task<EmergencyPowerGroupCompletion> completion,
            Task<EmergencyPowerGroupCompletion> controlReady = null)
        {
            CorrelationId = correlationId;
            Generation = generation;
            StartedUtc = startedUtc;
            IsFirst = isFirst;
            RequestCount = requestCount;
            ShouldPublishNonDaqFault = shouldPublishNonDaqFault;
            Completion = completion;
            ControlReady = controlReady;
        }

        internal Guid CorrelationId { get; }
        internal long Generation { get; }
        internal DateTime StartedUtc { get; }
        internal bool IsFirst { get; }
        internal int RequestCount { get; }
        internal bool ShouldPublishNonDaqFault { get; }
        internal Task<EmergencyPowerGroupCompletion> Completion { get; }
        // Preflight wake-up only; never a fresh physical-safety or business proof.
        internal Task<EmergencyPowerGroupCompletion> ControlReady { get; }
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
            internal BusinessVerification Business;
            internal Guid CorrelationId;
            internal long Generation;
            internal DateTime StartedUtc;
            internal int RequestCount;
            internal int NonDaqFaultPublished;
            internal bool ControlReadinessFailed;
            internal TaskCompletionSource<EmergencyPowerGroupCompletion> ControlReady =
                new TaskCompletionSource<EmergencyPowerGroupCompletion>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<EmergencyPowerGroupCompletion> Completion =
                new TaskCompletionSource<EmergencyPowerGroupCompletion>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentDictionary<int, Entry> _entries = new();
        // Register 和恢复证据的检查/移除必须线性化，不能在检查后合并进新的故障请求。
        private readonly object _gate = new();
        private long _generation;

        internal EmergencyPowerGroupRegistration Register(
            int groupId,
            Guid requestedCorrelationId,
            DateTime requestedUtc,
            bool nonDaqFault = false)
        {
            lock (_gate)
                return RegisterCore(groupId, requestedCorrelationId, requestedUtc, nonDaqFault);
        }

        private sealed class BusinessVerification
        {
            internal Guid VerificationId;
            internal Guid RunId;
            internal long RunEpoch, AfterAttempt;
            internal HashSet<int> Pending;
        }

        private EmergencyPowerGroupRegistration RegisterCore(
            int groupId, Guid requestedCorrelationId, DateTime requestedUtc, bool nonDaqFault)
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
            if (!isFirst)
            {
                active.Business = null;
                active.ControlReady.TrySetResult(new EmergencyPowerGroupCompletion(
                    EmergencyPowerGroupCompletionStatus.Superseded, "NewFaultRequest"));
                active.ControlReady = new TaskCompletionSource<EmergencyPowerGroupCompletion>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                active.ControlReadinessFailed = false;
            }
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
                active.Completion.Task, active.ControlReady.Task);
        }

        internal bool ContainsKey(int groupId)
        {
            return _entries.ContainsKey(groupId);
        }

        internal bool TryRemove(int groupId)
        {
            lock (_gate)
                return TryRemoveCore(groupId);
        }

        private bool TryRemoveCore(int groupId)
        {
            if (!_entries.TryRemove(groupId, out var active)) return false;
            active.ControlReady.TrySetResult(new EmergencyPowerGroupCompletion(
                EmergencyPowerGroupCompletionStatus.Superseded, "LatchRemoved"));
            active.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                EmergencyPowerGroupCompletionStatus.Recovered,
                "EmergencyPowerGroupRecovered"));
            return true;
        }

        internal bool TryRemove(int groupId, Guid correlationId, long generation = 0)
        {
            lock (_gate)
                return TryRemoveCore(groupId, correlationId, generation);
        }

        private bool TryRemoveCore(int groupId, Guid correlationId, long generation)
        {
            if (!_entries.TryGetValue(groupId, out var active) ||
                correlationId == Guid.Empty || active.CorrelationId != correlationId ||
                (generation > 0 && active.Generation != generation))
                return false;
            var removed = ((ICollection<KeyValuePair<int, Entry>>)_entries).Remove(
                new KeyValuePair<int, Entry>(groupId, active));
            if (removed)
            {
                active.ControlReady.TrySetResult(new EmergencyPowerGroupCompletion(
                    EmergencyPowerGroupCompletionStatus.Superseded, "LatchRemoved"));
                active.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                    EmergencyPowerGroupCompletionStatus.Recovered,
                    "EmergencyPowerGroupRecovered"));
            }
            return removed;
        }

        internal bool TryCapture(int groupId, out EmergencyPowerGroupRegistration snapshot)
        {
            lock (_gate)
            {
                snapshot = default;
                if (!_entries.TryGetValue(groupId, out var active)) return false;
                snapshot = new EmergencyPowerGroupRegistration(
                    active.CorrelationId, active.Generation, active.StartedUtc,
                    false, active.RequestCount, false, active.Completion.Task, active.ControlReady.Task);
                return true;
            }
        }

        // 仅执行精确清理；调用方仍须持有本次整组物理安全和真实业务提交证据。
        internal bool TryRemoveUnchanged(int groupId, EmergencyPowerGroupRegistration snapshot)
        {
            lock (_gate)
            {
                if (!snapshot.IsValid || snapshot.Generation <= 0 || snapshot.RequestCount <= 0 ||
                    !_entries.TryGetValue(groupId, out var active) ||
                    active.CorrelationId != snapshot.CorrelationId ||
                    active.Generation != snapshot.Generation ||
                    active.RequestCount != snapshot.RequestCount ||
                    !ReferenceEquals(active.Completion.Task, snapshot.Completion))
                    return false;
                return TryRemoveCore(groupId, snapshot.CorrelationId, snapshot.Generation);
            }
        }

        internal bool TryMarkControlReady(int groupId, EmergencyPowerGroupRegistration snapshot)
        {
            lock (_gate)
            {
                if (!snapshot.IsValid || !_entries.TryGetValue(groupId, out var active) ||
                    active.ControlReadinessFailed ||
                    active.CorrelationId != snapshot.CorrelationId || active.Generation != snapshot.Generation ||
                    active.RequestCount != snapshot.RequestCount ||
                    !ReferenceEquals(active.ControlReady.Task, snapshot.ControlReady) ||
                    !ReferenceEquals(active.Completion.Task, snapshot.Completion)) return false;
                return active.ControlReady.TrySetResult(new EmergencyPowerGroupCompletion(
                    EmergencyPowerGroupCompletionStatus.ControlReady, "ControlReadyForPreflight"));
            }
        }

        internal bool IsControlReadyCurrent(int groupId, EmergencyPowerGroupRegistration snapshot)
        {
            lock (_gate)
            {
                return snapshot.IsValid && _entries.TryGetValue(groupId, out var active) &&
                       !active.ControlReadinessFailed &&
                       active.CorrelationId == snapshot.CorrelationId &&
                       active.Generation == snapshot.Generation && active.RequestCount == snapshot.RequestCount &&
                       ReferenceEquals(active.Completion.Task, snapshot.Completion) &&
                       ReferenceEquals(active.ControlReady.Task, snapshot.ControlReady) &&
                       active.ControlReady.Task.Status == TaskStatus.RanToCompletion &&
                       active.ControlReady.Task.Result.Status == EmergencyPowerGroupCompletionStatus.ControlReady;
            }
        }

        internal bool TryFail(int groupId, Guid correlationId, string reason)
        {
            lock (_gate)
                return TryFailCore(groupId, correlationId, reason);
        }

        internal bool TryFailUnchanged(int groupId, EmergencyPowerGroupRegistration snapshot, string reason)
        {
            lock (_gate)
            {
                if (!snapshot.IsValid || !_entries.TryGetValue(groupId, out var active) ||
                    active.CorrelationId != snapshot.CorrelationId || active.Generation != snapshot.Generation ||
                    active.RequestCount != snapshot.RequestCount ||
                    !ReferenceEquals(active.Completion.Task, snapshot.Completion) ||
                    !ReferenceEquals(active.ControlReady.Task, snapshot.ControlReady)) return false;
                return TryFailCore(groupId, snapshot.CorrelationId, reason);
            }
        }

        // Caller must first confirm fresh physical safety under its exact owner.
        // This records business evidence requirements, never supplies safety proof.
        internal bool TryArmBusinessVerification(int groupId, EmergencyPowerGroupRegistration snapshot,
            Guid runId, long runEpoch, IEnumerable<int> channels, long afterAttempt, Guid verificationId,
            bool replaceAfterFreshSafety = false)
        {
            var required = channels?.Distinct().ToArray();
            if (verificationId == Guid.Empty || runId == Guid.Empty || runEpoch <= 0 || afterAttempt < 0 || required == null ||
                required.Length == 0 || required.Any(channel => channel < 1 || channel > 12)) return false;
            lock (_gate)
            {
                if (!MatchesSnapshot(groupId, snapshot, out var active) ||
                    (active.Business != null && !replaceAfterFreshSafety)) return false;
                active.Business = new BusinessVerification { VerificationId = verificationId, RunId = runId, RunEpoch = runEpoch,
                    AfterAttempt = afterAttempt, Pending = new HashSet<int>(required) };
                return true;
            }
        }

        internal void InvalidateBusinessVerification(int groupId, EmergencyPowerGroupRegistration snapshot, Guid verificationId)
        {
            lock (_gate)
                if (verificationId != Guid.Empty && MatchesSnapshot(groupId, snapshot, out var active) &&
                    active.Business?.VerificationId == verificationId) active.Business = null;
        }

        private bool MatchesSnapshot(int groupId, EmergencyPowerGroupRegistration snapshot, out Entry active)
        {
            active = null;
            return snapshot.IsValid && _entries.TryGetValue(groupId, out active) &&
                active.CorrelationId == snapshot.CorrelationId && active.Generation == snapshot.Generation &&
                active.RequestCount == snapshot.RequestCount &&
                ReferenceEquals(active.Completion.Task, snapshot.Completion) &&
                ReferenceEquals(active.ControlReady.Task, snapshot.ControlReady);
        }

        internal bool ConfirmBusinessCycle(int groupId, CycleAttemptContext attempt, bool actionSucceeded)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(groupId, out var active) || active.Business == null ||
                    attempt == null || (attempt.Kind != CycleAttemptKind.FormalBatch &&
                        attempt.Kind != CycleAttemptKind.FormalSingle && attempt.Kind != CycleAttemptKind.FormalRecovery))
                    return false;
                var proof = active.Business;
                if (!EpbManager.IsVerifiedBusinessRejoin(proof.RunId, proof.RunEpoch, proof.AfterAttempt,
                        attempt, actionSucceeded) || !proof.Pending.Remove(attempt.Channel)) return false;
                return proof.Pending.Count == 0 && TryRemoveCore(groupId);
            }
        }

        private bool TryFailCore(int groupId, Guid correlationId, string reason)
        {
            if (!_entries.TryGetValue(groupId, out var active) ||
                correlationId == Guid.Empty || active.CorrelationId != correlationId)
                return false;
            active.ControlReadinessFailed = true;
            active.Business = null;
            active.ControlReady.TrySetResult(new EmergencyPowerGroupCompletion(
                EmergencyPowerGroupCompletionStatus.FailedSafe, reason));
            return active.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                EmergencyPowerGroupCompletionStatus.FailedSafe,
                reason));
        }

        internal void Clear()
        {
            lock (_gate)
                ClearCore();
        }

        private void ClearCore()
        {
            foreach (var pair in _entries)
            {
                if (((ICollection<KeyValuePair<int, Entry>>)_entries).Remove(pair))
                {
                    pair.Value.ControlReady.TrySetResult(new EmergencyPowerGroupCompletion(
                        EmergencyPowerGroupCompletionStatus.Superseded, "EmergencyPowerGroupCleared"));
                    pair.Value.Completion.TrySetResult(new EmergencyPowerGroupCompletion(
                        EmergencyPowerGroupCompletionStatus.Superseded,
                        "EmergencyPowerGroupCleared"));
                }
            }
        }
    }
}
