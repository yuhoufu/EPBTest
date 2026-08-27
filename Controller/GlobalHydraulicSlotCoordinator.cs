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
        public bool ShouldDeferHealthyGroups => HasFailures &&
            !string.Equals(
                AlignmentState,
                "DegradedHealthyGroupsContinue",
                StringComparison.Ordinal);
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

    internal sealed class GlobalHydraulicSlotAdmissionResult
    {
        internal GlobalHydraulicSlotAdmissionResult(
            GlobalHydraulicSlotResult slot,
            bool admitted,
            string skipReason)
        {
            Slot = slot;
            Admitted = admitted;
            SkipReason = skipReason ?? string.Empty;
        }

        public GlobalHydraulicSlotResult Slot { get; }
        public bool Admitted { get; }
        public string SkipReason { get; }
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

        private sealed class FormalAdmissionEntry
        {
            public readonly object Gate = new object();
            public readonly Dictionary<int, HashSet<int>> Arrivals =
                new Dictionary<int, HashSet<int>>();
            public readonly Dictionary<int, string> Skipped =
                new Dictionary<int, string>();
            public string ConfigurationSignature = string.Empty;
            public bool Frozen;
            public IReadOnlyDictionary<int, IReadOnlyList<int>> FrozenParticipants;
            public Task<GlobalHydraulicSlotResult> Operation;
        }

        private readonly ConcurrentDictionary<GlobalHydraulicSlotKey, SlotEntry> _slots = new();
        private readonly ConcurrentDictionary<GlobalHydraulicSlotKey, FormalAdmissionEntry>
            _formalAdmissions = new();

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
                        releaseSuccessfulOnFailure: true,
                        token: token),
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

        public async Task<GlobalHydraulicSlotAdmissionResult> JoinFormalAsync(
            GlobalHydraulicSlotKey key,
            int hydraulicId,
            int channel,
            int admissionWindowMs,
            DateTime pressureBuildPlannedUtc,
            DateTime wallClockAnchorUtc,
            int periodMs,
            int maxPhaseMs,
            int motorGuardMs,
            Func<int, IReadOnlyList<int>, CancellationToken, Task<HydraulicCycleLease>> enterGroupAsync,
            Func<IReadOnlyList<int>, string, Task> releaseSuccessfulGroupsAsync,
            CancellationToken operationToken,
            CancellationToken waitToken)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.PhaseKind != HydraulicPhaseKind.Formal)
                throw new InvalidOperationException($"FormalAdmissionRequiresFormalKey Key={key}");
            if (hydraulicId <= 0) throw new ArgumentOutOfRangeException(nameof(hydraulicId));
            if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel));
            if (admissionWindowMs < 1) throw new ArgumentOutOfRangeException(nameof(admissionWindowMs));
            if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
            if (enterGroupAsync == null) throw new ArgumentNullException(nameof(enterGroupAsync));
            if (releaseSuccessfulGroupsAsync == null)
                throw new ArgumentNullException(nameof(releaseSuccessfulGroupsAsync));

            var normalizedPressureUtc = NormalizeUtc(pressureBuildPlannedUtc);
            var normalizedWallClockUtc = NormalizeUtc(wallClockAnchorUtc);
            var signature = string.Join(
                ":",
                normalizedPressureUtc.Ticks,
                normalizedWallClockUtc.Ticks,
                periodMs,
                maxPhaseMs,
                Math.Max(2, motorGuardMs),
                admissionWindowMs);
            var entry = _formalAdmissions.GetOrAdd(key, _ => new FormalAdmissionEntry());
            Task<GlobalHydraulicSlotResult> operation;
            bool admitted;
            string skipReason;
            lock (entry.Gate)
            {
                if (entry.ConfigurationSignature.Length == 0)
                    entry.ConfigurationSignature = signature;
                else if (!string.Equals(
                             entry.ConfigurationSignature,
                             signature,
                             StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"GlobalFormalSlotConfigurationImmutable Key={key} " +
                        $"Existing={entry.ConfigurationSignature} Requested={signature}");

                if (entry.Frozen)
                {
                    admitted = entry.FrozenParticipants != null &&
                               entry.FrozenParticipants.TryGetValue(hydraulicId, out var members) &&
                               members.Contains(channel);
                    skipReason = admitted
                        ? string.Empty
                        : entry.Skipped.TryGetValue(channel, out var frozenReason)
                            ? frozenReason
                            : "AdmissionWindowClosed";
                }
                else if (entry.Skipped.TryGetValue(channel, out var reason))
                {
                    admitted = false;
                    skipReason = reason;
                }
                else
                {
                    if (!entry.Arrivals.TryGetValue(hydraulicId, out var arrivals))
                    {
                        arrivals = new HashSet<int>();
                        entry.Arrivals[hydraulicId] = arrivals;
                    }
                    arrivals.Add(channel);
                    admitted = true;
                    skipReason = string.Empty;
                }

                if (entry.Operation == null && entry.Arrivals.Values.Any(set => set.Count > 0))
                    entry.Operation = CloseAndBuildFormalSlotAsync(
                        key,
                        entry,
                        admissionWindowMs,
                        normalizedPressureUtc,
                        normalizedWallClockUtc,
                        periodMs,
                        maxPhaseMs,
                        Math.Max(2, motorGuardMs),
                        enterGroupAsync,
                        releaseSuccessfulGroupsAsync,
                        operationToken);
                operation = entry.Operation;
            }

            if (!admitted && operation == null)
                return new GlobalHydraulicSlotAdmissionResult(null, false, skipReason);
            if (operation == null)
                throw new InvalidOperationException($"GlobalFormalSlotMissingOperation Key={key}");

            var completed = await Task.WhenAny(
                    operation,
                    Task.Delay(Timeout.Infinite, waitToken))
                .ConfigureAwait(false);
            if (completed != operation)
            {
                waitToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(waitToken);
            }
            var result = await operation.ConfigureAwait(false);
            lock (entry.Gate)
            {
                admitted = entry.FrozenParticipants != null &&
                           entry.FrozenParticipants.TryGetValue(hydraulicId, out var members) &&
                           members.Contains(channel) &&
                           !entry.Skipped.ContainsKey(channel);
                if (!admitted && string.IsNullOrWhiteSpace(skipReason))
                    skipReason = entry.Skipped.TryGetValue(channel, out var finalReason)
                        ? finalReason
                        : "AdmissionWindowClosed";
            }
            TrimCompletedFormalSlots(key);
            return new GlobalHydraulicSlotAdmissionResult(result, admitted, skipReason);
        }

        public bool SkipFormalSlot(GlobalHydraulicSlotKey key, int channel, string reason)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.PhaseKind != HydraulicPhaseKind.Formal)
                throw new InvalidOperationException($"FormalSkipRequiresFormalKey Key={key}");
            if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel));
            var entry = _formalAdmissions.GetOrAdd(key, _ => new FormalAdmissionEntry());
            lock (entry.Gate)
            {
                entry.Skipped[channel] = string.IsNullOrWhiteSpace(reason)
                    ? "SkippedForSlot"
                    : reason.Trim();
                if (entry.Frozen) return false;
                foreach (var arrivals in entry.Arrivals.Values)
                    arrivals.Remove(channel);
                return true;
            }
        }

        public void ClearRun(Guid runId)
        {
            if (runId == Guid.Empty) return;
            foreach (var pair in _slots.Where(pair => pair.Key.RunId == runId).ToArray())
                _slots.TryRemove(pair.Key, out _);
            foreach (var pair in _formalAdmissions.Where(pair => pair.Key.RunId == runId).ToArray())
                _formalAdmissions.TryRemove(pair.Key, out _);
        }

        private static async Task<GlobalHydraulicSlotResult> CloseAndBuildFormalSlotAsync(
            GlobalHydraulicSlotKey key,
            FormalAdmissionEntry entry,
            int admissionWindowMs,
            DateTime pressureBuildPlannedUtc,
            DateTime wallClockAnchorUtc,
            int periodMs,
            int maxPhaseMs,
            int motorGuardMs,
            Func<int, IReadOnlyList<int>, CancellationToken, Task<HydraulicCycleLease>> enterGroupAsync,
            Func<IReadOnlyList<int>, string, Task> releaseSuccessfulGroupsAsync,
            CancellationToken token)
        {
            await Task.Delay(admissionWindowMs, token).ConfigureAwait(false);
            IReadOnlyDictionary<int, IReadOnlyList<int>> participants;
            lock (entry.Gate)
            {
                entry.Frozen = true;
                participants = entry.Arrivals
                    .Where(pair => pair.Value.Count > 0)
                    .OrderBy(pair => pair.Key)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<int>)pair.Value
                            .Where(channel => !entry.Skipped.ContainsKey(channel))
                            .OrderBy(channel => channel)
                            .ToArray());
                participants = participants
                    .Where(pair => pair.Value.Count > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                entry.FrozenParticipants = participants;
            }
            if (participants.Count == 0)
                throw new InvalidOperationException($"GlobalFormalSlotEmptyAfterAdmission Key={key}");
            return await BuildSlotAsync(
                    key,
                    participants,
                    pressureBuildPlannedUtc,
                    wallClockAnchorUtc,
                    periodMs,
                    maxPhaseMs,
                    motorGuardMs,
                    enterGroupAsync,
                    releaseSuccessfulGroupsAsync,
                    releaseSuccessfulOnFailure: false,
                    token: token)
                .ConfigureAwait(false);
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
            bool releaseSuccessfulOnFailure,
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
            var hasFailures = outcomes.Any(item => !item.IsSuccess);
            if (hasFailures && releaseSuccessfulOnFailure)
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

            var successfulOutcomes = outcomes.Where(item => item.IsSuccess).ToArray();
            if (successfulOutcomes.Length == 0)
                return new GlobalHydraulicSlotResult(
                    key,
                    pressureBuildPlannedUtc,
                    null,
                    null,
                    "AllGroupsFailed",
                    outcomes);

            var lastQualifiedUtc = successfulOutcomes
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
                hasFailures ? "DegradedHealthyGroupsContinue" : "Aligned",
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

        private void TrimCompletedFormalSlots(GlobalHydraulicSlotKey current)
        {
            foreach (var pair in _formalAdmissions.Where(pair =>
                         pair.Key.RunId == current.RunId &&
                         pair.Key.PhaseKind == HydraulicPhaseKind.Formal &&
                         pair.Key.Slot < current.Slot - 4).ToArray())
            {
                Task<GlobalHydraulicSlotResult> operation;
                lock (pair.Value.Gate) operation = pair.Value.Operation;
                if (operation == null || operation.IsCompleted)
                    _formalAdmissions.TryRemove(pair.Key, out _);
            }
        }

        private static DateTime NormalizeUtc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}
