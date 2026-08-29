using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// 正式阶段参与者的精确租约。退休、槽准入和异步安全确认只能作用于同一
    /// Run/RunEpoch/ParticipantGeneration，禁止旧回调污染后来重新加入的通道。
    /// </summary>
    internal sealed class FormalBatchParticipantLease
    {
        internal FormalBatchParticipantLease(
            Guid runId,
            long runEpoch,
            int channel,
            long participantGeneration)
        {
            RunId = runId;
            RunEpoch = runEpoch;
            Channel = channel;
            ParticipantGeneration = participantGeneration;
        }

        public Guid RunId { get; }
        public long RunEpoch { get; }
        public int Channel { get; }
        public long ParticipantGeneration { get; }

        internal bool SameIdentity(FormalBatchParticipantLease other) =>
            other != null &&
            RunId == other.RunId &&
            RunEpoch == other.RunEpoch &&
            Channel == other.Channel &&
            ParticipantGeneration == other.ParticipantGeneration;
    }

    internal sealed class FormalBatchParticipantTerminal
    {
        public int Channel { get; set; }
        public bool MotorOffConfirmed { get; set; }
        public bool MechanicalCycleCompleted { get; set; }
        public bool HydraulicMemberReleased { get; set; }
        public bool ControlSucceeded { get; set; }
        public bool PersistenceBoundaryRequired { get; set; }
        public bool PersistenceCommitted { get; set; }
        public bool RetirementBoundaryRequired { get; set; }
        public bool ExecutionPermitRevoked { get; set; }
        public bool PermanentlyIsolated { get; set; }
        public long CallbackElapsedMs { get; set; }
        public long PhysicalActionElapsedMs { get; set; }
        public long SharedCoordinationWaitMs { get; set; }
        public string Result { get; set; } = string.Empty;
        public DateTime CompletedUtc { get; set; }
    }

    internal sealed class FormalBatchSlotScope : IDisposable
    {
        private FormalBatchSlotCoordinator _owner;
        private readonly FormalBatchSlotCoordinator.SlotEntry _entry;
        private readonly Func<FormalBatchParticipantTerminal> _fallbackTerminalFactory;
        private int _completed;

        internal FormalBatchSlotScope(
            FormalBatchSlotCoordinator owner,
            FormalBatchSlotCoordinator.SlotEntry entry,
            int channel,
            long sharedWaitMs,
            Func<FormalBatchParticipantTerminal> fallbackTerminalFactory)
        {
            _owner = owner;
            _entry = entry;
            _fallbackTerminalFactory = fallbackTerminalFactory;
            Channel = channel;
            SharedWaitMs = Math.Max(0, sharedWaitMs);
        }

        public int Channel { get; }
        public long SharedWaitMs { get; }

        public void Complete(FormalBatchParticipantTerminal terminal)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;
            terminal ??= new FormalBatchParticipantTerminal();
            terminal.Channel = Channel;
            terminal.SharedCoordinationWaitMs = Math.Max(
                terminal.SharedCoordinationWaitMs,
                SharedWaitMs);
            if (terminal.CompletedUtc == default)
                terminal.CompletedUtc = DateTime.UtcNow;
            owner.CompleteParticipant(_entry, terminal);
        }

        public void Dispose()
        {
            FormalBatchParticipantTerminal terminal = null;
            try { terminal = _fallbackTerminalFactory?.Invoke(); }
            catch { }
            Complete(terminal ?? new FormalBatchParticipantTerminal
            {
                Channel = Channel,
                MotorOffConfirmed = false,
                HydraulicMemberReleased = false,
                PersistenceBoundaryRequired = true,
                PersistenceCommitted = false,
                Result = "CallbackExitedWithoutExplicitTerminal",
                CompletedUtc = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// 冻结同一正式槽的全批参与者，并在上一槽完成后统一滚动到首个未来墙钟边界。
    /// 协调器不执行硬件操作；调用方必须在提交终态前完成电机 OFF、液压释放和持久化封圈。
    /// </summary>
    internal sealed class FormalBatchSlotCoordinator
    {
        internal sealed class SlotEntry
        {
            internal readonly object Gate = new object();
            internal Guid RunId;
            internal long RunEpoch;
            internal long SlotOrdinal;
            internal DateTime WallClockAnchorUtc;
            internal int PeriodMs;
            internal HashSet<int> Participants;
            internal Dictionary<int, FormalBatchParticipantLease> ParticipantLeases;
            internal HashSet<int> Pending;
            internal HashSet<int> Entered;
            internal Dictionary<int, FormalBatchParticipantTerminal> Terminals;
            internal readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal DateTime? CompletedUtc;
            internal DateTime? NextReleaseUtc;
            internal string SafetyFailure;
        }

        private readonly ConcurrentDictionary<string, SlotEntry> _slots =
            new ConcurrentDictionary<string, SlotEntry>();
        private readonly ConcurrentDictionary<string, string> _retirementRequests =
            new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, FormalBatchParticipantTerminal>
            _confirmedRetirements =
                new ConcurrentDictionary<string, FormalBatchParticipantTerminal>();
        private readonly ConcurrentDictionary<string, FormalBatchParticipantLease> _activeLeases =
            new ConcurrentDictionary<string, FormalBatchParticipantLease>();
        private long _participantGeneration;

        public FormalBatchParticipantLease RegisterParticipant(
            Guid runId,
            long runEpoch,
            int channel)
        {
            if (runId == Guid.Empty) throw new ArgumentException("正式参与者 RunId 不能为空。", nameof(runId));
            if (runEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(runEpoch));
            if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel));

            var generation = Interlocked.Increment(ref _participantGeneration);
            var lease = new FormalBatchParticipantLease(runId, runEpoch, channel, generation);
            _activeLeases[CreateActiveLeaseKey(runId, runEpoch, channel)] = lease;
            return lease;
        }

        public async Task<FormalBatchSlotScope> EnterAsync(
            Guid runId,
            long slotOrdinal,
            int channel,
            IEnumerable<int> participants,
            DateTime wallClockAnchorUtc,
            int periodMs,
            Action waitingCallback,
            CancellationToken token,
            Func<FormalBatchParticipantTerminal> fallbackTerminalFactory = null)
        {
            var frozenChannels = (participants ?? Array.Empty<int>())
                .Where(item => item > 0)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();
            var frozenLeases = frozenChannels
                .Select(item => GetOrRegisterLegacyLease(runId, item))
                .ToArray();
            var participantLease = frozenLeases.FirstOrDefault(item => item.Channel == channel) ??
                                   GetOrRegisterLegacyLease(runId, channel);
            return await EnterAsync(
                    participantLease,
                    slotOrdinal,
                    frozenLeases,
                    wallClockAnchorUtc,
                    periodMs,
                    waitingCallback,
                    token,
                    fallbackTerminalFactory)
                .ConfigureAwait(false);
        }

        public async Task<FormalBatchSlotScope> EnterAsync(
            FormalBatchParticipantLease participantLease,
            long slotOrdinal,
            IEnumerable<FormalBatchParticipantLease> participants,
            DateTime wallClockAnchorUtc,
            int periodMs,
            Action waitingCallback,
            CancellationToken token,
            Func<FormalBatchParticipantTerminal> fallbackTerminalFactory = null)
        {
            if (participantLease == null) throw new ArgumentNullException(nameof(participantLease));
            var runId = participantLease.RunId;
            var runEpoch = participantLease.RunEpoch;
            var channel = participantLease.Channel;
            if (runId == Guid.Empty) throw new ArgumentException("正式槽 RunId 不能为空。", nameof(participantLease));
            if (runEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(participantLease));
            if (slotOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(slotOrdinal));
            if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel));
            if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));

            var frozenLeases = (participants ?? Array.Empty<FormalBatchParticipantLease>())
                .Where(item => item != null && item.RunId == runId && item.RunEpoch == runEpoch && item.Channel > 0)
                .GroupBy(item => item.Channel)
                .Select(group => group.Single())
                .OrderBy(item => item.Channel)
                .ToArray();
            var frozen = frozenLeases.Select(item => item.Channel).ToArray();
            if (!frozen.Contains(channel))
                throw new InvalidOperationException(
                    $"FormalBatchSlotMemberMissing Run={runId:N} Slot={slotOrdinal} EPB={channel}");
            if (!IsLeaseCurrent(participantLease))
                throw new InvalidOperationException(
                    $"FormalBatchParticipantLeaseStale Run={runId:N} Epoch={runEpoch} " +
                    $"Slot={slotOrdinal} EPB={channel} Generation={participantLease.ParticipantGeneration}");

            var normalizedAnchor = wallClockAnchorUtc.Kind == DateTimeKind.Utc
                ? wallClockAnchorUtc
                : wallClockAnchorUtc.ToUniversalTime();
            var key = CreateKey(runId, runEpoch, slotOrdinal);
            var candidate = new SlotEntry
            {
                RunId = runId,
                RunEpoch = runEpoch,
                SlotOrdinal = slotOrdinal,
                WallClockAnchorUtc = normalizedAnchor,
                PeriodMs = periodMs,
                Participants = new HashSet<int>(frozen),
                ParticipantLeases = frozenLeases.ToDictionary(item => item.Channel),
                Pending = new HashSet<int>(frozen),
                Entered = new HashSet<int>(),
                Terminals = new Dictionary<int, FormalBatchParticipantTerminal>()
            };
            var entry = _slots.GetOrAdd(key, candidate);
            ValidateImmutable(entry, frozenLeases, normalizedAnchor, periodMs);
            var completedByRetirement = false;
            string admissionFailure = null;
            lock (entry.Gate)
            {
                completedByRetirement = ApplyConfirmedRetirementsUnderLock(entry);
                if (!entry.Pending.Contains(channel))
                    admissionFailure =
                        $"FormalBatchSlotMemberAlreadyRetired Run={runId:N} Slot={slotOrdinal} EPB={channel}";
                else if (!entry.Entered.Add(channel))
                    admissionFailure =
                        $"FormalBatchSlotMemberAlreadyEntered Run={runId:N} Slot={slotOrdinal} EPB={channel}";
            }
            PublishCompletion(entry, completedByRetirement);
            if (admissionFailure != null)
                throw new InvalidOperationException(admissionFailure);

            var waitStartedUtc = DateTime.UtcNow;
            try
            {
                if (_slots.TryGetValue(CreateKey(runId, runEpoch, slotOrdinal - 1L), out var previous))
                {
                    if (!previous.Completion.Task.IsCompleted)
                        waitingCallback?.Invoke();
                    await AwaitWithCancellation(previous.Completion.Task, token).ConfigureAwait(false);

                    DateTime releaseUtc;
                    lock (previous.Gate)
                    {
                        if (!previous.NextReleaseUtc.HasValue)
                        {
                            previous.NextReleaseUtc = CalculateFirstFutureBoundary(
                                previous.WallClockAnchorUtc,
                                previous.CompletedUtc ?? DateTime.UtcNow,
                                previous.PeriodMs);
                        }
                        releaseUtc = previous.NextReleaseUtc.Value;
                    }

                    var delay = releaseUtc - DateTime.UtcNow;
                    if (delay.TotalMilliseconds > 1)
                    {
                        waitingCallback?.Invoke();
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    else
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            catch
            {
                WithdrawBeforeAdmission(entry, channel);
                throw;
            }

            TrimCompleted(runId, runEpoch, slotOrdinal);
            return new FormalBatchSlotScope(
                this,
                entry,
                channel,
                (long)Math.Max(0, (DateTime.UtcNow - waitStartedUtc).TotalMilliseconds),
                fallbackTerminalFactory);
        }

        public bool RequestRetirement(Guid runId, int channel, string reason)
        {
            if (runId == Guid.Empty || channel <= 0) return false;
            var lease = FindActiveLease(runId, channel) ?? GetOrRegisterLegacyLease(runId, channel);
            return RequestRetirement(lease, reason);
        }

        public bool RequestRetirement(FormalBatchParticipantLease lease, string reason)
        {
            if (!IsLeaseCurrent(lease)) return false;
            return _retirementRequests.TryAdd(CreateRetirementKey(lease), reason ?? string.Empty);
        }

        public void ConfirmRetirement(
            Guid runId,
            int channel,
            FormalBatchParticipantTerminal terminal)
        {
            if (runId == Guid.Empty || channel <= 0 || terminal == null) return;
            var lease = FindActiveLease(runId, channel);
            if (lease == null) return;
            ConfirmRetirement(lease, terminal);
        }

        public void ConfirmRetirement(
            FormalBatchParticipantLease lease,
            FormalBatchParticipantTerminal terminal)
        {
            if (lease == null || terminal == null || !IsLeaseCurrent(lease)) return;
            if (!_retirementRequests.TryRemove(CreateRetirementKey(lease), out var reason))
                return;
            var channel = lease.Channel;
            var retirementTerminal = CloneRetirementTerminal(terminal, channel, reason);
            _confirmedRetirements[CreateRetirementKey(lease)] = retirementTerminal;
            foreach (var entry in _slots.Values.Where(item =>
                         item.RunId == lease.RunId && item.RunEpoch == lease.RunEpoch).ToArray())
            {
                var complete = false;
                lock (entry.Gate)
                {
                    // 已进入本槽的回调必须由自己的 scope 提交完整终态；退休
                    // 栅栏只代为关闭尚未进入、但已被兄弟通道预创建的未来槽。
                    if (entry.Entered.Contains(channel) || !entry.Pending.Contains(channel) ||
                        !entry.ParticipantLeases.TryGetValue(channel, out var entryLease) ||
                        !entryLease.SameIdentity(lease))
                        continue;
                    complete = CompleteParticipantUnderLock(entry, retirementTerminal);
                }
                PublishCompletion(entry, complete);
            }
        }

        public void ClearRun(Guid runId)
        {
            if (runId == Guid.Empty) return;
            foreach (var pair in _slots.Where(pair => pair.Value.RunId == runId).ToArray())
            {
                if (_slots.TryRemove(pair.Key, out var entry))
                {
                    lock (entry.Gate)
                    {
                        entry.Pending.Clear();
                        entry.CompletedUtc = DateTime.UtcNow;
                    }
                    entry.Completion.TrySetResult(true);
                }
            }
            foreach (var key in _retirementRequests.Keys
                         .Where(key => key.StartsWith(runId.ToString("N") + ":", StringComparison.Ordinal))
                         .ToArray())
                _retirementRequests.TryRemove(key, out _);
            foreach (var key in _confirmedRetirements.Keys
                         .Where(key => key.StartsWith(runId.ToString("N") + ":", StringComparison.Ordinal))
                         .ToArray())
                _confirmedRetirements.TryRemove(key, out _);
            foreach (var key in _activeLeases.Keys
                         .Where(key => key.StartsWith(runId.ToString("N") + ":", StringComparison.Ordinal))
                         .ToArray())
                _activeLeases.TryRemove(key, out _);
        }

        internal void CompleteParticipant(
            SlotEntry entry,
            FormalBatchParticipantTerminal terminal)
        {
            if (entry == null || terminal == null) return;
            bool complete;
            lock (entry.Gate)
            {
                complete = CompleteParticipantUnderLock(entry, terminal);
            }
            PublishCompletion(entry, complete);
        }

        private bool ApplyConfirmedRetirementsUnderLock(SlotEntry entry)
        {
            var complete = false;
            foreach (var channel in entry.Participants.ToArray())
            {
                if (entry.Entered.Contains(channel) || !entry.Pending.Contains(channel))
                    continue;
                if (_confirmedRetirements.TryGetValue(
                        CreateRetirementKey(entry.ParticipantLeases[channel]),
                        out var terminal))
                    complete = CompleteParticipantUnderLock(entry, terminal) || complete;
            }
            return complete;
        }

        private static bool CompleteParticipantUnderLock(
            SlotEntry entry,
            FormalBatchParticipantTerminal terminal)
        {
            if (!entry.Pending.Remove(terminal.Channel)) return false;
            entry.Terminals[terminal.Channel] = terminal;
            if (!terminal.MotorOffConfirmed || !terminal.HydraulicMemberReleased ||
                (terminal.PersistenceBoundaryRequired && !terminal.PersistenceCommitted) ||
                (terminal.RetirementBoundaryRequired && !terminal.ExecutionPermitRevoked))
                entry.SafetyFailure =
                    $"FormalSlotSafetyBoundaryFailed EPB={terminal.Channel} " +
                    $"MotorOff={terminal.MotorOffConfirmed} " +
                    $"HydraulicReleased={terminal.HydraulicMemberReleased} " +
                    $"Persistence={terminal.PersistenceCommitted} " +
                    $"ExecutionRevoked={terminal.ExecutionPermitRevoked}";
            var complete = entry.Pending.Count == 0;
            if (complete)
            {
                entry.CompletedUtc = DateTime.UtcNow;
                entry.NextReleaseUtc = CalculateFirstFutureBoundary(
                    entry.WallClockAnchorUtc,
                    entry.CompletedUtc.Value,
                    entry.PeriodMs);
            }
            return complete;
        }

        private static void PublishCompletion(SlotEntry entry, bool complete)
        {
            if (!complete) return;
            if (string.IsNullOrWhiteSpace(entry.SafetyFailure))
                entry.Completion.TrySetResult(true);
            else
                entry.Completion.TrySetException(
                    new InvalidOperationException(entry.SafetyFailure));
        }

        private static void WithdrawBeforeAdmission(SlotEntry entry, int channel)
        {
            var complete = false;
            lock (entry.Gate)
            {
                // EnterAsync has not returned a scope yet, so this participant
                // cannot have started this slot's physical action or persistence
                // boundary. Remove only its provisional admission; a participant
                // that already obtained a scope must still submit a full terminal.
                if (!entry.Entered.Remove(channel)) return;
                if (!entry.Pending.Remove(channel)) return;
                complete = entry.Pending.Count == 0;
                if (complete)
                {
                    entry.CompletedUtc = DateTime.UtcNow;
                    entry.NextReleaseUtc = CalculateFirstFutureBoundary(
                        entry.WallClockAnchorUtc,
                        entry.CompletedUtc.Value,
                        entry.PeriodMs);
                }
            }
            if (complete)
                entry.Completion.TrySetResult(true);
        }

        internal static DateTime CalculateFirstFutureBoundary(
            DateTime anchorUtc,
            DateTime afterUtc,
            int periodMs)
        {
            var anchor = anchorUtc.Kind == DateTimeKind.Utc
                ? anchorUtc
                : anchorUtc.ToUniversalTime();
            var after = afterUtc.Kind == DateTimeKind.Utc
                ? afterUtc
                : afterUtc.ToUniversalTime();
            var elapsedMs = Math.Max(0, (after - anchor).TotalMilliseconds);
            var ordinal = (long)Math.Floor(elapsedMs / Math.Max(1, periodMs)) + 1L;
            var boundary = anchor.AddMilliseconds(ordinal * (double)periodMs);
            while (boundary <= after)
            {
                ordinal++;
                boundary = anchor.AddMilliseconds(ordinal * (double)periodMs);
            }
            return boundary;
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken token)
        {
            var canceled = Task.Delay(Timeout.Infinite, token);
            var completed = await Task.WhenAny(task, canceled).ConfigureAwait(false);
            if (completed != task)
                token.ThrowIfCancellationRequested();
            await task.ConfigureAwait(false);
        }

        private static string CreateKey(Guid runId, long runEpoch, long slotOrdinal) =>
            $"{runId:N}:{runEpoch}:{slotOrdinal}";

        private static string CreateActiveLeaseKey(Guid runId, long runEpoch, int channel) =>
            $"{runId:N}:{runEpoch}:{channel}";

        private static string CreateRetirementKey(FormalBatchParticipantLease lease) =>
            $"{lease.RunId:N}:{lease.RunEpoch}:{lease.Channel}:{lease.ParticipantGeneration}";

        private FormalBatchParticipantLease GetOrRegisterLegacyLease(Guid runId, int channel)
        {
            if (runId == Guid.Empty || channel <= 0)
                throw new ArgumentException("旧正式槽参与者身份无效。");
            var key = CreateActiveLeaseKey(runId, 1, channel);
            return _activeLeases.GetOrAdd(key, _ =>
                new FormalBatchParticipantLease(
                    runId,
                    1,
                    channel,
                    Interlocked.Increment(ref _participantGeneration)));
        }

        private FormalBatchParticipantLease FindActiveLease(Guid runId, int channel)
        {
            return _activeLeases.Values
                .Where(item => item.RunId == runId && item.Channel == channel)
                .OrderByDescending(item => item.RunEpoch)
                .ThenByDescending(item => item.ParticipantGeneration)
                .FirstOrDefault();
        }

        private bool IsLeaseCurrent(FormalBatchParticipantLease lease)
        {
            if (lease == null) return false;
            return _activeLeases.TryGetValue(
                       CreateActiveLeaseKey(lease.RunId, lease.RunEpoch, lease.Channel),
                       out var current) &&
                   current.SameIdentity(lease);
        }

        private static FormalBatchParticipantTerminal CloneRetirementTerminal(
            FormalBatchParticipantTerminal source,
            int channel,
            string reason)
        {
            return new FormalBatchParticipantTerminal
            {
                Channel = channel,
                MotorOffConfirmed = source.MotorOffConfirmed,
                MechanicalCycleCompleted = source.MechanicalCycleCompleted,
                HydraulicMemberReleased = source.HydraulicMemberReleased,
                ControlSucceeded = source.ControlSucceeded,
                PersistenceBoundaryRequired = source.PersistenceBoundaryRequired,
                PersistenceCommitted = source.PersistenceCommitted,
                RetirementBoundaryRequired = true,
                ExecutionPermitRevoked = source.ExecutionPermitRevoked,
                PermanentlyIsolated = true,
                CallbackElapsedMs = source.CallbackElapsedMs,
                PhysicalActionElapsedMs = source.PhysicalActionElapsedMs,
                SharedCoordinationWaitMs = source.SharedCoordinationWaitMs,
                Result = string.IsNullOrWhiteSpace(reason) ? source.Result : reason,
                CompletedUtc = source.CompletedUtc == default ? DateTime.UtcNow : source.CompletedUtc
            };
        }

        private static void ValidateImmutable(
            SlotEntry entry,
            IReadOnlyCollection<FormalBatchParticipantLease> participants,
            DateTime anchorUtc,
            int periodMs)
        {
            var sameMembers = entry.Participants.SetEquals(participants.Select(item => item.Channel));
            var sameLeases = sameMembers && participants.All(item =>
                entry.ParticipantLeases.TryGetValue(item.Channel, out var frozen) &&
                frozen.SameIdentity(item));
            if (!sameLeases || entry.WallClockAnchorUtc != anchorUtc || entry.PeriodMs != periodMs)
                throw new InvalidOperationException(
                    $"FormalBatchSlotImmutableMismatch Run={entry.RunId:N} Slot={entry.SlotOrdinal}");
        }

        private void TrimCompleted(Guid runId, long runEpoch, long currentSlot)
        {
            foreach (var pair in _slots.Where(pair =>
                         pair.Value.RunId == runId &&
                         pair.Value.RunEpoch == runEpoch &&
                         pair.Value.SlotOrdinal < currentSlot - 4L &&
                         pair.Value.Completion.Task.IsCompleted).ToArray())
                _slots.TryRemove(pair.Key, out _);
        }
    }
}
