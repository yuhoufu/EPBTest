using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Config.Models;
using DataOperation;
using IO.NI;
using Timing;

namespace Controller
{
    public sealed class ChannelStartFault
    {
        public ChannelStartFault(int channel, string stage, string reason, FaultScope scope)
        {
            Channel = channel;
            Stage = stage ?? string.Empty;
            Reason = reason ?? string.Empty;
            Scope = scope;
        }
        public int Channel { get; }
        public string Stage { get; }
        public string Reason { get; }
        public FaultScope Scope { get; }
    }

    public sealed class BatchStartResult
    {
        public BatchStartResult(
            Guid testRunId,
            int[] startedChannels,
            ChannelStartFault[] faults,
            int[] completedDuringStartChannels = null)
        {
            TestRunId = testRunId;
            StartedChannels = startedChannels ?? Array.Empty<int>();
            Faults = faults ?? Array.Empty<ChannelStartFault>();
            CompletedDuringStartChannels = completedDuringStartChannels ?? Array.Empty<int>();
        }
        public Guid TestRunId { get; }
        public int[] StartedChannels { get; }
        public ChannelStartFault[] Faults { get; }
        public int[] CompletedDuringStartChannels { get; }
        public int[] QuarantinedChannels => Faults.Select(x => x.Channel).Distinct().OrderBy(x => x).ToArray();
    }

    internal sealed class UnattendedRemainingCyclePlan
    {
        internal UnattendedRemainingCyclePlan(
            int[] channels,
            Dictionary<int, int> remainingCycles,
            string error)
        {
            Channels = channels ?? Array.Empty<int>();
            RemainingCycles = remainingCycles ?? new Dictionary<int, int>();
            Error = error ?? string.Empty;
        }

        internal int[] Channels { get; }
        internal Dictionary<int, int> RemainingCycles { get; }
        internal string Error { get; }
        internal bool IsValid => string.IsNullOrWhiteSpace(Error);
    }

    /// <summary>
    ///     EpbManager 扩展：批量启动（学习 + 正式），并为每个压力组建立“锚点时间轴”，
    ///     让每个通道以固定相位（电源组内索引 × Δ）锁相到这条时间轴，保证“每圈对齐 + 组内错峰”。
    ///     使用方式：
    ///     await manager.StartBatchSynchronizedAsync(new[]{1,2,4,6}, learnCycles:5, token);
    /// </summary>
    public partial class EpbManager
    {
        /// <summary>
        /// Keep a learning/qualification runner transactionally aligned with
        /// the persisted profile.  SaveWithReceipt can fail after replacing
        /// the file and roll the store back; callers must therefore restore the
        /// runner immediately before any retry/failure handling runs.
        /// </summary>
        internal static void RestoreRunnerAdaptiveProfile(
            IEpbCycleRunner runner,
            EpbAdaptiveProfile modelBeforeLogicalCycle)
        {
            if (runner == null || modelBeforeLogicalCycle == null) return;
            runner.RestoreAdaptiveProfile(modelBeforeLogicalCycle);
        }

        internal static long NormalizeRecoveryRunEpoch(long suppliedRunEpoch, long currentRunEpoch)
        {
            return Math.Max(1, Math.Max(suppliedRunEpoch, currentRunEpoch));
        }

        // 字段区
        private ConcurrentDictionary<int, EpbCycleRunner> _runnerCache => _runnerRuntime.Cache;
        private ConcurrentDictionary<int, HighPrecisionTimer> _timerCache => _timerRuntime.Cache;
        private int _batchSessionActive;
        private CancellationTokenSource _batchSessionCts;
        private readonly BatchStartLifecycleGate _batchLifecycleGate = new BatchStartLifecycleGate();
        private int _formalPhaseCommitted;
        private int _idleSessionClosureScheduled;
        private long _learningRetryGeneration;
        private long _softwareHydraulicRetryGeneration;
        private ElectricalStaggerPlan _activeStaggerPlan;
        private RunChainIdentity _activeRunChainIdentity;
        private int _learningManifestPublished;
        private string _learningManifestReason = string.Empty;
        private int[] _activePlannedChannels = Array.Empty<int>();
        private int _activePlannedLearningCycles;
        private int _activePlannedQualificationCycles;
        // During a learning/qualification logical cycle the runner is allowed
        // to update its in-memory adaptive profile, but disk persistence is
        // deferred until the evidence directory has been durably sealed.
        private readonly ConcurrentDictionary<int, byte> _deferredAdaptivePersistence =
            new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentDictionary<int, string> _learningModelReceipts =
            new ConcurrentDictionary<int, string>();

        /// <summary>
        /// 复用活动错峰计划前必须确认它覆盖本次恢复通道。单通道无人值守恢复会
        /// 生成自己的计划；若多个通道依次恢复，旧版直接复用最后一个计划，导致
        /// “通道不在当前错峰计划中”并再次把已清理的通道送回自维护。
        /// </summary>
        private ElectricalStaggerPlan GetCompatibleStaggerPlan(IEnumerable<int> channels)
        {
            var selected = (channels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException("无法为空通道集合生成错峰计划。");

            var active = _activeStaggerPlan;
            if (active != null &&
                active.PeriodMs == PeriodMs &&
                selected.All(channel => active.Assignments.ContainsKey(channel)))
                return active;

            return ElectricalStaggerPlanner.Build(selected, _cfg.Test.Groups, PeriodMs);
        }
        private readonly ConcurrentDictionary<int, DateTime> _activeFormalT0ByPressureGroup =
            new ConcurrentDictionary<int, DateTime>();
        private Guid _activeBatchId;

        private void InitializeRunChainIdentity(RunChainIdentity supplied, Guid runId)
        {
            var normalized = (supplied ?? new RunChainIdentity(runId)).Normalize(runId);
            // Manual starts always own a new chain.  Recovery callers explicitly
            // pass RootRunId/ParentRunId and are the only path allowed to join an
            // existing chain.
            if (supplied == null)
                normalized = new RunChainIdentity(runId, runId, Guid.Empty, 0,
                    Interlocked.Read(ref _runEpoch));
            else if (normalized.RunEpoch <= 0)
                normalized = new RunChainIdentity(
                    normalized.RunId,
                    normalized.EffectiveRootRunId,
                    normalized.ParentRunId,
                    normalized.RestartGeneration,
                    NormalizeRecoveryRunEpoch(normalized.RunEpoch, Interlocked.Read(ref _runEpoch)));
            _activeRunChainIdentity = normalized;
            Interlocked.Exchange(ref _learningManifestPublished, 0);
            _learningManifestReason = string.Empty;
            _learningModelReceipts.Clear();
        }

        private sealed class LearningEvidenceContext
        {
            public Guid RunId { get; set; }
            public Guid ChainId { get; set; }
            public string ChainDirectory { get; set; }
            public string ExecutionDirectory { get; set; }
        }

        internal static Guid ResolveLearningChainId(RunChainIdentity identity, Guid runId)
        {
            var root = identity?.EffectiveRootRunId ?? Guid.Empty;
            return root != Guid.Empty ? root : runId;
        }

        private LearningEvidenceContext CaptureLearningEvidenceContext(Guid runId)
        {
            // Stop/close clears the mutable active batch fields while the last
            // learning channels may still be sealing their evidence.  Freeze
            // both identities and paths once per learning phase so an in-flight
            // seal can never dereference a concurrently cleared chain object or
            // fall through to the all-zero directory.
            var identity = _activeRunChainIdentity;
            var chainId = ResolveLearningChainId(identity, runId);
            var chainDirectory = Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                "LearningCycles",
                chainId.ToString("N"));
            return new LearningEvidenceContext
            {
                RunId = runId,
                ChainId = chainId,
                ChainDirectory = chainDirectory,
                ExecutionDirectory = Path.Combine(
                    chainDirectory,
                    "Executions",
                    runId.ToString("N"))
            };
        }

