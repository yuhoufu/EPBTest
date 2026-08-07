using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    internal enum RecoveryOwnerPriority
    {
        AlarmChannel = 10,
        Hydraulic = 20,
        PowerSupply = 30,
        Daq = 40,
        AffectedGroupReset = 50
    }

    /// <summary>
    /// 每个液压组最多允许一个恢复所有者。更高优先级恢复会先取消旧所有者，
    /// 并等待旧任务明确退出后才取得所有权，避免两个恢复路径同时重建同一代次。
    /// </summary>
    internal sealed class HydraulicRecoveryOwnershipCoordinator
    {
        internal sealed class OwnerState
        {
            internal int HydraulicGroupId;
            internal string OwnerId;
            internal RecoveryOwnerPriority Priority;
            internal CancellationTokenSource Cancellation;
            internal TaskCompletionSource<bool> Completion;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<int, OwnerState> _owners = new Dictionary<int, OwnerState>();

        internal int ActiveCount
        {
            get
            {
                lock (_gate) return _owners.Count;
            }
        }

        internal string GetOwner(int hydraulicGroupId)
        {
            lock (_gate)
                return _owners.TryGetValue(hydraulicGroupId, out var owner)
                    ? owner.OwnerId
                    : string.Empty;
        }

        internal async Task<bool> CancelAllAsync(int timeoutMs, CancellationToken token)
        {
            OwnerState[] owners;
            lock (_gate)
            {
                owners = _owners.Values.ToArray();
                foreach (var owner in owners)
                {
                    try { owner.Cancellation.Cancel(); }
                    catch { }
                }
            }

            if (owners.Length == 0) return true;
            var all = Task.WhenAll(owners.Select(owner => owner.Completion.Task));
            var deadline = Task.Delay(Math.Max(1, timeoutMs), token);
            var completed = await Task.WhenAny(all, deadline).ConfigureAwait(false);
            if (completed == all)
            {
                await all.ConfigureAwait(false);
                return true;
            }

            token.ThrowIfCancellationRequested();
            return false;
        }

        internal async Task<HydraulicRecoveryOwnershipLease> AcquireAsync(
            int hydraulicGroupId,
            string ownerId,
            RecoveryOwnerPriority priority,
            int takeoverTimeoutMs,
            CancellationToken token)
        {
            if (hydraulicGroupId < 1 || hydraulicGroupId > 2)
                throw new ArgumentOutOfRangeException(nameof(hydraulicGroupId));
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("恢复所有者不能为空。", nameof(ownerId));

            var timeoutMs = Math.Max(1, takeoverTimeoutMs);
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(timeoutMs / 1000.0 * Stopwatch.Frequency);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                OwnerState current;
                lock (_gate)
                {
                    if (!_owners.TryGetValue(hydraulicGroupId, out current))
                    {
                        var created = new OwnerState
                        {
                            HydraulicGroupId = hydraulicGroupId,
                            OwnerId = ownerId,
                            Priority = priority,
                            Cancellation = new CancellationTokenSource(),
                            Completion = new TaskCompletionSource<bool>(
                                TaskCreationOptions.RunContinuationsAsynchronously)
                        };
                        _owners.Add(hydraulicGroupId, created);
                        return new HydraulicRecoveryOwnershipLease(this, created);
                    }

                    if (priority >= current.Priority)
                    {
                        try { current.Cancellation.Cancel(); }
                        catch { }
                    }
                }

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                    throw new RecoveryOwnershipTimeoutException(
                        hydraulicGroupId,
                        ownerId,
                        current.OwnerId,
                        timeoutMs);
                var remainingMs = Math.Max(
                    1,
                    (int)Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency));
                var timeoutTask = Task.Delay(remainingMs, token);
                var completed = await Task.WhenAny(current.Completion.Task, timeoutTask)
                    .ConfigureAwait(false);
                if (completed != current.Completion.Task)
                {
                    token.ThrowIfCancellationRequested();
                    throw new RecoveryOwnershipTimeoutException(
                        hydraulicGroupId,
                        ownerId,
                        current.OwnerId,
                        timeoutMs);
                }
                await current.Completion.Task.ConfigureAwait(false);
            }
        }

        private void Release(OwnerState state)
        {
            if (state == null) return;
            lock (_gate)
            {
                if (_owners.TryGetValue(state.HydraulicGroupId, out var current) &&
                    ReferenceEquals(current, state))
                    _owners.Remove(state.HydraulicGroupId);
            }
            state.Completion.TrySetResult(true);
            try { state.Cancellation.Dispose(); }
            catch { }
        }

        internal sealed class HydraulicRecoveryOwnershipLease : IDisposable
        {
            private HydraulicRecoveryOwnershipCoordinator _coordinator;
            private OwnerState _state;

            internal HydraulicRecoveryOwnershipLease(
                HydraulicRecoveryOwnershipCoordinator coordinator,
                OwnerState state)
            {
                _coordinator = coordinator;
                _state = state;
            }

            internal int HydraulicGroupId => _state?.HydraulicGroupId ?? 0;
            internal string OwnerId => _state?.OwnerId ?? string.Empty;
            internal CancellationToken Token => _state?.Cancellation.Token ??
                                                   new CancellationToken(true);

            public void Dispose()
            {
                var coordinator = Interlocked.Exchange(ref _coordinator, null);
                var state = Interlocked.Exchange(ref _state, null);
                coordinator?.Release(state);
            }
        }
    }

    internal sealed class RecoveryOwnershipTimeoutException : TimeoutException
    {
        internal RecoveryOwnershipTimeoutException(
            int hydraulicGroupId,
            string requestedOwner,
            string activeOwner,
            int timeoutMs)
            : base(
                $"RecoveryOwnershipTimeout Hydraulic={hydraulicGroupId} " +
                $"Requested={requestedOwner} Active={activeOwner} TimeoutMs={timeoutMs}")
        {
        }
    }

    internal sealed class RecoveryStageTimeoutException : TimeoutException
    {
        internal RecoveryStageTimeoutException(string stage, int timeoutMs)
            : base($"RecoveryStageTimeout Stage={stage} TimeoutMs={timeoutMs}")
        {
            Stage = stage ?? string.Empty;
            TimeoutMs = timeoutMs;
        }

        internal string Stage { get; }
        internal int TimeoutMs { get; }
    }

    internal static class RecoveryStageDeadline
    {
        internal static async Task RunAsync(
            string stage,
            int timeoutMs,
            Func<CancellationToken, Task> action,
            CancellationToken token)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            token.ThrowIfCancellationRequested();
            var boundedMs = Math.Max(1, timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var actionTask = Task.Factory.StartNew(
                    () => action(linked.Token),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();
            var deadlineTask = Task.Delay(boundedMs, token);
            var completed = await Task.WhenAny(actionTask, deadlineTask).ConfigureAwait(false);
            if (completed == actionTask)
            {
                await actionTask.ConfigureAwait(false);
                return;
            }

            try { linked.Cancel(); }
            catch { }
            ObserveLateFault(actionTask);
            token.ThrowIfCancellationRequested();
            throw new RecoveryStageTimeoutException(stage, boundedMs);
        }

        private static void ObserveLateFault(Task task)
        {
            if (task == null) return;
            task.ContinueWith(
                completed =>
                {
                    var ignored = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal sealed class RecoveryFailureBackoffState
    {
        private int _consecutiveFailures;

        internal int Current => Volatile.Read(ref _consecutiveFailures);

        internal int RecordFailure()
        {
            return Interlocked.Increment(ref _consecutiveFailures);
        }

        internal void CommitSuccess()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
    }

    internal static class FormalSlotEligibility
    {
        internal static bool IsEligible(long firstEligibleSlot, long currentSlot)
        {
            return firstEligibleSlot < 0 || currentSlot >= firstEligibleSlot;
        }

        internal static int[] Filter(
            IEnumerable<int> participants,
            IReadOnlyDictionary<int, long> firstEligibleSlots,
            long currentSlot)
        {
            return (participants ?? Enumerable.Empty<int>())
                .Where(channel =>
                    firstEligibleSlots == null ||
                    !firstEligibleSlots.TryGetValue(channel, out var firstSlot) ||
                    IsEligible(firstSlot, currentSlot))
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }
    }
}
