using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    internal sealed class FormalBatchParticipantTerminal
    {
        public int Channel { get; set; }
        public bool MotorOffConfirmed { get; set; }
        public bool MechanicalCycleCompleted { get; set; }
        public bool HydraulicMemberReleased { get; set; }
        public bool ControlSucceeded { get; set; }
        public bool PersistenceBoundaryRequired { get; set; }
        public bool PersistenceCommitted { get; set; }
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
            internal long SlotOrdinal;
            internal DateTime WallClockAnchorUtc;
            internal int PeriodMs;
            internal HashSet<int> Participants;
            internal HashSet<int> Pending;
            internal Dictionary<int, FormalBatchParticipantTerminal> Terminals;
            internal readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal DateTime? CompletedUtc;
            internal DateTime? NextReleaseUtc;
            internal string SafetyFailure;
        }

        private readonly ConcurrentDictionary<string, SlotEntry> _slots =
            new ConcurrentDictionary<string, SlotEntry>();

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
            if (runId == Guid.Empty) throw new ArgumentException("正式槽 RunId 不能为空。", nameof(runId));
            if (slotOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(slotOrdinal));
            if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel));
            if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));

            var frozen = (participants ?? Array.Empty<int>())
                .Where(item => item > 0)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();
            if (!frozen.Contains(channel))
                throw new InvalidOperationException(
                    $"FormalBatchSlotMemberMissing Run={runId:N} Slot={slotOrdinal} EPB={channel}");

            var normalizedAnchor = wallClockAnchorUtc.Kind == DateTimeKind.Utc
                ? wallClockAnchorUtc
                : wallClockAnchorUtc.ToUniversalTime();
            var key = CreateKey(runId, slotOrdinal);
            var candidate = new SlotEntry
            {
                RunId = runId,
                SlotOrdinal = slotOrdinal,
                WallClockAnchorUtc = normalizedAnchor,
                PeriodMs = periodMs,
                Participants = new HashSet<int>(frozen),
                Pending = new HashSet<int>(frozen),
                Terminals = new Dictionary<int, FormalBatchParticipantTerminal>()
            };
            var entry = _slots.GetOrAdd(key, candidate);
            ValidateImmutable(entry, frozen, normalizedAnchor, periodMs);

            var waitStartedUtc = DateTime.UtcNow;
            if (_slots.TryGetValue(CreateKey(runId, slotOrdinal - 1L), out var previous))
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

            TrimCompleted(runId, slotOrdinal);
            return new FormalBatchSlotScope(
                this,
                entry,
                channel,
                (long)Math.Max(0, (DateTime.UtcNow - waitStartedUtc).TotalMilliseconds),
                fallbackTerminalFactory);
        }

        public void RetireParticipant(Guid runId, int channel, string reason)
        {
            if (runId == Guid.Empty || channel <= 0) return;
            foreach (var entry in _slots.Values.Where(item => item.RunId == runId).ToArray())
            {
                var complete = false;
                lock (entry.Gate)
                {
                    if (!entry.Pending.Remove(channel)) continue;
                    entry.Terminals[channel] = new FormalBatchParticipantTerminal
                    {
                        Channel = channel,
                        MotorOffConfirmed = true,
                        HydraulicMemberReleased = true,
                        PermanentlyIsolated = true,
                        Result = string.IsNullOrWhiteSpace(reason) ? "ParticipantRetired" : reason,
                        CompletedUtc = DateTime.UtcNow
                    };
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
                if (complete) entry.Completion.TrySetResult(true);
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
        }

        internal void CompleteParticipant(
            SlotEntry entry,
            FormalBatchParticipantTerminal terminal)
        {
            if (entry == null || terminal == null) return;
            var complete = false;
            lock (entry.Gate)
            {
                if (!entry.Pending.Remove(terminal.Channel)) return;
                entry.Terminals[terminal.Channel] = terminal;
                if (!terminal.MotorOffConfirmed || !terminal.HydraulicMemberReleased ||
                    (terminal.PersistenceBoundaryRequired && !terminal.PersistenceCommitted))
                    entry.SafetyFailure =
                        $"FormalSlotSafetyBoundaryFailed EPB={terminal.Channel} " +
                        $"MotorOff={terminal.MotorOffConfirmed} " +
                        $"HydraulicReleased={terminal.HydraulicMemberReleased} " +
                        $"Persistence={terminal.PersistenceCommitted}";
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
            {
                if (string.IsNullOrWhiteSpace(entry.SafetyFailure))
                    entry.Completion.TrySetResult(true);
                else
                    entry.Completion.TrySetException(
                        new InvalidOperationException(entry.SafetyFailure));
            }
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

        private static string CreateKey(Guid runId, long slotOrdinal) =>
            $"{runId:N}:{slotOrdinal}";

        private static void ValidateImmutable(
            SlotEntry entry,
            IReadOnlyCollection<int> participants,
            DateTime anchorUtc,
            int periodMs)
        {
            var sameMembers = entry.Participants.SetEquals(participants);
            if (!sameMembers || entry.WallClockAnchorUtc != anchorUtc || entry.PeriodMs != periodMs)
                throw new InvalidOperationException(
                    $"FormalBatchSlotImmutableMismatch Run={entry.RunId:N} Slot={entry.SlotOrdinal}");
        }

        private void TrimCompleted(Guid runId, long currentSlot)
        {
            foreach (var pair in _slots.Where(pair =>
                         pair.Value.RunId == runId &&
                         pair.Value.SlotOrdinal < currentSlot - 4L &&
                         pair.Value.Completion.Task.IsCompleted).ToArray())
                _slots.TryRemove(pair.Key, out _);
        }
    }
}
