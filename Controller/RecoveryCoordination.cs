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
            internal int LeaseCount;
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

        /// <summary>
        /// 硬件确认是当前组所有软件恢复事务的终止事实。先同步发出取消，调用方
        /// 随后立即执行物理OFF；不在故障线程等待旧owner退出。
        /// </summary>
        internal bool CancelGroup(int hydraulicGroupId)
        {
            OwnerState owner;
            lock (_gate)
            {
                if (!_owners.TryGetValue(hydraulicGroupId, out owner)) return false;
                try { owner.Cancellation.Cancel(); }
                catch { }
            }
            return true;
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
                                TaskCreationOptions.RunContinuationsAsynchronously),
                            LeaseCount = 1
                        };
                        _owners.Add(hydraulicGroupId, created);
                        return new HydraulicRecoveryOwnershipLease(this, created);
                    }

                    // 同一次双DAQ批次恢复会由两个设备上下文到达同一液压组。它们共享
                    // ownerId 时属于一个事务，不得按“同优先级抢占”互相取消。
                    if (priority == current.Priority &&
                        string.Equals(ownerId, current.OwnerId, StringComparison.Ordinal))
                    {
                        current.LeaseCount++;
                        return new HydraulicRecoveryOwnershipLease(this, current);
                    }

                    // 只有严格更高优先级才能抢占。两个不同电源组可能同时映射到
                    // 同一液压组；同优先级恢复必须排队，不能互相取消后都把状态
                    // 留在 Recovering。相同 ownerId 的同一事务已在上方共享租约。
                    if (priority > current.Priority)
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
                // Completion is published by Dispose. Yield once before the
                // successor can acquire so the cancelled owner's finally
                // block can finish its remaining local terminal bookkeeping.
                await Task.Yield();
            }
        }

        private void Release(OwnerState state)
        {
            if (state == null) return;
            var complete = false;
            lock (_gate)
            {
                if (_owners.TryGetValue(state.HydraulicGroupId, out var current) &&
                    ReferenceEquals(current, state))
                {
                    state.LeaseCount = Math.Max(0, state.LeaseCount - 1);
                    if (state.LeaseCount == 0)
                    {
                        _owners.Remove(state.HydraulicGroupId);
                        complete = true;
                    }
                }
            }
            if (!complete) return;
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

    internal enum DaqRecoveryPhase
    {
        StaleDetected = 0,
        DoOffSubmitted = 1,
        DoOffConfirmed = 2,
        PowerOffSubmitted = 3,
        PowerOffConfirmed = 4,
        CutoffCompleted = 5,
        DaqRestartStarted = 6,
        FirstFreshBatch = 7,
        PressureRevalidated = 8,
        PersistenceBoundaryClosed = 9,
        Validating = 10,
        Rejoining = 11,
        Committed = 12,
        SafeIdle = 13,
        Terminal = 14,

        // Source compatibility for the older phase names.  They intentionally
        // alias the new monotonic stages; no caller can move the phase backward.
        Created = StaleDetected,
        CutoffStarted = DoOffSubmitted,
        ValidationReady = PersistenceBoundaryClosed,
        RejoinCompleted = Rejoining
    }

    /// <summary>
    ///     DAQ 自恢复的单调阶段门。恢复事件可以先于整组截止完成到达，但验证与重入
    ///     必须等待 CutoffCompleted；重建后的首批新鲜数据、压力和持久化边界
    ///     依次确认后才允许 Validation；
    ///     恢复终态只能在共同重入成功后提交。
    /// </summary>
    internal sealed class DaqRecoveryPhaseGate
    {
        private int _phase;
        private int _rejoinCompleted;
        private readonly TaskCompletionSource<bool> _cutoffCompleted =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _validationReady =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal DaqRecoveryPhase Current =>
            (DaqRecoveryPhase)Volatile.Read(ref _phase);

        internal bool BeginCutoff()
        {
            // Entering the cutoff workflow is not a physical DO submission.
            // The controller publishes DoOffSubmitted only after the real
            // high-priority batch admission returns success.
            return Current == DaqRecoveryPhase.StaleDetected;
        }

        internal bool CompleteCutoff()
        {
            // Cutoff is a real safety boundary.  It may only be published after
            // both the DO and the electrical-group OFF confirmations have been
            // observed; allowing this helper to jump from DoOffSubmitted made a
            // late DAQ callback publish a completed cutoff while power was still
            // being disabled.
            if (Current != DaqRecoveryPhase.PowerOffConfirmed)
                return false;
            var advanced = TryAdvance(DaqRecoveryPhase.CutoffCompleted);
            if ((int)Current >= (int)DaqRecoveryPhase.CutoffCompleted)
                _cutoffCompleted.TrySetResult(true);
            return advanced;
        }

        internal Task WaitForCutoffAsync(CancellationToken token)
        {
            return WaitWithCancellationAsync(_cutoffCompleted.Task, token);
        }

        internal bool EnableValidation()
        {
            // This method is retained for source compatibility with older
            // callers, but it is a barrier acknowledgement only.  It must not
            // manufacture PersistenceBoundaryClosed (or skip Restart/Fresh/
            // Pressure) before the controller has published those real events.
            var advanced = Current >= DaqRecoveryPhase.PersistenceBoundaryClosed &&
                           Current < DaqRecoveryPhase.Terminal;
            if ((int)Current >= (int)DaqRecoveryPhase.PersistenceBoundaryClosed)
                _validationReady.TrySetResult(true);
            return advanced;
        }

        internal Task WaitForValidationReadyAsync(CancellationToken token)
        {
            return WaitWithCancellationAsync(_validationReady.Task, token);
        }

        internal bool TryBeginValidation()
        {
            while (true)
            {
                var current = Current;
                if (current == DaqRecoveryPhase.Validating) return true;
                if (current != DaqRecoveryPhase.PersistenceBoundaryClosed) return false;
                if (Interlocked.CompareExchange(
                        ref _phase,
                        (int)DaqRecoveryPhase.Validating,
                        (int)DaqRecoveryPhase.PersistenceBoundaryClosed) ==
                    (int)DaqRecoveryPhase.PersistenceBoundaryClosed)
                    return true;
            }
        }

        internal bool TryBeginRejoin()
        {
            return Current == DaqRecoveryPhase.Validating &&
                   TryAdvance(DaqRecoveryPhase.Rejoining);
        }

        internal bool CompleteRejoin()
        {
            if (Current != DaqRecoveryPhase.Rejoining) return false;
            Interlocked.Exchange(ref _rejoinCompleted, 1);
            return true;
        }

        internal bool TryCommit()
        {
            if (Volatile.Read(ref _rejoinCompleted) == 0) return false;
            return TryAdvance(DaqRecoveryPhase.Committed);
        }

        internal void FailRejoin()
        {
            Interlocked.Exchange(ref _rejoinCompleted, 0);
            Interlocked.CompareExchange(
                ref _phase,
                (int)DaqRecoveryPhase.Validating,
                (int)DaqRecoveryPhase.Rejoining);
        }

        internal bool TryAdvance(DaqRecoveryPhase next)
        {
            var target = (int)next;
            if (target < (int)DaqRecoveryPhase.StaleDetected ||
                target > (int)DaqRecoveryPhase.Terminal)
                return false;
            while (true)
            {
                var current = Volatile.Read(ref _phase);
                if (current == target) return true;
                if (current >= (int)DaqRecoveryPhase.Terminal) return false;
                // SafeIdle is a terminal safety branch; it cannot be promoted
                // back into validation/rejoin by a late callback.
                if (current == (int)DaqRecoveryPhase.SafeIdle &&
                    target != (int)DaqRecoveryPhase.Terminal)
                    return false;
                if (target < current) return false;

                // Normal recovery is deliberately a strict state machine.  A
                // caller cannot publish PowerOffConfirmed while the preceding
                // DO/power submission stages are absent, nor can validation be
                // enabled by jumping over the fresh-batch and persistence
                // evidence.  SafeIdle is the only intentional branch out of
                // the normal sequence and is handled by MarkSafeIdle below.
                if (target == (int)DaqRecoveryPhase.Terminal)
                {
                    if (current != (int)DaqRecoveryPhase.Committed &&
                        current != (int)DaqRecoveryPhase.SafeIdle)
                        return false;
                }
                else if (target != current + 1)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref _phase, target, current) == current)
                {
                    if (target >= (int)DaqRecoveryPhase.CutoffCompleted)
                        _cutoffCompleted.TrySetResult(true);
                    if (target >= (int)DaqRecoveryPhase.PersistenceBoundaryClosed)
                        _validationReady.TrySetResult(true);
                    return true;
                }
            }
        }

        internal bool MarkSafeIdle()
        {
            while (true)
            {
                var current = (int)Current;
                if (current >= (int)DaqRecoveryPhase.Committed)
                    return false;
                if (current == (int)DaqRecoveryPhase.SafeIdle)
                    return true;
                if (current >= (int)DaqRecoveryPhase.Terminal)
                    return false;
                if (Interlocked.CompareExchange(
                        ref _phase,
                        (int)DaqRecoveryPhase.SafeIdle,
                        current) == current)
                    return true;
            }
        }

        internal bool MarkTerminal()
        {
            return TryAdvance(DaqRecoveryPhase.Terminal);
        }

        private static async Task WaitWithCancellationAsync(
            Task task,
            CancellationToken token)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            token.ThrowIfCancellationRequested();
            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
        }
    }

    internal static class RecoveryEpochGuard
    {
        internal static bool CanApplyParticipantCleanup(
            bool participantExists,
            long expectedVersion,
            long currentVersion)
        {
            if (!participantExists) return true;
            return expectedVersion > 0 && expectedVersion == currentVersion;
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