        private void PublishLearningRunManifest(string finalStatus, string reason)
        {
            if (Interlocked.Exchange(ref _learningManifestPublished, 1) != 0) return;
            var identity = _activeRunChainIdentity?.Normalize(_activeBatchId) ??
                           new RunChainIdentity(_activeBatchId, _activeBatchId, Guid.Empty, 0,
                               Interlocked.Read(ref _runEpoch));
            var chainDirectory = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName,
                "LearningCycles", identity.EffectiveRootRunId.ToString("N"));
            try
            {
                // Product identity belongs to the entry/runtime build, not the
                // Controller library (whose assembly version is intentionally
                // independent and may remain 1.0.0.0).
                var appVersion = RuntimeBuildIdentity.Capture().ProductVersion ?? string.Empty;
                var modelHash = string.Empty;
                var modelReceipt = string.Empty;
                var modelConfirmed = _adaptiveProfileStore != null &&
                                     File.Exists(_adaptiveProfileStore.FilePath) &&
                                     (_activePlannedChannels ?? Array.Empty<int>()).All(channel =>
                                     {
                                         try { return _adaptiveProfileStore.GetOrCreate(channel)?.IsStable == true; }
                                         catch { return false; }
                                     }) &&
                                     (_activePlannedChannels ?? Array.Empty<int>()).All(channel =>
                                         _learningModelReceipts.TryGetValue(channel, out var receipt) &&
                                         !string.IsNullOrWhiteSpace(receipt));
                if (modelConfirmed)
                {
                    modelHash = LearningRunManifestStore.ComputeSha256(_adaptiveProfileStore.FilePath);
                    modelReceipt = "sha256:" + modelHash;
                }
                else if (string.Equals(finalStatus, "Successful", StringComparison.OrdinalIgnoreCase))
                {
                    // A successful terminal status is only eligible for automatic
                    // retention once the persisted model and its read-back receipt
                    // are both confirmed.  If that proof is unavailable we must
                    // publish an explicitly non-terminal/unknown execution rather
                    // than manufacturing a Failed result that could be retained or
                    // cleaned as a normal failure.
                    finalStatus = "Unknown";
                    reason = string.IsNullOrWhiteSpace(reason)
                        ? "ModelPersistenceUnconfirmed"
                        : reason + ";ModelPersistenceUnconfirmed";
                }
                var manifest = LearningRunManifestStore.BuildFromDirectory(
                    chainDirectory,
                    identity,
                    appVersion,
                    string.Empty,
                    _activePlannedChannels ?? Enumerable.Empty<int>(),
                    _activePlannedLearningCycles,
                    _activePlannedQualificationCycles,
                    finalStatus,
                    reason,
                    modelHash,
                    modelReceipt);
                manifest.ModelCommitReceipts = _learningModelReceipts
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"EPB{pair.Key:D2}:{pair.Value}")
                    .ToList();
                if (string.Equals(finalStatus, "Successful", StringComparison.OrdinalIgnoreCase) &&
                    !LearningRunManifestStore.HasPlannedEvidence(manifest))
                {
                    manifest.FinalStatus = "Unknown";
                    manifest.Reason = string.IsNullOrWhiteSpace(manifest.Reason)
                        ? "LearningEvidenceIncomplete"
                        : manifest.Reason + ";LearningEvidenceIncomplete";
                }
                LearningRunManifestStore.PublishAtomic(chainDirectory, manifest, out _);
                if (_learningRetentionMode == LearningRetentionMode.Count)
                    _dataHousekeeping?.Enqueue(chainDirectory);
                _log?.Info($"Learning运行链manifest已原子发布：Chain={identity.RunChainId:N} Status={finalStatus}", "落盘");
            }
            catch (Exception ex)
            {
                _learningManifestReason = ex.Message;
                _log?.Warn($"Learning运行链manifest发布失败，保留Unknown不清理：{ex.Message}", "落盘");
            }
        }

        /// <summary>当前是否已有批量学习或正式试验会话。</summary>
        public bool IsBatchSessionActive => Volatile.Read(ref _batchSessionActive) != 0;
        internal bool IsFormalPhaseCommitted => Volatile.Read(ref _formalPhaseCommitted) != 0;

        /// <summary>
        /// 自动进程交接不能沿用人工入口的“健康通道先跑、故障通道留给操作员”语义。
        /// 检查点授权的是一个完整通道集合；其中任意通道未启动都意味着同一授权运行链
        /// 无法完成每通道剩余正式圈，必须回到有界进程恢复，禁止静默部分运行。
        /// </summary>
        internal static string ValidateUnattendedBatchStartResult(
            IEnumerable<int> expectedChannels,
            BatchStartResult result)
        {
            var expected = (expectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (expected.Length == 0)
                return "UnattendedStartExpectedChannelsMissing";
            if (result == null)
                return "UnattendedStartResultMissing";
            if (result.TestRunId == Guid.Empty)
                return "UnattendedStartRunIdMissing";

            var started = (result.StartedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var completedDuringStart = (result.CompletedDuringStartChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var satisfied = started.Concat(completedDuringStart).Distinct().ToArray();
            var missing = expected.Except(satisfied).ToArray();
            var unexpected = started.Except(expected).ToArray();
            var unexpectedCompleted = completedDuringStart.Except(expected).ToArray();
            var faults = result.Faults ?? Array.Empty<ChannelStartFault>();
            if (missing.Length == 0 && unexpected.Length == 0 &&
                unexpectedCompleted.Length == 0 && faults.Length == 0)
                return string.Empty;

            var faultSummary = string.Join(",", faults
                .OrderBy(fault => fault.Channel)
                .Select(fault => $"EPB{fault.Channel}:{fault.Stage}:{fault.Reason}"));
            return
                $"UnattendedStartIncomplete Missing=[{string.Join(",", missing)}] " +
                $"Unexpected=[{string.Join(",", unexpected)}] " +
                $"UnexpectedCompleted=[{string.Join(",", unexpectedCompleted)}] " +
                $"Faults=[{faultSummary}]";
        }

        /// <summary>
        /// 自动恢复时以 SQLite 中的成功正式圈数为当前权威值，并以恢复检查点
        /// 检查点作为单调性护栏。DB 可能比检查点更新（提交成功后进程在事件回调前
        /// 退出），但绝不能比检查点倒退；已经完成的通道不得再次启动一圈。
        /// </summary>
        internal static UnattendedRemainingCyclePlan BuildUnattendedRemainingCyclePlan(
            IEnumerable<int> authorizedChannels,
            IDictionary<string, int> checkpointRemaining,
            IDictionary<int, int> durableRemaining)
        {
            var authorized = (authorizedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (authorized.Length == 0)
                return new UnattendedRemainingCyclePlan(
                    Array.Empty<int>(),
                    null,
                    "UnattendedProgressAuthorizedChannelsMissing");
            if (checkpointRemaining == null || durableRemaining == null)
                return new UnattendedRemainingCyclePlan(
                    Array.Empty<int>(),
                    null,
                    "UnattendedProgressEvidenceMissing");

            var remaining = new Dictionary<int, int>();
            foreach (var channel in authorized)
            {
                var key = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!checkpointRemaining.TryGetValue(key, out var checkpointValue) ||
                    checkpointValue < 0)
                    return new UnattendedRemainingCyclePlan(
                        Array.Empty<int>(),
                        null,
                        $"UnattendedCheckpointRemainingMissing EPB={channel}");
                if (!durableRemaining.TryGetValue(channel, out var durableValue) ||
                    durableValue < 0)
                    return new UnattendedRemainingCyclePlan(
                        Array.Empty<int>(),
                        null,
                        $"UnattendedDurableRemainingMissing EPB={channel}");

                // 检查点只在成功正式圈提交事件后递减。若重载后的 SQLite/XML 成功圈
                // 反而更少，继续运行会掩盖数据丢失或配置回退，必须拒绝自动上电。
                if (durableValue > checkpointValue)
                    return new UnattendedRemainingCyclePlan(
                        Array.Empty<int>(),
                        null,
                        $"UnattendedProgressRegression EPB={channel} " +
                        $"DurableRemaining={durableValue} CheckpointRemaining={checkpointValue}");

                remaining[channel] = durableValue;
            }

            return new UnattendedRemainingCyclePlan(
                remaining.Where(pair => pair.Value > 0)
                    .Select(pair => pair.Key)
                    .OrderBy(channel => channel)
                    .ToArray(),
                remaining,
                string.Empty);
        }

        /// <summary>
        /// 同进程恢复不能只读取稍早写入的 JSON 检查点。正式圈的 SQLite 提交与
        /// 检查点事件之间存在极短窗口，故障恰好发生在窗口内时，数据库可能已经
        /// 多完成一圈。这里直接读取记录器的耐久成功圈计数，作为续跑的权威事实。
        /// </summary>
        internal int GetDurableCompletedFormalCycleCount(int channel)
        {
            if (channel < 1 || channel > 12)
                throw new ArgumentOutOfRangeException(nameof(channel));
            var recorder = _recorder ?? throw new InvalidOperationException(
                "正式圈记录器不可用，拒绝在缺少耐久进度证据时自动恢复。");
            return Math.Max(0, recorder.GetLastCycleNumber(channel));
        }

        internal long GetDurableMechanicalCycleCount(int channel)
        {
            if (channel < 1 || channel > 12)
                throw new ArgumentOutOfRangeException(nameof(channel));
            var durable = Recorder as IMechanicalCycleRecorder;
            if (durable == null)
                return GetObservedMechanicalCycleCount(channel);
            return Math.Max(
                GetObservedMechanicalCycleCount(channel),
                durable.GetMechanicalCycleCompletedCount(channel));
        }

        /// <summary>
        /// 对外暴露的“EPB 单圈完成”事件。
        /// 参数 1：EPB 通道号（1..12）；
        /// 参数 2：本次试验 Session 内已经完成的圈数（从 1 开始）。
        /// </summary>
        public event Action<int, int> ChannelCycleCompleted;
        /// <summary>任何实际完成夹紧+释放的机械圈；独立于正式证据是否提交。</summary>
        public event Action<int, CycleAttemptKind, int> ChannelMechanicalCycleCompleted;


        #region 对外主入口 Batch Start (Learning + Formal) with Group Anchor + Stagger Phases

        /// <summary>
        ///     批量启动 EPB 通道（学习 + 正式），每圈都对齐到“压力组锚点 + 固定相位”。<br />
        ///     关键增强：
        ///     <list type="number">
        ///         <item>为每个压力组的 <c>t0</c> 预留 <see cref="AnchorWarmupMs" /> 预热裕度，确保首圈（k=0）也有正的延时。</item>
        ///         <item>正式学习阶段内：对每个“圈 × 组”先起“液压锚点”任务，并在每个通道任务里 <c>await</c> 该任务（作为屏障）。</item>
        ///         <item>对很小/负的 <c>delay</c> 不再直接“零等待”，而是 <c>Task.Yield()</c> 打散同刻调度，降低通道扎堆概率。</item>
        ///     </list>
        /// </summary>
        /// <param name="channels">要启动的 EPB 通道（1..12）。例如 new[]{1,2,4,6}</param>
        /// <param name="learnCycles">自学习圈数。=0 则跳过学习。</param>
        /// <param name="token">取消令牌。</param>
        /// <remarks>
        ///     依赖：<br />
        ///     - <c>GroupByPressure(int[])</c>：将通道按压力组（1/2）分组。<br />
        ///     - <c>CeilToBoundary(DateTime,int)</c>：把时间上取整到周期边界。<br />
        ///     - <c>PreReleaseBatchStaggeredAsync</c>（可选）：你的“预释放三波错峰”方法。<br />
        ///     - <c>RunLearningPhaseAsync</c>（见下方替换版）：圈内并发 + 相位错峰。<br />
        ///     - <c>StartFormalPhaseTimers</c>：正式阶段的高精计时器（你已有）。<br />
        /// </remarks>
        /// <exception cref="ArgumentException">当 <paramref name="channels" /> 为空时抛出。</exception>
        public async Task StartBatchSynchronizedAsync(int[] channels, int learnCycles, CancellationToken token)
        {
            await StartBatchSynchronizedWithResultAsync(channels, learnCycles, token).ConfigureAwait(false);
        }

        public async Task<BatchStartResult> StartBatchSynchronizedWithResultAsync(
            int[] channels,
            int learnCycles,
            CancellationToken token)
        {
            return await StartBatchCoreAsync(
                    channels,
                    learnCycles,
                    qualificationCycles: 0,
                    reuseStableProfiles: false,
                    token,
                    chainIdentity: null)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Recovery-safe entry point.  The recovery coordinator supplies the
        /// parent/root execution identity explicitly; EpbManager never reads a
        /// checkpoint file to infer the retention chain.
        /// </summary>
        public Task<BatchStartResult> StartBatchSynchronizedWithResultAsync(
            int[] channels,
            int learnCycles,
            RunChainIdentity chainIdentity,
            CancellationToken token)
        {
            return StartBatchCoreAsync(
                channels,
                learnCycles,
                qualificationCycles: 0,
                reuseStableProfiles: false,
                token,
                chainIdentity);
        }

        /// <summary>
        /// 从已验证的正常暂停检查点恢复：复用稳定模型，先做资格圈，不把资格圈计入正式目标。
        /// </summary>
        public Task<BatchStartResult> StartBatchFromGracefulCheckpointAsync(
            int[] channels,
            int qualificationCycles,
            CancellationToken token)
        {
            return StartBatchFromGracefulCheckpointAsync(channels, qualificationCycles, null, token);
        }

        public Task<BatchStartResult> StartBatchFromGracefulCheckpointAsync(
            int[] channels,
            int qualificationCycles,
            RunChainIdentity chainIdentity,
            CancellationToken token)
        {
            if (qualificationCycles < 1 || qualificationCycles > 2)
                throw new ArgumentOutOfRangeException(
                    nameof(qualificationCycles),
                    "正常暂停后的资格复核必须为1或2圈。");

            var selected = (channels ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
            var unstable = selected
                .Where(channel => !GetAdaptiveProfile(channel).IsStable)
                .ToArray();
            if (unstable.Length > 0)
            {
                var learnCycles = Math.Max(5, _cfg.Test?.LearnCycles ?? 5);
                _log?.Warn(
                    $"暂停检查点的稳定模型不可用：EPB[{string.Join(",", unstable)}]；" +
                    $"本次点击自动转为完整{learnCycles}圈学习，不要求用户再次点击。",
                    "EPB");
                return StartBatchCoreAsync(
                    selected,
                    learnCycles,
                    qualificationCycles: 0,
                    reuseStableProfiles: false,
                    token,
                    chainIdentity);
            }
            return StartBatchCoreAsync(
                selected,
                learnCycles: 0,
                qualificationCycles,
                reuseStableProfiles: true,
                token,
                chainIdentity);
        }

        /// <summary>
        /// “重新开始”清场屏障：先合并/完成旧批次的安全停止，再等待旧启动调用彻底退出。
        /// 返回前不会遗留仍可能提交 StopChannel/StopAll 的旧启动尾声，调用方可以在同一次请求中直接启动新批次。
        /// </summary>
        public async Task<StopSafetyResult> PrepareForFreshRestartAsync(
            StopContext context,
            CancellationToken token = default,
            bool discardHistoricalStopChecks = false)
        {
            ThrowIfProcessRestartRequired();
            context ??= new StopContext
            {
                Source = StopSource.ManualUi,
                Reason = "重新开始前抛弃旧批次状态并安全清场",
                Initiator = nameof(PrepareForFreshRestartAsync),
                CorrelationId = Guid.NewGuid().ToString("N"),
                RequestedUtc = DateTime.UtcNow
            };

            // 安全停止不能因开始按钮调用方取消而半途退出；token 只控制调用方等待旧启动尾声。
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var safetyTask = StopAllAsync(context, CancellationToken.None);
                    var completed = await Task.WhenAny(
                            safetyTask,
                            Task.Delay(TimeSpan.FromSeconds(30), deadline.Token))
                        .ConfigureAwait(false);
                    if (completed != safetyTask)
                    {
                        token.ThrowIfCancellationRequested();
                        Interlocked.Exchange(ref _processRestartRequired, 1);
                        throw new TimeoutException("重新开始清场超过30秒；当前进程已撤权，必须外部恢复。");
                    }
                    var safety = await safetyTask.ConfigureAwait(false);
                    await _batchLifecycleGate.JoinAsync(deadline.Token).ConfigureAwait(false);
                    safety = await FinalizeLogicalQuiescenceForRestartAsync(
                            safety,
                            "PrepareForFreshRestart",
                            deadline.Token)
                        .ConfigureAwait(false);

                var canDiscardHistoricalChecks =
                CanDiscardHistoricalStopChecksForExplicitRestart(
                    safety,
                    discardHistoricalStopChecks);
                if (!safety.CanRestartInProcess && !canDiscardHistoricalChecks)
                {
                    throw new InvalidOperationException(
                    "重新开始清场未通过物理安全与软件逻辑不变量。" +
                    $" Motor={safety.MotorError}; Power={safety.PowerError}; " +
                    $"Pressure={safety.PressureError}; Logical={safety.LogicalError}");
                }

                if (!safety.CanRestartInProcess)
                    _log?.Warn(
                    "操作员已先执行“停止试验”；上一批次的持久化、数据连续性或逻辑清场" +
                    "未确认项只保留为诊断，不再阻止本次完整学习启动。" +
                    $" Motor={safety.MotorError}; Power={safety.PowerError}; " +
                    $"Pressure={safety.PressureError}; Persistence={safety.PersistenceError}; " +
                    $"DataContinuity={safety.DataContinuityError}; Logical={safety.LogicalError}",
                    "EPB");

                _log?.Info(
                "重新开始清场完成：旧批次软件状态、在途启动与瞬态故障已抛弃；开始执行新批次实时预检。",
                "EPB");
                    return safety;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _processRestartRequired, 1);
                throw new TimeoutException(
                    "重新开始清场超过30秒；当前进程已撤权，必须外部恢复。");
            }
        }

        /// <summary>
        /// 独立进程看门狗只读取逻辑存活证据，不获得任何硬件控制对象。
        /// 返回值为新快照，调用方不得据此直接操作 DAQ/DO/电源。
        /// </summary>
        public LogicalQuiescenceSnapshot CaptureWatchdogLogicalSnapshot()
        {
            return CaptureLogicalQuiescenceSnapshot();
        }

        public Guid WatchdogRunId => _activeBatchId;
        public long WatchdogRunEpoch => Interlocked.Read(ref _runEpoch);

        /// <summary>
        /// Controller 权威的永久卡钳报警集合；看门狗不得从 UI 文本或 AlarmStopped
        /// 状态推断永久性。返回新数组，调用方不能修改控制层状态。
        /// </summary>
        public int[] CaptureWatchdogPermanentAlarmedChannels()
        {
            return _nonRecoverableChannelFaultLatch.Keys
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        public IReadOnlyDictionary<int, string> CaptureWatchdogPermanentAlarmReasons()
        {
            return _nonRecoverableChannelFaultReasons
                .Where(pair => _nonRecoverableChannelFaultLatch.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Key)
                .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty);
        }

        public WatchdogRecoverySnapshot CaptureWatchdogRecoverySnapshot()
        {
            var permanent = CaptureWatchdogPermanentAlarmedChannels();
            var reasons = CaptureWatchdogPermanentAlarmReasons();
            var current = _daqAutoRecovery.Values
                .Where(context => context != null &&
                                  context.RunId == _activeBatchId &&
                                  context.RunEpoch == Interlocked.Read(ref _runEpoch) &&
                                  context.Terminal.Current == DaqRecoveryTerminal.None)
                .OrderBy(context => context.StartedUtc)
                .FirstOrDefault();
            var orphan = _timers
                .Where(pair =>
                {
                    var timer = pair.Value;
                    var state = _channelRuntimeStateStore.Get(pair.Key);
                    var device = _acq.GetDeviceForEpbChannel(pair.Key);
                    var hasOwner = !string.IsNullOrWhiteSpace(device) &&
                                   _daqAutoRecovery.TryGetValue(device, out var owner) &&
                                   owner != null &&
                                   owner.Terminal.Current == DaqRecoveryTerminal.None &&
                                   owner.RunId == _activeBatchId &&
                                   owner.RunEpoch == Interlocked.Read(ref _runEpoch);
                    return timer != null &&
                           (timer.RuntimeState == Timing.HighPrecisionTimerRuntimeState.Paused ||
                            timer.RuntimeState == Timing.HighPrecisionTimerRuntimeState.PausePending) &&
                           (timer.PauseReason ?? string.Empty).IndexOf("Daq", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           !hasOwner &&
                           state != null &&
                           IsChannelEnabled(pair.Key) &&
                           state.State != ChannelRuntimeState.Completed &&
                           state.State != ChannelRuntimeState.NotEnabled &&
                           state.State != ChannelRuntimeState.AlarmStopped &&
                           state.State != ChannelRuntimeState.InterlockStopped &&
                           state.State != ChannelRuntimeState.SystemFault &&
                           state.State != ChannelRuntimeState.ManualStopped &&
                           !_channelPausedUtc.ContainsKey(pair.Key) &&
                           !_manualStopRequestedChannels.ContainsKey(pair.Key);
                })
                .Select(pair => pair.Key)
                .OrderBy(channel => channel)
                .ToArray();
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var recoveringStates = _channelRuntimeStateStore.Snapshot()
                .Where(state => state != null &&
                                state.State == ChannelRuntimeState.Recovering &&
                                state.RunId == _activeBatchId &&
                                state.RunEpoch == runEpoch &&
                                state.Enabled)
                .OrderBy(state => state.Channel)
                .ToArray();
            var recoveringChannels = recoveringStates
                .Select(state => state.Channel)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var nowMonotonicTicks = Stopwatch.GetTimestamp();
            var orphanRecovering = recoveringStates
                .Where(state =>
                {
                    var since = _recoveringSinceMonotonicTicks.TryGetValue(
                        state.Channel,
                        out var ticks)
                        ? ticks
                        : nowMonotonicTicks;
                    return nowMonotonicTicks - since >= Stopwatch.Frequency &&
                           !HasRecoveryExecutionCoverage(state, runEpoch);
                })
                .Select(state => state.Channel)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var recoveringStartedTicks = recoveringStates
                .Select(state => _recoveringSinceUtcTicks.TryGetValue(state.Channel, out var ticks)
                    ? ticks
                    : state.TimestampUtc.Ticks)
                .Where(ticks => ticks > 0)
                .DefaultIfEmpty(0L)
                .Min();
            var cutoffCompleted = current != null &&
                                  current.Phase.Current >= DaqRecoveryPhase.CutoffCompleted;
            var powerGroups = current == null || _powerSupply == null || cutoffCompleted
                ? Array.Empty<int>()
                : current.AffectedChannels
                    .Select(GetElectricalGroupId)
                    .Where(id => id > 0)
                    .Distinct()
                    .Where(id =>
                    {
                        var state = _powerSupply.GetRuntimeState(id);
                        var taskPending = current.PowerDisableTasksByGroup != null &&
                                           current.PowerDisableTasksByGroup.TryGetValue(id, out var task) &&
                                           task != null && !task.IsCompleted;
                        return taskPending ||
                               state.ExpectedOutputEnabled ||
                               state.TelemetryOutputEnabled;
                    })
                    .OrderBy(id => id)
                    .ToArray();
            var genericPowerGroups = _powerSupply == null
                ? Array.Empty<int>()
                : recoveringChannels
                    .Where(IsChannelEnergized)
                    .Select(GetElectricalGroupId)
                    .Where(id => id > 0)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
            powerGroups = powerGroups.Concat(genericPowerGroups)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            // Orphan pauses are independently actionable even when another DAQ device
            // currently owns a recovery context.  Do not hide them behind `current`.
            var orphanPaused = orphan.Length > 0 || orphanRecovering.Length > 0;
            var pauseSinceUtcTicks = orphan.Length == 0
                ? 0L
                : _timers
                    .Where(pair => orphan.Contains(pair.Key))
                    .Select(pair => pair.Value.PauseUtc?.ToUniversalTime().Ticks ?? 0L)
                    .Where(ticks => ticks > 0)
                    .DefaultIfEmpty(0L)
                    .Min();
            var powerDisableSinceUtcTicks = powerGroups.Length == 0 || current == null
                ? 0L
                : Interlocked.Read(ref current.PowerDisableStartedUtcTicks);
            var expectedRecoveryChannels = (current?.AffectedChannels ?? Array.Empty<int>())
                .Concat(orphan)
                .Concat(recoveringChannels)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var genericOutputsConfirmedOff = recoveringChannels.Length > 0 &&
                                             recoveringChannels.All(channel =>
                                                 !IsChannelEnergized(channel));
            var recoveryDeadlineTicks = recoveringStartedTicks <= 0
                ? 0L
                : recoveringStartedTicks +
                  RecoveryGroupHardDeadlineMs * TimeSpan.TicksPerMillisecond;
            if (current != null)
            {
                var daqDeadline = current.StartedUtc
                    .AddMilliseconds(RecoveryGroupHardDeadlineMs).Ticks;
                recoveryDeadlineTicks = recoveryDeadlineTicks <= 0
                    ? daqDeadline
                    : Math.Min(recoveryDeadlineTicks, daqDeadline);
            }
            var primaryRecovering = recoveringStates.FirstOrDefault();
            return new WatchdogRecoverySnapshot
            {
                ActiveRecovery = current != null || orphan.Length > 0 || recoveringChannels.Length > 0,
                OrphanPaused = orphanPaused,
                PowerDisablePending = powerGroups.Length > 0,
                PowerOffUnconfirmed = powerGroups.Length > 0,
                OutputsConfirmedOff = cutoffCompleted || genericOutputsConfirmedOff,
                RecoveryHardDeadlineUtcTicks = recoveryDeadlineTicks,
                RunId = _activeBatchId,
                RunEpoch = runEpoch,
                IncidentId = current != null
                    ? current.CorrelationId.ToString("N")
                    : primaryRecovering?.CorrelationId.ToString("N") ?? string.Empty,
                RecoveryIncident = current != null
                    ? current.CorrelationId.ToString("N")
                    : primaryRecovering?.CorrelationId.ToString("N") ?? string.Empty,
                RecoveryContext = current != null
                    ? current.Device
                    : primaryRecovering?.ReasonCode ?? string.Empty,
                Device = current?.Device ?? string.Empty,
                CorrelationId = current?.CorrelationId ?? primaryRecovering?.CorrelationId ?? Guid.Empty,
                Stage = current?.ValidationPhase ??
                        (primaryRecovering?.ReasonCode ??
                         (orphan.Length == 0 ? string.Empty : "OrphanPause")),
                StageOrdinal = current == null
                    ? (recoveringChannels.Length > 0 || orphan.Length > 0 ? 5 : 0)
                    : (int)current.Phase.Current,
                StartedUtc = current?.StartedUtc ??
                             (recoveringStartedTicks > 0
                                 ? new DateTime(recoveringStartedTicks, DateTimeKind.Utc)
                                 : DateTime.UtcNow),
                PauseSinceUtcTicks = pauseSinceUtcTicks,
                PowerDisableSinceUtcTicks = powerDisableSinceUtcTicks,
                ExpectedChannels = expectedRecoveryChannels,
                ExpectedRecoveryChannels = expectedRecoveryChannels,
                OrphanPausedChannels = orphan,
                RecoveringChannels = recoveringChannels,
                OrphanRecoveryChannels = orphanRecovering,
                PowerDisablePendingGroups = powerGroups,
                PowerDisableSinceUtc = powerDisableSinceUtcTicks <= 0
                    ? (DateTime?)null
                    : new DateTime(
                        powerDisableSinceUtcTicks,
                        DateTimeKind.Utc),
                PermanentAlarmedChannels = permanent,
                PermanentAlarmReasons = reasons
            };
        }

        public WatchdogStorageSnapshot CaptureWatchdogStorageSnapshot()
        {
            WatchdogDeviceStorageSnapshot Capture(string device)
            {
                var persistence = _persistence.GetSnapshot(device);
                var freshness = _acq.GetDaqFreshnessSnapshot(device, 100);
                var frozen = _daqAutoRecovery.TryGetValue(device, out var recovery) &&
                             recovery?.CutoffSnapshot != null
                    ? recovery.CutoffSnapshot.FrozenBoundary
                    : 0;
                return new WatchdogDeviceStorageSnapshot
                {
                    Device = device,
                    CallbackGapCount = freshness.CallbackGapEventCount,
                    Generation = (int)Math.Min(int.MaxValue, _acq.GetCurrentGeneration(device)),
                    FrozenBoundary = frozen,
                    Persisted = persistence.Sequence,
                    Head = persistence.PendingHeadSequence,
                    InFlight = persistence.InFlightSequence,
                    QueueDepth = persistence.QueueDepth,
                    PersistenceState = persistence.State.ToString()
                };
            }
            return new WatchdogStorageSnapshot { Dev1 = Capture("Dev1"), Dev2 = Capture("Dev2") };
        }

        /// <summary>
        /// 显式停止后的新批次只要求电机断能命令和程控电源关闭已确认；上一批次的
        /// 压力证据、写盘、连续性和逻辑清场结果不再作为新批次许可条件。
        /// 新批次仍会重新执行实时 DAQ、电源、液压和完整学习预检。
        /// </summary>
        internal static bool CanDiscardHistoricalStopChecksForExplicitRestart(
            StopSafetyResult safety,
            bool explicitlyStopped)
        {
            return explicitlyStopped &&
                   safety?.CanReleaseAcquisition == true;
        }

        private async Task<BatchStartResult> StartBatchCoreAsync(
            int[] channels,
            int learnCycles,
            int qualificationCycles,
            bool reuseStableProfiles,
            CancellationToken token,
            RunChainIdentity chainIdentity = null)
        {
            return await _batchLifecycleGate.RunAsync(
                    () => StartBatchCoreUnderLifecycleGateAsync(
                        channels,
                        learnCycles,
                        qualificationCycles,
                        reuseStableProfiles,
                        chainIdentity,
                        token),
                    token)
                .ConfigureAwait(false);
        }

        private async Task<BatchStartResult> StartBatchCoreUnderLifecycleGateAsync(
            int[] channels,
            int learnCycles,
            int qualificationCycles,
            bool reuseStableProfiles,
            RunChainIdentity chainIdentity,
            CancellationToken token)
        {
            ThrowIfProcessRestartRequired();
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空", nameof(channels));

            var requested = channels.Distinct().OrderBy(x => x).ToArray();
            var alreadyTargetCompleted = requested
                .Where(IsMechanicalTargetReached)
                .ToArray();
            var selected = requested
                .Except(alreadyTargetCompleted)
                .OrderBy(x => x)
                .ToArray();
            if (selected.Length == 0)
            {
                foreach (var completedChannel in alreadyTargetCompleted)
                    PublishChannelRuntimeState(
                        completedChannel,
                        ChannelRuntimeState.Completed,
                        "MechanicalTargetAlreadyCompleted",
                        "机械目标圈已完成，本次开始请求未执行任何硬件动作");
                return new BatchStartResult(
                    Guid.NewGuid(),
                    Array.Empty<int>(),
                    Array.Empty<ChannelStartFault>(),
                    alreadyTargetCompleted);
            }
            _activePlannedChannels = selected.ToArray();
            _activePlannedLearningCycles = Math.Max(0, learnCycles);
            _activePlannedQualificationCycles = Math.Max(0, qualificationCycles);
            if (!reuseStableProfiles && learnCycles < 5)
                throw new InvalidOperationException("严格完整曲线控制要求 LearnCycle 至少为5圈。");
            if (reuseStableProfiles)
                EnsureAdaptiveProfilesReady(selected);
            var staggerPlan = ElectricalStaggerPlanner.Build(selected, _cfg.Test.Groups, PeriodMs);
            var sessionToken = BeginBatchSession(token);
            var startFaults = new List<ChannelStartFault>();
            var completedDuringStart = new List<int>(alreadyTargetCompleted);
            try
            {
                _activeBatchId = Guid.NewGuid();
                InitializeRunChainIdentity(chainIdentity, _activeBatchId);
                _daqLivenessLogTransitions.BeginSession(
                    _activeBatchId,
                    Interlocked.Read(ref _runEpoch));
                Interlocked.Exchange(ref _idleSessionClosureScheduled, 0);
                BeginDaqIncidentRun(_activeBatchId, selected);
                InvalidateStopSafetyCache();
                await EnsureHydraulicCoordinatorHealthyBeforeStartAsync(selected, sessionToken)
                    .ConfigureAwait(false);
                ResetTransientFaultStateForRestart(
                    selected,
                    reuseStableProfiles ? "GracefulCheckpointResume" : "FreshBatchStart");
                foreach (var channel in selected)
                    _faultConfirmationTracker.ResetScope($"Channel:{channel}");
                var warningConfig = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
                if (warningConfig.Enabled && !(Recorder is ICycleEvidenceExporter))
                {
                    // 预警截图是诊断证据，不是电机、液压或电源的安全许可条件。
                    // 记录器能力不匹配时继续运行；后续请求仍会明确报告导出失败，不能反向阻断试验。
                    ReportSnapshotFailure(
                        "WarningSnapshots 已启用，但圈记录器不支持 ICycleEvidenceExporter；" +
                        "本次运行跳过预警证据导出，不阻碍重新开始。");
                }
                _emergencyPowerGroupLatch.Clear();
                _daqRecoveryAttemptsByDevice.Clear();
                _daqClockRecoveryAttempts.Clear();
                await EnsureDaqReadyBeforeStartAsync(selected, sessionToken).ConfigureAwait(false);
                BeginPowerSupplyTelemetryRecording(_activeBatchId);
                EnsureStrictCurveControl(selected);
                SaveProgramSafetySnapshot();
                await EnsurePowerSupplyReadyBeforeStartAsync(selected, sessionToken)
                    .ConfigureAwait(false);

                // 只有实时 DAQ、电源和程序安全预检全部通过，且进程从未发生 Stop 超时，
                // 才能为这个全新 Run 重新打开 DO 上电总闸。
                AuthorizeFreshRunAfterSafetyPreflight();

                // DAQ、电源及程序安全预检全部通过后，才允许旧停机锁存转为“启动中”。
                foreach (var channel in selected)
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Starting,
                        "Starting",
                        "安全预检通过，正在启动",
                        affectedChannels: selected,
                        correlationId: _activeBatchId,
                        allowTerminalReset: true);

                _activeStaggerPlan = staggerPlan;
                RegisterRunContext(_activeBatchId, staggerPlan);
                LogStaggerPlan(_activeBatchId, staggerPlan);

                // 新批次必须复位上一次运行留下的报警停机锁存。
                // 否则 IsAlarmStopRequested 会让后续成功圈也持续写成 status='alarm'，
                // 且重复报警会在 OnRunnerAlarmRaised 中被去重后直接返回。
                foreach (var channel in selected)
                {
                    _manualStopRequestedChannels.TryRemove(channel, out _);
                    _nonRecoverableChannelFaultLatch.TryRemove(channel, out _);
                    _alarmStopLatch.BeginRun(channel);
                }

                // —— 1) 按压力组归类，并为每组计算“锚点零相位” t0（含预热裕度 + 周期上取整）—— //
                var nowUtc = DateTime.UtcNow;
                var groups = GroupByPressure(selected); // Dictionary<int, List<int>>，键为 1/2
                var t0OfGroup = new Dictionary<int, DateTime>(); // key: PG(1/2), value: t0(UTC)

                foreach (var kv in groups)
                {
                    var pg = kv.Key;
                    var list = kv.Value;
                    if (list == null || list.Count == 0) continue;

                    // 预热裕度：避免首圈 k=0 时 delay ≤ 0 造成“零等待”扎堆
                    var warm = nowUtc.AddMilliseconds(AnchorWarmupMs);
                    // 上取整到下一个周期边界（使所有 pg 的 t0 对齐到统一节拍）
                    t0OfGroup[pg] = CeilToBoundary(warm, PeriodMs);
                }

                // —— 2) （可选）学习前启动定位：按电气组错峰，仅做一次 —— //
                if (learnCycles > 0 || reuseStableProfiles)
                {
                    var all = groups.Values.SelectMany(v => v).Distinct().OrderBy(x => x).ToArray();
                    _log?.Info(
                        $"批量启动定位：通道[{string.Join(",", all)}]，按XML电气组计划错峰。",
                        "EPB");

                    // keepMs=null → 由 Runner 内部使用 DefaultPreReleaseKeepMs
                    var preReleaseFailed = await PreReleaseBatchWithPlanAsync(
                            all,
                            /*keepMs*/ null,
                            staggerPlan,
                            sessionToken)
                        .ConfigureAwait(false);
                    if (preReleaseFailed.Length > 0)
                    {
                        foreach (var failedResult in preReleaseFailed)
                        {
                            var failedChannel = failedResult.Channel;
                            startFaults.Add(new ChannelStartFault(
                                failedChannel,
                                failedResult.Code,
                                $"启动定位失败：Stage={failedResult.Stage}，{failedResult.Reason}",
                                FaultScope.Channel));
                            PublishStartBlockedAfterCleanup(
                                failedChannel,
                                failedResult.Code,
                                failedResult.Reason,
                                _activeBatchId);
                            UnmarkHydraulicParticipant(failedChannel);
                            foreach (var list in groups.Values) list.Remove(failedChannel);
                            _log?.Error(
                                $"EPB[{failedChannel}] 启动定位失败，已隔离；健康通道继续启动。" +
                                $"Stage={failedResult.Stage} Code={failedResult.Code}。",
                                "EPB");
                        }
                    }
                }

                var activeChannels = groups.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToArray();
                if (activeChannels.Length == 0)
                    throw new InvalidOperationException("全部选中通道均在预释放阶段被隔离，未启动正式试验。");

                // —— 3) 学习阶段：次数不多，用“每圈循环 + 锚点屏障 + 相位延时”实现稳定对齐 —— //
                if (learnCycles > 0)
                {
                    foreach (var channel in activeChannels)
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Learning,
                            "Learning",
                            "正在执行自学习",
                            affectedChannels: activeChannels,
                            correlationId: _activeBatchId);
                    var learningFailed = await RunLearningPhaseAsync(
                            groups, t0OfGroup, learnCycles, staggerPlan, sessionToken)
                        .ConfigureAwait(false);
                    foreach (var failedChannel in learningFailed)
                    {
                        startFaults.Add(new ChannelStartFault(
                            failedChannel,
                            "Learning",
                            "自学习失败，已隔离通道。",
                            FaultScope.Channel));
                        PublishStartBlockedAfterCleanup(
                            failedChannel,
                            "LearningFailed",
                            "自学习失败，已隔离通道",
                            _activeBatchId);
                        UnmarkHydraulicParticipant(failedChannel);
                        foreach (var list in groups.Values) list.Remove(failedChannel);
                    }
                }

                if (reuseStableProfiles)
                {
                    activeChannels = groups.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToArray();
                    foreach (var channel in activeChannels)
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Qualification,
                            "CheckpointQualification",
                            $"正常暂停检查点恢复：执行{qualificationCycles}圈资格复核（不计正式目标）",
                            affectedChannels: activeChannels,
                            correlationId: _activeBatchId);
                    var qualificationFailed = await RunPausedQualificationAsync(
                                activeChannels,
                                qualificationCycles,
                                sessionToken)
                            .ConfigureAwait(false);
                    foreach (var failedChannel in qualificationFailed)
                    {
                        startFaults.Add(new ChannelStartFault(
                            failedChannel,
                            "Qualification",
                            "资格复核失败，已隔离通道。",
                            FaultScope.Channel));
                        PublishStartBlockedAfterCleanup(
                            failedChannel,
                            "QualificationFailed",
                            "资格复核失败，已隔离通道",
                            _activeBatchId);
                        UnmarkHydraulicParticipant(failedChannel);
                        foreach (var list in groups.Values) list.Remove(failedChannel);
                    }
                }

                activeChannels = groups.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToArray();
                if (activeChannels.Length == 0)
                    throw new InvalidOperationException("全部选中通道均在学习阶段被隔离，未启动正式试验。");

                var targetCompletedChannels = activeChannels
                    .Where(IsMechanicalTargetReached)
                    .OrderBy(channel => channel)
                    .ToArray();
                activeChannels = activeChannels
                    .Except(targetCompletedChannels)
                    .OrderBy(channel => channel)
                    .ToArray();

                // A learning/qualification attempt is a real mechanical circle.
                // If it consumes the final target circle, do not demand another
                // model/evidence retry and do not arm a formal timer.
                if (activeChannels.Length > 0)
                    EnsureAdaptiveProfilesReady(activeChannels);

                // Learning/qualification and the model commit are complete before
                // the formal timers are armed.  Publish this terminal execution
                // now; later alarm/stop events must not rewrite a successful
                // learning result.
                PublishLearningRunManifest(
                    startFaults.Count == 0 ? "Successful" : "Unknown",
                    startFaults.Count == 0 ? string.Empty : "OneOrMoreChannelsIsolated");
                if (_learningRetentionMode == LearningRetentionMode.Count)
                    _dataHousekeeping?.EnqueueNewManifestChains(
                        _learningSuccessfulRunRetainCount,
                        _learningFailedRunRetainCount);

                var resultRunId = _activeBatchId;
                foreach (var completedChannel in targetCompletedChannels)
                {
                    completedDuringStart.Add(completedChannel);
                    foreach (var list in groups.Values)
                        list.Remove(completedChannel);
                    FinalizeChannelAfterNaturalCompletion(
                        completedChannel,
                        Recorder?.GetLastCycleNumber(completedChannel) ?? 0);
                }

                if (activeChannels.Length == 0)
                {
                    _log?.Info(
                        $"批量启动的学习/资格阶段已消费全部剩余机械目标圈；" +
                        $"完成通道=[{string.Join(",", completedDuringStart)}]，不再启动正式圈。",
                        "EPB");
                    return new BatchStartResult(
                        resultRunId,
                        Array.Empty<int>(),
                        startFaults.ToArray(),
                        completedDuringStart.ToArray());
                }

                foreach (var channel in activeChannels)
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Running,
                        "Running",
                        "正式试验运行中",
                        affectedChannels: activeChannels,
                        correlationId: _activeBatchId);

                // —— 4) 正式阶段：为每个通道创建对齐到“锚点+相位”的高精计时器 —— //
                StartFormalPhaseTimers(groups, t0OfGroup, staggerPlan, sessionToken);
                MarkBatchRunning(activeChannels, "正式试验运行中");
                LogFieldSessionMetric("Start", _activeBatchId, activeChannels, false, "BatchFormal");
                LogDaqLivenessRunBinding(_activeBatchId);
                return new BatchStartResult(
                    resultRunId,
                    activeChannels,
                    startFaults.ToArray(),
                    completedDuringStart.ToArray());
            }
            catch (Exception ex)
            {
                var failedRunId = _activeBatchId == Guid.Empty ? Guid.NewGuid() : _activeBatchId;
                var circuitFailure = FindInnerException<SoftwareSelfHealingExhaustedException>(ex);
                var circuitOpen = circuitFailure != null;
                var expectedCancellation = IsExpectedBatchCancellation(
                    ex,
                    sessionToken.IsCancellationRequested,
                    token.IsCancellationRequested);
                if (expectedCancellation)
                    _log?.Info($"批量启动已取消：{ex.Message}", "EPB");
                else
                    _log?.Error($"批量启动异常：{ex}", "EPB", ex);
                if (_activeBatchId != Guid.Empty && Volatile.Read(ref _learningManifestPublished) == 0)
                    PublishLearningRunManifest(
                        // A failed/cancelled execution may still be recoverable
                        // through its RootRunId.  Keep it Active/Unknown until
                        // the recovery coordinator explicitly closes the chain;
                        // Unknown is never admitted to retention.
                        "Unknown",
                        (expectedCancellation ? "Cancelled" : "Failed") + ":" + ex.Message);
                EndBatchSession(cancel: true, terminalStatus: expectedCancellation ? "Cancelled" : "Failed", terminalReason: ex.Message);

                foreach (var channel in selected)
                {
                    try
                    {
                        if (expectedCancellation)
                        {
                            StopChannelForInternalCleanup(channel);
                            PublishChannelRuntimeState(
                                channel,
                                ChannelRuntimeState.ManualStopped,
                                "StartCanceled",
                                ex.Message,
                                affectedChannels: selected,
                                correlationId: failedRunId,
                                allowTerminalReset: true);
                        }
                        else
                        {
                            PublishStartBlockedAfterCleanup(
                                channel,
                                "StartFailed",
                                ex.Message,
                                failedRunId,
                                selected);
                        }
                    }
                    catch (Exception stopEx)
                    {
                        _log?.Warn($"批量启动异常后停止 EPB[{channel}] 失败：{stopEx}", "EPB");
                    }
                }

                try
                {
                    await StopAllAsync(
                            new StopContext
                            {
                                Source = circuitOpen
                                    ? StopSource.SystemFault
                                    : StopSource.StartupRollback,
                                Reason = ex.Message,
                                Initiator = nameof(StartBatchSynchronizedWithResultAsync),
                                CorrelationId = _activeBatchId == Guid.Empty
                                    ? Guid.NewGuid().ToString("N")
                                    : _activeBatchId.ToString("N"),
                                RequestedUtc = DateTime.UtcNow
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }

                if (circuitOpen)
                {
                    await ExportSoftwareRecoveryCircuitDiagnosticOnceAsync(
                            circuitFailure,
                            selected)
                        .ConfigureAwait(false);
                    var fault = new ControlFault(
                        "SoftwareRecoveryCircuitOpen",
                        ex.Message,
                        FaultScope.Global,
                        selected,
                        null,
                        DateTime.UtcNow,
                        Guid.NewGuid(),
                        FaultClassification.SystemFault,
                        FaultRecoveryPolicy.UnattendedBatchRecycle,
                        failedRunId);
                    NonCriticalObserver.Invoke(
                        SystemFaultRaised,
                        fault,
                        observerEx => _log?.Warn(
                            $"无人值守整批恢复观察者异常：{observerEx.Message}",
                            "EPB"));
                }

                throw;
            }
        }

        private async Task EnsureDaqReadyBeforeStartAsync(
            int[] selected,
            CancellationToken token)
        {
            if (_acq == null)
                throw new InvalidOperationException("DAQ采集器未初始化，拒绝启动试验。Code=DaqNotInitialized");

            foreach (var device in (selected ?? Array.Empty<int>())
                         .Select(_acq.GetDeviceForEpbChannel)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                _persistence.ResumeAdmission(device, _acq.GetLastAcceptedSequence(device));

            var attempt = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                attempt++;
                var results = await _acq.EnsureChannelsReadyAsync(
                        selected,
                        timeoutMs: 3000,
                        requiredFreshCallbacks: 3,
                        maxAgeMs: 100,
                        token: token)
                    .ConfigureAwait(false);
                var failed = results.Where(r => !r.Recovered).ToArray();
                if (failed.Length == 0)
                {
                    var devices = string.Join(",", results.Select(r => r.Device).Distinct());
                    _log?.Info(
                        $"DAQ启动健康检查通过：Devices=[{devices}] Attempt={attempt}。",
                        "AI");
                    return;
                }

                var unmapped = failed.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Device));
                if (unmapped != null)
                    throw new InvalidOperationException(
                        $"DAQ通道映射无效，无法自愈。Code=DaqChannelUnmapped；{unmapped.FailureReason}");

                foreach (var failure in failed)
                {
                    if (await ConfirmStartupDaqHardwareMissingAsync(failure, token)
                            .ConfigureAwait(false))
                        throw new InvalidOperationException(
                            $"DAQ设备物理缺失已由两次独立探测确认。Code=DaqHardwareConfirmed；" +
                            $"Device={failure.Device}；{failure.FailureReason}");
                }

                var details = string.Join("；", failed.Select(r =>
                    $"{r.Device}: {r.FailureReason}, Fresh={r.FreshCallbacks}/{r.RequiredFreshCallbacks}"));
                var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                foreach (var channel in selected ?? Array.Empty<int>())
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Recovering,
                        "DaqStartSelfHealing",
                        $"DAQ软件自愈第{attempt}次未通过，{delayMs}ms后继续重试；未使能电源与液压。",
                        affectedChannels: selected,
                        correlationId: _activeBatchId,
                        allowTerminalReset: false);
                _log?.Warn(
                    $"DAQ启动预检未通过，按软件瞬态有界自愈。" +
                    $"Attempt={attempt} DelayMs={delayMs}；{details}",
                    "AI");
                if (attempt >= 3)
                    throw new SoftwareSelfHealingExhaustedException(
                        "DaqStartPreflight",
                        attempt,
                        new InvalidOperationException(details));
                await Task.Delay(delayMs, token).ConfigureAwait(false);
            }
        }

        private async Task EnsureHydraulicCoordinatorHealthyBeforeStartAsync(
            int[] selected,
            CancellationToken token)
        {
            if (_hydCoordinator == null) return;
            var hydraulicIds = (_cfg.Test?.Hydraulics ?? new List<HydraulicItem>())
                .Where(item => item?.Enabled == true)
                .Select(item => item.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            foreach (var hydraulicId in hydraulicIds)
            {
                token.ThrowIfCancellationRequested();
                var snapshot = _hydCoordinator.ProbeGroupHealth(hydraulicId);
                if (!snapshot.IsHealthyForFreshStart)
                {
                    _log.Warn(
                        $"批次动作前发现液压组逻辑状态不健康，执行一次安全重建。{snapshot}",
                        "液压协调");
                    snapshot = await _hydCoordinator.RebuildGroupAsync(
                            hydraulicId,
                            "BatchStartPreflight",
                            timeoutMs: 10000,
                            token)
                        .ConfigureAwait(false);
                }
                if (!snapshot.IsHealthyForFreshStart)
                    throw new InvalidOperationException(
                        $"HydraulicCoordinatorStartBlocked {snapshot}; " +
                        $"Selected=[{string.Join(",", selected ?? Array.Empty<int>())}]");
            }
        }

        private async Task<bool> ConfirmStartupDaqHardwareMissingAsync(
            DaqRecoveryResult failure,
            CancellationToken token)
        {
            if (failure == null ||
                !string.Equals(failure.FailureKind, "DaqDeviceMissing", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(failure.Device))
                return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(3000);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                DaqHardwareProbeResult probe;
                try
                {
                    probe = await _daqHardwareProbe.ProbeAsync(failure.Device, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return false;
                }
                if (probe == null || !probe.IndependentFailureConfirmed) return false;
                if (attempt == 0)
                    await Task.Delay(500, timeout.Token).ConfigureAwait(false);
            }
            return true;
        }

        private async Task EnsurePowerSupplyReadyBeforeStartAsync(
            int[] selected,
            CancellationToken token)
        {
            var channels = (selected ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
            await InvokeAfterCycleExecutionQuiescenceAsync(
                    channels,
                    WaitForPreviousCycleExecutionAsync,
                    ct => EnsurePowerSupplyReadyBeforeStartCoreAsync(channels, ct),
                    "PowerReadyBeforeStart",
                    token)
                .ConfigureAwait(false);
        }

        private async Task EnsurePowerSupplyReadyBeforeStartCoreAsync(
            int[] channels,
            CancellationToken token)
        {
            if (_powerSupply == null) return;

            var groupIds = channels
                .Select(GetElectricalGroupId)
                .Where(groupId => groupId > 0)
                .Distinct()
                .OrderBy(groupId => groupId)
                .ToArray();
            var attempt = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    await _powerSupply.PrepareAndEnableAsync(channels, token).ConfigureAwait(false);
                    _log?.Info(
                        $"程控电源启动实时预检通过：Groups=[{string.Join(",", groupIds)}] " +
                        $"Attempt={attempt}。",
                        "程控电源");
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var protectionGroups = groupIds
                        .Where(groupId =>
                        {
                            var snapshot = _powerSupply.GetLatestSnapshot(groupId);
                            return snapshot?.ProtectionTripped == true &&
                                   (DateTime.UtcNow - snapshot.TimestampUtc.ToUniversalTime()) <=
                                   TimeSpan.FromSeconds(5);
                        })
                        .ToArray();
                    if (protectionGroups.Length > 0)
                        throw new InvalidOperationException(
                            $"程控电源实时回读确认保护已触发，停止本次启动。" +
                            $"Code=PowerProtectionHardwareConfirmed; Groups=[{string.Join(",", protectionGroups)}]",
                            ex);

                    var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                    foreach (var channel in channels)
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "PowerStartSelfHealing",
                            $"程控电源软件自愈第{attempt}次未通过，{delayMs}ms后继续完整重连预检。",
                            affectedChannels: channels,
                            correlationId: _activeBatchId,
                            allowTerminalReset: false);
                    _log?.Warn(
                        $"程控电源启动预检未通过，无新鲜保护触发证据，按软件瞬态有界自愈。" +
                        $"Attempt={attempt} DelayMs={delayMs} Error={ex.Message}",
                        "程控电源");
                    if (attempt >= 3)
                        throw new SoftwareSelfHealingExhaustedException(
                            "PowerStartPreflight",
                            attempt,
                            ex);
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
            }
        }

        internal static bool IsExpectedBatchCancellation(
            Exception exception,
            bool sessionCancellationRequested,
            bool externalCancellationRequested)
        {
            return exception is OperationCanceledException &&
                   (sessionCancellationRequested || externalCancellationRequested);
        }

        private static TException FindInnerException<TException>(Exception exception)
            where TException : Exception
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is TException match) return match;
            }

            return null;
        }

        private async Task ExportSoftwareRecoveryCircuitDiagnosticOnceAsync(
            SoftwareSelfHealingExhaustedException failure,
            int[] affectedChannels)
        {
            if (failure == null) return;
            var channels = (affectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var fingerprint = $"{failure.Stage}|{failure.InnerException?.GetType().FullName}|" +
                              $"{failure.InnerException?.Message}|{string.Join(",", channels)}";
            if (!_softwareRecoveryCircuitDiagnostics.TryAdd(fingerprint, 0)) return;

            var channel = channels.FirstOrDefault();
            if (channel <= 0) return;
            try
            {
                await ExportAlarmSnapshotAsync(
                        channel,
                        "SoftwareRecoveryCircuitOpen " + failure.Message,
                        DateTime.UtcNow)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Warn(
                    $"软件恢复熔断完整诊断快照导出失败（本指纹不重复导出）：{ex.Message}",
                    "落盘");
            }
        }

        private CancellationToken BeginBatchSession(CancellationToken externalToken)
        {
            if (Interlocked.CompareExchange(ref _batchSessionActive, 1, 0) != 0)
            {
                const string message = "已有批量试验正在启动或运行，请勿重复点击“开始试验”。";
                _log?.Warn(message, "EPB");
                throw new InvalidOperationException(message);
            }

            try
            {
                _softwareRecoveryEscalation.Reset();
                _isolatedInfrastructureRecoveryAttempts.Clear();
                _daqRecoveryBatchBarriers.Clear();
                _daqRecoveryBatchAliases.Clear();
                _daqLivenessLatchedGeneration.Clear();
                _daqLivenessObservedGapEvents.Clear();
                _daqLivenessLogTransitions.BeginSession(
                    Guid.Empty,
                    Interlocked.Read(ref _runEpoch) + 1);
                Interlocked.Increment(ref _runEpoch);
                Interlocked.Exchange(ref _formalPhaseCommitted, 0);
                var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
                var previous = Interlocked.Exchange(ref _batchSessionCts, linked);
                previous?.Dispose();
                return linked.Token;
            }
            catch
            {
                Interlocked.Exchange(ref _batchSessionActive, 0);
                throw;
            }
        }

        private void EndBatchSession(bool cancel, bool publishIdleState = true,
            string terminalStatus = null, string terminalReason = null)
        {
            // Learning terminal state is published immediately before formal
            // timers are armed.  Do not rewrite it when a later formal run stops.
            var cts = Interlocked.Exchange(ref _batchSessionCts, null);
            if (cts != null)
            {
                if (cancel)
                {
                    try { cts.Cancel(); }
                    catch { /* 停止路径不得因取消异常中断 */ }
                }

                try { cts.Dispose(); }
                catch { /* ignore */ }
            }

            Interlocked.Exchange(ref _batchSessionActive, 0);
            Interlocked.Exchange(ref _formalPhaseCommitted, 0);
            _activeStaggerPlan = null;
            _activeFormalT0ByPressureGroup.Clear();
            _activeBatchId = Guid.Empty;
            _activeRunChainIdentity = null;
            _activePlannedChannels = Array.Empty<int>();
            _activePlannedLearningCycles = 0;
            _activePlannedQualificationCycles = 0;
            if (publishIdleState)
                MarkBatchIdle(cancel ? "批次已取消" : "批次已结束");
        }

        private void LogStaggerPlan(Guid batchId, ElectricalStaggerPlan plan)
        {
            _log?.Info(
                $"EPB错峰计划 Batch={batchId:N} Period={plan.PeriodMs}ms Created={plan.CreatedUtc:O}",
                "EPB");

            foreach (var group in plan.Assignments.Values
                         .GroupBy(x => x.ElectricalGroupId)
                         .OrderBy(x => x.Key))
            {
                var assignments = string.Join(
                    ", ",
                    group.OrderBy(x => x.SelectedIndexInGroup)
                        .Select(x =>
                            $"EPB{x.Channel}(index={x.SelectedIndexInGroup},phase={x.PhaseMs}ms)"));
                _log?.Info(
                    $"Group{group.Key} Stagger={group.First().StaggerMs}ms: {assignments}",
                    "EPB");
            }
        }

        internal static bool AreSessionRuntimesIdle(
            int activeTimers,
            int cachedTimers,
            int activeRunners,
            int cachedRunners)
        {
            return activeTimers <= 0 && cachedTimers <= 0 &&
                   activeRunners <= 0 && cachedRunners <= 0;
        }

        private void TryEndBatchSessionWhenIdle(string terminalPhase = "Complete")
        {
            if (!AreSessionRuntimesIdle(
                    _timers.Count,
                    _timerCache.Count,
                    _runners.Count,
                    _runnerCache.Count))
                return;

            var runId = _activeBatchId;
            if (runId == Guid.Empty) return;
            if (Interlocked.CompareExchange(ref _idleSessionClosureScheduled, 1, 0) != 0) return;

            var source = string.Equals(terminalPhase, "Complete", StringComparison.OrdinalIgnoreCase)
                ? StopSource.TargetCompleted
                : string.Equals(terminalPhase, "AlarmStop", StringComparison.OrdinalIgnoreCase)
                    ? StopSource.AlarmInterlock
                    : StopSource.ManualUi;
            var context = new StopContext
            {
                Source = source,
                Reason = $"最后运行通道已进入终态，执行统一安全与耐久收尾。Phase={terminalPhase}",
                Initiator = nameof(TryEndBatchSessionWhenIdle),
                CorrelationId = runId.ToString("N"),
                RequestedUtc = DateTime.UtcNow
            };
            ObserveBackgroundTask(
                FinalizeIdleSessionAsync(runId, context),
                "FinalizeIdleSession");
        }

        private async Task FinalizeIdleSessionAsync(Guid runId, StopContext context)
        {
            try
            {
                await StopAllAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // StopAll 正常路径会清除活动 RunId。若异常提前退出且本次 Run 仍然活动，
                // 允许后续终态通知重新登记收尾，不能永久卡死在“已安排”假状态。
                if (_activeBatchId == runId)
                    Interlocked.Exchange(ref _idleSessionClosureScheduled, 0);
            }
        }

        #endregion

        #region 正式阶段（12 个计时器锁相到锚点 + 固定相位）

        /// <summary>
        ///     为每个通道创建并启动 HighPrecisionTimer，使其每圈在 t0 + k*Period + phase 触发。
        ///     计时器采用 AlignToWallClock，保证“每圈都与墙钟对齐（不累积误差）”。
        /// </summary>
        private void StartFormalPhaseTimers(
            Dictionary<int, List<int>> groups,
            Dictionary<int, DateTime> t0OfGroup,
            ElectricalStaggerPlan staggerPlan,
            CancellationToken token)
        {
            foreach (var kv in groups)
            {
                var pg = kv.Key;
                var list = kv.Value;

                if (list.Count == 0) continue;

                var t0 = t0OfGroup[pg];
                _activeFormalT0ByPressureGroup[pg] = t0;
                var enabled = list.OrderBy(x => x).ToList();

                // 正式阶段可能恰好在一个周期的 0ms 相位之后、800ms 相位之前启动。
                // 若按每个通道自己的 phase 计算首延迟，0ms 通道会滚到下一槽，而
                // 800ms 通道仍进入当前槽，同一液压代次便会永久缺员并在 BarrierTimeout
                // 后误报硬故障。整组必须从同一个未来零相位开始，电气错峰只在液压
                // 资格完成后的共享 phaseWindow 内执行。
                var scheduleCreatedUtc = DateTime.UtcNow;
                var firstFormalSlot = CalculateFirstFutureFormalSlot(
                    t0,
                    scheduleCreatedUtc,
                    PeriodMs);
                var firstGroupCallbackUtc = t0.AddMilliseconds(firstFormalSlot * (double)PeriodMs);
                _log?.Info(
                    $"正式阶段液压槽已统一：Run={_activeBatchId:N} Hydraulic={pg} " +
                    $"FirstSlot={firstFormalSlot} CallbackUtc={firstGroupCallbackUtc:O} " +
                    $"Members=[{string.Join(",", enabled)}]",
                    "液压协调");

                foreach (var ch in enabled)
                {
                    var phase = staggerPlan.Get(ch).PhaseMs;
                    // 所有成员在同一零相位回调，避免因启动时刻落在两个电气相位之间
                    // 而被拆进相邻液压槽。使用固定 UTC 目标抵消逐通道创建计时器的耗时。
                    var initialDelay = Math.Max(
                        1,
                        (int)Math.Ceiling((firstGroupCallbackUtc - DateTime.UtcNow).TotalMilliseconds));

                    var runner = GetRunner(ch);
                    PrepareRunnerForNoHeadAndTailCompensation(ch);

                    // 标记为“参与液压判定”：本批次运行中将用于过滤建压锚点的 InFlight 登记
                    MarkHydraulicParticipant(ch);

                    // 本次启动为该通道刷新“硬停机”取消源
                    var stopCts = RenewStopCts(ch);

                    var timer = GetTimer(ch, PeriodMs, OverrunPolicy.AlignToWallClock);

                    // 每个通道单独算一个“起始圈号基准”
                    var last = Recorder?.GetLastCycleNumber(ch);
                    var baseCycle = last ?? 0;   // 这次试验第1圈就是 baseCycle + 1

                    // 学习/资格以及证据作废但已完整释放的圈都消耗耐久目标。
                    // 必须在学习结束后重新读取进程内机械事实，不能沿用开始按钮点击前
                    // 按正式证据圈计算的旧剩余值。
                    var runs = GetRemainingMechanicalTargetCycles(ch);
                    EpbTestCycle[ch] = runs;
                    if (runs <= 0)
                    {
                        FinalizeChannelAfterNaturalCompletion(
                            ch,
                            Recorder?.GetLastCycleNumber(ch) ?? 0);
                        continue;
                    }
                    var successfulCycles = 0;
                    
                    // —— 计时器每圈工作（cycleIndex 从 1 开始） —— //
                    ObserveBackgroundTask(timer.StartAsync(
                        repeat: null, // 由成功圈计数停止；可恢复失败尝试不消耗目标圈数
                        initialDelay,
                        async (cycleIndex, ct) =>
                        {
                            var cyclePauseCts = RenewCyclePauseCts(ch);
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                                ct,
                                stopCts.Token,
                                cyclePauseCts.Token);
                            var token = linked.Token;
                            // cycleIndex 是本计时器的逻辑圈序号；所有组员使用共同首槽，
                            // 因而即使实际回调有毫秒级抖动，也不会在周期边界两侧分槽。
                            var phaseSlot = firstFormalSlot + cycleIndex - 1L;

                            if (!await WaitForPreviousCycleExecutionAsync(ch, token)
                                    .ConfigureAwait(false))
                            {
                                ReleaseCyclePauseCts(ch, cyclePauseCts);
                                return false;
                            }
                            await WaitForDaqRecoveryAsync(ch, token).ConfigureAwait(false);
                            await EnsurePowerSupplyReadyForChannelsAsync(new[] { ch }, token)
                                .ConfigureAwait(false);

                            // 1) 在本圈锚点时刻为该压力组建压：
                            //    对本组所有参与通道调用 EnterElectricalPhaseAsync，
                            //    这样 HydraulicGroupCoordinator 能正确维护 InFlight 集合。
                            var participants = GetHydraulicParticipantsInPressureGroupSnapshot(
                                pg,
                                enabled,
                                phaseSlot);
                            var hydraulicKey = new HydraulicGenerationKey(
                                _activeBatchId,
                                pg,
                                HydraulicPhaseKind.Formal,
                                phaseSlot);
                            var lease = await HydraulicEnterAtGroupAnchorAsync(
                                    hydraulicKey,
                                    participants,
                                    token)
                                .ConfigureAwait(false);

                            // 液压资格完成后，所有等待同一代次的通道使用同一个未来锚点，
                            // 再叠加各自电气相位。不能让过期的0/800ms相位同时补发。
                            var maxPhaseMs = participants.Count == 0
                                ? phase
                                : participants.Max(member => staggerPlan.Get(member).PhaseMs);
                            var phaseWindow = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                                lease?.ActuationAnchorUtc ?? DateTime.UtcNow.AddMilliseconds(2),
                                t0,
                                PeriodMs,
                                maxPhaseMs);
                            var plannedStartUtc = phaseWindow.GetDueUtc(phase);
                            var delay = plannedStartUtc - DateTime.UtcNow;
                            if (delay.TotalMilliseconds > 1)
                                await Task.Delay(delay, token).ConfigureAwait(false);
                            else
                            {
                                token.ThrowIfCancellationRequested();
                                await Task.Yield();
                            }

                            token.ThrowIfCancellationRequested();
                            if (IsAlarmStopRequested(ch)) return false;

                            var actualStartUtc = DateTime.UtcNow;
                            MarkElectricalPhaseDue(ch, plannedStartUtc);
                            _log?.Info(
                                $"正式阶段启动 Run={_activeBatchId:N} EPB={ch} Group={staggerPlan.Get(ch).ElectricalGroupId} " +
                                $"Cycle={cycleIndex} Phase={phase}ms PlannedUtc={plannedStartUtc:O} " +
                                $"ActualUtc={actualStartUtc:O} DeviationMs={(actualStartUtc - plannedStartUtc).TotalMilliseconds:F3} " +
                                $"HydraulicQualifiedUtc={lease?.Qualification?.ReachedUtc:O}",
                                "EPB");

                            // 本压力组按最后一个相位选择统一墙钟截止点；资格过晚时整组共同顺延。
                            var deadlineUtc = phaseWindow.DeadlineUtc;

                            // 2.5) ★ 圈开始：通知 Recorder
                            var cycleNumber = cycleIndex + baseCycle;
                            var recorder = Recorder;
                            if (!TryBeginFormalCycleAttempt(
                                    recorder,
                                    ch,
                                    cycleNumber,
                                    DateTime.UtcNow,
                                    _activeBatchId,
                                    CycleAttemptKind.FormalBatch,
                                    token,
                                    out var cycleAttempt))
                            {
                                await AbortHydraulicLeaseForChannelAsync(
                                        ch,
                                        "FormalCyclePersistenceBoundaryRejected")
                                    .ConfigureAwait(false);
                                ReleaseCyclePauseCts(ch, cyclePauseCts);
                                return false;
                            }

                            if (!cycleAttempt.MarkExecutionStarted())
                            {
                                if (!cycleAttempt.IsExecutionStarted)
                                    CompleteCycleAttemptExecution(cycleAttempt);
                                await AbortHydraulicLeaseForChannelAsync(
                                        ch,
                                        "FormalCycleExecutionOwnershipRejected")
                                    .ConfigureAwait(false);
                                ReleaseCyclePauseCts(ch, cyclePauseCts);
                                return false;
                            }
                            using var executionScope =
                                CompleteCycleAttemptExecutionOnCallbackExit(cycleAttempt);

                            // 3) 跑一圈（对齐外壳版）
                            var ok = false;
                            Adaptive.EpbCycleOutcome cycleOutcome;
                            try
                            {
                                ok = await runner.RunOneAlignedAsync(
                                    PeriodMs,
                                    T8BaseMs,
                                    phase,
                                    T8MinMs,
                                    deadlineUtc,
                                    cycleAttempt.AttemptCts.Token
                                ).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                ok = false;
                            }
                            catch
                            {
                                ok = false;
                            }
                            finally
                            {
                                // 必须在释放 execution tombstone 前取得本圈不可变引用；
                                // 下一圈获准复用 Runner 后会替换 LastCycleOutcome。
                                cycleOutcome = runner.LastCycleOutcome;
                                if (_hydraulicLeaseByChannel.TryGetValue(ch, out var activeScope) &&
                                    !activeScope.IsClosed)
                                    await AbortHydraulicLeaseForChannelAsync(
                                            ch,
                                            "FormalCycleAttemptFinalizer")
                                        .ConfigureAwait(false);
                            }
                            var controlSucceeded = IsFormalControlSucceeded(
                                ok,
                                cycleOutcome.IsSuccess);
                            if (controlSucceeded)
                                _watchdogConsecutiveSoftwareAborts[ch] = 0;
                            var mechanicalTargetReached = false;
                            if (cycleOutcome.MechanicalCycleCompleted)
                            {
                                OnMechanicalCycleCompleted(
                                    ch,
                                    CycleAttemptKind.FormalBatch,
                                    cycleNumber);
                                mechanicalTargetReached = IsMechanicalTargetReached(ch);
                            }
                            var controlNeedsSoftwareRecovery =
                                cycleOutcome.Kind ==
                                Adaptive.EpbCycleOutcomeKind.SoftwareRecovery;
                            if (controlNeedsSoftwareRecovery)
                            {
                                RecordWatchdogSoftwareAbort(ch);
                                ReportFormalControlSoftwareRecovery(
                                    ch,
                                    cycleNumber,
                                    cycleOutcome.Reason);
                            }

                            if (!ok)
                            {
                                var failed = cycleOutcome;
                                _log?.Warn(
                                    $"EPB[{ch}] 周期 {cycleNumber} 返回失败：" +
                                    $"Kind={failed.Kind} Stage={failed.Stage} Reason={failed.Reason} " +
                                    $"Peak={failed.PeakCurrentA:F3}A Target={failed.TargetCurrentA:F3}A " +
                                    $"Error={failed.PeakErrorA:+0.000;-0.000;0.000}A",
                                    "EPB");
                            }

                            // 4) ★ 圈结束：根据是否报警停机决定封圈状态
                            var persistenceCommitted = false;
                            try
                            {
                                if (TryConsumeDaqClockCycleAbort(
                                        cycleAttempt.RunId,
                                        cycleAttempt.RunEpoch,
                                        ch,
                                        cycleNumber))
                                {
                                    AbortFormalCycleAttempt(
                                        cycleAttempt,
                                        recorder,
                                        DateTime.UtcNow,
                                        "AbortedBySoftwareRecovery");
                                    _log?.Warn(
                                        $"EPB[{ch}] 周期 {cycleNumber} 已由DAQ时钟恢复流程封存，" +
                                        "跳过周期尾重复记账。",
                                        "落盘");
                                    goto CyclePersistenceFinished;
                                }
                                var finalN = recorder?.GetCurrentCycleSampleCount(ch) ?? 0;
                                if (IsAlarmStopRequested(ch))
                                {
                                    // 报警后台流程会在确认当前圈 CSV/BIN 快照存在后封为 alarm；
                                    // 若快照失败则封为 failed。这里保持活动身份，避免先写无文件的 alarm。
                                }
                                else if (controlNeedsSoftwareRecovery)
                                    AbortFormalCycleAttempt(
                                        cycleAttempt,
                                        recorder,
                                        DateTime.UtcNow,
                                        "AbortedBySoftwareRecovery");
                                else if (cycleOutcome.Kind == Adaptive.EpbCycleOutcomeKind.HardFault)
                                    AbortFormalCycleAttempt(
                                        cycleAttempt,
                                        recorder,
                                        DateTime.UtcNow,
                                        "failed");
                                else if (controlSucceeded)
                                    persistenceCommitted = CompleteFormalCycleAttempt(
                                        cycleAttempt,
                                        recorder,
                                        finalN,
                                        DateTime.UtcNow);
                                else
                                    AbortFormalCycleAttempt(
                                        cycleAttempt,
                                        recorder,
                                        DateTime.UtcNow,
                                        cycleOutcome.Kind == Adaptive.EpbCycleOutcomeKind.Canceled
                                            ? "canceled"
                                            : "failed");
                            }
                            catch (Exception ex)
                            {
                                PreserveFormalCycleForPersistenceRecovery(
                                    ch,
                                    cycleNumber,
                                    "CycleFinalizer",
                                    ex);
                            }

                        CyclePersistenceFinished:
                            // 若该通道自然完成最后一圈：统一收尾（含“停止即存最近10圈”），
                            // 并从运行集合中移除，避免影响其它仍在运行通道的逻辑。
                            if (IsFormalCycleCountable(
                                    controlSucceeded,
                                    persistenceCommitted))
                            {
                                var committedCycles = Interlocked.Increment(ref successfulCycles);
                                var nonRecoverableAlarm =
                                    OnFormalCycleCommittedAndEvaluateClampFault(
                                        runner,
                                        ch,
                                        cycleNumber,
                                        committedCycles,
                                        phaseSlot);
                                if (!nonRecoverableAlarm && mechanicalTargetReached)
                                {
                                    FinalizeChannelAfterNaturalCompletion(ch, cycleNumber);
                                    timer.Stop();
                                }
                            }
                            else if (mechanicalTargetReached && !IsAlarmStopRequested(ch))
                            {
                                // 控制证据可能因软件恢复而作废，但夹紧+释放已经真实发生；
                                // 达到耐久目标后不得为追求正式证据再额外磨损一卡钳一圈。
                                FinalizeChannelAfterNaturalCompletion(ch, cycleNumber);
                                timer.Stop();
                            }

                            ReleaseCyclePauseCts(ch, cyclePauseCts);
                            return controlSucceeded && persistenceCommitted;

                        }), "BatchChannelTimer", ch);
                }
            }
        }

        internal static long CalculateFirstFutureFormalSlot(
            DateTime t0Utc,
            DateTime nowUtc,
            int periodMs)
        {
            var period = Math.Max(1, periodMs);
            var elapsedMs = (nowUtc - t0Utc).TotalMilliseconds;
            if (elapsedMs < 0)
                return 0;

            // 已到或越过零相位时一律选择下一完整槽；不能让较晚电气相位留在
            // 当前槽，否则组员会跨槽。浮点边界由最终 UTC 复查兜底。
            var slot = (long)Math.Floor(elapsedMs / period) + 1L;
            while (t0Utc.AddMilliseconds(slot * (double)period) <= nowUtc)
                slot++;
            return slot;
        }

        /// <summary>
        ///     从指定压力组的候选通道中，筛选出“当前仍参与液压判定”的通道快照。
        /// </summary>
        /// <param name="pressureGroupId">压力组编号：1 表示 1..6，2 表示 7..12。</param>
        /// <param name="candidateChannels">
        ///     候选通道列表（通常是本批次启动时该压力组的 enabled 通道集合）。
        /// </param>
        /// <returns>
        ///     当前快照下仍参与该压力组液压判定的通道列表。
        ///     若全部已停止/结束，则返回空列表（此时不会触发建压登记）。
        /// </returns>
        /// <remarks>
        ///     业务背景：
        ///     <list type="bullet">
        ///         <item>报警停机的通道必须被排除，否则会被重复登记进 InFlight 并阻塞释压；</item>
        ///         <item>同批次中各通道圈数可能不同，提前结束的通道同样必须排除；</item>
        ///         <item>该方法只做快照过滤，不做任何 IO，线程安全。</item>
        ///     </list>
        /// </remarks>
        private IReadOnlyList<int> GetHydraulicParticipantsInPressureGroupSnapshot(
            int pressureGroupId,
            IReadOnlyList<int> candidateChannels,
            long formalSlot = -1)
        {
            if (candidateChannels == null || candidateChannels.Count == 0)
                return Array.Empty<int>();

            var list = new List<int>(candidateChannels.Count);
            for (var i = 0; i < candidateChannels.Count; i++)
            {
                var ch = candidateChannels[i];
                if (pressureGroupId == 1)
                {
                    if (ch < 1 || ch > 6) continue;
                }
                else if (pressureGroupId == 2)
                {
                    if (ch < 7 || ch > 12) continue;
                }
                else
                {
                    continue;
                }

                if (!IsHydraulicParticipant(ch)) continue;
                if (formalSlot >= 0 &&
                    _firstEligibleFormalSlotByChannel.TryGetValue(ch, out var firstEligibleSlot) &&
                    !FormalSlotEligibility.IsEligible(firstEligibleSlot, formalSlot))
                    continue;
                list.Add(ch);
            }

            return list;
        }

        #endregion


        #region 学习阶段（循环+延时：轻量且每圈对齐）

        /// <summary>
        ///     学习阶段外壳：并发“圈 × 组”，同组内在液压资格完成后的共享窗口中按固定相位（0/Δ/2Δ）错峰起跑。<br />
        ///     关键增强：
        ///     <list type="number">
        ///         <item>对每个“圈 × 组”先创建 <c>HydraulicEnterAtGroupAnchorAsync</c> 任务作为屏障；</item>
        ///         <item>液压资格完成后整组共享执行锚点，避免不同相位被拆到相邻周期；</item>
        ///         <item>确保首圈也不会出现负延时导致的“同刻上电”。</item>
        ///         <item>【新增】在学习阶段的首尾对 <c>SafetyMargin</c> 做“开始聚合/收敛落地”。</item>
        ///     </list>
        /// </summary>
        /// <param name="groups">按压力组分组的通道集合（key: 1/2）。</param>
        /// <param name="t0OfGroup">
        ///     各压力组的零相位锚点（UTC）。建议由 <c>CeilToBoundary(DateTime.UtcNow.AddMilliseconds(AnchorWarmupMs), PeriodMs)</c> 生成，
        ///     以便给首圈留下预热裕度。
        /// </param>
        /// <param name="learnCycles">学习圈数（&gt;0）。</param>
        /// <param name="token">取消令牌。</param>
        private async Task<int[]> RunLearningPhaseAsync(
            Dictionary<int, List<int>> groups,
            Dictionary<int, DateTime> t0OfGroup,
            int learnCycles,
            ElectricalStaggerPlan staggerPlan,
            CancellationToken token)
        {
            // —— 保护：无任务直接返回 —— //
            if (groups == null || groups.Count == 0 || learnCycles <= 0)
                return Array.Empty<int>();

            // 组级/通道级故障只取消其各自 stop token。学习阶段不得再有一个共享
            // “任一故障取消整批”的令牌，否则健康电源组也会被启动回滚停止。
            var phaseToken = token;
            var stopCtsByChannel = new Dictionary<int, CancellationTokenSource>();
            var learningRunId = _activeBatchId;
            var learningEvidence = CaptureLearningEvidenceContext(learningRunId);
            var quarantined = new ConcurrentDictionary<int, string>();

            // —— 0) 让所有 Runner 进入“无① + ⑧外壳收尾（学习不等尾）”模式，并开启聚合 —— //
            foreach (var list in groups.Values)
            {
                var enabled = list == null ? null : list.OrderBy(x => x).ToList();
                if (enabled == null || enabled.Count == 0) continue;

                for (var i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    var r = GetRunner(ch);
                    MarkHydraulicParticipant(ch);
                    stopCtsByChannel[ch] = RenewStopCts(ch);

                    r.UseNoHeadPhase = true; // 学习不做①，错峰由外层“相位”承担
                    r.EnableTailCompensation = true; // ⑧尾部由外壳统一对齐（学习单圈不等待）
                    r.TailMinMs = T8MinMs;

                    if (GetEpbControlMode(ch) == Adaptive.EpbControlMode.AdaptiveCurrent)
                    {
                        _log?.Info(
                            $"EPB[{ch}] 自适应模型学习开始：学习圈直接使用电流状态机和硬保护。",
                            "EPB");
                    }
                    else
                    {
                        // 旧模式继续学习时间参数和 SafetyMargin。
                        r.BeginLearnAggregation();
                        r.BeginSafetyMarginLearning();
                        _log?.Info($"EPB[{ch}] SafetyMargin 学习开始！", "EPB");
                    }
                }
            }

            // —— 1) 多圈学习 —— //
            for (var k = 0; k < learnCycles; k++)
            {
                phaseToken.ThrowIfCancellationRequested();
                var tasksAllGroups = new List<Task>();

                foreach (var kv in groups)
                {
                    var pg = kv.Key; // 压力组 ID：1/2
                    var list = kv.Value;
                    if (list == null || list.Count == 0) continue;

                    // 本圈该压力组的锚点时刻
                    var t0 = t0OfGroup[pg];
                    var tk = t0.AddMilliseconds(k * PeriodMs);

                    // —— 1.2) 组内通道：液压资格后的共享窗口 + 相位错峰（0/Δ/2Δ） —— //
                    var enabled = list
                        .Where(ch => !quarantined.ContainsKey(ch))
                        .Where(ch => !IsMechanicalTargetReached(ch))
                        .OrderBy(x => x)
                        .ToList();
                    if (enabled.Count == 0) continue;

                    // —— 1.1) 组锚点任务（屏障） —— //
                    var hydraulicKey = new HydraulicGenerationKey(
                        learningRunId,
                        pg,
                        HydraulicPhaseKind.Learning,
                        k + 1L);
                    var anchorTask = EnterHydraulicStartupPhaseWithSelfHealingAsync(
                        hydraulicKey,
                        enabled,
                        phaseToken);
                    var maxPhaseMs = enabled.Max(member => staggerPlan.Get(member).PhaseMs);

                    for (var i = 0; i < enabled.Count; i++)
                    {
                        var ch = enabled[i];
                        var phase = staggerPlan.Get(ch).PhaseMs;
                        var stopCts = stopCtsByChannel[ch];

                        tasksAllGroups.Add(Task.Run(async () =>
                        {
                            using var channelLinkedCts =
                                CancellationTokenSource.CreateLinkedTokenSource(
                                    phaseToken,
                                    stopCts.Token);
                            var channelToken = channelLinkedCts.Token;

                            await FaultIsolatedPhaseWork.RunAsync(
                                async () =>
                                {
                                    // ① 等待液压锚点到位（屏障：确保本组已经建压 + 所有通道已登记 InFlight）。
                                    // 锚点异常也必须在本组/本通道内收口，不能越过 WhenAll 触发整批启动回滚。
                                    var lease = await anchorTask.ConfigureAwait(false);
                                    channelToken.ThrowIfCancellationRequested();
                                    try
                                    {

                                    // ② 液压资格完成后整组共享同一执行窗口。
                                    // 禁止各通道按自己的原始相位独立滚动，否则资格时刻恰好落在
                                    // 0ms 与 800ms 相位之间时，会把同代次成员拆到相邻两个周期。
                                    var phaseWindow = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                                        lease?.ActuationAnchorUtc ?? DateTime.UtcNow.AddMilliseconds(2),
                                        tk,
                                        PeriodMs,
                                        maxPhaseMs);
                                    var atFuture = phaseWindow.GetDueUtc(phase);
                                    var now = DateTime.UtcNow;

                                    var delay = atFuture - now;
                                    _log?.Info(
                                        $"通道{ch}: tk={tk:HH:mm:ss.fff}, phase={phase}ms, qualified-at={atFuture:HH:mm:ss.fff}, delay={delay.TotalMilliseconds}ms");

                                    var ms = (int)Math.Floor(delay.TotalMilliseconds);
                                    if (ms > 0)
                                        await Task.Delay(ms, channelToken).ConfigureAwait(false);
                                    else
                                        await Task.Yield();

                                    var actualStartUtc = DateTime.UtcNow;
                                    MarkElectricalPhaseDue(ch, atFuture);
                                    _log?.Info(
                                        $"学习阶段启动 Run={learningRunId:N} EPB={ch} Group={staggerPlan.Get(ch).ElectricalGroupId} " +
                                        $"LearnCycle={k + 1} Phase={phase}ms PlannedUtc={atFuture:O} " +
                                        $"ActualUtc={actualStartUtc:O} DeviationMs={(actualStartUtc - atFuture).TotalMilliseconds:F3}",
                                        "EPB");

                                    await RunLearningLogicalCycleWithSelfHealingAsync(
                                            GetRunner(ch),
                                            ch,
                                            pg,
                                            phase,
                                            k + 1,
                                            learningRunId,
                                            learningEvidence,
                                            channelToken)
                                        .ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        if (_hydraulicLeaseByChannel.TryGetValue(ch, out var phaseScope) &&
                                            !phaseScope.IsClosed)
                                            await AbortHydraulicLeaseForChannelAsync(
                                                    ch,
                                                    "LearningPhaseWorkFinalizer")
                                                .ConfigureAwait(false);
                                    }
                                },
                                phaseToken,
                                (ex, channelCanceled) =>
                                {
                                    quarantined[ch] = channelCanceled ? "ChannelCanceled" : ex.Message;
                                    if (channelCanceled) return;
                                    UnmarkHydraulicParticipant(ch);
                                    try { CommandEpbOff(ch, "LearningChannelIsolation"); } catch { }
                                    _log?.Error(
                                        $"EPB[{ch}] 学习失败已按通道隔离，其他健康通道继续。原因={ex.Message}",
                                        "EPB",
                                        ex);
                                }).ConfigureAwait(false);
                        }, phaseToken));
                    }
                }

                // 本圈所有任务结束后进入下一圈
                await Task.WhenAll(tasksAllGroups).ConfigureAwait(false);
            }

            // —— 2) 学习聚合结束：写回中位数/统计量 —— //
            foreach (var list in groups.Values)
            {
                var enabled = list == null ? null : list.OrderBy(x => x).ToList();
                if (enabled == null || enabled.Count == 0) continue;

                for (var i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    if (quarantined.ContainsKey(ch)) continue;
                    var r = GetRunner(ch);

                    if (GetEpbControlMode(ch) == Adaptive.EpbControlMode.AdaptiveCurrent)
                    {
                        _log?.Info($"EPB[{ch}] 自适应模型学习阶段结束。", "EPB");
                    }
                    else
                    {
                        r.FinalizeLearnAggregation();
                        r.FinalizeSafetyMarginLearning();
                    }
                }
            }

            return quarantined.Keys.OrderBy(x => x).ToArray();
        }

        private async Task RunLearningLogicalCycleWithSelfHealingAsync(
            IEpbCycleRunner runner,
            int channel,
            int pressureGroup,
            int phaseMs,
            int learningOrdinal,
            Guid runId,
            LearningEvidenceContext learningEvidence,
            CancellationToken token)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            var modelBeforeLogicalCycle = runner.CaptureAdaptiveProfile();
            var learningCycleNumber = 0;
            BeginLearningProfileTransaction(channel);

            int attempts;
            try
            {
                attempts = await SoftwareSelfHealingLoop.RunAsync(
                    async (attempt, attemptToken) =>
                    {
                        if (IsMechanicalTargetReached(channel))
                        {
                            _log?.Info(
                                $"EPB[{channel}] 学习重试前已达到机械目标圈，禁止再做一圈。",
                                "EPB");
                            return;
                        }
                        await EnsurePowerSupplyReadyForChannelsAsync(new[] { channel }, attemptToken)
                            .ConfigureAwait(false);
                        if (attempt > 1)
                        {
                            await EnterHydraulicStartupPhaseWithSelfHealingAsync(
                                    new HydraulicGenerationKey(
                                        runId,
                                        pressureGroup,
                                        HydraulicPhaseKind.Recovery,
                                        Interlocked.Increment(ref _learningRetryGeneration)),
                                    new[] { channel },
                                    attemptToken)
                                .ConfigureAwait(false);
                        }

                        EpbCycleRunner.LearnSample pendingLegacySample = null;
                        try
                        {
                            try
                            {
                                learningCycleNumber =
                                    Recorder?.BeginLearningCycle(channel, DateTime.UtcNow) ?? 0;
                            }
                            catch (Exception ex)
                            {
                                throw new SoftwareSelfHealingRetryException(
                                    $"EPB[{channel}] 无法建立学习圈落盘边界。",
                                    ex);
                            }
                            if (learningCycleNumber != 0)
                                MarkCurrentCycleNumber(channel, learningCycleNumber);

                            if (GetEpbControlMode(channel) == Adaptive.EpbControlMode.AdaptiveCurrent)
                            {
                                var outcome = await runner.RunOneAdaptiveLearningAsync(
                                        PeriodMs,
                                        attemptToken)
                                    .ConfigureAwait(false);
                                if (outcome.MechanicalCycleCompleted)
                                    OnMechanicalCycleCompleted(
                                        channel,
                                        CycleAttemptKind.Learning,
                                        learningCycleNumber);

                                if (!outcome.IsSuccess &&
                                    outcome.Reason?.IndexOf(
                                        "DaqSampleStale",
                                        StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    // DAQ软件恢复前的失败尝试只留证，不得污染模型或占用逻辑学习圈。
                                    await SealLearningCycleAsync(
                                            channel,
                                            learningCycleNumber,
                                            runId,
                                            learningEvidence,
                                            learningOrdinal,
                                            "learning_failed",
                                            requireValidEvidence: true,
                                            softwareAttempt: attempt)
                                        .ConfigureAwait(false);
                                    learningCycleNumber = 0;
                                    RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                                    if (IsMechanicalTargetReached(channel))
                                    {
                                        _log?.Info(
                                            $"EPB[{channel}] DAQ证据作废圈已完成最终机械目标；" +
                                            "跳过DAQ恢复后的同圈重做。",
                                            "EPB");
                                        return;
                                    }
                                    await AbortHydraulicLeaseForChannelAsync(
                                            channel,
                                            "LearningDaqStaleBeforeRecoveryGeneration")
                                        .ConfigureAwait(false);
                                    await WaitForDaqRecoveryAsync(channel, attemptToken).ConfigureAwait(false);
                                    await EnterHydraulicStartupPhaseWithSelfHealingAsync(
                                            new HydraulicGenerationKey(
                                                runId,
                                                pressureGroup,
                                                HydraulicPhaseKind.Recovery,
                                                Interlocked.Increment(ref _learningRetryGeneration)),
                                            new[] { channel },
                                            attemptToken)
                                        .ConfigureAwait(false);

                                    try
                                    {
                                        learningCycleNumber =
                                            Recorder?.BeginLearningCycle(channel, DateTime.UtcNow) ?? 0;
                                    }
                                    catch (Exception ex)
                                    {
                                        throw new SoftwareSelfHealingRetryException(
                                            $"EPB[{channel}] DAQ恢复后无法重建学习圈落盘边界。",
                                            ex);
                                    }
                                    if (learningCycleNumber != 0)
                                        MarkCurrentCycleNumber(channel, learningCycleNumber);
                                    outcome = await runner.RunOneAdaptiveLearningAsync(
                                            PeriodMs,
                                            attemptToken)
                                        .ConfigureAwait(false);
                                    if (outcome.MechanicalCycleCompleted)
                                        OnMechanicalCycleCompleted(
                                            channel,
                                            CycleAttemptKind.Learning,
                                            learningCycleNumber);
                                }

                                if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.Canceled)
                                    throw new OperationCanceledException(attemptToken);
                                if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.SoftwareRecovery)
                                {
                                    RecordWatchdogSoftwareAbort(channel);
                                    throw new SoftwareSelfHealingRetryException(
                                        $"EPB[{channel}] 自适应学习圈遇到软件瞬态；" +
                                        $"本次尝试作废后重做。Reason={outcome.Reason}");
                                }
                                if (!outcome.IsSuccess)
                                    throw new InvalidOperationException(
                                        $"EPB[{channel}] 自适应学习圈失败：" +
                                        $"阶段={outcome.Stage}，原因={outcome.Reason}");
                                _watchdogConsecutiveSoftwareAborts[channel] = 0;
                            }
                            else
                            {
                                pendingLegacySample = await runner.LearnOneAlignedCoreAsync(
                                        PeriodMs,
                                        T8BaseMs,
                                        phaseMs,
                                        T8MinMs,
                                        attemptToken)
                                    .ConfigureAwait(false);
                                OnMechanicalCycleCompleted(
                                    channel,
                                    CycleAttemptKind.Learning,
                                    learningCycleNumber);
                                _watchdogConsecutiveSoftwareAborts[channel] = 0;
                            }

                            await SealLearningCycleAsync(
                                    channel,
                                    learningCycleNumber,
                                    runId,
                                    learningEvidence,
                                    learningOrdinal,
                                    "learning_completed",
                                    requireValidEvidence: true,
                                    softwareAttempt: attempt)
                                .ConfigureAwait(false);
                            learningCycleNumber = 0;

                            // 旧时序模型也只在证据可靠封存后提交，避免作废圈进入聚合。
                            if (pendingLegacySample != null)
                                runner.ApplyLearnSample(pendingLegacySample);

                            // The runner's adaptive callback is deferred during
                            // the logical cycle.  Commit only after evidence is
                            // durable and verify the persisted profile by readback.
                            try
                            {
                                SaveAdaptiveProfileWithReceipt(runner.CaptureAdaptiveProfile());
                                UpdateLearningAttemptReceiptStatus(
                                    learningEvidence,
                                    channel, learningOrdinal, attempt, "Successful", string.Empty,
                                    qualification: false);
                            }
                            catch (EpbAdaptiveProfilePersistenceFatalException fatalEx)
                            {
                                // A rollback failure is a non-retryable
                                // persistence boundary.  Restore runner state,
                                // then make the failed receipt best-effort only;
                                // receipt I/O must never replace the original
                                // fatal exception or re-enter self-healing.
                                RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                                TryUpdateLearningAttemptReceiptStatusBestEffort(
                                    learningEvidence,
                                    channel,
                                    learningOrdinal,
                                    attempt,
                                    "ModelCommitFatal:" + fatalEx.Message,
                                    qualification: false,
                                    failureKind: "Fatal",
                                    originalFailure: fatalEx);
                                throw;
                            }
                            catch (Exception saveEx)
                            {
                                // SaveWithReceipt may have rolled the store back
                                // after replace/readback failure, but the runner
                                // has already advanced its in-memory model.  Roll
                                // that state back immediately, before receipt
                                // bookkeeping or software self-healing observes
                                // the failed attempt.
                                RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                                TryUpdateLearningAttemptReceiptStatusBestEffort(
                                    learningEvidence, channel, learningOrdinal, attempt,
                                    "ModelCommitFailed:" + saveEx.Message,
                                    qualification: false,
                                    failureKind: "Failure",
                                    originalFailure: saveEx);
                                throw;
                            }
                        }
                        catch (SoftwareSelfHealingRetryException)
                        {
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                            // 与资格圈相同：软件瞬态的学习尝试也必须先封存，才能开始
                            // 下一负圈。清零只清控制层变量，不能代替 Recorder 圈终态提交。
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        learningCycleNumber,
                                        runId,
                                        learningEvidence,
                                        learningOrdinal,
                                        "learning_failed",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            learningCycleNumber = 0;
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        learningCycleNumber,
                                        runId,
                                        learningEvidence,
                                        learningOrdinal,
                                        "learning_canceled",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            learningCycleNumber = 0;
                            throw;
                        }
                        catch (Exception ex) when (EpbCycleRunner.IsSoftwareRecoveryException(ex))
                        {
                            // Includes ordinary IOException from model
                            // SaveWithReceipt/readback.  Restore before sealing
                            // evidence and converting to a bounded retry.
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        learningCycleNumber,
                                        runId,
                                        learningEvidence,
                                        learningOrdinal,
                                        "learning_failed",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            learningCycleNumber = 0;
                            throw new SoftwareSelfHealingRetryException(
                                $"EPB[{channel}] 学习圈软件异常；本次尝试作废后重做。" +
                                $"{ex.GetType().Name}: {ex.Message}",
                                ex);
                        }
                        catch
                        {
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        learningCycleNumber,
                                        runId,
                                        learningEvidence,
                                        learningOrdinal,
                                        "learning_failed",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            learningCycleNumber = 0;
                            throw;
                        }
                        finally
                        {
                            if (_hydraulicLeaseByChannel.TryGetValue(channel, out var activeScope) &&
                                !activeScope.IsClosed)
                                await AbortHydraulicLeaseForChannelAsync(
                                        channel,
                                        "LearningAttemptFinalizer")
                                    .ConfigureAwait(false);
                        }
                    },
                    async (attempt, ex, attemptToken) =>
                    {
                        RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                        await AbortHydraulicLeaseForChannelAsync(
                                channel,
                                "LearningPersistenceSelfHealing")
                            .ConfigureAwait(false);
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "LearningPersistenceSelfHealing",
                            $"学习圈证据软件自愈第{attempt}次：本次尝试已作废，随后重做同一逻辑学习圈。",
                            affectedChannels: new[] { channel },
                            correlationId: runId,
                            allowTerminalReset: false);
                        _log?.Warn(
                            $"EPB[{channel}] 学习圈证据失败已作废，不取消其它通道或整批启动。" +
                            $"Attempt={attempt} DelayMs={GetDaqSelfMaintenanceDelayMs(attempt)} " +
                            $"Reason={ex.Message}",
                            "落盘");
                    },
                    GetDaqSelfMaintenanceDelayMs,
                    token)
                .ConfigureAwait(false);
            }
            finally
            {
                EndLearningProfileTransaction(channel);
            }

            if (attempts > 1)
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Learning,
                    "LearningPersistenceSelfHealed",
                    $"学习圈证据自愈完成，共尝试{attempts}次；只保留最后一次有效学习结果。",
                    affectedChannels: new[] { channel },
                    correlationId: runId,
                    allowTerminalReset: false);
        }

        private async Task SealLearningCycleAsync(
            int channel,
            int cycleNumber,
            Guid runId,
            LearningEvidenceContext learningEvidence,
            int learningOrdinal,
            string status,
            bool requireValidEvidence = false,
            int softwareAttempt = 1)
        {
            var recorder = Recorder;
            if (cycleNumber == 0 || recorder == null) return;
            if (learningEvidence == null || learningEvidence.RunId != runId)
                throw new InvalidOperationException("学习证据上下文缺失或已跨运行代次。");

            var exportDir = System.IO.Path.Combine(
                learningEvidence.ExecutionDirectory,
                $"EPB{channel:D2}",
                (status?.IndexOf("qualification", StringComparison.OrdinalIgnoreCase) >= 0
                    ? $"Qualification_{learningOrdinal:D4}"
                    : $"Learning_{learningOrdinal:D4}"),
                $"Attempt_{Math.Max(1, softwareAttempt):D4}");

            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = new Dictionary<int, int>
            {
                [channel] = cycleNumber
            };
            if (!await TryWaitForCycleDurableCutoffAsync(
                    cutoffCycles,
                    cutoffUtc,
                    $"LearningSeal:{status}",
                    _daqPersistenceRecoveryTimeoutMs,
                    CancellationToken.None)
                .ConfigureAwait(false))
                throw new SoftwareSelfHealingRetryException(
                    $"EPB[{channel}] 学习/资格圈 Raw/耐久边界尚未闭合。" +
                    $"Cycle={cycleNumber} Status={status}");

            AlarmCycleSnapshotEvidence evidence;
            try
            {
                evidence = recorder.SealAndExportCycle(
                    channel,
                    cycleNumber,
                    exportDir,
                    cutoffUtc,
                    status);
            }
            catch (Exception ex)
            {
                // 自定义/代理 Recorder 可能在取得圈边界后直接抛异常。显式封为软件作废，
                // 避免下一次重试继续撞到仍为 running 的旧负圈号。
                var abortCommitted = false;
                try
                {
                    recorder.AbortCycle(
                        channel,
                        cycleNumber,
                        Math.Max(0, recorder.GetCurrentCycleSampleCount(channel)),
                        DateTime.UtcNow,
                        "AbortedBySoftwareRecovery");
                    abortCommitted = true;
                }
                catch (Exception abortEx)
                {
                    _log?.Warn(
                        $"EPB[{channel}] 学习圈封存异常后的软件作废也未确认：{abortEx.Message}",
                        "落盘");
                }
                if (abortCommitted &&
                    _currentCycleNumberByChannel.TryGetValue(channel, out var abortCurrent) &&
                    abortCurrent == cycleNumber)
                    ClearCurrentCycleNumber(channel);

                if (!abortCommitted)
                    throw new SoftwareSelfHealingRetryException(
                        $"EPB[{channel}] 学习圈终态未提交，保留活动圈等待重试。" +
                        $"Cycle={cycleNumber} Status={status} Error={ex.Message}",
                        ex);

                if (requireValidEvidence)
                    throw new SoftwareSelfHealingRetryException(
                        $"EPB[{channel}] 学习圈封存/导出抛出软件异常：" +
                        $"Cycle={cycleNumber} Status={status} Error={ex.Message}",
                        ex);
                _log?.Warn(
                    $"EPB[{channel}] 失败/取消学习圈的辅助证据导出异常：{ex.Message}；" +
                    "不扩大停机范围。",
                    "落盘");
                return;
            }

            if (_currentCycleNumberByChannel.TryGetValue(channel, out var current) &&
                current == cycleNumber)
            {
                ClearCurrentCycleNumber(channel);
            }

            // 报警后台已取得封存权时，学习收尾只退出，不重复生成文件或改写状态。
            if (!evidence.WasClaimed)
                return;
            if (!evidence.IsValid)
            {
                var reason =
                    $"EPB[{channel}] 学习圈落盘失败：Cycle={cycleNumber} Status={status} " +
                    $"Error={evidence.ValidationError}";

                // 数据证据失败是软件/存储瞬态：作废尝试并重做同一逻辑学习圈，
                // 不取消其它通道、不发布全局故障，也不驱动硬件报警灯和蜂鸣器。
                _log?.Warn(reason, "落盘");
                FlushPersistentLog();
                if (requireValidEvidence)
                    throw new SoftwareSelfHealingRetryException(reason);
                return;
            }

            // Seal is the evidence boundary.  Publish a receipt in the same
            // evidence directory before the model commit; successful status is
            // upgraded only after SaveWithReceipt/read-back succeeds.
            WriteLearningAttemptReceipt(
                learningEvidence,
                exportDir,
                channel,
                learningOrdinal,
                softwareAttempt,
                cycleNumber,
                status,
                evidence.SampleCount,
                cutoffUtc,
                DateTime.UtcNow,
                evidence.ValidationError);

            _log?.Info(
                $"EPB[{channel}] 学习圈已封存：Run={runId:N} LearnCycle={learningOrdinal} " +
                $"InternalCycle={cycleNumber} Status={status} Samples={evidence.SampleCount} " +
                $"Dir={exportDir}",
                "落盘");
        }

        private void WriteLearningAttemptReceipt(
            LearningEvidenceContext learningEvidence,
            string exportDir,
            int channel,
            int logicalOrdinal,
            int attempt,
            int internalCycle,
            string status,
            long sampleCount,
            DateTime startedUtc,
            DateTime completedUtc,
            string reason)
        {
            if (learningEvidence == null)
                throw new ArgumentNullException(nameof(learningEvidence));
            var chainRoot = learningEvidence.ChainDirectory;
            var artifacts = new List<LearningArtifact>();
            if (Directory.Exists(exportDir))
            {
                foreach (var file in Directory.EnumerateFiles(exportDir, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFileName(file), LearningRunManifestStore.AttemptReceiptFileName,
                            StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(file).IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    var info = new FileInfo(file);
                    var fullChain = chainRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var relative = Path.GetFullPath(file).Substring(fullChain.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    artifacts.Add(new LearningArtifact
                    {
                        RelativePath = relative,
                        Format = info.Extension.TrimStart('.').ToUpperInvariant(),
                        Sha256 = LearningRunManifestStore.ComputeSha256(file),
                        Bytes = info.Length,
                        SampleCount = string.Equals(info.Extension, ".csv", StringComparison.OrdinalIgnoreCase)
                            ? Math.Max(0, File.ReadLines(file).LongCount() - 1)
                            : sampleCount,
                        StartedUtc = info.CreationTimeUtc,
                        CompletedUtc = info.LastWriteTimeUtc
                    });
                }
            }
            LearningRunManifestStore.WriteAttemptReceiptAtomic(
                exportDir,
                new LearningAttemptReceipt
                {
                    Phase = status?.IndexOf("qualification", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Qualification" : "Learning",
                    LogicalOrdinal = logicalOrdinal,
                    Attempt = Math.Max(1, attempt),
                    InternalCycle = internalCycle,
                    Status = status?.IndexOf("completed", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "EvidenceCompleted" : (status ?? "Unknown"),
                    SampleCount = Math.Max(0, sampleCount),
                    StartedUtc = startedUtc,
                    CompletedUtc = completedUtc,
                    Reason = reason ?? string.Empty,
                    Artifacts = artifacts
                });
        }

        private void UpdateLearningAttemptReceiptStatus(
            LearningEvidenceContext learningEvidence,
            int channel,
            int logicalOrdinal,
            int attempt,
            string status,
            string reason,
            bool qualification)
        {
            var phase = qualification ? "Qualification" : "Learning";
            var dir = Path.Combine(
                learningEvidence?.ExecutionDirectory ??
                throw new ArgumentNullException(nameof(learningEvidence)),
                $"EPB{channel:D2}",
                $"{phase}_{logicalOrdinal:D4}",
                $"Attempt_{Math.Max(1, attempt):D4}");
            var path = LearningRunManifestStore.AttemptReceiptPath(dir);
            if (!LearningRunManifestStore.TryReadAttemptReceipt(path, out var receipt)) return;
            receipt.Status = status ?? "Unknown";
            receipt.Reason = reason ?? string.Empty;
            receipt.CompletedUtc = DateTime.UtcNow;
            LearningRunManifestStore.WriteAttemptReceiptAtomic(dir, receipt);
        }

        private void TryUpdateLearningAttemptReceiptStatusBestEffort(
            LearningEvidenceContext learningEvidence,
            int channel,
            int logicalOrdinal,
            int attempt,
            string reason,
            bool qualification,
            string failureKind,
            Exception originalFailure)
        {
            PreserveLearningPersistenceFailure(
                originalFailure,
                () => UpdateLearningAttemptReceiptStatus(
                    learningEvidence, channel, logicalOrdinal, attempt, "Failed", reason, qualification),
                receiptEx =>
                {
                    // Receipt persistence is diagnostic after the model commit
                    // boundary.  It must not mask the original save/fatal error.
                    try
                    {
                        _log?.Warn(
                            $"学习{(qualification ? "资格" : "学习")}圈失败receipt更新失败（保留原{failureKind}异常）：" +
                            receiptEx.Message,
                            "落盘");
                    }
                    catch { }
                });
        }

        /// <summary>
        /// Execute failed-receipt persistence without ever replacing the
        /// original model commit exception.  The callback is intentionally
        /// internal so focused tests can inject a receipt I/O failure and
        /// assert that the fatal identity remains intact.
        /// </summary>
        internal static Exception PreserveLearningPersistenceFailure(
            Exception originalFailure,
            Action receiptUpdate,
            Action<Exception> receiptFailureSink = null)
        {
            if (originalFailure == null) throw new ArgumentNullException(nameof(originalFailure));
            try { receiptUpdate?.Invoke(); }
            catch (Exception receiptEx)
            {
                try { receiptFailureSink?.Invoke(receiptEx); }
                catch { }
            }
            return originalFailure;
        }

        #endregion

        #region 可调参数

        /// <summary>试验总循环次数</summary>
        public int TestCycle { get; set; } = 100;

        public Dictionary<int, int> EpbTestCycle { get; set; } = new Dictionary<int, int>();

        /// <summary>每圈目标周期（毫秒）。必须与现有配置一致。</summary>
        public int PeriodMs { get; set; } = 30000;

        private int _legacyStaggerDeltaMs;

        /// <summary>
        /// 仅为二进制/源码兼容保留。批量调度不再读取此值，实际错峰来自
        /// TestConfig.xml 的 ElectricalGroups/Group/StaggerMs。
        /// </summary>
        [Obsolete("错峰值已改由XML ElectricalGroups/Group/StaggerMs提供；此属性不再参与调度。")]
        public int StaggerDeltaMs
        {
            get => _legacyStaggerDeltaMs;
            set => _legacyStaggerDeltaMs = value;
        }

        /// <summary>将旧①“头部未上电”的时间并入⑧后的“尾段基准时长”（毫秒）。</summary>
        public int T8BaseMs { get; set; } = 800;

        /// <summary>⑧尾段最小时长保护（毫秒）。不足时先压缩⑦，再降 Δ 并告警。</summary>
        public int T8MinMs { get; set; } = 150;

        /// <summary>锚点预热（毫秒）：让建压有窗口覆盖 0/Δ/2Δ 三波上电。</summary>
        public int AnchorWarmupMs { get; set; } = 2000;

        #endregion

        #region 与现有工程对接的辅助（把这些方法体替换成你的实际实现）

        /// <summary>按压力组归类：PG1=1..6，PG2=7..12。</summary>
        private static Dictionary<int, List<int>> GroupByPressure(IEnumerable<int> channels)
        {
            var dict = new Dictionary<int, List<int>>
            {
                [1] = new(),
                [2] = new()
            };

            foreach (var ch in channels)
            {
                var pg = ch >= 1 && ch <= 6 ? 1 : 2;
                dict[pg].Add(ch);
            }

            return dict;
        }

        /// <summary>向上取整到周期边界（UTC）。</summary>
        private static DateTime CeilToBoundary(DateTime utcNow, int periodMs)
        {
            var ticksPerMs = TimeSpan.TicksPerMillisecond;
            var ms = utcNow.Ticks / ticksPerMs;
            var rem = ms % periodMs;
            var add = rem == 0 ? 0 : periodMs - rem;
            return new DateTime((ms + add) * ticksPerMs, DateTimeKind.Utc);
        }

        /// <summary>
        ///     使 Runner 进入“无① + ⑧扣回 + 尾段保护”的工作模式。
        ///     该方法只设置标志位，不会触发任何动作。
        /// </summary>
        private void PrepareRunnerForNoHeadAndTailCompensation(int ch)
        {
            var r = GetRunner(ch);
            r.UseNoHeadPhase = true;
            r.EnableTailCompensation = true;
            r.TailMinMs = T8MinMs;
        }


        /// <summary>
        /// 获取指定通道的 EPB 循环运行器。
        /// 注意：如果命中 _runnerCache（上一次运行留下的实例），需要重新登记到 _runners，
        /// 以便采集回调 OnFastEpbCurrent 能再次把样本喂给该 Runner。
        /// 同时在此处确保事件订阅已建立（避免重复绑定）。 
        /// </summary>
        private IEpbCycleRunner GetRunner(int channel)
        {
            var runner = _runnerRuntime.GetOrCreate(
                channel,
                () => CreateRunner(channel),
                AttachRunnerEvents,
                out var created);
            _log?.Info(
                $"EPB[{channel}] Runner {(created ? "已创建" : "缓存命中并激活")}，" +
                $"Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(runner)}。",
                "EPB并发");
            return runner;
        }

        private EpbCycleRunner CreateRunner(int channel)
        {
            var hydId = channel <= 6 ? 1 : 2;
            var rcfg = _cfg.Test?.EpbCycleRunner.GetRunnerChannel(channel);
            if (rcfg == null)
                throw new InvalidOperationException($"EPB[{channel}] 缺少循环运行配置。");

            var sampleMs = 2;
            var forwardA = rcfg.ForwardA;
            var holdMs = rcfg.HoldMs;
            holdMs = holdMs <= 0 ? 1000 : holdMs;

            // 峰值超限报警增量阈值（可配；<=0 表示禁用）
            var overshootDeltaA = 0.0;
            try
            {
                var m = AlarmConfig?.Mappings?.Epb?.FirstOrDefault(x => x.Channel == channel);
                overshootDeltaA = m?.OvershootAlarmDeltaA ?? AlarmConfig?.Behavior?.OvershootAlarmDeltaA ?? 0.0;
            }
            catch
            {
                overshootDeltaA = 0.0;
            }

            var runner = new EpbCycleRunner(
                channel,
                hydId,
                _readCurrent,
                _do,
                _acq,
                _hydraulic,
                forwardA,
                holdMs,
                sampleMs,
                rcfg.PeakIgnoreMs,
                _log,
                _cfg,
                this,
                overshootAlarmDeltaA: overshootDeltaA,
                adaptiveOvershootWarningDeltaA:
                    AlarmConfig?.Behavior?.AdaptiveOvershootWarningDeltaA ?? 0.8,
                adaptivePermanentOvershootDeltaA:
                    AlarmConfig?.Behavior?.AdaptivePermanentOvershootDeltaA ?? 2.0,
                adaptiveOvershootConfirmCycles:
                    AlarmConfig?.Behavior?.AdaptiveOvershootConfirmCycles ?? 8,
                adaptiveForwardStallConfirmCycles:
                    AlarmConfig?.Behavior?.AdaptiveForwardStallConfirmCycles ?? 8,
                peakEvidenceMismatchConfirmCycles:
                    AlarmConfig?.Behavior?.PeakEvidenceMismatchConfirmCycles ?? 3,
                safetyMarginControlMode: _safetyMarginControlMode,
                epbControlMode: GetEpbControlMode(channel),
                adaptiveShadowMode: _adaptiveShadowMode,
                adaptiveProfile: GetAdaptiveProfile(channel),
                saveAdaptiveProfile: SaveAdaptiveProfile,
                programSafetySettings: _programSafetySettings);

            return runner;
        }


        /// <summary>
        /// 统一为 Runner 绑定单圈完成事件（防重复绑定）。 
        /// </summary>
        /// <param name="runner">具体的 EPB 循环运行器实例。</param>
        private void AttachRunnerEvents(EpbCycleRunner runner)
        {
            if (runner == null) return;

            runner.AlarmRaised -= OnRunnerAlarmRaised;
            runner.AlarmRaised += OnRunnerAlarmRaised;

            runner.WarningRaised -= OnRunnerWarningRaised;
            runner.WarningRaised += OnRunnerWarningRaised;

            runner.WarningEvidenceRaised -= OnRunnerWarningEvidenceRaised;
            runner.WarningEvidenceRaised += OnRunnerWarningEvidenceRaised;

            runner.RecoverableFaultRaised -= OnRunnerRecoverableFaultRaised;
            runner.RecoverableFaultRaised += OnRunnerRecoverableFaultRaised;

            runner.AdaptiveDecisionObserved -= OnRunnerAdaptiveDecisionObserved;
            runner.AdaptiveDecisionObserved += OnRunnerAdaptiveDecisionObserved;
        }

        private void DetachRunnerEvents(EpbCycleRunner runner)
        {
            if (runner == null) return;
            runner.AlarmRaised -= OnRunnerAlarmRaised;
            runner.WarningRaised -= OnRunnerWarningRaised;
            runner.WarningEvidenceRaised -= OnRunnerWarningEvidenceRaised;
            runner.RecoverableFaultRaised -= OnRunnerRecoverableFaultRaised;
            runner.AdaptiveDecisionObserved -= OnRunnerAdaptiveDecisionObserved;
        }

        private void RemoveRunnerRuntime(int channel, string reason)
        {
            var removed = _runnerRuntime.Remove(channel, DetachRunnerEvents);
            foreach (var runner in removed)
                _log?.Info(
                    $"EPB[{channel}] Runner 已移除，" +
                    $"Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(runner)} Reason={reason}。",
                    "EPB并发");
        }

        private void RemoveTimerRuntime(int channel, string reason)
        {
            var removed = _timerRuntime.Remove(channel, timer =>
            {
                DetachTimerRuntimeObserver(channel, timer);
                try { timer.Stop(); } catch { }
            });
            foreach (var timer in removed)
                _log?.Info(
                    $"EPB[{channel}] Timer 已停止并移除，" +
                    $"Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(timer)} Reason={reason}。",
                    "EPB并发");
        }

        private void ClearChannelRuntimes(string reason)
        {
            foreach (var channel in _timers.Keys.Concat(_timerCache.Keys).Distinct().ToArray())
                RemoveTimerRuntime(channel, reason);
            foreach (var channel in _runners.Keys.Concat(_runnerCache.Keys).Distinct().ToArray())
                RemoveRunnerRuntime(channel, reason);
        }


        /// <summary>获取指定通道的高精计时器（必须在 StartChannelAsync 后调用）</summary>
        private HighPrecisionTimer GetTimer(int ch, int periodMs, OverrunPolicy overrunPolicy)
        {
            var timer = _timerRuntime.GetOrCreate(
                ch,
                () => new HighPrecisionTimer(periodMs, overrunPolicy, _log),
                value => AttachTimerRuntimeObserver(ch, value),
                out var created);
            _log?.Info(
                $"EPB[{ch}] Timer {(created ? "已创建" : "缓存命中并激活")}，" +
                $"Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(timer)}。",
                "EPB并发");
            return timer;
        }


        /// <summary>
        /// 在指定压力组的“锚点时刻”触发建压保持。
        /// 调用策略：
        /// <list type="number">
        ///     <item>根据 <paramref name="pressureGroupId"/> + <paramref name="channelsInGroup"/> 过滤出本组中实际参与的 EPB 通道；</item>
        ///     <item>对每个通道调用一次 <see cref="HydraulicGroupCoordinator.EnterElectricalPhaseAsync(int,CancellationToken)"/>；</item>
        ///     <item>
        ///         <b>协调器内部是幂等的</b>：同一液压组第一次调用会真正建压，后续通道只是在
        ///         InFlight 集合中登记自己，供统一释放时使用。
        ///     </item>
        /// </list>
        /// </summary>
        /// <param name="pressureGroupId">压力组编号：1 表示 1..6，2 表示 7..12。</param>
        /// <param name="channelsInGroup">本压力组内，本轮实际参与的 EPB 通道列表。</param>
        /// <param name="token">取消令牌。</param>
        private async Task<HydraulicCycleLease> HydraulicEnterAtGroupAnchorAsync(
            HydraulicGenerationKey generationKey,
            IReadOnlyList<int> channelsInGroup,
            CancellationToken token)
        {
            if (generationKey == null) throw new ArgumentNullException(nameof(generationKey));
            var pressureGroupId = generationKey.HydraulicId;
            if (_hydCoordinator == null)
            {
                _log.Warn($"压力组[{pressureGroupId}] 无可用液压协调器，跳过建压保持", "液压协调");
                return null;
            }

            if (channelsInGroup == null || channelsInGroup.Count == 0)
                return null;

            // 保险起见，再按 pressureGroupId 过滤一遍
            var channelList = channelsInGroup
                .Where(ch =>
                    (pressureGroupId == 1 && ch >= 1 && ch <= 6) ||
                    (pressureGroupId == 2 && ch >= 7 && ch <= 12))
                .Distinct()
                .ToArray();

            if (channelList.Length == 0)
                return null;

            // 旧作用域必须在申请新代次 Gate 前完成归还，否则泄漏代次会让新申请
            // 永久等待在旧 Gate 上，连后续重建机会也拿不到。
            foreach (var ch in channelList)
            {
                if (!_hydraulicLeaseByChannel.TryGetValue(ch, out var existing) ||
                    (!existing.IsClosed && existing.Key.Equals(generationKey)))
                    continue;
                await AbortHydraulicLeaseForChannelAsync(
                        ch,
                        "HydraulicGenerationBeforeEnter")
                    .ConfigureAwait(false);
            }

            var lease = await _hydCoordinator.EnterGenerationAsync(generationKey, channelList, token)
                .ConfigureAwait(false);
            foreach (var ch in channelList)
            {
                if (_hydraulicLeaseByChannel.TryGetValue(ch, out var existing))
                {
                    if (!existing.IsClosed &&
                        existing.Key.Equals(lease.Key) &&
                        existing.CoordinatorEpoch == lease.CoordinatorEpoch)
                        continue;
                    await AbortHydraulicLeaseForChannelAsync(
                            ch,
                            "HydraulicGenerationScopeReplacement")
                        .ConfigureAwait(false);
                }

                var scope = _hydCoordinator.CreateChannelScope(lease, ch);
                if (!_hydraulicLeaseByChannel.TryAdd(ch, scope))
                {
                    try { await scope.AbortAsync("HydraulicScopeRegistrationRace").ConfigureAwait(false); }
                    catch { }
                    throw new InvalidOperationException(
                        $"HydraulicScopeRegistrationRace EPB={ch} Key={lease.Key}");
                }
            }
            NonCriticalObserver.Invoke(
                PressureQualificationChanged,
                lease.Qualification,
                ex => _log?.Warn($"压力资格观察者异常已隔离：{ex.Message}", "液压协调"));
            return lease;
        }

        /// <summary>
        /// 启动、学习和资格阶段的液压软件同步自愈。屏障缺员和旧成员不可变均属于
        /// 软件代次问题，安全释压后持续创建 Recovery 代次；建压、保压、释压等
        /// 实时硬件证据仍原样抛出，由故障组隔离逻辑处理。
        /// </summary>
        private async Task<HydraulicCycleLease> EnterHydraulicStartupPhaseWithSelfHealingAsync(
            HydraulicGenerationKey initialKey,
            IReadOnlyList<int> channelsInGroup,
            CancellationToken token)
        {
            if (initialKey == null) throw new ArgumentNullException(nameof(initialKey));
            var attempt = 0;
            var key = initialKey;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return await HydraulicEnterAtGroupAnchorAsync(key, channelsInGroup, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsHydraulicSoftwareRecoveryCandidate(ex))
                {
                    attempt++;
                    var offFailedChannels = new List<int>();
                    foreach (var channel in channelsInGroup ?? Array.Empty<int>())
                    {
                        var offSucceeded = false;
                        try { offSucceeded = CommandEpbOffSafetyImmediate(channel); } catch { }
                        if (!offSucceeded)
                        {
                            offFailedChannels.Add(channel);
                            RequestElectricalGroupEmergencyShutdown(
                                channel,
                                "HydraulicGenerationSelfHealing OutputOffCommandFailed");
                        }
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "HydraulicGenerationSelfHealing",
                            $"液压软件代次自愈第{attempt}次；已抛弃旧代次并重新建压。",
                            affectedChannels: channelsInGroup?.ToArray() ?? Array.Empty<int>(),
                            correlationId: initialKey.TestRunId,
                            allowTerminalReset: false);
                    }
                    try
                    {
                        await _hydCoordinator.ForceReleaseAsync(
                                initialKey.HydraulicId,
                                $"StartupGenerationSelfHealing:{ex.GetType().Name}")
                            .ConfigureAwait(false);
                    }
                    catch (Exception releaseEx)
                    {
                        throw new InvalidOperationException(
                            $"液压组{initialKey.HydraulicId}软件代次清理后无法确认安全释压。",
                            releaseEx);
                    }

                    if (offFailedChannels.Count > 0)
                        throw new EpbOutputCommandException(
                            offFailedChannels[0],
                            "HydraulicGenerationSelfHealingOff");

                    var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                    _log.Warn(
                        $"液压组{initialKey.HydraulicId}启动/学习代次软件异常，" +
                        $"{delayMs}ms后创建全新Recovery代次。Attempt={attempt} Error={ex.Message}",
                        "液压协调");
                    if (attempt >= 3)
                        throw new SoftwareSelfHealingExhaustedException(
                            $"HydraulicGeneration:{initialKey.HydraulicId}",
                            attempt,
                            ex);
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                    key = new HydraulicGenerationKey(
                        initialKey.TestRunId,
                        initialKey.HydraulicId,
                        HydraulicPhaseKind.Recovery,
                        Interlocked.Increment(ref _softwareHydraulicRetryGeneration));
                }
            }
        }

        internal static bool IsHydraulicSoftwareRecoveryCandidate(Exception exception)
        {
            if (exception is HydraulicBarrierTimeoutException) return true;
            if (exception is HydraulicCoordinatorRebuildingException) return true;
            if (exception is HydraulicBuildTimeoutException buildTimeout)
                return HydraulicGroupCoordinator.ClassifyFault(buildTimeout) !=
                       FaultClassification.HardwareConfirmed;
            if (exception is HydraulicPressureLostException pressureLost)
                return HydraulicGroupCoordinator.ClassifyFault(pressureLost) !=
                       FaultClassification.HardwareConfirmed;
            var message = exception?.Message ?? string.Empty;
            return message.IndexOf("members are immutable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("成员不可变", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion


        /// <summary>
        /// 正式圈控制与落盘均提交后，再向 UI/检查点转发一次完成事件。
        /// Runner 的物理动作成功不能提前计数，因为随后落盘失败的圈必须作废。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <param name="sessionRunCount">本次试验 Session 内的运行次数（从 1 开始）。</param>
        private void OnFormalCycleCommitted(
            int channel,
            int sessionRunCount,
            long groupCycleSlot)
        {
            _faultConfirmationTracker.ResetScope($"Channel:{channel}");
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (!string.IsNullOrWhiteSpace(device))
                _faultConfirmationTracker.ResetScope($"Daq:{device}");
            CompleteFormalSoftwareRecoveryAfterCommit(channel);
            // 完整正式圈已经通过控制与持久化提交，等价于本动作具备新鲜DAQ电流证据。
            // 电源通信降级确认按共享物理槽位去重，组内多个通道不会重复计圈。
            _powerSupply?.RecordSuccessfulActionCycle(channel, groupCycleSlot);
            var current = _channelRuntimeStateStore.Get(channel);
            if (current?.State == ChannelRuntimeState.WarningRunning)
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Running,
                    "WarningCleared",
                    "后续完整圈正常，软预警已解除");
            NonCriticalObserver.Invoke(
                ChannelCycleCompleted,
                channel,
                sessionRunCount,
                ex => _log?.Warn(
                    $"EPB[{channel}] 正式圈完成观察者异常已隔离，不影响后续试验：{ex.Message}",
                    "EPB"));
        }

        private void OnMechanicalCycleCompleted(
            int channel,
            CycleAttemptKind kind,
            int cycleNumber)
        {
            var completedUtc = DateTime.UtcNow;
            var nowTicks = completedUtc.Ticks;
            if (cycleNumber != 0 && Recorder is IMechanicalCycleRecorder durableRecorder)
            {
                try
                {
                    durableRecorder.MarkMechanicalCycleCompleted(channel, cycleNumber, completedUtc);
                }
                catch (Exception ex)
                {
                    // 物理事实已发生，不能因索引辅助字段写入失败而重做一圈。
                    _log?.Error(
                        $"EPB[{channel}] 机械完成事实写入 index.db 失败；继续以内存/XML计数，" +
                        $"禁止重做本圈。Cycle={cycleNumber} Kind={kind} Error={ex.Message}",
                        "落盘",
                        ex);
                }
            }
            _watchdogLastMechanicalCompletedUtcTicks[channel] = nowTicks;
            _watchdogMechanicalCompletedCount.AddOrUpdate(channel, 1, (_, value) => value + 1);
            NonCriticalObserver.Invoke(
                ChannelMechanicalCycleCompleted,
                channel,
                kind,
                cycleNumber,
                ex => _log?.Warn(
                    $"EPB[{channel}] 机械完成圈观察者异常已隔离：{ex.Message}",
                    "EPB"));
        }

        private long GetObservedMechanicalCycleCount(int channel)
        {
            _mechanicalCycleBaseline.TryGetValue(channel, out var baseline);
            _watchdogMechanicalCompletedCount.TryGetValue(channel, out var processCompleted);
            var observed = baseline + processCompleted;
            var record = _cfg.Test.GetEpbRecord(channel);
            if (record != null)
                observed = Math.Max(observed, record.EffectiveMechanicalCycleCount);
            return Math.Max(0L, observed);
        }

        private int GetRemainingMechanicalTargetCycles(int channel)
        {
            var record = _cfg.Test.GetEpbRecord(channel);
            if (record == null) return 0;
            var total = record.TotalCount > 0 ? record.TotalCount : Math.Max(0, _cfg.Test.TestTarget);
            return (int)Math.Max(0L, total - GetObservedMechanicalCycleCount(channel));
        }

        private bool IsMechanicalTargetReached(int channel)
            => GetRemainingMechanicalTargetCycles(channel) <= 0;

        private void RecordWatchdogSoftwareAbort(int channel)
        {
            _watchdogConsecutiveSoftwareAborts.AddOrUpdate(
                channel,
                1,
                (_, value) => value >= int.MaxValue ? int.MaxValue : value + 1);
        }

        private bool OnFormalCycleCommittedAndEvaluateClampFault(
            IEpbCycleRunner runner,
            int channel,
            int cycleNumber,
            int sessionRunCount,
            long groupCycleSlot)
        {
            OnFormalCycleCommitted(channel, sessionRunCount, groupCycleSlot);
            Adaptive.FormalCycleFaultCommitResult result;
            try
            {
                result = runner.CommitFormalCycleFaultEvidence(
                    _activeBatchId,
                    cycleNumber);
            }
            catch (Exception ex)
            {
                _log?.Error(
                    $"EPB[{channel}] 正式圈已完成，但卡钳异常证据提交失败：" +
                    $"RunId={_activeBatchId:N} Cycle={cycleNumber} {ex.Message}",
                    "报警",
                    ex);
                return false;
            }

            if (result == null || !result.ShouldLatchAlarm) return false;
            // 正式圈已在上方完成耐久提交，统一 attempt 会随即清除“当前圈”投影。
            // 永久报警必须携带这一个不可变的触发圈号，不能再让异步报警链回读瞬时状态。
            OnRunnerAlarmRaised(
                channel,
                "AdaptiveHardFault " + result.AlarmReason,
                cycleNumber);
            return true;
        }
    }

    #region 对接所需接口（如果你的类型名不同，请改成你的）

    // public enum OverrunPolicy
    // {
    //     AlignToWallClock,
    //     RunToCompletionSkipMissed,
    //     SkipNextIfOverrun,
    //     Throw
    // }

    // public interface IHighPrecisionTimer
    // {
    //     /// <summary>启动周期性任务。</summary>
    //     Task StartAsync(int repeat, int startDelayMs, Func<int, CancellationToken, Task<bool>> work);
    //
    //     void Stop();
    // }

    public interface IEpbCycleRunnerOld
    {
        /// <summary>是否移除①头部等待（周期起点即进入上电相关流程）。</summary>
        bool UseNoHeadPhase { get; set; }

        /// <summary>是否在⑧尾段中扣回“相位延时 + 迟到量”。</summary>
        bool EnableTailCompensation { get; set; }

        /// <summary>⑧尾段最小时长保护（毫秒）。</summary>
        int TailMinMs { get; set; }

        /// <summary>
        ///     学习单圈（对齐外壳版）：不做①，⑧按 (tailBaseMs - phaseMs) 扣回，保证周期结束点对齐。
        /// </summary>
        Task<bool> LearnOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs,
            CancellationToken token);

        /// <summary>
        ///     正式单圈（对齐外壳版）：不做①，⑧按 (tailBaseMs - phaseMs - lateness) 扣回，
        ///     并以 <paramref name="deadlineUtc" /> 为“硬截止”统一收尾。
        /// </summary>
        Task<bool> RunOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, DateTime deadlineUtc,
            CancellationToken token);
    }


    /// <summary>
    ///     EPB 单通道“单圈执行 + 学习/运行外壳”接口。
    /// </summary>
    public interface IEpbCycleRunner
    {
        /// <summary>最近一次正式单圈的结构化结果。</summary>
        Adaptive.EpbCycleOutcome LastCycleOutcome { get; }

        /// <summary>正式圈落盘成功后提交本圈卡钳异常候选，并返回是否达到永久报警门槛。</summary>
        Adaptive.FormalCycleFaultCommitResult CommitFormalCycleFaultEvidence(
            Guid testRunId,
            int cycleNumber);

        /// <summary>
        /// 使用自适应状态机执行一个启动学习圈；不增加正式成功圈计数。
        /// </summary>
        Task<Adaptive.EpbCycleOutcome> RunOneAdaptiveLearningAsync(
            int targetPeriodMs,
            CancellationToken token);

        /// <summary>捕获本圈开始前的自适应模型，用于软件作废圈回滚。</summary>
        EpbAdaptiveProfile CaptureAdaptiveProfile();

        /// <summary>恢复模型快照，撤销软件作废圈对模型和瞬态计数的全部影响。</summary>
        void RestoreAdaptiveProfile(EpbAdaptiveProfile snapshot);

        /// <summary>是否移除①头部等待（由外部“相位对齐”承担）。</summary>
        bool UseNoHeadPhase { get; set; }

        /// <summary>是否在⑧尾段由外壳统一扣回（相位延时 + 迟到量）。</summary>
        bool EnableTailCompensation { get; set; }

        /// <summary>⑧尾段最小时长保护（毫秒）。</summary>
        int TailMinMs { get; set; }

        /// <summary>
        ///     学习单圈（对齐外壳版）：<b>不做①</b>，⑧按 <c>(tailBaseMs - phaseMs)</c> 扣回（统一收尾）。
        ///     <para>注意：该方法为历史兼容入口，在“批量学习”中推荐使用 <see cref="LearnOneAlignedCoreAsync" /> + 聚合。</para>
        /// </summary>
        Task<bool> LearnOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs,
            CancellationToken token);

        /// <summary>
        ///     正式单圈（对齐外壳版）：<b>不做①</b>，⑧按 <c>(tailBaseMs - phaseMs - lateness)</c> 扣回，
        ///     并以 <paramref name="deadlineUtc" /> 为“硬截止”统一收尾。
        /// </summary>
        Task<bool> RunOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, DateTime deadlineUtc,
            CancellationToken token);

        // ===================== 批量学习（推荐） =====================

        /// <summary>
        ///     开始一轮批量学习的聚合（清空内部样本缓存）。
        ///     必须在发起多圈学习前调用一次。
        /// </summary>
        void BeginLearnAggregation();

        /// <summary>
        ///     单圈学习的“核心版本”：<b>不做①</b>、⑧不等待（交由外壳统一对齐），
        ///     返回本圈测得的各阶段耗时/电流作为样本；失败圈返回 null。
        /// </summary>
        Task<EpbCycleRunner.LearnSample> LearnOneAlignedCoreAsync(int periodMs, int tailBaseMs, int phaseMs,
            int tailMinMs, CancellationToken token);

        /// <summary>
        ///     将 <paramref name="sample" /> 并入当前聚合容器（仅在 <see cref="BeginLearnAggregation" /> 之后有效）。
        /// </summary>
        void ApplyLearnSample(EpbCycleRunner.LearnSample sample);

        /// <summary>
        ///     结束本轮聚合：将样本的统计量（建议中位数）写回 Runner 的估计字段（如 _tFwdPeakDecayMs 等）。
        /// </summary>
        void FinalizeLearnAggregation();

        int DefaultPreReleaseDetectTimeoutMs { get; }
        Task<bool> PreReleaseAsync(int? keepMs, CancellationToken token);
        Task<bool> PreReleaseAsync(int? keepMs, int? detectTimeoutMs, CancellationToken token);
        void BeginSafetyMarginLearning();
        void FinalizeSafetyMarginLearning();
    }

    #endregion
}
