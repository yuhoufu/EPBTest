// ====================== HydraulicGroupCoordinator.cs（内联到 EpbManager.cs）======================
// 目的：统一管理“组内首个电控前建压；最后一个电压释放点后释放液压”的跨卡钳节拍。
// 依赖：TestConfig.Hydraulics[*].Members 列出该液压控制的所有卡钳通道号；
//       若 Members 为空，则回退到 DOConfig 中每个 EPB Record 的可选 HydraulicId 关联。
// 线程安全：内部用 ConcurrentDictionary + 锁；可同时被多路 EpbCycleRunner 调用。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    public enum FaultScope
    {
        Channel,
        ElectricalGroup,
        HydraulicGroup,
        DaqGroup,
        Global
    }

    public enum FaultRecoveryPolicy
    {
        Recoverable = 0,
        NonRecoverable = 1,
        NonRecoverableDisableChannel = 2,
        UnattendedBatchRecycle = 3
    }

    public sealed class ControlFault
    {
        public ControlFault(string code, string reason, FaultScope scope, int[] affectedChannels,
            int? groupId, DateTime timestampUtc, Guid correlationId,
            FaultClassification classification = FaultClassification.HardwareConfirmed,
            FaultRecoveryPolicy recoveryPolicy = FaultRecoveryPolicy.NonRecoverable,
            Guid runId = default)
        {
            Code = code ?? string.Empty;
            Reason = reason ?? string.Empty;
            Scope = scope;
            AffectedChannels = affectedChannels ?? Array.Empty<int>();
            GroupId = groupId;
            TimestampUtc = timestampUtc;
            CorrelationId = correlationId;
            RunId = runId;
            Classification = classification;
            RecoveryPolicy = recoveryPolicy;
        }
        public string Code { get; }
        public string Reason { get; }
        public FaultScope Scope { get; }
        public int[] AffectedChannels { get; }
        public int? GroupId { get; }
        public DateTime TimestampUtc { get; }
        public Guid CorrelationId { get; }
        /// <summary>
        /// 触发本故障的正式运行身份。它与用于日志/诊断去重的 CorrelationId 独立；
        /// 无人值守恢复只能用此字段校验检查点授权。
        /// </summary>
        public Guid RunId { get; }
        public FaultClassification Classification { get; }
        public FaultRecoveryPolicy RecoveryPolicy { get; }
        public bool DisableChannelOnLatch =>
            RecoveryPolicy == FaultRecoveryPolicy.NonRecoverableDisableChannel;
    }

    public enum HydraulicPhaseKind
    {
        PreRelease,
        Learning,
        Formal,
        SingleChannel,
        Recovery,
        Qualification
    }

    public sealed class HydraulicGenerationKey : IEquatable<HydraulicGenerationKey>
    {
        public HydraulicGenerationKey(Guid testRunId, int hydraulicId, HydraulicPhaseKind phaseKind, long slot)
        {
            TestRunId = testRunId;
            HydraulicId = hydraulicId;
            PhaseKind = phaseKind;
            Slot = slot;
        }
        public Guid TestRunId { get; }
        public int HydraulicId { get; }
        public HydraulicPhaseKind PhaseKind { get; }
        public long Slot { get; }
        public bool Equals(HydraulicGenerationKey other) => other != null && TestRunId == other.TestRunId &&
            HydraulicId == other.HydraulicId && PhaseKind == other.PhaseKind && Slot == other.Slot;
        public override bool Equals(object obj) => Equals(obj as HydraulicGenerationKey);
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = TestRunId.GetHashCode();
                hash = (hash * 397) ^ HydraulicId;
                hash = (hash * 397) ^ (int)PhaseKind;
                hash = (hash * 397) ^ Slot.GetHashCode();
                return hash;
            }
        }
        public override string ToString() => $"{TestRunId:N}/{HydraulicId}/{PhaseKind}/{Slot}";
    }

    public sealed class HydraulicCycleLease
    {
        internal HydraulicCycleLease(
            HydraulicGenerationKey key,
            IReadOnlyList<int> members,
            PressureQualification qualification,
            DateTime actuationAnchorUtc,
            Task completion,
            long coordinatorEpoch)
        {
            Key = key;
            Members = members;
            Qualification = qualification;
            ActuationAnchorUtc = actuationAnchorUtc;
            Completion = completion ?? Task.CompletedTask;
            CoordinatorEpoch = coordinatorEpoch;
        }

        public HydraulicGenerationKey Key { get; }
        public IReadOnlyList<int> Members { get; }
        public PressureQualification Qualification { get; }
        /// <summary>
        /// 同一液压代次内所有电机相位共同使用的未来锚点。该值只在资格完成时生成一次，
        /// 防止多个等待者分别以自己的恢复时刻计算相位而重新聚拢。
        /// </summary>
        public DateTime ActuationAnchorUtc { get; }
        public long CoordinatorEpoch { get; }
        internal Task Completion { get; }
    }

    public sealed class HydraulicGenerationSnapshot
    {
        public int HydraulicId { get; set; }
        public long CoordinatorEpoch { get; set; }
        public bool AdmissionOpen { get; set; }
        public int ActiveGenerationCount { get; set; }
        public int ActiveOperationCount { get; set; }
        public int ActiveLeaseCount { get; set; }
        public bool GenerationGateAvailable { get; set; }
        public int RebuildCount { get; set; }
        public string[] ActiveGenerationKeys { get; set; } = Array.Empty<string>();
        public int[] PendingMembers { get; set; } = Array.Empty<int>();
        public bool IsHealthyForFreshStart => AdmissionOpen &&
                                              ActiveGenerationCount == 0 &&
                                              ActiveOperationCount == 0 &&
                                              ActiveLeaseCount == 0 &&
                                              GenerationGateAvailable;

        public override string ToString()
        {
            return $"Hydraulic={HydraulicId} Epoch={CoordinatorEpoch} " +
                   $"Admission={(AdmissionOpen ? "Open" : "Closed")} " +
                   $"Generations={ActiveGenerationCount} Operations={ActiveOperationCount} " +
                   $"Leases={ActiveLeaseCount} " +
                   $"Gate={(GenerationGateAvailable ? "Available" : "Busy")} " +
                   $"Pending=[{string.Join(",", PendingMembers ?? Array.Empty<int>())}] " +
                   $"Rebuilds={RebuildCount}";
        }
    }

    public sealed class HydraulicChannelLeaseScope
    {
        private readonly HydraulicGroupCoordinator _owner;
        private readonly object _gate = new object();
        private Task _closeTask;
        private int _closeKind;

        internal HydraulicChannelLeaseScope(
            HydraulicGroupCoordinator owner,
            HydraulicCycleLease lease,
            int channel)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Lease = lease ?? throw new ArgumentNullException(nameof(lease));
            Channel = channel;
        }

        public HydraulicCycleLease Lease { get; }
        public int Channel { get; }
        public HydraulicGenerationKey Key => Lease.Key;
        public long CoordinatorEpoch => Lease.CoordinatorEpoch;
        public bool IsClosed => Volatile.Read(ref _closeKind) != 0;
        public Task Completion
        {
            get
            {
                lock (_gate) return _closeTask ?? Task.CompletedTask;
            }
        }

        public Task CompleteAsync()
        {
            return CloseAsync(aborted: false, reason: null);
        }

        public Task AbortAsync(string reason)
        {
            return CloseAsync(aborted: true, reason: reason);
        }

        private Task CloseAsync(bool aborted, string reason)
        {
            lock (_gate)
            {
                if (_closeTask != null) return _closeTask;
                _closeKind = aborted ? 2 : 1;
                _closeTask = _owner.CloseChannelScopeAsync(this, aborted, reason);
                return _closeTask;
            }
        }
    }

    internal sealed class HydraulicCoordinatorRebuildingException : InvalidOperationException
    {
        public HydraulicCoordinatorRebuildingException(int hydraulicId)
            : base($"HydraulicCoordinatorRebuilding Hydraulic={hydraulicId}") { }
    }

    internal sealed class HydraulicReleaseTimeoutException : TimeoutException
    {
        public HydraulicReleaseTimeoutException(
            int hydraulicId,
            double lastPressureBar,
            double safePressureBar,
            int timeoutMs,
            string detail = null)
            : base(
                $"HydraulicReleaseTimeout Hydraulic={hydraulicId} " +
                $"LastPressure={lastPressureBar:F3}bar SafePressure={safePressureBar:F3}bar " +
                $"Timeout={timeoutMs}ms" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Detail={detail}"))
        {
            HydraulicId = hydraulicId;
            LastPressureBar = lastPressureBar;
            SafePressureBar = safePressureBar;
            TimeoutMs = timeoutMs;
            Detail = detail ?? string.Empty;
        }

        public int HydraulicId { get; }
        public double LastPressureBar { get; }
        public double SafePressureBar { get; }
        public int TimeoutMs { get; }
        public string Detail { get; }

        public bool IsPressureEvidenceUnavailable =>
            Detail.StartsWith("PressureReaderUnavailable", StringComparison.OrdinalIgnoreCase) ||
            Detail.StartsWith("PressureReadFailed", StringComparison.OrdinalIgnoreCase) ||
            Detail.StartsWith("PressureSampleNoSample", StringComparison.OrdinalIgnoreCase) ||
            Detail.StartsWith("PressureSampleInvalidValue", StringComparison.OrdinalIgnoreCase) ||
            Detail.StartsWith("PressureSampleStaleSample", StringComparison.OrdinalIgnoreCase);
    }

    public enum HydraulicPressureFailureReason
    {
        NoSample,
        InvalidValue,
        StaleSample,
        BelowMinimum
    }

    public sealed class HydraulicPressureLostException : InvalidOperationException
    {
        public HydraulicPressureLostException(
            int hydraulicId,
            long generationId,
            double actualBar,
            double minimumBar,
            HydraulicPressureFailureReason failureReason,
            double sampleAgeMs,
            int confirmationStreak = 0,
            int confirmationThreshold = 3)
            : base($"HydraulicPressureLost Hydraulic={hydraulicId} Generation={generationId} " +
                   $"Reason={failureReason} Actual={actualBar:F3}bar Minimum={minimumBar:F3}bar " +
                   $"AgeMs={sampleAgeMs:F1}" +
                   (confirmationStreak > 0
                       ? $" Confirmation={confirmationStreak}/{Math.Max(3, confirmationThreshold)}"
                       : string.Empty))
        {
            FailureReason = failureReason;
            SampleAgeMs = sampleAgeMs;
            ConfirmationStreak = Math.Max(0, confirmationStreak);
            ConfirmationThreshold = Math.Max(3, confirmationThreshold);
        }

        public HydraulicPressureFailureReason FailureReason { get; }
        public double SampleAgeMs { get; }
        public int ConfirmationStreak { get; }
        public int ConfirmationThreshold { get; }
        public bool IsConfirmed => ConfirmationStreak >= ConfirmationThreshold;
    }

    public sealed class HydraulicBarrierTimeoutException : TimeoutException
    {
        public HydraulicBarrierTimeoutException(
            HydraulicGenerationKey key,
            int timeoutMs,
            IEnumerable<int> pendingMembers)
            : base($"HydraulicBarrierTimeout Hydraulic={key.HydraulicId} Run={key.TestRunId:N} " +
                   $"Phase={key.PhaseKind} Slot={key.Slot} Timeout={timeoutMs}ms " +
                   $"Pending=[{string.Join(",", pendingMembers ?? Array.Empty<int>())}]") { }
    }

    internal sealed class HydraulicGroupCoordinator
    {
        private const int PressureFaultConfirmGenerations = 3;
        private readonly AoController _ao;

        // channel -> hydId（优先 TestConfig.Hydraulics[*].Members；其次 DOConfig.EPB[*].HydraulicId）
        private readonly Dictionary<int, int> _channel2Hyd = new();
        private readonly DoController _do;
        private readonly DoConfig _doCfg;

        // 可选：引用已有液压控制器。若存在则优先用它的“保持-外部释放”能力。
        // 注意：我们改成使用你现有的 HydraulicController.BuildAndHoldAsync / Release / TryGetReleaseDelegate
        private readonly HydraulicController _hydCtl;

        private readonly ConcurrentDictionary<int, Latch> _latches = new();
        private readonly IAppLogger _log;
        private readonly TaskSupervisor _tasks;
        private readonly Func<int, double> _readPressure;
        private readonly Func<int, PressureSample> _readPressureSample;
        private readonly Func<int, Task> _testReleaseAction;
        private readonly TestConfig _test;
        private readonly ConcurrentDictionary<HydraulicGenerationKey, GenerationState> _generations = new();
        private readonly ConcurrentDictionary<int, int> _buildFailureStreaks = new();
        private readonly ConcurrentDictionary<int, int> _holdFailureStreaks = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _generationGates = new();
        private readonly ConcurrentDictionary<int, long> _coordinatorEpochs = new();
        private readonly ConcurrentDictionary<int, int> _groupRebuildFlags = new();
        private readonly ConcurrentDictionary<int, int> _activeOperationsByGroup = new();
        private readonly ConcurrentDictionary<int, int> _groupRebuildCounts = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _groupRebuildGates = new();
        private readonly ConcurrentDictionary<HydraulicChannelLeaseScope, byte> _activeLeaseScopes = new();

        public event Action<ControlFault> FaultRaised;

        internal Task<bool> DrainBackgroundTasksAsync(int timeoutMs)
        {
            return _tasks.DrainAsync(timeoutMs);
        }

        public HydraulicGroupCoordinator(TestConfig test,
            DoConfig dO,
            DoController doController,
            Func<int, double> readPressure,
            AoController aoController,
            HydraulicController hydCtl,
            IAppLogger log)
        {
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _doCfg = dO ?? new DoConfig();
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _ao = aoController; // 可空：无 AO 时仍可只用 HydraulicController
            _readPressure = readPressure; // 可空：若仅用 HydraulicController，可不需要读压
            _readPressureSample = readPressure == null
                ? null
                : id => new PressureSample(id, readPressure(id), DateTime.UtcNow, Stopwatch.GetTimestamp());
            _hydCtl = hydCtl; // 可空：无专用控制器则走 Fallback
            _log = log ?? NullLogger.Instance;
            _tasks = new TaskSupervisor(_log);

            // 1) 先用 TestConfig.Hydraulics[*].Members 做映射
            foreach (var h in _test.Hydraulics)
            {
                if (h?.Enabled != true) continue;
                if (h.Members != null && h.Members.Count > 0)
                    foreach (var ch in h.Members.Distinct())
                        _channel2Hyd[ch] = h.Id;
            }

            // 2) 对未覆盖的通道，用 DOConfig.EPB 的 HydraulicId 兜底
            // 这段代码暂时无用，doconfig中暂时没有hydraulicid字段
            if (_doCfg?.Epb != null)
                foreach (var r in _doCfg.Epb)
                    if (r.Enabled && r.Channel > 0 && r.HydraulicId.HasValue && !_channel2Hyd.ContainsKey(r.Channel))
                        _channel2Hyd[r.Channel] = r.HydraulicId.Value;
        }

        public HydraulicGroupCoordinator(TestConfig test,
            DoConfig dO,
            DoController doController,
            Func<int, PressureSample> readPressureSample,
            AoController aoController,
            HydraulicController hydCtl,
            IAppLogger log)
            : this(
                test,
                dO,
                doController,
                id => readPressureSample(id).ValueBar,
                aoController,
                hydCtl,
                log)
        {
            _readPressureSample = readPressureSample ?? throw new ArgumentNullException(nameof(readPressureSample));
        }

        internal HydraulicGroupCoordinator(
            TestConfig test,
            Func<int, double> readPressure,
            Func<int, Task> releaseAction,
            IAppLogger log = null)
        {
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _doCfg = new DoConfig();
            _readPressure = readPressure;
            _readPressureSample = readPressure == null
                ? null
                : id => new PressureSample(id, readPressure(id), DateTime.UtcNow, Stopwatch.GetTimestamp());
            _testReleaseAction = releaseAction ?? throw new ArgumentNullException(nameof(releaseAction));
            _log = log ?? NullLogger.Instance;
            _tasks = new TaskSupervisor(_log);

            foreach (var h in _test.Hydraulics)
            {
                if (h?.Enabled != true || h.Members == null) continue;
                foreach (var ch in h.Members.Distinct())
                    _channel2Hyd[ch] = h.Id;
            }
        }

        internal HydraulicGroupCoordinator(
            TestConfig test,
            Func<int, PressureSample> readPressureSample,
            Func<int, Task> releaseAction,
            IAppLogger log = null)
            : this(test, sampleReader: readPressureSample, releaseAction, hydCtl: null, log)
        {
        }

        private HydraulicGroupCoordinator(
            TestConfig test,
            Func<int, PressureSample> sampleReader,
            Func<int, Task> releaseAction,
            HydraulicController hydCtl,
            IAppLogger log)
        {
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _doCfg = new DoConfig();
            _readPressureSample = sampleReader ?? throw new ArgumentNullException(nameof(sampleReader));
            _readPressure = id => sampleReader(id).ValueBar;
            _testReleaseAction = releaseAction;
            _hydCtl = hydCtl;
            _log = log ?? NullLogger.Instance;
            _tasks = new TaskSupervisor(_log);
            foreach (var h in _test.Hydraulics)
            {
                if (h?.Enabled != true || h.Members == null) continue;
                foreach (var ch in h.Members.Distinct()) _channel2Hyd[ch] = h.Id;
            }
        }

        /// <summary>
        /// 以压力组代次为单位冻结成员并完成实际压力资格。相同Key重复调用只返回同一任务，
        /// 不会把已经释放的成员重新加入。
        /// </summary>
        public async Task<HydraulicCycleLease> EnterGenerationAsync(
            HydraulicGenerationKey key,
            IReadOnlyCollection<int> participants,
            CancellationToken token)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (_groupRebuildFlags.ContainsKey(key.HydraulicId))
                throw new HydraulicCoordinatorRebuildingException(key.HydraulicId);
            IncrementActiveOperation(key.HydraulicId);
            try
            {
            var members = (participants ?? Array.Empty<int>())
                .Where(ch => _channel2Hyd.TryGetValue(ch, out var h) && h == key.HydraulicId)
                .Distinct()
                .OrderBy(ch => ch)
                .ToArray();
            if (members.Length == 0)
                throw new InvalidOperationException($"Hydraulic={key.HydraulicId} 代次没有有效参与成员。");

            var epoch = _coordinatorEpochs.GetOrAdd(key.HydraulicId, 1);
            var state = _generations.GetOrAdd(key, _ => new GenerationState(key, members, epoch));
            if (!state.Members.SequenceEqual(members))
                throw new InvalidOperationException(
                    $"Hydraulic generation members are immutable. Key={key} " +
                    $"Existing=[{string.Join(",", state.Members)}] Requested=[{string.Join(",", members)}]");

            Task<HydraulicCycleLease> initialize;
            lock (state.Gate)
            {
                initialize = state.InitializeTask;
                if (initialize == null)
                {
                    initialize = InitializeGenerationAsync(state, token);
                    state.InitializeTask = initialize;
                }
            }

            return await initialize.ConfigureAwait(false);
            }
            finally
            {
                DecrementActiveOperation(key.HydraulicId);
            }
        }

        public async Task MarkVoltageReleaseAsync(HydraulicCycleLease lease, int epbChannel)
        {
            if (lease == null) return;
            IncrementActiveOperation(lease.Key.HydraulicId);
            try
            {
            if (!_generations.TryGetValue(lease.Key, out var state))
            {
                // 代次从活动表清理后，旧租约仍必须观察到它所属代次的
                // 终态。成功则幂等返回，失败则重新抛出原异常，不能因清理而吞掉故障。
                await lease.Completion.ConfigureAwait(false);
                return;
            }
            if (state.CoordinatorEpoch != lease.CoordinatorEpoch)
            {
                _log.Warn(
                    $"忽略迟到旧液压租约：EPB={epbChannel} Key={lease.Key} " +
                    $"LeaseEpoch={lease.CoordinatorEpoch} CurrentEpoch={state.CoordinatorEpoch}",
                    "液压协调");
                await lease.Completion.ConfigureAwait(false);
                return;
            }

            var releaseNow = false;
            lock (state.Gate)
            {
                if (!state.Remaining.Remove(epbChannel))
                {
                    _log.Warn(
                        $"液压释放重复或代次不匹配：EPB={epbChannel} Key={lease.Key}",
                        "液压协调");
                }
                if (state.Remaining.Count == 0 && !state.ReleaseStarted)
                {
                    state.ReleaseStarted = true;
                    state.BarrierReached.TrySetResult(true);
                    releaseNow = true;
                }
            }

            if (releaseNow)
                _tasks.Observe(
                    CompleteGenerationReleaseAsync(state),
                    "HydraulicGenerationRelease",
                    state.Key.TestRunId);

            var item = GetHydraulicItem(lease.Key.HydraulicId);
            var timeoutMs = item.BarrierTimeoutMs > 0
                ? item.BarrierTimeoutMs
                : Math.Max(1, _test.PeriodMs);
            var completed = await Task.WhenAny(
                    state.BarrierReached.Task,
                    Task.Delay(timeoutMs))
                .ConfigureAwait(false);
            if (completed != state.BarrierReached.Task)
            {
                // 定时器与最后一个成员可能在同一时刻竞争完成。先复查屏障状态，
                // 避免最后成员已经移除后仍构造 Pending=[] 的假超时。
                if (state.BarrierReached.Task.IsCompleted)
                {
                    await state.BarrierReached.Task.ConfigureAwait(false);
                    await state.Completion.Task.ConfigureAwait(false);
                    return;
                }

                int[] pending;
                lock (state.Gate) pending = state.Remaining.OrderBy(x => x).ToArray();
                if (pending.Length == 0)
                {
                    await state.BarrierReached.Task.ConfigureAwait(false);
                    await state.Completion.Task.ConfigureAwait(false);
                    return;
                }

                if (state.BarrierReached.Task.IsCompleted)
                {
                    await state.BarrierReached.Task.ConfigureAwait(false);
                    await state.Completion.Task.ConfigureAwait(false);
                    return;
                }

                var ex = new HydraulicBarrierTimeoutException(lease.Key, timeoutMs, pending);
                await FailGenerationAsync(state, ex).ConfigureAwait(false);
                throw ex;
            }

            // BarrierTimeout 只约束同代次成员到达释放点的等待时间。
            // 全员到齐后的实际释压由 ReleaseTimeoutMs 独立约束，不能把两段时间叠加后
            // 误报成 Pending=[] 的屏障超时。
            await state.BarrierReached.Task.ConfigureAwait(false);
            await state.Completion.Task.ConfigureAwait(false);
            }
            finally
            {
                DecrementActiveOperation(lease.Key.HydraulicId);
            }
        }

        public HydraulicChannelLeaseScope CreateChannelScope(
            HydraulicCycleLease lease,
            int epbChannel)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (!lease.Members.Contains(epbChannel))
                throw new InvalidOperationException(
                    $"EPB={epbChannel} 不属于液压代次 {lease.Key}。 ");
            var scope = new HydraulicChannelLeaseScope(this, lease, epbChannel);
            if (!_activeLeaseScopes.TryAdd(scope, 0))
                throw new InvalidOperationException(
                    $"HydraulicLeaseScopeRegisterFailed EPB={epbChannel} Key={lease.Key}");
            return scope;
        }

        internal async Task CloseChannelScopeAsync(
            HydraulicChannelLeaseScope scope,
            bool aborted,
            string reason)
        {
            if (scope == null) return;
            try
            {
                if (aborted)
                    await AbortGenerationMemberAsync(scope.Lease, scope.Channel, reason)
                        .ConfigureAwait(false);
                else
                    await MarkVoltageReleaseAsync(scope.Lease, scope.Channel)
                        .ConfigureAwait(false);
            }
            finally
            {
                _activeLeaseScopes.TryRemove(scope, out _);
            }
        }

        public Task AbortGenerationMemberAsync(
            HydraulicCycleLease lease,
            int epbChannel,
            string reason)
        {
            if (lease == null) return Task.CompletedTask;
            _log.Warn(
                $"液压代次成员作废：EPB={epbChannel} Key={lease.Key} " +
                $"Epoch={lease.CoordinatorEpoch} Reason={reason ?? "AttemptAborted"}",
                "液压协调");
            return MarkVoltageReleaseAsync(lease, epbChannel);
        }

        public async Task ForceReleaseAsync(int hydraulicId, string reason)
        {
            var active = _generations.Values
                .Where(x => x.Key.HydraulicId == hydraulicId && !x.Completion.Task.IsCompleted)
                .ToArray();
            foreach (var state in active)
                await FailGenerationAsync(
                        state,
                        new OperationCanceledException($"HydraulicForceRelease Reason={reason}"),
                        publishFault: false)
                    .ConfigureAwait(false);

            // 即使某代次已由另一故障线程进入 FailGeneration，也再次幂等回零，
            // 并以实际压力连续安全作为 StopAll/恢复流程的完成依据。
            await ExecuteReleaseOutputAsync(hydraulicId).ConfigureAwait(false);
            await WaitForSafePressureAsync(hydraulicId).ConfigureAwait(false);
        }

        public HydraulicGenerationSnapshot ProbeGroupHealth(int hydraulicId)
        {
            var states = _generations.Values
                .Where(state => state.Key.HydraulicId == hydraulicId &&
                                !state.Completion.Task.IsCompleted)
                .ToArray();
            var pending = new List<int>();
            foreach (var state in states)
            {
                lock (state.Gate) pending.AddRange(state.Remaining);
            }

            var admissionOpen = !_groupRebuildFlags.ContainsKey(hydraulicId);
            var leaseCount = _activeLeaseScopes.Keys.Count(
                scope => scope.Key.HydraulicId == hydraulicId);
            var gateAvailable = false;
            var gate = _generationGates.GetOrAdd(hydraulicId, _ => new SemaphoreSlim(1, 1));
            if (admissionOpen && states.Length == 0 && gate.Wait(0))
            {
                gateAvailable = true;
                gate.Release();
            }

            return new HydraulicGenerationSnapshot
            {
                HydraulicId = hydraulicId,
                CoordinatorEpoch = _coordinatorEpochs.GetOrAdd(hydraulicId, 1),
                AdmissionOpen = admissionOpen,
                ActiveGenerationCount = states.Length,
                ActiveOperationCount = GetActiveOperationCount(hydraulicId),
                ActiveLeaseCount = leaseCount,
                GenerationGateAvailable = gateAvailable,
                RebuildCount = _groupRebuildCounts.TryGetValue(hydraulicId, out var rebuilds)
                    ? rebuilds
                    : 0,
                ActiveGenerationKeys = states.Select(state => state.Key.ToString()).ToArray(),
                PendingMembers = pending.Distinct().OrderBy(channel => channel).ToArray()
            };
        }

        public async Task<HydraulicGenerationSnapshot> RebuildGroupAsync(
            int hydraulicId,
            string reason,
            int timeoutMs = 10000,
            CancellationToken token = default)
        {
            var rebuildGate = _groupRebuildGates.GetOrAdd(
                hydraulicId,
                _ => new SemaphoreSlim(1, 1));
            await rebuildGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!_groupRebuildFlags.TryAdd(hydraulicId, 0))
                    throw new HydraulicCoordinatorRebuildingException(hydraulicId);
                try
                {
                    try
                    {
                        await ForceReleaseAsync(
                                hydraulicId,
                                "CoordinatorRebuild:" + (reason ?? "Unknown"))
                            .ConfigureAwait(false);
                    }
                    catch (HydraulicReleaseTimeoutException ex)
                        when (ex.IsPressureEvidenceUnavailable)
                    {
                        // 故障 DAQ 不能同时成为“允许重建该 DAQ”的前置证据。
                        // DO/AO 已幂等回零；压力不可观测时按配置的机械稳定窗口有界等待，
                        // 重建完成后仍必须由新 generation 的新鲜样本通过后续启动资格。
                        var item = _test.Hydraulics.FirstOrDefault(h => h.Id == hydraulicId);
                        var settleMs = Math.Max(100, item?.ReleaseStableMs ?? 100);
                        _log.Warn(
                            $"液压组{hydraulicId}压力证据不可观测，已完成DO/AO去能量化；" +
                            $"等待{settleMs}ms后仅重建协调/DAQ逻辑，后续重新加入仍要求新鲜压力。" +
                            $" Detail={ex.Detail} Reason={reason}",
                            "液压协调");
                        await Task.Delay(settleMs, token).ConfigureAwait(false);
                    }

                    var oldScopes = _activeLeaseScopes.Keys
                        .Where(scope => scope.Key.HydraulicId == hydraulicId)
                        .ToArray();
                    if (oldScopes.Length > 0)
                    {
                        var closeTasks = oldScopes
                            .Select(scope => scope.AbortAsync("CoordinatorRebuild:" + reason))
                            .ToArray();
                        try { await Task.WhenAll(closeTasks).ConfigureAwait(false); }
                        catch
                        {
                            // ForceRelease 会以取消异常结束旧代次；作用域 finally 已完成注销。
                        }
                    }

                    var deadline = Stopwatch.StartNew();
                    while ((GetActiveOperationCount(hydraulicId) > 0 ||
                            _activeLeaseScopes.Keys.Any(scope => scope.Key.HydraulicId == hydraulicId)) &&
                           deadline.ElapsedMilliseconds < Math.Max(1, timeoutMs))
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }
                    var remainingLeaseCount = _activeLeaseScopes.Keys.Count(
                        scope => scope.Key.HydraulicId == hydraulicId);
                    if (GetActiveOperationCount(hydraulicId) > 0 || remainingLeaseCount > 0)
                        throw new TimeoutException(
                            $"HydraulicCoordinatorRebuildOperationsTimeout Hydraulic={hydraulicId} " +
                            $"Operations={GetActiveOperationCount(hydraulicId)} " +
                            $"Leases={remainingLeaseCount} Timeout={timeoutMs}ms");

                    foreach (var pair in _generations
                                 .Where(pair => pair.Key.HydraulicId == hydraulicId)
                                 .ToArray())
                        _generations.TryRemove(pair.Key, out _);

                    // 旧 Semaphore 可能已经发生“无活动代次但计数为0”的孤儿占用。
                    // 所有旧操作退出后直接替换，迟到旧 state 只持有旧实例，无法污染新 epoch。
                    _generationGates[hydraulicId] = new SemaphoreSlim(1, 1);
                    _coordinatorEpochs.AddOrUpdate(hydraulicId, 2, (_, epoch) => epoch + 1);
                    _groupRebuildCounts.AddOrUpdate(hydraulicId, 1, (_, count) => count + 1);
                    _buildFailureStreaks.TryRemove(hydraulicId, out _);
                    _holdFailureStreaks.TryRemove(hydraulicId, out _);
                    _log.Warn(
                        $"液压组{hydraulicId}协调状态已安全重建。" +
                        $"Epoch={_coordinatorEpochs[hydraulicId]} Reason={reason}",
                        "液压协调");
                }
                finally
                {
                    _groupRebuildFlags.TryRemove(hydraulicId, out _);
                }
            }
            finally
            {
                rebuildGate.Release();
            }

            var snapshot = ProbeGroupHealth(hydraulicId);
            if (!snapshot.IsHealthyForFreshStart)
                throw new InvalidOperationException(
                    "HydraulicCoordinatorRebuildVerificationFailed " + snapshot);
            return snapshot;
        }

        private void IncrementActiveOperation(int hydraulicId)
        {
            _activeOperationsByGroup.AddOrUpdate(hydraulicId, 1, (_, count) => count + 1);
        }

        private void DecrementActiveOperation(int hydraulicId)
        {
            _activeOperationsByGroup.AddOrUpdate(
                hydraulicId,
                0,
                (_, count) => Math.Max(0, count - 1));
        }

        private int GetActiveOperationCount(int hydraulicId)
        {
            return _activeOperationsByGroup.TryGetValue(hydraulicId, out var count)
                ? Math.Max(0, count)
                : 0;
        }

        private async Task<HydraulicCycleLease> InitializeGenerationAsync(
            GenerationState state,
            CancellationToken token)
        {
            var gate = _generationGates.GetOrAdd(state.Key.HydraulicId, _ => new SemaphoreSlim(1, 1));
            var item = GetHydraulicItem(state.Key.HydraulicId);
            var timeoutMs = item.BarrierTimeoutMs > 0 ? item.BarrierTimeoutMs : Math.Max(1, _test.PeriodMs);
            if (!await gate.WaitAsync(timeoutMs, token).ConfigureAwait(false))
                throw new HydraulicBarrierTimeoutException(state.Key, timeoutMs, state.Members);
            state.GenerationGate = gate;

            try
            {
                PressureQualification qualification;
                if (_hydCtl != null)
                {
                    qualification = await _hydCtl.BuildAndQualifyAsync(
                            state.Key.HydraulicId,
                            state.Key.Slot,
                            token)
                        .ConfigureAwait(false);
                }
                else
                {
                    // 测试构造路径由测试释放委托模拟硬件；仍返回明确资格对象。
                    var sample = _readPressureSample(state.Key.HydraulicId);
                    qualification = new PressureQualification(
                        state.Key.HydraulicId,
                        state.Key.Slot,
                        item.PressureThresholdBar,
                        sample.ValueBar,
                        DateTime.UtcNow,
                        item.BuildStableMs,
                        sample.ValueBar,
                        sample.ValueBar,
                        item.PressureThresholdBar,
                        double.NaN);
                }

                state.Qualification = qualification;
                _buildFailureStreaks.TryRemove(state.Key.HydraulicId, out _);
                state.MonitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                state.MonitorTask = MonitorQualifiedPressureAsync(state, state.MonitorCts.Token);
                // 所有等待同一 InitializeTask 的通道拿到完全相同的未来执行锚点。
                // 留出极小调度裕量，保证零相位也不会以“过期任务”立即补发。
                var actuationAnchorUtc = DateTime.UtcNow.AddMilliseconds(2);
                return new HydraulicCycleLease(
                    state.Key,
                    state.Members,
                    qualification,
                    actuationAnchorUtc,
                    state.Completion.Task,
                    state.CoordinatorEpoch);
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                // 暂停、停止、DAQ 自愈等正常控制流会取消正在建压的代次。
                // 取消不代表液压硬件失效，也不能进入三代次压力故障计数。
                await FailGenerationAsync(state, ex, publishFault: false).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                var classified = PrepareGenerationFailure(state.Key.HydraulicId, ex);
                await FailGenerationAsync(state, classified).ConfigureAwait(false);
                if (ReferenceEquals(classified, ex)) throw;
                throw classified;
            }
        }

        private Exception PrepareGenerationFailure(int hydraulicId, Exception exception)
        {
            if (exception is not HydraulicBuildTimeoutException buildTimeout)
                return exception;

            // 压力样本缺失/无效/陈旧不能证明液压硬件无法建压，保持软件自愈语义。
            // 只有新鲜样本连续低于建压窗口才计入“连续三代次无法建压”。
            if (!buildTimeout.IsBelowToleranceWindow)
            {
                _buildFailureStreaks.TryRemove(hydraulicId, out _);
                return buildTimeout;
            }

            var streak = _buildFailureStreaks.AddOrUpdate(hydraulicId, 1, (_, value) => value + 1);
            return buildTimeout.WithConfirmation(streak, PressureFaultConfirmGenerations);
        }

        private async Task MonitorQualifiedPressureAsync(GenerationState state, CancellationToken token)
        {
            var item = GetHydraulicItem(state.Key.HydraulicId);
            // 保压下限不得严于本代次刚通过的建压下限。旧配置即使仍是 5bar/100ms，
            // 运行时也按至少 PressureToleranceBar/1000ms 处理，避免配置升级遗漏复发。
            var minimumBar = item.PressureThresholdBar - item.EffectiveHoldDropToleranceBar;
            var confirmMs = item.EffectiveHoldDropConfirmMs;
            long? lowSince = null;
            var clock = Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    lock (state.Gate)
                    {
                        if (state.ReleaseStarted) return;
                    }

                    var sample = _readPressureSample(state.Key.HydraulicId);
                    var failureReason = HydraulicController.ClassifyPressureSampleFailure(
                        sample,
                        minimumBar,
                        item.PressureSampleMaxAgeMs);
                    var valid = !failureReason.HasValue;
                    if (!valid)
                    {
                        if (!lowSince.HasValue) lowSince = clock.ElapsedMilliseconds;
                        if (clock.ElapsedMilliseconds - lowSince.Value >= confirmMs)
                        {
                            if (failureReason != HydraulicPressureFailureReason.BelowMinimum)
                                _holdFailureStreaks.TryRemove(state.Key.HydraulicId, out _);
                            var confirmationStreak = failureReason == HydraulicPressureFailureReason.BelowMinimum
                                ? _holdFailureStreaks.AddOrUpdate(
                                    state.Key.HydraulicId,
                                    1,
                                    (_, value) => value + 1)
                                : 0;
                            var ex = new HydraulicPressureLostException(
                                state.Key.HydraulicId,
                                state.Key.Slot,
                                sample.ValueBar,
                                minimumBar,
                                failureReason ?? HydraulicPressureFailureReason.InvalidValue,
                                sample.AgeMs,
                                confirmationStreak,
                                PressureFaultConfirmGenerations);
                            await FailGenerationAsync(state, ex).ConfigureAwait(false);
                            return;
                        }
                    }
                    else
                    {
                        lowSince = null;
                    }
                    await Task.Delay(10, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task CompleteGenerationReleaseAsync(GenerationState state)
        {
            try
            {
                state.MonitorCts?.Cancel();
                await ExecuteReleaseOutputAsync(state.Key.HydraulicId).ConfigureAwait(false);
                await WaitForSafePressureAsync(state.Key.HydraulicId).ConfigureAwait(false);
                _holdFailureStreaks.TryRemove(state.Key.HydraulicId, out _);
                state.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                state.Completion.TrySetException(ex);
                PublishFault(state, ex);
            }
            finally
            {
                ReleaseGenerationGate(state);
                RemoveCompletedGeneration(state);
            }
        }

        private async Task FailGenerationAsync(
            GenerationState state,
            Exception exception,
            bool publishFault = true)
        {
            if (Interlocked.Exchange(ref state.FailStarted, 1) != 0) return;
            lock (state.Gate) state.ReleaseStarted = true;
            try
            {
                state.MonitorCts?.Cancel();
                await ExecuteReleaseOutputAsync(state.Key.HydraulicId).ConfigureAwait(false);
            }
            finally
            {
                state.BarrierReached.TrySetException(exception);
                state.Completion.TrySetException(exception);
                if (publishFault)
                    PublishFault(state, exception);
                ReleaseGenerationGate(state);
                RemoveCompletedGeneration(state);
            }
        }

        private void RemoveCompletedGeneration(GenerationState state)
        {
            if (state == null) return;
            if (_generations.TryGetValue(state.Key, out var current) && ReferenceEquals(current, state))
                _generations.TryRemove(state.Key, out _);
        }

        private async Task ExecuteReleaseOutputAsync(int hydraulicId)
        {
            if (_testReleaseAction != null)
            {
                await _testReleaseAction(hydraulicId).ConfigureAwait(false);
                return;
            }
            if (_hydCtl != null)
            {
                await _hydCtl.ForceReleaseAsync(hydraulicId).ConfigureAwait(false);
                return;
            }
            try { _do?.SetPressure(hydraulicId, false); } catch { }
            try { _ao?.WritePressure(hydraulicId == 1 ? "Cylinder1" : "Cylinder2", 0); } catch { }
        }

        private void PublishFault(GenerationState state, Exception exception)
        {
            _log.Error(exception.Message, "液压协调", exception);
            try
            {
                var classification = ClassifyFault(exception);
                var fault = new ControlFault(
                    exception is HydraulicBarrierTimeoutException ? "HydraulicBarrierTimeout" :
                    exception is OperationCanceledException ? "HydraulicOperationCanceled" :
                    exception is HydraulicReleaseTimeoutException ? "HydraulicReleaseTimeout" :
                    exception is HydraulicPressureLostException pressureLost &&
                        pressureLost.FailureReason != HydraulicPressureFailureReason.BelowMinimum
                        ? "PressureSampleUnavailable" :
                    exception is HydraulicPressureLostException ? "HydraulicPressureLost" :
                    exception is HydraulicBuildTimeoutException ? "HydraulicBuildTimeout" :
                    "HydraulicFault",
                    exception.Message,
                    FaultScope.HydraulicGroup,
                    state.Members.ToArray(),
                    state.Key.HydraulicId,
                    DateTime.UtcNow,
                    state.Key.TestRunId,
                    classification);
                NonCriticalObserver.Invoke(
                    FaultRaised,
                    fault,
                    ex => _log?.Warn(
                        $"液压故障观察者异常已隔离：{ex.Message}",
                        "液压协调"));
            }
            catch { }
        }

        internal static FaultClassification ClassifyFault(Exception exception)
        {
            if (exception is OperationCanceledException)
                return FaultClassification.SoftwareTransient;
            if (exception is HydraulicBarrierTimeoutException)
                return FaultClassification.SystemFault;
            if (exception is HydraulicReleaseTimeoutException)
                return FaultClassification.HardwareConfirmed;
            if (exception is HydraulicBuildTimeoutException buildTimeout)
            {
                if (buildTimeout.IsPressureEvidenceUnavailable ||
                    buildTimeout.IsWithinToleranceButUnstable)
                    return FaultClassification.SystemFault;
                if (buildTimeout.IsBelowToleranceWindow && !buildTimeout.IsConfirmed)
                    return FaultClassification.SystemFault;
                return FaultClassification.HardwareConfirmed;
            }
            if (exception is HydraulicPressureLostException pressureLost)
            {
                if (pressureLost.FailureReason != HydraulicPressureFailureReason.BelowMinimum)
                    return FaultClassification.SystemFault;
                return pressureLost.IsConfirmed
                    ? FaultClassification.HardwareConfirmed
                    : FaultClassification.SystemFault;
            }
            // 未知异常没有独立、可复核的硬件证据。保持系统故障/自愈语义，
            // 禁止兜底分支绕过连续三代次压力确认而误报硬件。
            return FaultClassification.SystemFault;
        }

        private void ReleaseGenerationGate(GenerationState state)
        {
            if (Interlocked.Exchange(ref state.GateReleased, 1) != 0) return;
            try { state.GenerationGate?.Release(); } catch { }
        }

        private HydraulicItem GetHydraulicItem(int hydraulicId)
        {
            return _test.Hydraulics.FirstOrDefault(x => x.Id == hydraulicId)
                   ?? throw new InvalidOperationException($"Hydraulic={hydraulicId} 缺少配置。");
        }

        /// <summary>
        ///     ★ 接入点（上电前调用）：声明“我这个通道要开始电控了”，若该组还没建压则先建压。
        ///     - 若有 _hydCtl：启动 BuildAndHoldAsync(hydId, token)，并把 ReleaseAction 绑定为 _hydCtl.Release(hydId)
        ///     - 否则：使用 Fallback DO/AO + 读压保持，到统一释放时撤销
        /// </summary>
        public async Task EnterElectricalPhaseAsync(int epbChannel, CancellationToken token)
        {
            if (!_channel2Hyd.TryGetValue(epbChannel, out var hydId))
            {
                _log.Warn($"EPB[{epbChannel}] 未映射到液压，跳过建压逻辑。", "液压协调");
                return;
            }

            var latch = _latches.GetOrAdd(hydId, _ => new Latch()); // 每个 hydId 一份

            while (true)
            {
                Task priorRelease = null;
                bool needBuild;
                lock (latch.Gate)
                {
                    if (latch.ReleaseStarted)
                    {
                        priorRelease = latch.ReleaseCompletion?.Task ?? Task.CompletedTask;
                        needBuild = false;
                    }
                    else
                    {
                        needBuild = !latch.PressureOn;
                        if (needBuild)
                        {
                            latch.Generation++;
                            latch.PressureOn = true;
                            latch.ReleaseStarted = false;
                            latch.ReleaseCompletion =
                                new TaskCompletionSource<bool>(
                                    TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        latch.InFlight.Add(epbChannel);
                    }
                }

                if (priorRelease == null)
                {
                    if (!needBuild) return;
                    break;
                }

                await priorRelease.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            try
            {
                if (_testReleaseAction != null)
                {
                    lock (latch.Gate)
                        latch.ReleaseActionAsync = () => _testReleaseAction(hydId);
                    return;
                }

                if (_hydCtl != null)
                {
                    // 方式A：优先用你已有的 HydraulicController —— BuildAndHoldAsync + Release
                    // BuildAndHoldAsync 会：DO 打开 + AO 输出到设定百分比，并保持，直到 Release(hydId) 或 token 取消。
                    // 我们在后台开一个保持任务；ReleaseAction 直接调用 _hydCtl.Release(hydId)。
                    latch.ReleaseActionAsync = async () =>
                    {
                        try
                        {
                            _hydCtl.Release(hydId);
                        }
                        catch
                        {
                            /* 忽略异常 */
                        }

                        await Task.CompletedTask;
                    };

                    // 异步起保持任务（不阻塞 EPB 的电控流程）
                    _tasks.Observe(
                        Task.Run(() => _hydCtl.BuildAndHoldAsync(hydId, token), token),
                        "HydraulicBuildAndHold",
                        Guid.Empty);

                    _log.Info($"液压[{hydId}] 进入保持（HydraulicController.BuildAndHoldAsync）。", "液压协调");

                    // 兼容 RunOnceAsync(HoldUntilRelease) 的“委托释放”用法（可选）：
                    // 若你的业务在别处以 RunOnceAsync(HoldUntilRelease) 启动保持，这里也尝试获取一次释放委托。
                    if (_hydCtl.TryGetReleaseDelegate(hydId, out var rel))
                    {
                        // 如果拿到了委托，优先用控制器的委托（写入日志方便排查）
                        latch.ReleaseActionAsync = rel;
                        _log.Info($"液压[{hydId}] 获取到 TryGetReleaseDelegate 的释放委托。", "液压协调");
                    }
                }
                else
                {
                    // 方式B：回退方案 —— 直接 DO/AO + 读压保持，直到统一释放时取消
                    if (_ao == null || _readPressure == null)
                    {
                        _log.Warn($"液压[{hydId}] 无法进入保持：缺少 AO 或 读压方法。", "液压协调");
                        lock (latch.Gate)
                        {
                            latch.PressureOn = false;
                        }

                        return;
                    }

                    latch.Cts = new CancellationTokenSource();
                    _tasks.Observe(
                        Task.Run(
                            () => FallbackHoldLoopAsync(hydId, latch.Cts.Token),
                            latch.Cts.Token),
                        "HydraulicFallbackHold",
                        Guid.Empty);
                    latch.ReleaseActionAsync = async () =>
                    {
                        try
                        {
                            latch.Cts?.Cancel();
                        }
                        catch
                        {
                        }

                        await Task.Delay(10);
                        try
                        {
                            _do.SetPressure(hydId, false);
                        }
                        catch
                        {
                        }

                        try
                        {
                            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
                            _ao.WritePressure(dev, 0);
                        }
                        catch
                        {
                        }
                    };
                    _log.Info($"液压[{hydId}] 进入保持（Fallback）。", "液压协调");
                }
            }
            catch (Exception ex)
            {
                lock (latch.Gate)
                {
                    latch.PressureOn = false;
                    latch.ReleaseStarted = false;
                    latch.ReleaseCompletion?.TrySetException(ex);
                }

                _log.Error($"液压[{hydId}] 建压保持失败：{ex.Message}", "液压协调", ex);
                throw;
            }
        }

        /// <summary>
        ///     ★ 接入点（单通道到达“电压释放点”时调用）：
        ///     若这是该液压组内最后一个待释放的通道，则统一释放液压（调用 ReleaseAction）。
        /// </summary>
        public async Task MarkVoltageReleaseAsync(int epbChannel)
        {
            if (!_channel2Hyd.TryGetValue(epbChannel, out var hydId)) return;
            if (!_latches.TryGetValue(hydId, out var latch)) return;

            bool needReleaseNow;
            int generation;
            Task releaseCompletion;
            Func<Task> releaseAction;
            lock (latch.Gate)
            {
                latch.InFlight.Remove(epbChannel);
                if (!latch.PressureOn || latch.ReleaseCompletion == null)
                    return;

                generation = latch.Generation;
                releaseCompletion = latch.ReleaseCompletion.Task;
                needReleaseNow = latch.InFlight.Count == 0 && !latch.ReleaseStarted;
                if (needReleaseNow)
                    latch.ReleaseStarted = true;
                releaseAction = latch.ReleaseActionAsync;
            }

            if (needReleaseNow)
            {
                try
                {
                    _log.Info(
                        $"液压[{hydId}] 本轮所有成员已到电压释放点：统一释压并等待低压确认。",
                        "液压协调");

                    if (releaseAction != null)
                    {
                        await releaseAction().ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            _do?.SetPressure(hydId, false);
                        }
                        catch
                        {
                        }

                        try
                        {
                            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
                            _ao?.WritePressure(dev, 0);
                        }
                        catch
                        {
                        }
                    }

                    await WaitForSafePressureAsync(hydId).ConfigureAwait(false);
                    latch.ReleaseCompletion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    latch.ReleaseCompletion.TrySetException(ex);
                }
                finally
                {
                    lock (latch.Gate)
                    {
                        if (latch.Generation == generation)
                        {
                            latch.InFlight.Clear();
                            latch.PressureOn = false;
                            latch.ReleaseStarted = false;
                            latch.Cts?.Dispose();
                            latch.Cts = null;
                            latch.ReleaseActionAsync = null;
                        }
                    }
                }
            }

            await releaseCompletion.ConfigureAwait(false);
        }

        private async Task WaitForSafePressureAsync(int hydId)
        {
            var item = _test.Hydraulics.FirstOrDefault(h => h.Id == hydId);
            var safePressureBar = Math.Max(0, item?.ReleaseSafePressureBar ?? 5);
            var stableMs = Math.Max(0, item?.ReleaseStableMs ?? 100);
            var timeoutMs = Math.Max(1, item?.ReleaseTimeoutMs ?? 5000);
            if (_readPressureSample == null)
            {
                throw new HydraulicReleaseTimeoutException(
                    hydId,
                    double.NaN,
                    safePressureBar,
                    timeoutMs,
                    "PressureReaderUnavailable");
            }

            var clock = Stopwatch.StartNew();
            long? stableSinceMs = null;
            var lastPressureBar = double.NaN;
            var lastDetail = string.Empty;
            while (clock.ElapsedMilliseconds <= timeoutMs)
            {
                PressureSample sample;
                try
                {
                    sample = _readPressureSample(hydId);
                    lastPressureBar = sample.ValueBar;
                }
                catch (Exception ex)
                {
                    throw new HydraulicReleaseTimeoutException(
                        hydId,
                        double.NaN,
                        safePressureBar,
                        timeoutMs,
                        "PressureReadFailed:" + ex.Message);
                }

                var sampleFailure = HydraulicController.ClassifyPressureSampleFailure(
                    sample,
                    double.NegativeInfinity,
                    item?.PressureSampleMaxAgeMs ?? 100);
                var safe = !sampleFailure.HasValue && lastPressureBar <= safePressureBar;
                lastDetail = sampleFailure.HasValue
                    ? $"PressureSample{sampleFailure.Value} AgeMs={sample.AgeMs:F1}"
                    : string.Empty;
                if (safe)
                {
                    if (!stableSinceMs.HasValue)
                        stableSinceMs = clock.ElapsedMilliseconds;
                    if (clock.ElapsedMilliseconds - stableSinceMs.Value >= stableMs)
                    {
                        _log.Info(
                            $"液压[{hydId}] 释压确认完成：Pressure={lastPressureBar:F3}bar，" +
                            $"阈值={safePressureBar:F3}bar，稳定={stableMs}ms。",
                            "液压协调");
                        return;
                    }
                }
                else
                {
                    stableSinceMs = null;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new HydraulicReleaseTimeoutException(
                hydId,
                lastPressureBar,
                safePressureBar,
                timeoutMs,
                lastDetail);
        }

        // —— 回退保持实现：DO 打开 + AO 输出百分比，达到阈值后保持，直到外部取消 —— //
        private async Task FallbackHoldLoopAsync(int hydId, CancellationToken token)
        {
            var item = _test.Hydraulics.FirstOrDefault(h => h.Id == hydId);
            if (item == null || !item.Enabled)
            {
                _log.Warn($"液压[{hydId}] 未启用/找不到配置。", "液压协调");
                return;
            }

            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
            if (!_do.SetPressure(hydId, true))
            {
                _log.Error($"液压[{hydId}] DO 打开失败（Fallback）。", "液压协调");
                return;
            }

            if (!_ao.WritePressure(dev, item.PressureThresholdBar))
            {
                _log.Error($"液压[{hydId}] AO 输出失败（Fallback）。", "液压协调");
                return;
            }

            if (item.Mode == HydraulicMode.ByPressure || item.Mode == HydraulicMode.Either)
                while (!token.IsCancellationRequested)
                    try
                    {
                        if (HydraulicController.IsPressureWithinTarget(
                                _readPressure(hydId),
                                item.PressureThresholdBar,
                                item.PressureToleranceBar)) break;
                        await Task.Delay(5, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
            else if (item.Mode == HydraulicMode.ByDuration)
                try
                {
                    await Task.Delay(Math.Max(0, item.DurationMs), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

            // 保持到取消
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                /* 正常 */
            }
        }

        // hydId 对应的一轮“闩锁”状态
        /// <summary>
        ///     记录某个液压组的当前状态。
        /// </summary>
        private sealed class Latch
        {
            public readonly object Gate = new(); // 
            public readonly HashSet<int> InFlight = new(); // 仍未到“电压释放点”的通道
            public CancellationTokenSource Cts; // Fallback 持有的 CTS（仅回退方案用）
            public bool PressureOn; // 是否已进入“建压保持”状态
            public bool ReleaseStarted;
            public int Generation;
            public TaskCompletionSource<bool> ReleaseCompletion;
            public Func<Task> ReleaseActionAsync; // 统一“释压”动作（优先使用 HydraulicController）
        }

        private sealed class GenerationState
        {
            public GenerationState(HydraulicGenerationKey key, int[] members, long coordinatorEpoch)
            {
                Key = key;
                Members = members;
                Remaining = new HashSet<int>(members);
                CoordinatorEpoch = coordinatorEpoch;
                ObserveInternalFault(Completion.Task);
                ObserveInternalFault(BarrierReached.Task);
            }

            // 代次故障会同时广播给多个内部等待点；某些竞态路径只等待其中一个。
            // 主动读取无人等待任务的 Exception，避免 GC 线程再次抛出
            // TaskScheduler.UnobservedTaskException 噪声，同时不改变正常 await 的故障语义。
            private static void ObserveInternalFault(Task task)
            {
                _ = task.ContinueWith(
                    faulted => { _ = faulted.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            public readonly object Gate = new();
            public HydraulicGenerationKey Key { get; }
            public long CoordinatorEpoch { get; }
            public int[] Members { get; }
            public HashSet<int> Remaining { get; }
            public Task<HydraulicCycleLease> InitializeTask;
            public PressureQualification Qualification;
            public readonly TaskCompletionSource<bool> Completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> BarrierReached =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenSource MonitorCts;
            public Task MonitorTask;
            public SemaphoreSlim GenerationGate;
            public bool ReleaseStarted;
            public int FailStarted;
            public int GateReleased;
        }
    }
}
