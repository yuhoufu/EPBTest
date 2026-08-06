using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        public BatchStartResult(Guid testRunId, int[] startedChannels, ChannelStartFault[] faults)
        {
            TestRunId = testRunId;
            StartedChannels = startedChannels ?? Array.Empty<int>();
            Faults = faults ?? Array.Empty<ChannelStartFault>();
        }
        public Guid TestRunId { get; }
        public int[] StartedChannels { get; }
        public ChannelStartFault[] Faults { get; }
        public int[] QuarantinedChannels => Faults.Select(x => x.Channel).Distinct().OrderBy(x => x).ToArray();
    }

    /// <summary>
    ///     EpbManager 扩展：批量启动（学习 + 正式），并为每个压力组建立“锚点时间轴”，
    ///     让每个通道以固定相位（电源组内索引 × Δ）锁相到这条时间轴，保证“每圈对齐 + 组内错峰”。
    ///     使用方式：
    ///     await manager.StartBatchSynchronizedAsync(new[]{1,2,4,6}, learnCycles:5, token);
    /// </summary>
    public partial class EpbManager
    {
        // 字段区
        private ConcurrentDictionary<int, EpbCycleRunner> _runnerCache => _runnerRuntime.Cache;
        private ConcurrentDictionary<int, HighPrecisionTimer> _timerCache => _timerRuntime.Cache;
        private int _batchSessionActive;
        private CancellationTokenSource _batchSessionCts;
        private readonly BatchStartLifecycleGate _batchLifecycleGate = new BatchStartLifecycleGate();
        private long _learningRetryGeneration;
        private long _softwareHydraulicRetryGeneration;
        private ElectricalStaggerPlan _activeStaggerPlan;
        private readonly ConcurrentDictionary<int, DateTime> _activeFormalT0ByPressureGroup =
            new ConcurrentDictionary<int, DateTime>();
        private Guid _activeBatchId;

        /// <summary>当前是否已有批量学习或正式试验会话。</summary>
        public bool IsBatchSessionActive => Volatile.Read(ref _batchSessionActive) != 0;

        /// <summary>
        /// 对外暴露的“EPB 单圈完成”事件。
        /// 参数 1：EPB 通道号（1..12）；
        /// 参数 2：本次试验 Session 内已经完成的圈数（从 1 开始）。
        /// </summary>
        public event Action<int, int> ChannelCycleCompleted;


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
                    token)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 从已验证的正常暂停检查点恢复：复用稳定模型，先做资格圈，不把资格圈计入正式目标。
        /// </summary>
        public Task<BatchStartResult> StartBatchFromGracefulCheckpointAsync(
            int[] channels,
            int qualificationCycles,
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
                    token);
            }
            return StartBatchCoreAsync(
                selected,
                learnCycles: 0,
                qualificationCycles,
                reuseStableProfiles: true,
                token);
        }

        /// <summary>
        /// “重新开始”清场屏障：先合并/完成旧批次的安全停止，再等待旧启动调用彻底退出。
        /// 返回前不会遗留仍可能提交 StopChannel/StopAll 的旧启动尾声，调用方可以在同一次请求中直接启动新批次。
        /// </summary>
        public async Task<StopSafetyResult> PrepareForFreshRestartAsync(
            StopContext context,
            CancellationToken token = default)
        {
            context ??= new StopContext
            {
                Source = StopSource.ManualUi,
                Reason = "重新开始前抛弃旧批次状态并安全清场",
                Initiator = nameof(PrepareForFreshRestartAsync),
                CorrelationId = Guid.NewGuid().ToString("N"),
                RequestedUtc = DateTime.UtcNow
            };

            // 安全停止不能因开始按钮调用方取消而半途退出；token 只控制调用方等待旧启动尾声。
            var safety = await StopAllAsync(context, CancellationToken.None).ConfigureAwait(false);
            await _batchLifecycleGate.JoinAsync(token).ConfigureAwait(false);

            if (!safety.CanReleaseAcquisition)
            {
                throw new InvalidOperationException(
                    "重新开始清场未确认电机DO及程控电源均已关闭。" +
                    $" Motor={safety.MotorError}; Power={safety.PowerError}");
            }

            _log?.Info(
                "重新开始清场完成：旧批次软件状态、在途启动与瞬态故障已抛弃；开始执行新批次实时预检。",
                "EPB");
            return safety;
        }

        private async Task<BatchStartResult> StartBatchCoreAsync(
            int[] channels,
            int learnCycles,
            int qualificationCycles,
            bool reuseStableProfiles,
            CancellationToken token)
        {
            return await _batchLifecycleGate.RunAsync(
                    () => StartBatchCoreUnderLifecycleGateAsync(
                        channels,
                        learnCycles,
                        qualificationCycles,
                        reuseStableProfiles,
                        token),
                    token)
                .ConfigureAwait(false);
        }

        private async Task<BatchStartResult> StartBatchCoreUnderLifecycleGateAsync(
            int[] channels,
            int learnCycles,
            int qualificationCycles,
            bool reuseStableProfiles,
            CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空", nameof(channels));

            var selected = channels.Distinct().OrderBy(x => x).ToArray();
            if (!reuseStableProfiles && learnCycles < 5)
                throw new InvalidOperationException("严格完整曲线控制要求 LearnCycle 至少为5圈。");
            if (reuseStableProfiles)
                EnsureAdaptiveProfilesReady(selected);
            var staggerPlan = ElectricalStaggerPlanner.Build(selected, _cfg.Test.Groups, PeriodMs);
            var sessionToken = BeginBatchSession(token);
            var startFaults = new List<ChannelStartFault>();
            try
            {
                _activeBatchId = Guid.NewGuid();
                BeginDaqIncidentRun(_activeBatchId, selected);
                InvalidateStopSafetyCache();
                ResetTransientFaultStateForRestart(
                    selected,
                    reuseStableProfiles ? "GracefulCheckpointResume" : "FreshBatchStart");
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
                    _alarmStopLatch.BeginRun(channel);

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
                            PublishChannelRuntimeState(
                                failedChannel,
                                ChannelRuntimeState.StartBlocked,
                                failedResult.Code,
                                failedResult.Reason,
                                failedChannel,
                                new[] { failedChannel },
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
                        PublishChannelRuntimeState(
                            failedChannel,
                            ChannelRuntimeState.StartBlocked,
                            "LearningFailed",
                            "自学习失败，已隔离通道",
                            failedChannel,
                            new[] { failedChannel },
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
                        PublishChannelRuntimeState(
                            failedChannel,
                            ChannelRuntimeState.StartBlocked,
                            "QualificationFailed",
                            "资格复核失败，已隔离通道",
                            failedChannel,
                            new[] { failedChannel },
                            _activeBatchId);
                        UnmarkHydraulicParticipant(failedChannel);
                        foreach (var list in groups.Values) list.Remove(failedChannel);
                    }
                }

                activeChannels = groups.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToArray();
                if (activeChannels.Length == 0)
                    throw new InvalidOperationException("全部选中通道均在学习阶段被隔离，未启动正式试验。");

                EnsureAdaptiveProfilesReady(activeChannels);

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
                return new BatchStartResult(_activeBatchId, activeChannels, startFaults.ToArray());
            }
            catch (Exception ex)
            {
                var expectedCancellation = IsExpectedBatchCancellation(
                    ex,
                    sessionToken.IsCancellationRequested,
                    token.IsCancellationRequested);
                if (expectedCancellation)
                    _log?.Info($"批量启动已取消：{ex.Message}", "EPB");
                else
                    _log?.Error($"批量启动异常：{ex}", "EPB", ex);
                EndBatchSession(cancel: true);

                foreach (var channel in selected)
                    PublishChannelRuntimeState(
                        channel,
                        expectedCancellation
                            ? ChannelRuntimeState.ManualStopped
                            : ChannelRuntimeState.StartBlocked,
                        expectedCancellation ? "StartCanceled" : "StartFailed",
                        ex.Message,
                        affectedChannels: selected,
                        correlationId: _activeBatchId,
                        allowTerminalReset: !expectedCancellation);

                foreach (var channel in selected)
                {
                    try { StopChannel(channel); }
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
                                Source = StopSource.StartupRollback,
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
                _persistence.ResumeAdmission(device);

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
                        allowTerminalReset: true);
                _log?.Warn(
                    $"DAQ启动预检未通过，按软件瞬态持续自愈，不标记启动受阻。" +
                    $"Attempt={attempt} DelayMs={delayMs}；{details}",
                    "AI");
                await Task.Delay(delayMs, token).ConfigureAwait(false);
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
            if (_powerSupply == null) return;

            var channels = (selected ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
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
                            allowTerminalReset: true);
                    _log?.Warn(
                        $"程控电源启动预检未通过，无新鲜保护触发证据，按软件瞬态持续自愈。" +
                        $"Attempt={attempt} DelayMs={delayMs} Error={ex.Message}",
                        "程控电源");
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
                Interlocked.Increment(ref _runEpoch);
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

        private void EndBatchSession(bool cancel)
        {
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
            _activeStaggerPlan = null;
            _activeFormalT0ByPressureGroup.Clear();
            _activeBatchId = Guid.Empty;
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

        private void TryEndBatchSessionWhenIdle()
        {
            if (_timers.Count == 0 && _timerCache.Count == 0 && _runners.Count == 0)
                EndBatchSession(cancel: false);
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

                    var runs = EpbTestCycle[ch]; // 正式阶段总圈数（可调）
                    var successfulCycles = 0;
                    
                    // —— 计时器每圈工作（cycleIndex 从 1 开始） —— //
                    _ = timer.StartAsync(
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

                            await WaitForDaqRecoveryAsync(ch, token).ConfigureAwait(false);

                            // 1) 在本圈锚点时刻为该压力组建压：
                            //    对本组所有参与通道调用 EnterElectricalPhaseAsync，
                            //    这样 HydraulicGroupCoordinator 能正确维护 InFlight 集合。
                            var participants = GetHydraulicParticipantsInPressureGroupSnapshot(pg, enabled);
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
                            if (!TryBeginFormalCycle(recorder, ch, cycleNumber, DateTime.UtcNow))
                            {
                                ReleaseCyclePauseCts(ch, cyclePauseCts);
                                return false;
                            }

                            // 3) 跑一圈（对齐外壳版）
                            var ok = false;
                            try
                            {
                                ok = await runner.RunOneAlignedAsync(
                                    PeriodMs,
                                    T8BaseMs,
                                    phase,
                                    T8MinMs,
                                    deadlineUtc,
                                    token
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
                            var controlSucceeded = IsFormalControlSucceeded(
                                ok,
                                runner.LastCycleOutcome.IsSuccess);
                            var controlNeedsSoftwareRecovery =
                                runner.LastCycleOutcome.Kind ==
                                Adaptive.EpbCycleOutcomeKind.SoftwareRecovery;
                            if (controlNeedsSoftwareRecovery)
                                ReportFormalControlSoftwareRecovery(
                                    ch,
                                    cycleNumber,
                                    runner.LastCycleOutcome.Reason);

                            if (!ok)
                            {
                                var failed = runner.LastCycleOutcome;
                                _log?.Warn(
                                    $"EPB[{ch}] 周期 {cycleNumber} 返回失败：" +
                                    $"Kind={failed.Kind} Stage={failed.Stage} Reason={failed.Reason} " +
                                    $"Peak={failed.PeakCurrentA:F3}A Target={failed.TargetCurrentA:F3}A " +
                                    $"Error={failed.PeakErrorA:+0.000;-0.000;0.000}A",
                                    "EPB");
                            }

                            // 4) ★ 圈结束：根据是否报警停机决定封圈状态
                            var persistenceCommitted = recorder == null;
                            if (recorder != null)
                            {
                                try
                                {
                                    if (TryConsumeDaqClockCycleAbort(ch, cycleNumber))
                                    {
                                        _log?.Warn(
                                            $"EPB[{ch}] 周期 {cycleNumber} 已由DAQ时钟恢复流程封存，" +
                                            "跳过周期尾重复记账。",
                                            "落盘");
                                        goto CyclePersistenceFinished;
                                    }
                                    var finalN = recorder.GetCurrentCycleSampleCount(ch);
                                    if (IsAlarmStopRequested(ch))
                                    {
                                        // 报警后台流程会在确认当前圈 CSV/BIN 快照存在后封为 alarm；
                                        // 若快照失败则封为 failed。这里保持 running，避免先写无文件的 alarm。
                                    }
                                    else if (controlNeedsSoftwareRecovery)
                                    {
                                        AbortCycleAfterPersistence(
                                            recorder,
                                            ch,
                                            cycleNumber,
                                            DateTime.UtcNow,
                                            "AbortedBySoftwareRecovery");
                                    }
                                    else if (runner.LastCycleOutcome.Kind == Adaptive.EpbCycleOutcomeKind.HardFault)
                                    {
                                        AbortCycleAfterPersistence(
                                            recorder,
                                            ch,
                                            cycleNumber,
                                            DateTime.UtcNow,
                                            "failed");
                                    }
                                    else if (controlSucceeded)
                                    {
                                        persistenceCommitted = CompleteCycleAndScheduleEvidence(
                                            recorder,
                                            ch,
                                            cycleNumber,
                                            finalN,
                                            DateTime.UtcNow);
                                    }
                                    else
                                    {
                                        AbortCycleAfterPersistence(
                                            recorder,
                                            ch,
                                            cycleNumber,
                                            DateTime.UtcNow,
                                            runner.LastCycleOutcome.Kind == Adaptive.EpbCycleOutcomeKind.Canceled
                                                ? "canceled"
                                                : "failed");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    if (controlSucceeded)
                                        AbortFormalCycleWithoutPersistenceBarrier(
                                            recorder,
                                            ch,
                                            cycleNumber,
                                            DateTime.UtcNow,
                                            0,
                                            ex);
                                    else
                                        _log?.Warn(
                                            $"EPB[{ch}] 失败圈封存异常 Cycle={cycleNumber}: {ex.Message}",
                                            "落盘");
                                }
                            }

                        CyclePersistenceFinished:
                            if (!IsAlarmStopRequested(ch))
                                ClearCurrentCycleNumber(ch);

                            // 若该通道自然完成最后一圈：统一收尾（含“停止即存最近10圈”），
                            // 并从运行集合中移除，避免影响其它仍在运行通道的逻辑。
                            if (IsFormalCycleCountable(
                                    controlSucceeded,
                                    persistenceCommitted))
                            {
                                var committedCycles = Interlocked.Increment(ref successfulCycles);
                                OnFormalCycleCommitted(ch, committedCycles);
                                if (committedCycles >= runs)
                                {
                                    FinalizeChannelAfterNaturalCompletion(ch);
                                    timer.Stop();
                                }
                            }

                            ReleaseCyclePauseCts(ch, cyclePauseCts);
                            return controlSucceeded && persistenceCommitted;

                        });
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
            IReadOnlyList<int> candidateChannels)
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

                if (IsHydraulicParticipant(ch))
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
                    var enabled = list.Where(ch => !quarantined.ContainsKey(ch)).OrderBy(x => x).ToList();
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
                                            channelToken)
                                        .ConfigureAwait(false);
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
            CancellationToken token)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            var modelBeforeLogicalCycle = runner.CaptureAdaptiveProfile();
            var learningCycleNumber = 0;

            var attempts = await SoftwareSelfHealingLoop.RunAsync(
                    async (attempt, attemptToken) =>
                    {
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
                                            learningOrdinal,
                                            "learning_failed",
                                            requireValidEvidence: true,
                                            softwareAttempt: attempt)
                                        .ConfigureAwait(false);
                                    learningCycleNumber = 0;
                                    runner.RestoreAdaptiveProfile(modelBeforeLogicalCycle);
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
                                }

                                if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.Canceled)
                                    throw new OperationCanceledException(attemptToken);
                                if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.SoftwareRecovery)
                                    throw new SoftwareSelfHealingRetryException(
                                        $"EPB[{channel}] 自适应学习圈遇到软件瞬态；" +
                                        $"本次尝试作废后重做。Reason={outcome.Reason}");
                                if (!outcome.IsSuccess)
                                    throw new InvalidOperationException(
                                        $"EPB[{channel}] 自适应学习圈失败：" +
                                        $"阶段={outcome.Stage}，原因={outcome.Reason}");
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
                            }

                            await SealLearningCycleAsync(
                                    channel,
                                    learningCycleNumber,
                                    runId,
                                    learningOrdinal,
                                    "learning_completed",
                                    requireValidEvidence: true,
                                    softwareAttempt: attempt)
                                .ConfigureAwait(false);
                            learningCycleNumber = 0;

                            // 旧时序模型也只在证据可靠封存后提交，避免作废圈进入聚合。
                            if (pendingLegacySample != null)
                                runner.ApplyLearnSample(pendingLegacySample);
                        }
                        catch (SoftwareSelfHealingRetryException)
                        {
                            learningCycleNumber = 0;
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        learningCycleNumber,
                                        runId,
                                        learningOrdinal,
                                        "learning_canceled",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            learningCycleNumber = 0;
                            throw;
                        }
                        catch (Exception ex) when (EpbCycleRunner.IsSoftwareRecoveryException(ex))
                        {
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        learningCycleNumber,
                                        runId,
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
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        learningCycleNumber,
                                        runId,
                                        learningOrdinal,
                                        "learning_failed",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            learningCycleNumber = 0;
                            throw;
                        }
                    },
                    (attempt, ex, attemptToken) =>
                    {
                        runner.RestoreAdaptiveProfile(modelBeforeLogicalCycle);
                        RequireSoftwareRecoveryOutputOff(
                            channel,
                            "LearningPersistenceSelfHealing");
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "LearningPersistenceSelfHealing",
                            $"学习圈证据软件自愈第{attempt}次：本次尝试已作废，随后重做同一逻辑学习圈。",
                            affectedChannels: new[] { channel },
                            correlationId: runId,
                            allowTerminalReset: true);
                        _log?.Warn(
                            $"EPB[{channel}] 学习圈证据失败已作废，不取消其它通道或整批启动。" +
                            $"Attempt={attempt} DelayMs={GetDaqSelfMaintenanceDelayMs(attempt)} " +
                            $"Reason={ex.Message}",
                            "落盘");
                        return Task.CompletedTask;
                    },
                    GetDaqSelfMaintenanceDelayMs,
                    token)
                .ConfigureAwait(false);

            if (attempts > 1)
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Learning,
                    "LearningPersistenceSelfHealed",
                    $"学习圈证据自愈完成，共尝试{attempts}次；只保留最后一次有效学习结果。",
                    affectedChannels: new[] { channel },
                    correlationId: runId,
                    allowTerminalReset: true);
        }

        private Task SealLearningCycleAsync(
            int channel,
            int cycleNumber,
            Guid runId,
            int learningOrdinal,
            string status,
            bool requireValidEvidence = false,
            int softwareAttempt = 1)
        {
            var recorder = Recorder;
            if (cycleNumber == 0 || recorder == null) return Task.CompletedTask;

            var exportDir = System.IO.Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                "LearningCycles",
                runId.ToString("N"),
                $"EPB{channel:D2}",
                $"Learning_{learningOrdinal:D4}");
            if (softwareAttempt > 1)
                exportDir = System.IO.Path.Combine(exportDir, $"Attempt_{softwareAttempt:D4}");

            AlarmCycleSnapshotEvidence evidence;
            try
            {
                evidence = recorder.SealAndExportCycle(
                    channel,
                    cycleNumber,
                    exportDir,
                    DateTime.UtcNow,
                    status);
            }
            catch (Exception ex)
            {
                // 自定义/代理 Recorder 可能在取得圈边界后直接抛异常。显式封为软件作废，
                // 避免下一次重试继续撞到仍为 running 的旧负圈号。
                try
                {
                    recorder.AbortCycle(
                        channel,
                        cycleNumber,
                        Math.Max(0, recorder.GetCurrentCycleSampleCount(channel)),
                        DateTime.UtcNow,
                        "AbortedBySoftwareRecovery");
                }
                catch (Exception abortEx)
                {
                    _log?.Warn(
                        $"EPB[{channel}] 学习圈封存异常后的软件作废也未确认：{abortEx.Message}",
                        "落盘");
                }
                if (_currentCycleNumberByChannel.TryGetValue(channel, out var abortCurrent) &&
                    abortCurrent == cycleNumber)
                    _currentCycleNumberByChannel.TryRemove(channel, out _);

                if (requireValidEvidence)
                    throw new SoftwareSelfHealingRetryException(
                        $"EPB[{channel}] 学习圈封存/导出抛出软件异常：" +
                        $"Cycle={cycleNumber} Status={status} Error={ex.Message}",
                        ex);
                _log?.Warn(
                    $"EPB[{channel}] 失败/取消学习圈的辅助证据导出异常：{ex.Message}；" +
                    "不扩大停机范围。",
                    "落盘");
                return Task.CompletedTask;
            }

            if (_currentCycleNumberByChannel.TryGetValue(channel, out var current) &&
                current == cycleNumber)
            {
                _currentCycleNumberByChannel.TryRemove(channel, out _);
            }

            // 报警后台已取得封存权时，学习收尾只退出，不重复生成文件或改写状态。
            if (!evidence.WasClaimed)
                return Task.CompletedTask;
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
                return Task.CompletedTask;
            }

            _log?.Info(
                $"EPB[{channel}] 学习圈已封存：Run={runId:N} LearnCycle={learningOrdinal} " +
                $"InternalCycle={cycleNumber} Status={status} Samples={evidence.SampleCount} " +
                $"Dir={exportDir}",
                "落盘");
            return Task.CompletedTask;
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
                adaptiveOvershootConfirmCycles:
                    AlarmConfig?.Behavior?.AdaptiveOvershootConfirmCycles ?? 5,
                adaptiveForwardStallConfirmCycles:
                    AlarmConfig?.Behavior?.AdaptiveForwardStallConfirmCycles ?? 5,
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
                activate: null,
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

            var lease = await _hydCoordinator.EnterGenerationAsync(generationKey, channelList, token)
                .ConfigureAwait(false);
            foreach (var ch in channelList)
                _hydraulicLeaseByChannel[ch] = lease;
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
                            allowTerminalReset: true);
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
        private void OnFormalCycleCommitted(int channel, int sessionRunCount)
        {
            CompleteFormalSoftwareRecoveryAfterCommit(channel);
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
