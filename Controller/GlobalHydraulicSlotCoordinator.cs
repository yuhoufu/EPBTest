using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>跨液压组不可变周期槽身份。</summary>
    internal sealed class GlobalHydraulicSlotKey : IEquatable<GlobalHydraulicSlotKey>
    {
        public GlobalHydraulicSlotKey(Guid runId, HydraulicPhaseKind phaseKind, long slot)
        {
            RunId = runId;
            PhaseKind = phaseKind;
            Slot = slot;
        }

        public Guid RunId { get; }
        public HydraulicPhaseKind PhaseKind { get; }
        public long Slot { get; }

        public bool Equals(GlobalHydraulicSlotKey other) =>
            other != null && RunId == other.RunId && PhaseKind == other.PhaseKind && Slot == other.Slot;

        public override bool Equals(object obj) => Equals(obj as GlobalHydraulicSlotKey);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = RunId.GetHashCode();
                hash = (hash * 397) ^ (int)PhaseKind;
                hash = (hash * 397) ^ Slot.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => $"{RunId:N}/{PhaseKind}/{Slot}";
    }

    internal sealed class GlobalHydraulicGroupOutcome
    {
        public int HydraulicId { get; set; }
        public int[] Participants { get; set; } = Array.Empty<int>();
        public DateTime DispatchUtc { get; set; }
        public HydraulicCycleLease Lease { get; set; }
        public Exception Error { get; set; }
        public bool IsSuccess => Lease != null && Error == null;
        public DateTime? BuildStartedUtc => Lease?.Qualification?.BuildStartedUtc;
        public DateTime? QualifiedUtc => Lease?.Qualification?.ReachedUtc;
    }

    internal sealed class GlobalHydraulicSlotResult
    {
        private readonly IReadOnlyDictionary<int, GlobalHydraulicGroupOutcome> _groups;
        private int _logOwnership;

        internal GlobalHydraulicSlotResult(
            GlobalHydraulicSlotKey key,
            DateTime pressureBuildPlannedUtc,
            DateTime? motorAnchorUtc,
            DateTime? motorDeadlineUtc,
            string alignmentState,
            IEnumerable<GlobalHydraulicGroupOutcome> groups)
        {
            Key = key;
            PressureBuildPlannedUtc = pressureBuildPlannedUtc;
            MotorAnchorUtc = motorAnchorUtc;
            MotorDeadlineUtc = motorDeadlineUtc;
            AlignmentState = alignmentState ?? "Unknown";
            _groups = (groups ?? Array.Empty<GlobalHydraulicGroupOutcome>())
                .ToDictionary(item => item.HydraulicId);
        }

        public GlobalHydraulicSlotKey Key { get; }
        public DateTime PressureBuildPlannedUtc { get; }
        public DateTime? MotorAnchorUtc { get; }
        public DateTime? MotorDeadlineUtc { get; }
        public string AlignmentState { get; }
        public IReadOnlyDictionary<int, GlobalHydraulicGroupOutcome> Groups => _groups;
        public bool HasFailures => _groups.Values.Any(item => !item.IsSuccess);
        public bool ShouldDeferHealthyGroups => HasFailures;
        public bool TryClaimLogOwnership() => Interlocked.CompareExchange(ref _logOwnership, 1, 0) == 0;

        public HydraulicCycleLease GetLeaseOrThrow(int hydraulicId)
        {
            if (!_groups.TryGetValue(hydraulicId, out var outcome))
                throw new InvalidOperationException(
                    $"GlobalHydraulicSlotMissingGroup Key={Key} Hydraulic={hydraulicId}");
            if (outcome.Error != null)
                ExceptionDispatchInfo.Capture(outcome.Error).Throw();
            if (outcome.Lease == null)
                throw new InvalidOperationException(
                    $"GlobalHydraulicSlotMissingLease Key={Key} Hydraulic={hydraulicId}");
            return outcome.Lease;
        }
    }

    /// <summary>
    /// 同一 Run/Phase/Slot 只允许一个 owner 同时发起所有活动液压建压；
    /// 全部资格完成后只生成一个电机锚点。失败槽会先释放已成功建压的健康组，
    /// 健康通道跳过当前半槽并等待下一个完整槽，避免扩大故障范围。
    /// </summary>
    internal sealed class GlobalHydraulicSlotCoordinator
    {
        private sealed class SlotEntry
        {
            public string ParticipantSignature { get; set; }
            public Lazy<Task<GlobalHydraulicSlotResult>> Operation { get; set; }
        }

        private readonly ConcurrentDictionary<GlobalHydraulicSlotKey, SlotEntry> _slots = new();

        public Task<GlobalHydraulicSlotResult> EnterAsync(
            GlobalHydraulicSlotKey key,
            IReadOnlyDictionary<int, IReadOnlyList<int>> participantsByHydraulic,
            DateTime pressureBuildPlannedUtc,
            DateTime wallClockAnchorUtc,
            int periodMs,
            int maxPhaseMs,
            int motorGuardMs,
            Func<int, IReadOnlyList<int>, CancellationToken, Task<HydraulicCycleLease>> enterGroupAsync,
            Func<IReadOnlyList<int>, string, Task> releaseSuccessfulGroupsAsync,
            CancellationToken token)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (participantsByHydraulic == null) throw new ArgumentNullException(nameof(participantsByHydraulic));
            if (enterGroupAsync == null) throw new ArgumentNullException(nameof(enterGroupAsync));
            if (releaseSuccessfulGroupsAsync == null)
                throw new ArgumentNullException(nameof(releaseSuccessfulGroupsAsync));
            if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
            if (maxPhaseMs < 0) throw new ArgumentOutOfRangeException(nameof(maxPhaseMs));

            var snapshot = participantsByHydraulic
                .Where(pair => pair.Value != null && pair.Value.Count > 0)
                .OrderBy(pair => pair.Key)
                .ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<int>)pair.Value.Distinct().OrderBy(channel => channel).ToArray());
            if (snapshot.Count == 0)
                throw new InvalidOperationException($"GlobalHydraulicSlotEmpty Key={key}");

            var signature = string.Join(
                "|",
                snapshot.Select(pair => $"{pair.Key}:{string.Join(",", pair.Value)}"));
            var candidate = new SlotEntry
            {
                ParticipantSignature = signature,
                Operation = new Lazy<Task<GlobalHydraulicSlotResult>>(
                    () => BuildSlotAsync(
                        key,
                        snapshot,
                        NormalizeUtc(pressureBuildPlannedUtc),
                        NormalizeUtc(wallClockAnchorUtc),
                        periodMs,
                        maxPhaseMs,
                        Math.Max(2, motorGuardMs),
                        enterGroupAsync,
                        releaseSuccessfulGroupsAsync,
                        token),
                    LazyThreadSafetyMode.ExecutionAndPublication)
            };

            var entry = _slots.GetOrAdd(key, candidate);
            if (!string.Equals(entry.ParticipantSignature, signature, StringComparison.Ordinal))
                return Task.FromException<GlobalHydraulicSlotResult>(
                    new InvalidOperationException(
                        $"GlobalHydraulicSlotMembersImmutable Key={key} " +
                        $"Existing={entry.ParticipantSignature} Requested={signature}"));

            TrimCompletedSlots(key);
            return entry.Operation.Value;
        }

        public void ClearRun(Guid runId)
        {
            if (runId == Guid.Empty) return;
            foreach (var pair in _slots.Where(pair => pair.Key.RunId == runId).ToArray())
                _slots.TryRemove(pair.Key, out _);
        }

        private static async Task<GlobalHydraulicSlotResult> BuildSlotAsync(
            GlobalHydraulicSlotKey key,
            IReadOnlyDictionary<int, IReadOnlyList<int>> participants,
            DateTime pressureBuildPlannedUtc,
            DateTime wallClockAnchorUtc,
            int periodMs,
            int maxPhaseMs,
            int motorGuardMs,
            Func<int, IReadOnlyList<int>, CancellationToken, Task<HydraulicCycleLease>> enterGroupAsync,
            Func<IReadOnlyList<int>, string, Task> releaseSuccessfulGroupsAsync,
            CancellationToken token)
        {
            var delay = pressureBuildPlannedUtc - DateTime.UtcNow;
            if (delay.TotalMilliseconds > 1)
                await Task.Delay(delay, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = participants.Select(pair => Task.Run(async () =>
            {
                await startGate.Task.ConfigureAwait(false);
                var outcome = new GlobalHydraulicGroupOutcome
                {
                    HydraulicId = pair.Key,
                    Participants = pair.Value.ToArray(),
                    DispatchUtc = DateTime.UtcNow
                };
                try
                {
                    outcome.Lease = await enterGroupAsync(pair.Key, pair.Value, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // 整批停止/暂停所有者取消必须保持取消语义，不能伪装成某一
                    // 液压组硬件失败并触发通道隔离。
                    throw;
                }
                catch (Exception ex)
                {
                    outcome.Error = ex;
                }
                return outcome;
            })).ToArray();

            startGate.TrySetResult(true);
            var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (outcomes.Any(item => !item.IsSuccess))
            {
                var successfulMembers = outcomes
                    .Where(item => item.IsSuccess)
                    .SelectMany(item => item.Participants)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                if (successfulMembers.Length > 0)
                    await releaseSuccessfulGroupsAsync(
                            successfulMembers,
                            $"GlobalSlotDegraded:{key}")
                        .ConfigureAwait(false);

                return new GlobalHydraulicSlotResult(
                    key,
                    pressureBuildPlannedUtc,
                    null,
                    null,
                    "DegradedGroupIsolated",
                    outcomes);
            }

            var lastQualifiedUtc = outcomes
                .Select(item => item.QualifiedUtc ?? DateTime.UtcNow)
                .Max();
            var rawAnchorUtc = new[] { DateTime.UtcNow, lastQualifiedUtc }
                .Max()
                .AddMilliseconds(motorGuardMs);
            var window = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                rawAnchorUtc,
                wallClockAnchorUtc,
                periodMs,
                maxPhaseMs);
            return new GlobalHydraulicSlotResult(
                key,
                pressureBuildPlannedUtc,
                window.ActuationAnchorUtc,
                window.DeadlineUtc,
                "Aligned",
                outcomes);
        }

        private void TrimCompletedSlots(GlobalHydraulicSlotKey current)
        {
            foreach (var pair in _slots.Where(pair =>
                         pair.Key.RunId == current.RunId &&
                         pair.Key.PhaseKind == current.PhaseKind &&
                         pair.Key.Slot < current.Slot - 4).ToArray())
            {
                if (!pair.Value.Operation.IsValueCreated || pair.Value.Operation.Value.IsCompleted)
                    _slots.TryRemove(pair.Key, out _);
            }
        }

        private static DateTime NormalizeUtc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}
