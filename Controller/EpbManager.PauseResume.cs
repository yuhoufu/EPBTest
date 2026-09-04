using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Timing;

namespace Controller
{
    internal sealed class BatchPauseSnapshot
    {
        internal BatchPauseSnapshot(
            BatchPauseState state,
            long generation,
            Guid runId,
            long runEpoch,
            Guid commandId,
            IEnumerable<int> frozenChannels)
        {
            State = state;
            Generation = generation;
            RunId = runId;
            RunEpoch = runEpoch;
            CommandId = commandId;
            FrozenChannels = (frozenChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        internal BatchPauseState State { get; }
        internal long Generation { get; }
        internal Guid RunId { get; }
        internal long RunEpoch { get; }
        internal Guid CommandId { get; }
        internal int[] FrozenChannels { get; }

        internal bool Owns(Guid runId, long runEpoch, int channel)
        {
            return Generation > 0 && CommandId != Guid.Empty &&
                   RunId == runId && RunEpoch == runEpoch &&
                   FrozenChannels.Contains(channel);
        }
    }

    internal sealed class DaqDataContinuityCompromisedException : InvalidOperationException
    {
        internal DaqDataContinuityCompromisedException(string message) : base(message) { }
    }

    internal sealed class BatchResumePreflight
    {
        internal BatchPauseSnapshot Pause { get; set; }
        internal int[] Channels { get; set; } = Array.Empty<int>();
        internal Dictionary<int, ChannelExecutionPermit> ExecutionPermits { get; set; } =
            new Dictionary<int, ChannelExecutionPermit>();
    }

    public sealed partial class EpbManager
    {
        private readonly SemaphoreSlim _pauseResumeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, DateTime> _channelPausedUtc =
            new ConcurrentDictionary<int, DateTime>();
        private readonly object _batchPauseStateGate = new object();
        private BatchPauseSnapshot _batchPauseSnapshot =
            new BatchPauseSnapshot(
                BatchPauseState.Idle,
                0,
                Guid.Empty,
                0,
                Guid.Empty,
                Array.Empty<int>());
        private DateTime _batchPausedUtc = DateTime.MinValue;
        private int[] _batchPausedChannels = Array.Empty<int>();
        private long _qualificationGeneration;
        private Func<IReadOnlyDictionary<string, long>, CancellationToken, Task> _pausePersistenceFlush;
        private readonly object _manualPauseProgressGate = new object();
        private ManualPauseProgressSnapshot _manualPauseProgress = new ManualPauseProgressSnapshot();

        public event Action<BatchPauseStateChangedEvent> BatchPauseStateChanged;
        public event Action<ManualPauseProgressSnapshot> ManualPauseProgressChanged;

        /// <summary>
        /// 由宿主注册Raw发布/文件写入排空回调。优雅暂停只有在圈数据与原始数据链均排空后才完成。
        /// </summary>
        public void RegisterPausePersistenceFlush(
            Func<IReadOnlyDictionary<string, long>, CancellationToken, Task> flush)
        {
            Interlocked.Exchange(ref _pausePersistenceFlush, flush);
        }

        public BatchPauseState CurrentBatchPauseState =>
            Volatile.Read(ref _batchPauseSnapshot).State;

        public long CurrentBatchPauseGeneration =>
            Volatile.Read(ref _batchPauseSnapshot).Generation;

        internal BatchPauseSnapshot CaptureBatchPauseSnapshot() =>
            Volatile.Read(ref _batchPauseSnapshot);

        private bool TryCaptureExactManualPauseOwner(
            Guid runId,
            long runEpoch,
            IEnumerable<int> channels,
            out BatchPauseSnapshot pause)
        {
            pause = CaptureBatchPauseSnapshot();
            if (pause == null || pause.RunId != runId || pause.RunEpoch != runEpoch ||
                pause.CommandId == Guid.Empty ||
                (pause.State != BatchPauseState.PausePending &&
                 pause.State != BatchPauseState.Paused &&
                 pause.State != BatchPauseState.PauseHolding &&
                 pause.State != BatchPauseState.ResumeChecking))
                return false;
            var capturedPause = pause;
            var active = (channels ?? Array.Empty<int>())
                .Where(channel => _timers.ContainsKey(channel) || _runners.ContainsKey(channel))
                .Distinct()
                .ToArray();
            return active.Length > 0 &&
                   active.All(channel => capturedPause.FrozenChannels.Contains(channel));
        }

        public bool IsBatchPaused => CurrentBatchPauseState == BatchPauseState.Paused ||
                                     CurrentBatchPauseState == BatchPauseState.PauseHolding;

        public DateTime? BatchPausedUtc => _batchPausedUtc == DateTime.MinValue
            ? (DateTime?)null
            : _batchPausedUtc;

        public static int SelectManualPauseHardDeadlineMilliseconds(int periodMs)
        {
            var period = Math.Max(1, periodMs);
            var calculated = Math.Max(30000L, period * 2L + 30000L);
            return (int)Math.Min(300000L, calculated);
        }

        public ManualPauseProgressSnapshot CaptureManualPauseProgress()
        {
            lock (_manualPauseProgressGate)
            {
                var energized = _manualPauseProgress.Active
                    ? _channelRuntimeStateStore.Snapshot()
                        .Where(state => _manualPauseProgress.Channels.Contains(state.Channel) && state.Energized)
                        .Select(state => state.Channel)
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray()
                    : Array.Empty<int>();
                if (!_manualPauseProgress.EnergizedChannels.SequenceEqual(energized))
                {
                    _manualPauseProgress.EnergizedChannels = energized;
                    _manualPauseProgress.ProgressVersion++;
                }
                return _manualPauseProgress.Clone();
            }
        }

        /// <summary>
        /// 批次优雅暂停：先同时封住所有通道的下一圈，等待在途圈自然结束，
        /// 再确认电机关闭、液压释放、持久化排空并导出最近10圈。
        /// </summary>
        public async Task PauseBatchGracefullyAsync(CancellationToken token = default)
        {
            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            var hardDeadlineMs = SelectManualPauseHardDeadlineMilliseconds(PeriodMs);
            var pauseStartedUtc = DateTime.UtcNow;
            try
            {
                if (!IsBatchSessionActive)
                    throw new InvalidOperationException("当前没有可暂停的批量试验。");
                if (CurrentBatchPauseState == BatchPauseState.Paused ||
                    CurrentBatchPauseState == BatchPauseState.PauseHolding)
                    return;
                if (CurrentBatchPauseState != BatchPauseState.Running)
                    throw new InvalidOperationException(
                        $"当前批次状态为 {CurrentBatchPauseState}，只能在正式运行阶段暂停。");

                var timers = _timers
                    .Where(pair => pair.Value != null)
                    .OrderBy(pair => pair.Key)
                    .ToArray();
                if (timers.Length == 0)
                    throw new InvalidOperationException("正式阶段尚未建立，启动定位或学习阶段不能暂停。");

                var channels = timers.Select(pair => pair.Key).ToArray();
                SetBatchPauseState(BatchPauseState.PausePending, channels, "等待所有通道完成当前圈");
                var pauseTransaction = CaptureBatchPauseSnapshot();
                BeginManualPauseProgress(
                    pauseTransaction,
                    pauseStartedUtc,
                    pauseStartedUtc.AddMilliseconds(hardDeadlineMs));
                _batchPausedChannels = pauseTransaction.FrozenChannels.ToArray();
                foreach (var channel in channels)
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.PausePending,
                        "PausePending",
                        "已收到暂停请求，等待当前圈完成",
                        affectedChannels: channels,
                        correlationId: _activeBatchId);

                var pauseAll = Task.WhenAll(timers.Select(pair =>
                    pair.Value.PauseAfterCurrentCycleAsync("BatchGracefulPause")));
                var remainingMs = Math.Max(
                    1,
                    (int)Math.Ceiling((_manualPauseProgress.HardDeadlineUtc - DateTime.UtcNow).TotalMilliseconds));
                var timeout = Task.Delay(remainingMs, token);
                if (await Task.WhenAny(pauseAll, timeout).ConfigureAwait(false) != pauseAll)
                {
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException($"优雅暂停等待当前圈结束超过动态硬截止（{hardDeadlineMs}ms）。");
                }
                await pauseAll.ConfigureAwait(false);

                var interrupted = channels.Where(channel =>
                {
                    var runtime = _channelRuntimeStateStore.Get(channel);
                    return !_timers.ContainsKey(channel) || runtime == null ||
                           ChannelRuntimeStateStore.IsLatchedStop(runtime.State);
                }).ToArray();
                if (interrupted.Length > 0)
                    throw new InvalidOperationException(
                        $"暂停过程中 EPB[{string.Join(",", interrupted)}] 发生停机或联锁，" +
                        "已取消暂停并升级为立即安全停止。");

                AdvanceManualPauseProgress(
                    ManualPauseStage.PhysicalOffConfirm,
                    "当前圈已收口，正在确认电机、电源和液压安全");
                SetBatchPauseState(
                    BatchPauseState.PausePending,
                    channels,
                    "当前圈已完成，正在确认全断能");
                await CompletePauseSafetyBoundaryAsync(channels, forceHydraulicGroups: true, token)
                    .ConfigureAwait(false);
                await WaitForBatchPauseRecoveryOwnersAsync(pauseTransaction, token)
                    .ConfigureAwait(false);
                CommitBatchPausedOrThrow(pauseTransaction);
                _batchPausedUtc = DateTime.UtcNow;
                _batchPausedChannels = channels;
                foreach (var channel in channels)
                {
                    _channelPausedUtc[channel] = _batchPausedUtc;
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Paused,
                        "GracefulPaused",
                        "当前圈已完成，试验安全暂停",
                        affectedChannels: channels,
                        correlationId: _activeBatchId);
                    NonCriticalObserver.Invoke(
                        ChannelPaused,
                        channel,
                        ex => _log?.Warn($"批次暂停观察者异常，已隔离：{ex.Message}", "EPB"));
                }

                AdvanceManualPauseProgress(ManualPauseStage.Completed, "全部通道已安全暂停");
                SetBatchPauseState(BatchPauseState.Paused, channels, "全部通道已安全暂停");
                FlushPersistentLog();
            }
            catch (Exception ex)
            {
                MarkManualPauseSafetyFault(ex.Message);
                SetBatchPauseState(BatchPauseState.Stopping, _batchPausedChannels, ex.Message);
                var continuityCompromised =
                    ex is DaqDataContinuityCompromisedException ||
                    TryGetPermanentDataContinuityGap(out _);
                await StopAllAsync(
                        new StopContext
                        {
                            Source = continuityCompromised
                                ? StopSource.SystemFault
                                : StopSource.ManualUi,
                            Reason = "优雅暂停失败，已升级为立即安全停止：" + ex.Message,
                            Initiator = nameof(PauseBatchGracefullyAsync),
                            CorrelationId = Guid.NewGuid().ToString("N")
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            finally
            {
                _pauseResumeGate.Release();
            }
        }

        /// <summary>
        /// 恢复同进程内的暂停批次。运行对象、稳定模型和正式次数仍在内存中，
        /// 完成DAQ/电源自愈后直接从未来完整圈继续，不以暂停时长增加资格门禁。
        /// </summary>
        public async Task ResumeBatchAsync(CancellationToken token = default)
        {
            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            CancellationTokenSource resumeCts = null;
            var channels = Array.Empty<int>();
            var powerEnableAttempted = false;
            try
            {
                if (!IsBatchSessionActive || CurrentBatchPauseState != BatchPauseState.Paused)
                    throw new InvalidOperationException("当前没有处于安全暂停状态的批次。");

                var preflight = CaptureBatchResumePreflight();
                channels = preflight.Channels;
                EnsureNoPermanentDataContinuityGap("批次恢复预检");
                resumeCts = CreateResumeLinkedTokenSource(token, channels, includeBatchSession: true);
                var resumeToken = resumeCts.Token;

                SetBatchPauseState(BatchPauseState.ResumeChecking, channels, "正在执行恢复安全预检");
                var resumePauseGeneration = CurrentBatchPauseGeneration;
                foreach (var channel in channels)
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.ResumeChecking,
                        "ResumeChecking",
                        "正在执行DAQ、电源、配置和模型预检",
                        affectedChannels: channels,
                        correlationId: _activeBatchId);

                // 暂停态 DAQ 自愈拥有自己的终态事务。“继续试验”必须先加入该事务，
                // 不能与仍在重建/断电确认的 Dev1/Dev2 恢复并行上电。
                await Task.WhenAll(channels
                        .Select(channel => WaitForDaqRecoveryAsync(channel, resumeToken)))
                    .ConfigureAwait(false);
                EnsureBatchResumeGenerationUnchanged(resumePauseGeneration);
                EnsureBatchResumePreflightCurrent(preflight);
                await EnsureDaqReadyBeforeStartAsync(channels, resumeToken).ConfigureAwait(false);
                EnsureStrictCurveControl(channels);
                EnsureAdaptiveProfilesReady(channels);
                // DAQ健康回调只证明采集硬件仍在工作，不能消除已锁存的
                // 工程/Raw数据空洞。上电前再检一次，封住预检期间的迟到故障。
                EnsureNoPermanentDataContinuityGap("批次恢复上电门禁");
                await Task.WhenAll(channels
                        .Select(channel => WaitForDaqRecoveryAsync(channel, resumeToken)))
                    .ConfigureAwait(false);
                EnsureBatchResumeGenerationUnchanged(resumePauseGeneration);
                EnsureBatchResumePreflightCurrent(preflight);

                var plan = GetCompatibleStaggerPlan(channels);
                // All structural, ownership and DAQ checks are complete before
                // this first command that can energize a power group.
                powerEnableAttempted = true;
                await EnsurePowerSupplyReadyBeforeStartAsync(channels, resumeToken).ConfigureAwait(false);
                EnsureBatchResumePreflightCurrent(preflight);
                ResetTransientFaultStateForRestart(channels, "BatchResume");
                RejoinFormalChannelsAtSharedFutureSlot(
                    channels,
                    plan,
                    "Resumed",
                    "批次已从安全暂停边界按当前公共节律槽重新加入",
                    allowTerminalReset: false);
                powerEnableAttempted = false;
                foreach (var channel in channels)
                    _channelPausedUtc.TryRemove(channel, out _);

                _batchPausedUtc = DateTime.MinValue;
                _batchPausedChannels = Array.Empty<int>();
                AdvanceManualPauseProgress(ManualPauseStage.Resumed, "同一批次已恢复运行");
                SetBatchPauseState(BatchPauseState.Running, channels, "试验已恢复");
            }
            catch (OperationCanceledException) when (
                token.IsCancellationRequested ||
                (resumeCts?.IsCancellationRequested ?? false))
            {
                if (powerEnableAttempted)
                    await DisableBatchResumePowerBestEffortAsync(channels, "BatchResumeCanceled")
                        .ConfigureAwait(false);
                if (TryGetPermanentDataContinuityGap(out var gapDetail))
                {
                    var stop = await StopAllAsync(
                            new StopContext
                            {
                                Source = StopSource.SystemFault,
                                Reason = "批次恢复取消时已检测到永久DAQ数据空洞：" + gapDetail,
                                Initiator = nameof(ResumeBatchAsync),
                                CorrelationId = Guid.NewGuid().ToString("N")
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var failureState = SelectBatchResumeFailureState(
                        continuityCompromised: true,
                        stopFullyConfirmed: stop?.FullyConfirmed == true);
                    if (failureState == BatchPauseState.Idle)
                        MarkBatchIdle("永久DAQ数据空洞已安全停机，等待进程回收");
                    else
                        SetBatchPauseState(
                            failureState,
                            _batchPausedChannels,
                            "永久DAQ数据空洞停机尚未完全确认，禁止同进程恢复");
                }
                else if (!IsBatchSessionActive)
                    SetBatchPauseState(BatchPauseState.Idle, Array.Empty<int>(), "恢复自愈已由停止请求取消");
                else
                {
                    SetBatchPauseState(BatchPauseState.Paused, _batchPausedChannels, "恢复自愈已取消，保持安全暂停");
                    PublishChannelsHeldAfterResumeFailure(channels, "BatchResumeCanceled");
                }
                throw;
            }
            catch (Exception ex)
            {
                if (powerEnableAttempted)
                    await DisableBatchResumePowerBestEffortAsync(channels, "BatchResumeFailed")
                        .ConfigureAwait(false);
                var continuityCompromised =
                    ex is DaqDataContinuityCompromisedException ||
                    TryGetPermanentDataContinuityGap(out _);
                if (continuityCompromised)
                {
                    var stop = await StopAllAsync(
                            new StopContext
                            {
                                Source = StopSource.SystemFault,
                                Reason = "批次暂停恢复发现DAQ数据连续性已破坏：" + ex.Message,
                                Initiator = nameof(ResumeBatchAsync),
                                CorrelationId = Guid.NewGuid().ToString("N")
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var failureState = SelectBatchResumeFailureState(
                        continuityCompromised: true,
                        stopFullyConfirmed: stop?.FullyConfirmed == true);
                    if (failureState == BatchPauseState.Idle)
                        MarkBatchIdle("永久DAQ数据空洞已安全停机，等待进程回收");
                    else
                        SetBatchPauseState(
                            failureState,
                            _batchPausedChannels,
                            "永久DAQ数据空洞停机尚未完全确认，禁止同进程恢复");
                }
                else
                {
                    // 普通预检或资格失败时保持定时器暂停，允许排障后再次继续。
                    SetBatchPauseState(BatchPauseState.Paused, _batchPausedChannels, "恢复失败，保持安全暂停");
                    PublishChannelsHeldAfterResumeFailure(channels, "BatchResumeFailed");
                }
                throw;
            }
            finally
            {
                resumeCts?.Dispose();
                _pauseResumeGate.Release();
            }
        }

        /// <summary>
        /// The batch Continue command is the operator's recovery intent for any
        /// hydraulic group that was isolated by confirmed pressure evidence.  Healthy
        /// groups are already running when this method is called; every failed group is
        /// therefore handled independently and a failed preflight must not roll the
        /// healthy groups back.
        /// </summary>
        public async Task<int[]> ResumeInfrastructureAlarmGroupsAsync(
            CancellationToken token = default)
        {
            var failedGroups = new List<int>();
            var groups = CaptureHydraulicAlarmGroupsForOperatorRecovery();
            foreach (var group in groups)
            {
                try
                {
                    await ResumeHydraulicAlarmGroupAsync(group.Key, group.Value, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedGroups.Add(group.Key);
                    _log.Warn(
                        $"HydraulicGroupOperatorResumeRejected Hydraulic={group.Key} " +
                        $"Channels=[{string.Join(",", group.Value)}] Error={ex.Message}; " +
                        "故障组保持OFF，健康组继续运行。",
                        "液压协调");
                }
            }
            return failedGroups.ToArray();
        }

        private SortedDictionary<int, int[]> CaptureHydraulicAlarmGroupsForOperatorRecovery()
        {
            var result = new SortedDictionary<int, int[]>();
            var candidates = _nonRecoverableChannelFaultReasons
                .Where(pair => pair.Key >= 1 && pair.Key <= 12 &&
                               _nonRecoverableChannelFaultLatch.ContainsKey(pair.Key) &&
                               pair.Value?.IndexOf("液压硬件故障", StringComparison.Ordinal) >= 0)
                .Select(pair => pair.Key)
                .GroupBy(GetHydraulicGroupForChannel);
            foreach (var group in candidates)
            {
                if (group.Key <= 0) continue;
                var configured = _cfg.Test.Hydraulics
                    .FirstOrDefault(item => item.Id == group.Key)?.Members ?? new List<int>();
                var channels = configured
                    .Where(IsChannelEnabled)
                    .Where(channel => _nonRecoverableChannelFaultLatch.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                if (channels.Length > 0) result[group.Key] = channels;
            }
            return result;
        }

        private async Task ResumeHydraulicAlarmGroupAsync(
            int hydraulicId,
            int[] channels,
            CancellationToken token)
        {
            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease ownership = null;
            CancellationTokenSource linked = null;
            try
            {
                if (!IsBatchSessionActive || !IsFormalPhaseCommitted)
                    throw new InvalidOperationException("当前没有可执行液压组恢复的正式批次。");
                var runId = _activeBatchId;
                var runEpoch = Interlocked.Read(ref _runEpoch);
                ownership = await _recoveryOwnership.AcquireAsync(
                        hydraulicId,
                        $"OPERATOR-HYDRAULIC:{hydraulicId}:{runId:N}",
                        RecoveryOwnerPriority.AlarmChannel,
                        RecoveryOwnershipTakeoverTimeoutMs,
                        token)
                    .ConfigureAwait(false);
                linked = CancellationTokenSource.CreateLinkedTokenSource(token, ownership.Token);
                var recoveryToken = linked.Token;

                foreach (var channel in channels)
                {
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.ResumeChecking,
                        "HydraulicGroupOperatorResumeChecking",
                        $"操作员继续试验：正在复核液压{hydraulicId}整组",
                        affectedChannels: channels,
                        correlationId: runId,
                        allowTerminalReset: true,
                        allowSystemFaultReset: true);
                    RenewStopCts(channel);
                    _alarmStopLatch.BeginRun(channel);
                    _nonRecoverableChannelFaultLatch.TryRemove(channel, out _);
                }

                await EnsureDaqReadyBeforeStartAsync(channels, recoveryToken)
                    .ConfigureAwait(false);
                EnsureStrictCurveControl(channels);
                EnsureAdaptiveProfilesReady(channels);
                await EnsurePowerSupplyReadyBeforeStartAsync(channels, recoveryToken)
                    .ConfigureAwait(false);

                var plan = GetCompatibleStaggerPlan(channels);
                var positioning = await PreReleaseBatchWithPlanAsync(
                        channels,
                        null,
                        plan,
                        recoveryToken)
                    .ConfigureAwait(false);
                if (positioning.Any())
                    throw new InvalidOperationException(
                        $"液压{hydraulicId}恢复定位失败：" +
                        string.Join(";", positioning.Select(item =>
                            $"EPB{item.Channel}:{item.Code}")));

                var qualificationFailed = await RunPausedQualificationAsync(
                        channels,
                        2,
                        recoveryToken)
                    .ConfigureAwait(false);
                if (qualificationFailed.Any())
                    throw new InvalidOperationException(
                        $"液压{hydraulicId}资格复核失败：" +
                        $"EPB[{string.Join(",", qualificationFailed)}]");

                ResetTransientFaultStateForRestart(
                    channels,
                    "HydraulicGroupOperatorResume");
                RejoinFormalChannelsAtSharedFutureSlot(
                    channels,
                    plan,
                    "HydraulicGroupOperatorResumed",
                    $"液压{hydraulicId}整组预检通过，已从未来公共槽重新加入",
                    allowTerminalReset: true,
                    allowSystemFaultReset: true);
                foreach (var channel in channels)
                {
                    _nonRecoverableChannelFaultReasons.TryRemove(channel, out _);
                    ClearAlarmIndicatorAfterRecoveryBestEffort(channel);
                }
                _log.Info(
                    $"HydraulicGroupOperatorResumed Hydraulic={hydraulicId} " +
                    $"Channels=[{string.Join(",", channels)}] Run={runId:N}/{runEpoch}",
                    "液压协调");
            }
            catch
            {
                var reason =
                    $"液压硬件故障恢复预检未通过；液压{hydraulicId}整组保持OFF，健康组继续运行。";
                try
                {
                    await _hydCoordinator.ForceReleaseAsync(
                            hydraulicId,
                            "HydraulicGroupOperatorResumeRejected")
                        .ConfigureAwait(false);
                }
                catch { }
                foreach (var channel in channels)
                {
                    try { CommandEpbOffHighPriority(channel, "HydraulicGroupOperatorResumeRejected"); }
                    catch { }
                    _nonRecoverableChannelFaultLatch[channel] = 0;
                    _nonRecoverableChannelFaultReasons[channel] = reason;
                    _alarmStopLatch.TryRequestStop(channel);
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.AlarmStopped,
                        "HydraulicGroupOperatorResumeRejected",
                        reason,
                        affectedChannels: channels,
                        correlationId: _activeBatchId,
                        allowTerminalReset: true,
                        allowSystemFaultReset: true);
                }
                throw;
            }
            finally
            {
                linked?.Dispose();
                ownership?.Dispose();
                _pauseResumeGate.Release();
            }
        }

        private void EnsureBatchResumeGenerationUnchanged(long expectedGeneration)
        {
            var snapshot = CaptureBatchPauseSnapshot();
            if (snapshot.Generation != expectedGeneration ||
                snapshot.State != BatchPauseState.ResumeChecking)
                throw new InvalidOperationException(
                    "批次暂停代次在DAQ恢复期间已变化，拒绝提交旧恢复结果。" +
                    $" Expected={expectedGeneration}/ResumeChecking" +
                    $" Actual={snapshot.Generation}/{snapshot.State}");
        }

        private BatchResumePreflight CaptureBatchResumePreflight()
        {
            var pause = CaptureBatchPauseSnapshot();
            if (pause.State != BatchPauseState.Paused ||
                pause.CommandId == Guid.Empty ||
                pause.RunId != _activeBatchId ||
                pause.RunEpoch != Interlocked.Read(ref _runEpoch))
                throw new InvalidOperationException("批次暂停所有权或运行代际无效，拒绝在上电前继续试验。");
            var channels = (pause.FrozenChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (channels.Length == 0)
                throw new InvalidOperationException("暂停批次没有冻结通道，拒绝继续试验。");
            if (!IsFormalPhaseCommitted)
                throw new InvalidOperationException("正式批次尚未提交，拒绝继续试验。");

            var permits = new Dictionary<int, ChannelExecutionPermit>();
            foreach (var channel in channels)
            {
                if (!_timers.ContainsKey(channel) || !_runners.ContainsKey(channel))
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 暂停运行对象缺失；未执行任何上电操作。");
                var runtime = _channelRuntimeStateStore.Get(channel);
                if (runtime == null || runtime.State != ChannelRuntimeState.Paused ||
                    runtime.RunId != pause.RunId || runtime.RunEpoch != pause.RunEpoch)
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 不处于同一暂停所有权；State={runtime?.State}，未执行任何上电操作。");
                var permit = _channelExecutionFence.Capture(channel);
                if (!IsChannelExecutionPermitCurrent(channel, permit))
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 执行许可已撤销；未执行任何上电操作。");
                permits[channel] = permit;
            }
            return new BatchResumePreflight
            {
                Pause = pause,
                Channels = channels,
                ExecutionPermits = permits
            };
        }

        private void EnsureBatchResumePreflightCurrent(BatchResumePreflight preflight)
        {
            if (preflight == null) throw new InvalidOperationException("批次恢复预检凭证缺失。");
            var pause = CaptureBatchPauseSnapshot();
            if (!IsBatchSessionActive || !IsFormalPhaseCommitted ||
                pause.State != BatchPauseState.ResumeChecking ||
                pause.Generation != preflight.Pause.Generation ||
                pause.CommandId != preflight.Pause.CommandId ||
                pause.RunId != preflight.Pause.RunId ||
                pause.RunEpoch != preflight.Pause.RunEpoch)
                throw new InvalidOperationException("批次恢复预检期间暂停所有权已变化，拒绝上电。");
            foreach (var channel in preflight.Channels)
            {
                if (!_timers.ContainsKey(channel) || !_runners.ContainsKey(channel) ||
                    !preflight.ExecutionPermits.TryGetValue(channel, out var permit) ||
                    !IsChannelExecutionPermitCurrent(channel, permit))
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 恢复预检凭证已失效，拒绝上电。");
                if (IsDaqRecoveryActiveForChannel(channel))
                    throw new InvalidOperationException(
                        $"EPB[{channel}] DAQ恢复尚未终态，拒绝上电。");
            }
        }

        private async Task DisableBatchResumePowerBestEffortAsync(
            IEnumerable<int> channels,
            string reason)
        {
            if (_powerSupply == null) return;
            var groups = (channels ?? Array.Empty<int>())
                .Select(GetElectricalGroupId)
                .Where(groupId => groupId > 0)
                .Distinct()
                .OrderBy(groupId => groupId)
                .ToArray();
            try
            {
                await Task.WhenAll(groups.Select(groupId =>
                        _powerSupply.DisableGroupAsync(
                            groupId,
                            reason ?? "BatchResumeRollback",
                            CancellationToken.None)))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Error(
                    $"批次恢复失败后的电源回滚异常 Groups=[{string.Join(",", groups)}]: {ex.Message}",
                    "程控电源",
                    ex);
            }
        }

        public async Task PauseChannelGracefullyAsync(int channel, CancellationToken token = default)
        {
            if (channel < 1 || channel > 12) throw new ArgumentOutOfRangeException(nameof(channel));
            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!_timers.TryGetValue(channel, out var timer))
                    throw new InvalidOperationException($"EPB[{channel}] 当前没有运行中的正式试验。");

                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.PausePending,
                    "ChannelPausePending",
                    "等待当前圈完成后暂停",
                    correlationId: _activeBatchId);
                var pause = timer.PauseAfterCurrentCycleAsync("ChannelGracefulPause");
                var timeoutMs = Math.Max(30000, Math.Min(180000, PeriodMs * 2 + 30000));
                if (await Task.WhenAny(pause, Task.Delay(timeoutMs, token)).ConfigureAwait(false) != pause)
                    throw new TimeoutException(
                        $"EPB[{channel}] 等待当前圈结束超时（{timeoutMs}ms）。");
                await pause.ConfigureAwait(false);
                // 封住下一圈后立即退出后续液压代次的参与者集合，避免兄弟通道新一圈等待已暂停成员。
                UnmarkHydraulicParticipant(channel);
                await CompletePauseSafetyBoundaryAsync(
                        new[] { channel },
                        forceHydraulicGroups: false,
                        token)
                    .ConfigureAwait(false);
                _channelPausedUtc[channel] = DateTime.UtcNow;
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Paused,
                    "ChannelGracefulPaused",
                    "通道已完成当前圈并安全暂停",
                    correlationId: _activeBatchId);
                NonCriticalObserver.Invoke(
                    ChannelPaused,
                    channel,
                    ex => _log?.Warn(
                        $"EPB[{channel}] 暂停观察者异常已隔离：{ex.Message}",
                        "EPB"));
            }
            catch (Exception ex)
            {
                var continuityCompromised =
                    ex is DaqDataContinuityCompromisedException ||
                    TryGetPermanentDataContinuityGap(out _);
                if (continuityCompromised)
                    await StopAllAsync(
                            new StopContext
                            {
                                Source = StopSource.SystemFault,
                                Reason = $"EPB[{channel}]优雅暂停发现DAQ数据连续性已破坏：{ex.Message}",
                                Initiator = nameof(PauseChannelGracefullyAsync),
                                CorrelationId = Guid.NewGuid().ToString("N")
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                else
                    // 单通道优雅暂停未能完成安全边界时，立即停止该通道，不能遗留在半暂停状态。
                    try { StopChannel(channel); } catch { }
                throw;
            }
            finally
            {
                _pauseResumeGate.Release();
            }
        }

        public async Task ResumePausedChannelAsync(int channel, CancellationToken token = default)
        {
            if (channel < 1 || channel > 12) throw new ArgumentOutOfRangeException(nameof(channel));
            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            CancellationTokenSource resumeCts = null;
            try
            {
                if (!_channelPausedUtc.TryGetValue(channel, out _) ||
                    !_timers.TryGetValue(channel, out var timer) ||
                    !_runners.ContainsKey(channel))
                    throw new InvalidOperationException($"EPB[{channel}] 不处于可恢复的安全暂停状态。");
                EnsureNoPermanentDataContinuityGap("单通道恢复预检");
                resumeCts = CreateResumeLinkedTokenSource(
                    token,
                    new[] { channel },
                    includeBatchSession: IsBatchSessionActive);
                var resumeToken = resumeCts.Token;

                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.ResumeChecking,
                    "ChannelResumeChecking",
                    "正在执行通道恢复预检",
                    correlationId: _activeBatchId);
                ResetTransientFaultStateForRestart(new[] { channel }, "ChannelResume");
                await EnsureDaqReadyBeforeStartAsync(new[] { channel }, resumeToken).ConfigureAwait(false);
                EnsureStrictCurveControl(new[] { channel });
                EnsureAdaptiveProfilesReady(new[] { channel });
                await EnsurePowerSupplyReadyBeforeStartAsync(new[] { channel }, resumeToken)
                    .ConfigureAwait(false);
                EnsureNoPermanentDataContinuityGap("单通道恢复上电门禁");

                var plan = GetCompatibleStaggerPlan(new[] { channel });
                RejoinFormalChannelsAtSharedFutureSlot(
                    new[] { channel },
                    plan,
                    "ChannelResumed",
                    "通道已从安全暂停边界按当前公共节律槽重新加入",
                    allowTerminalReset: false);
                _channelPausedUtc.TryRemove(channel, out _);
            }
            catch (Exception ex)
            {
                if (ex is DaqDataContinuityCompromisedException ||
                    TryGetPermanentDataContinuityGap(out _))
                    await StopAllAsync(
                            new StopContext
                            {
                                Source = StopSource.SystemFault,
                                Reason = $"EPB[{channel}]暂停恢复发现DAQ数据连续性已破坏：{ex.Message}",
                                Initiator = nameof(ResumePausedChannelAsync),
                                CorrelationId = Guid.NewGuid().ToString("N")
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                else if (_channelPausedUtc.ContainsKey(channel) && _timers.ContainsKey(channel))
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Paused,
                        "ChannelResumeFailed",
                        "通道恢复未完成，保持安全暂停，可再次继续",
                        affectedChannels: new[] { channel },
                        correlationId: _activeBatchId);
                throw;
            }
            finally
            {
                resumeCts?.Dispose();
                _pauseResumeGate.Release();
            }
        }

        internal void MarkBatchRunning(IEnumerable<int> channels, string reason)
        {
            Interlocked.Exchange(ref _formalPhaseCommitted, 1);
            _batchPausedUtc = DateTime.MinValue;
            _batchPausedChannels = Array.Empty<int>();
            SetBatchPauseState(
                BatchPauseState.Running,
                channels?.Distinct().OrderBy(x => x).ToArray() ?? Array.Empty<int>(),
                reason ?? "正式试验运行中");
        }

        private void PublishChannelsHeldAfterResumeFailure(
            IEnumerable<int> channels,
            string reasonCode)
        {
            var selected = (channels ?? Array.Empty<int>())
                .Distinct()
                .Where(channel => _timers.ContainsKey(channel) && !IsAlarmStopRequested(channel))
                .OrderBy(channel => channel)
                .ToArray();
            foreach (var channel in selected)
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Paused,
                    reasonCode,
                    "恢复未完成，保持安全暂停，可再次继续",
                    affectedChannels: selected,
                    correlationId: _activeBatchId);
        }

        internal void MarkBatchIdle(string reason)
        {
            _batchPausedUtc = DateTime.MinValue;
            _batchPausedChannels = Array.Empty<int>();
            _channelPausedUtc.Clear();
            SetBatchPauseState(BatchPauseState.Idle, Array.Empty<int>(), reason ?? "批次已结束");
        }

        private async Task CompletePauseSafetyBoundaryAsync(
            int[] channels,
            bool forceHydraulicGroups,
            CancellationToken token)
        {
            var selected = (channels ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
            EnsureNoPermanentDataContinuityGap("暂停安全边界");
            foreach (var channel in selected)
            {
                if (!CommandEpbOffHighPriority(channel, "GracefulPause"))
                    throw new InvalidOperationException($"EPB[{channel}] 暂停时电机关闭命令失败。");
            }

            if (forceHydraulicGroups)
            {
                var hydraulicIds = selected.Select(channel => channel <= 6 ? 1 : 2).Distinct().ToArray();
                await Task.WhenAll(hydraulicIds.Select(id =>
                        _hydCoordinator.ForceReleaseAsync(id, "GracefulPause")))
                    .ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAll(selected.Select(HydraulicMarkReleaseAsync)).ConfigureAwait(false);
            }

            if (_powerSupply == null)
                throw new InvalidOperationException("暂停安全边界缺少程控电源协调器。");
            if (forceHydraulicGroups)
                await _powerSupply.DisableAllAsync("GracefulPause", token).ConfigureAwait(false);
            else
            {
                // The manual gate serializes pause/resume. A paused channel can
                // retain a timer, while a live peer must retain its shared supply.
                var idleGroups = SelectPowerGroupsForChannelPause(_cfg.Test.Groups, selected, channel =>
                    IsHydraulicParticipant(channel) ||
                    (Interlocked.Read(ref _energizedChannelsMask) & (1L << channel)) != 0 ||
                    (!_channelPausedUtc.ContainsKey(channel) && (_timers.ContainsKey(channel) || _runners.ContainsKey(channel))));
                await Task.WhenAll(idleGroups.Select(group =>
                    _powerSupply.DisableGroupAsync(group, "ChannelGracefulPause", token))).ConfigureAwait(false);
            }

            if (CurrentBatchPauseState == BatchPauseState.PausePending)
            {
                AdvanceManualPauseProgress(
                    ManualPauseStage.PersistenceDrain,
                    "全断能命令已提交，正在排空 Raw、SQLite 与最近圈证据");
                SetBatchPauseState(
                    BatchPauseState.PausePending,
                    selected,
                    "全断能已提交，正在完成数据持久化边界");
            }

            var flushRaw = Volatile.Read(ref _pausePersistenceFlush);
            if (flushRaw == null)
                throw new InvalidOperationException("暂停Raw最终落盘回调未注册。");
            var pauseBoundaries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["Dev1"] = _acq.GetLastAcceptedSequence("Dev1"),
                ["Dev2"] = _acq.GetLastAcceptedSequence("Dev2")
            };
            try
            {
                await flushRaw(pauseBoundaries, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("暂停时Raw数据链排空/刷新未完成。", ex);
            }

            // Raw/工程链排到 pauseBoundaries 后才能等SQLite。若先Drain SQLite，
            // 上游在后续Raw drain中新移交的批次会落在门禁之后，暂停仍可虚假成功。
            var persistenceDeadline = Stopwatch.GetTimestamp() + 10L * Stopwatch.Frequency;
            foreach (var pair in pauseBoundaries)
            {
                var remainingMs = Math.Max(
                    1,
                    (int)((persistenceDeadline - Stopwatch.GetTimestamp()) * 1000.0 /
                          Stopwatch.Frequency));
                if (!await _persistence.WaitForDurablePrefixAsync(
                        pair.Key,
                        pair.Value,
                        remainingMs,
                        token).ConfigureAwait(false))
                    throw new TimeoutException(
                        $"暂停时{pair.Key}持久化前缀未越过序号{pair.Value}。");
            }
            EnsureNoPermanentDataContinuityGap("暂停提交门禁");

            try
            {
                var recorder = Recorder ??
                               throw new InvalidOperationException("暂停时Recorder不可用。");
                await Task.Run(() =>
                {
                    foreach (var channel in selected)
                        recorder.FlushRecent(channel, 10);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"暂停时最近10圈证据导出失败：{ex.Message}；暂停不能标记为完整完成。",
                    "落盘",
                    ex);
                throw new InvalidOperationException("暂停时最近10圈证据导出失败。", ex);
            }
        }

        internal static int[] SelectPowerGroupsForChannelPause(IEnumerable<ElectricalGroup> groups,
            IEnumerable<int> channels, Func<int, bool> peerMayExecute)
        {
            var selected = new HashSet<int>(channels ?? Array.Empty<int>());
            var topology = (groups ?? Enumerable.Empty<ElectricalGroup>()).ToArray();
            if (peerMayExecute == null || selected.Count == 0 || topology.Any(group => group == null || group.Id < 1 || group.Id > 4) ||
                topology.Select(group => group.Id).Distinct().Count() != topology.Length || selected.Any(channel => channel < 1 || channel > 12 ||
                topology.Count(group => group.Id > 0 && group.Members.Contains(channel)) != 1))
                throw new InvalidOperationException("ChannelPausePowerTopologyUnproven");
            return topology.Where(group => group.Members.Any(selected.Contains) &&
                    !group.Members.Any(channel => !selected.Contains(channel) && peerMayExecute(channel)))
                .Select(group => group.Id).Distinct().OrderBy(group => group).ToArray();
        }

        private bool TryGetPermanentDataContinuityGap(out string detail)
        {
            var gaps = new List<string>();
            var dev1Gap = false;
            var dev2Gap = false;
            foreach (var device in new[] { "Dev1", "Dev2" })
                if (_acq != null && _acq.TryGetDataContinuityGap(
                        device,
                        out var firstGap,
                        out var lastObserved))
                {
                    gaps.Add($"{device}:FirstGap={firstGap},LastObserved={lastObserved}," +
                             $"AbandonedTail={Math.Max(0, lastObserved - firstGap + 1)}");
                    if (string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase))
                        dev1Gap = true;
                    else
                        dev2Gap = true;
                }
            detail = string.Join("; ", gaps);
            return MustBlockPauseOrResumeForContinuity(dev1Gap, dev2Gap);
        }

        internal static bool MustBlockPauseOrResumeForContinuity(bool dev1Gap, bool dev2Gap)
            => dev1Gap || dev2Gap;

        internal static BatchPauseState SelectBatchResumeFailureState(
            bool continuityCompromised,
            bool stopFullyConfirmed)
        {
            if (!continuityCompromised) return BatchPauseState.Paused;
            return stopFullyConfirmed ? BatchPauseState.Idle : BatchPauseState.Stopping;
        }

        private void EnsureNoPermanentDataContinuityGap(string operation)
        {
            if (TryGetPermanentDataContinuityGap(out var detail))
                throw new DaqDataContinuityCompromisedException(
                    $"{operation}禁止同进程继续。Code=DaqDataContinuityGap; {detail}");
        }

        private async Task<int[]> RunPausedQualificationAsync(
            int[] channels,
            int cycles,
            CancellationToken token)
        {
            var selected = channels.Distinct().OrderBy(x => x).ToArray();
            EnsureAdaptiveProfilesReady(selected);
            var groups = GroupByPressure(selected);
            var staggerPlan = GetCompatibleStaggerPlan(selected);
            var quarantined = new ConcurrentDictionary<int, string>();

            for (var ordinal = 1; ordinal <= cycles; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                var tasks = new List<Task>();
                var participantsByHydraulic = groups
                    .Where(pair => pair.Value != null)
                    .Select(pair => new
                    {
                        HydraulicId = pair.Key,
                        Members = pair.Value
                            .Where(channel => !quarantined.ContainsKey(channel))
                            .Where(channel => !IsMechanicalTargetReached(channel))
                            .OrderBy(channel => channel)
                            .ToArray()
                    })
                    .Where(item => item.Members.Length > 0)
                    .ToDictionary(
                        item => item.HydraulicId,
                        item => (IReadOnlyList<int>)item.Members);
                if (participantsByHydraulic.Count == 0) break;

                var slot = Interlocked.Increment(ref _qualificationGeneration);
                var globalTimelineUtc = CeilToBoundary(
                    DateTime.UtcNow.AddMilliseconds(Math.Max(2, AnchorWarmupMs)),
                    PeriodMs);
                var globalSlot = await EnterGlobalHydraulicSlotAsync(
                        _activeBatchId,
                        HydraulicPhaseKind.Qualification,
                        slot,
                        participantsByHydraulic,
                        globalTimelineUtc,
                        globalTimelineUtc,
                        staggerPlan,
                        startupSelfHealing: true,
                        token)
                    .ConfigureAwait(false);

                foreach (var pair in participantsByHydraulic)
                {
                    var groupChannels = pair.Value.OrderBy(x => x).ToArray();
                    if (groupChannels.Length == 0) continue;
                    foreach (var channel in groupChannels)
                    {
                        var capturedChannel = channel;
                        var capturedOrdinal = ordinal;
                        var capturedPressureGroup = pair.Key;
                        var phase = staggerPlan.Get(channel).PhaseMs;
                        var channelStopToken = CancellationToken.None;
                        if (_stopCtsByChannel.TryGetValue(channel, out var channelStopCts))
                        {
                            try { channelStopToken = channelStopCts.Token; }
                            catch (ObjectDisposedException) { }
                        }
                        tasks.Add(Task.Run(async () =>
                        {
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                                token,
                                channelStopToken);
                            var channelToken = linked.Token;
                            await FaultIsolatedPhaseWork.RunAsync(
                                async () =>
                                {
                                    channelToken.ThrowIfCancellationRequested();
                                    if (globalSlot.HasFailures)
                                    {
                                        if (globalSlot.Groups.TryGetValue(capturedPressureGroup, out var outcome) &&
                                            outcome.IsSuccess)
                                            return;
                                        globalSlot.GetLeaseOrThrow(capturedPressureGroup);
                                        return;
                                    }

                                    globalSlot.GetLeaseOrThrow(capturedPressureGroup);
                                    var dueUtc = globalSlot.MotorAnchorUtc.Value
                                        .AddMilliseconds(phase);
                                    var delay = dueUtc - DateTime.UtcNow;
                                    if (delay.TotalMilliseconds > 1)
                                        await Task.Delay(delay, channelToken).ConfigureAwait(false);

                                    await RunQualificationLogicalCycleWithSelfHealingAsync(
                                            capturedChannel,
                                            capturedPressureGroup,
                                            capturedOrdinal,
                                            channelToken)
                                        .ConfigureAwait(false);
                                },
                                token,
                                (ex, channelCanceled) =>
                                {
                                    quarantined[capturedChannel] =
                                        channelCanceled ? "ChannelCanceled" : ex.Message;
                                    if (channelCanceled) return;
                                    UnmarkHydraulicParticipant(capturedChannel);
                                    try { CommandEpbOff(capturedChannel, "QualificationChannelIsolation"); }
                                    catch { }
                                    _log.Error(
                                        $"EPB[{capturedChannel}] 资格复核失败已按通道/故障组隔离，" +
                                        $"其他健康通道继续。原因={ex.Message}",
                                        "EPB",
                                        ex);
                                }).ConfigureAwait(false);
                        }, token));
                    }
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                if (globalSlot.HasFailures &&
                    groups.Values.SelectMany(list => list ?? new List<int>())
                        .Any(channel => !quarantined.ContainsKey(channel) &&
                                        !IsMechanicalTargetReached(channel)))
                {
                    _log?.Warn(
                        $"暂停资格逻辑圈{ordinal}遇到降级液压槽，健康组将在新全局槽重试本圈。",
                        "液压全局槽");
                    ordinal--;
                }
            }

            return quarantined.Keys.OrderBy(x => x).ToArray();
        }

        private async Task RunQualificationLogicalCycleWithSelfHealingAsync(
            int channel,
            int pressureGroup,
            int qualificationOrdinal,
            CancellationToken token)
        {
            if (!_runners.TryGetValue(channel, out var runner))
                throw new InvalidOperationException($"EPB[{channel}] Runner 已丢失。");

            var runId = _activeBatchId;
            var learningEvidence = CaptureLearningEvidenceContext(runId);
            var modelBeforeLogicalCycle = runner.CaptureAdaptiveProfile();
            var cycleNumber = 0;
            var runEpoch = Interlocked.Read(ref _runEpoch);
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
                                $"EPB[{channel}] 资格重试前已达到机械目标圈，禁止再做一圈。",
                                "EPB");
                            return;
                        }
                        await EnsurePowerSupplyReadyForChannelsAsync(new[] { channel }, attemptToken)
                            .ConfigureAwait(false);
                        if (attempt > 1)
                            await EnterHydraulicStartupPhaseWithSelfHealingAsync(
                                    new HydraulicGenerationKey(
                                        runId,
                                        pressureGroup,
                                        HydraulicPhaseKind.Qualification,
                                        Interlocked.Increment(ref _qualificationGeneration)),
                                    new[] { channel },
                                    attemptToken)
                                .ConfigureAwait(false);
                        try
                        {
                            long trustedAttemptId = 0;
                            var trustedCycleNumber = 0;
                            try
                            {
                                cycleNumber = Recorder?.BeginLearningCycle(channel, DateTime.UtcNow) ?? 0;
                            }
                            catch (Exception ex)
                            {
                                throw new SoftwareSelfHealingRetryException(
                                    $"EPB[{channel}] 无法建立资格圈落盘边界。",
                                    ex);
                            }
                            if (cycleNumber != 0)
                            {
                                MarkCurrentCycleNumber(channel, cycleNumber);
                                _currentAttemptIdByChannel.TryGetValue(channel, out trustedAttemptId);
                            }

                            var outcome = await runner.RunOneAdaptiveLearningAsync(PeriodMs, attemptToken)
                                .ConfigureAwait(false);
                            if (outcome.MechanicalCycleCompleted)
                                OnMechanicalCycleCompleted(
                                    channel,
                                    CycleAttemptKind.Qualification,
                                    cycleNumber);
                            if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.Canceled)
                                throw new OperationCanceledException(attemptToken);
                            if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.SoftwareRecovery)
                            {
                                RecordWatchdogSoftwareAbort(channel);
                                throw new SoftwareSelfHealingRetryException(
                                    $"EPB[{channel}] 资格圈遇到软件瞬态；" +
                                    $"本次尝试作废后重做。Reason={outcome.Reason}");
                            }
                            if (!outcome.IsSuccess &&
                                outcome.Reason?.IndexOf(
                                    "DaqSampleStale",
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                await SealLearningCycleAsync(
                                        channel,
                                        cycleNumber,
                                        runId,
                                        learningEvidence,
                                        qualificationOrdinal,
                                        "qualification_failed",
                                        requireValidEvidence: true,
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                                cycleNumber = 0;
                                RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                                await WaitForDaqRecoveryAsync(channel, attemptToken).ConfigureAwait(false);
                                throw new SoftwareSelfHealingRetryException(
                                    $"EPB[{channel}] 资格圈遇到DAQ陈旧数据；已作废并等待恢复后重做。");
                            }
                            if (!outcome.IsSuccess)
                                throw new InvalidOperationException(
                                    $"EPB[{channel}] 资格圈失败：{outcome.Stage}/{outcome.Reason}");
                            trustedCycleNumber = cycleNumber;
                            _watchdogConsecutiveSoftwareAborts[channel] = 0;

                            await SealLearningCycleAsync(
                                    channel,
                                    cycleNumber,
                                    runId,
                                    learningEvidence,
                                    qualificationOrdinal,
                                    "qualification_completed",
                                    requireValidEvidence: true,
                                    softwareAttempt: attempt)
                                .ConfigureAwait(false);
                            cycleNumber = 0;
                            try
                            {
                                SaveAdaptiveProfileWithReceipt(runner.CaptureAdaptiveProfile());
                                UpdateLearningAttemptReceiptStatus(
                                    learningEvidence,
                                    channel, qualificationOrdinal, attempt, "Successful", string.Empty,
                                    qualification: true);
                                TryClearChannelWarningOverlayAfterTrustedCycle(
                                    channel,
                                    runId,
                                    runEpoch,
                                    trustedCycleNumber,
                                    trustedAttemptId,
                                    outcome,
                                    "Qualification");
                            }
                            catch (EpbAdaptiveProfilePersistenceFatalException fatalEx)
                            {
                                // Preserve the non-retryable persistence fatal
                                // identity even when failed-receipt I/O also
                                // fails.  Runner state is restored first.
                                RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                                TryUpdateLearningAttemptReceiptStatusBestEffort(
                                    learningEvidence,
                                    channel,
                                    qualificationOrdinal,
                                    attempt,
                                    "ModelCommitFatal:" + fatalEx.Message,
                                    qualification: true,
                                    failureKind: "Fatal",
                                    originalFailure: fatalEx);
                                throw;
                            }
                            catch (Exception saveEx)
                            {
                                // Keep runner memory transactional with the
                                // store: SaveWithReceipt can restore disk while
                                // the runner still holds the failed model.
                                RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                                TryUpdateLearningAttemptReceiptStatusBestEffort(
                                    learningEvidence,
                                    channel,
                                    qualificationOrdinal,
                                    attempt,
                                    "ModelCommitFailed:" + saveEx.Message,
                                    qualification: true,
                                    failureKind: "Failure",
                                    originalFailure: saveEx);
                                throw;
                            }
                        }
                        catch (SoftwareSelfHealingRetryException)
                        {
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                            // 软件瞬态也必须把本次负圈封成明确终态。V2.12.0.2 在这里
                            // 直接把局部圈号清零，Recorder.CurrentCycle 仍保持 running，
                            // 后续每次资格重试都会永久失败于“上一圈尚未封存”。
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        cycleNumber,
                                        runId,
                                        learningEvidence,
                                        qualificationOrdinal,
                                        "qualification_failed",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            cycleNumber = 0;
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        cycleNumber,
                                        runId,
                                        learningEvidence,
                                        qualificationOrdinal,
                                        "qualification_canceled",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            cycleNumber = 0;
                            throw;
                        }
                        catch
                        {
                            // Ordinary IOException and other software failures
                            // must restore the runner before self-healing/failure
                            // handling, not merely roll back the profile store.
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        cycleNumber,
                                        runId,
                                        learningEvidence,
                                        qualificationOrdinal,
                                        "qualification_failed",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            cycleNumber = 0;
                            throw;
                        }
                        finally
                        {
                            if (_hydraulicLeaseByChannel.TryGetValue(channel, out var activeScope) &&
                                !activeScope.IsClosed)
                                await AbortHydraulicLeaseForChannelAsync(
                                        channel,
                                        "QualificationAttemptFinalizer")
                                    .ConfigureAwait(false);
                        }
                    },
                    async (attempt, ex, attemptToken) =>
                    {
                            RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                        await AbortHydraulicLeaseForChannelAsync(
                                channel,
                                "QualificationPersistenceSelfHealing")
                            .ConfigureAwait(false);
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Qualification,
                            "QualificationPersistenceSelfHealing",
                            $"资格圈证据软件自愈第{attempt}次：本次尝试已作废，随后重做同一资格圈。",
                            affectedChannels: new[] { channel },
                            correlationId: runId,
                            allowTerminalReset: false);
                        _log.Warn(
                            $"EPB[{channel}] 资格圈证据失败已作废，不停止批次。" +
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
                    ChannelRuntimeState.Qualification,
                    "QualificationPersistenceSelfHealed",
                    $"资格圈证据自愈完成，共尝试{attempts}次；只保留最后一次有效资格结果。",
                    affectedChannels: new[] { channel },
                    correlationId: runId,
                    allowTerminalReset: false);
        }

        private void SetBatchPauseState(BatchPauseState state, int[] channels, string reason)
        {
            BatchPauseSnapshot snapshot;
            // 与DAQ恢复终态提交共用收口门：暂停代次一旦发布，恢复事务必须
            // 先确认电源OFF；反之已取得提交门的事务完成后，暂停流程会立即
            // 看到其终态并按正常暂停路径断能。
            lock (_daqRecoveryCommitGate)
            {
                lock (_batchPauseStateGate)
                {
                    var previous = _batchPauseSnapshot;
                    var generation = previous.Generation;
                    var runId = previous.RunId;
                    var runEpoch = previous.RunEpoch;
                    var commandId = previous.CommandId;
                    var frozenChannels = previous.FrozenChannels;
                    if (state == BatchPauseState.PausePending &&
                        previous.State != BatchPauseState.PausePending &&
                        previous.State != BatchPauseState.Paused &&
                        previous.State != BatchPauseState.ResumeChecking &&
                        previous.State != BatchPauseState.Qualification)
                    {
                        generation++;
                        runId = _activeBatchId;
                        runEpoch = Interlocked.Read(ref _runEpoch);
                        commandId = Guid.NewGuid();
                        frozenChannels = (channels ?? Array.Empty<int>())
                            .Distinct()
                            .OrderBy(channel => channel)
                            .ToArray();
                    }
                    else if (state == BatchPauseState.Idle ||
                             (state == BatchPauseState.Running &&
                              previous.State != BatchPauseState.ResumeChecking &&
                              previous.State != BatchPauseState.Qualification))
                    {
                        runId = _activeBatchId;
                        runEpoch = Interlocked.Read(ref _runEpoch);
                        commandId = Guid.Empty;
                        frozenChannels = Array.Empty<int>();
                    }
                    snapshot = new BatchPauseSnapshot(
                        state,
                        generation,
                        runId,
                        runEpoch,
                        commandId,
                        frozenChannels);
                    Volatile.Write(ref _batchPauseSnapshot, snapshot);
                }
            }
            var update = new BatchPauseStateChangedEvent
            {
                Generation = snapshot.Generation,
                State = state,
                Channels = channels?.Distinct().OrderBy(x => x).ToArray() ?? Array.Empty<int>(),
                TimestampUtc = DateTime.UtcNow,
                Reason = reason ?? string.Empty,
                RunId = snapshot.RunId == Guid.Empty ? _activeBatchId : snapshot.RunId,
                RunEpoch = snapshot.RunEpoch,
                CommandId = snapshot.CommandId
            };
            _log?.Info(
                $"批次暂停状态={state} Generation={update.Generation} " +
                $"Channels=[{string.Join(",", update.Channels)}] Reason={update.Reason}",
                "EPB");
            NonCriticalObserver.Invoke(
                BatchPauseStateChanged,
                update,
                ex => _log?.Warn($"批次暂停状态观察者异常，已隔离：{ex.Message}", "EPB"));
        }

        private void CommitBatchPausedOrThrow(BatchPauseSnapshot expected)
        {
            if (expected == null || expected.CommandId == Guid.Empty)
                throw new InvalidOperationException("暂停事务身份缺失，拒绝提交 Paused。");

            lock (_daqRecoveryCommitGate)
            {
                var current = CaptureBatchPauseSnapshot();
                if (current.State != BatchPauseState.PausePending ||
                    current.Generation != expected.Generation ||
                    current.RunId != expected.RunId ||
                    current.RunEpoch != expected.RunEpoch ||
                    current.CommandId != expected.CommandId)
                    throw new InvalidOperationException(
                        "暂停事务在安全边界期间已被替换，拒绝提交旧 Paused。" +
                        $" Expected={expected.RunId:N}/{expected.RunEpoch}/{expected.Generation}/{expected.CommandId:N}" +
                        $" Actual={current.RunId:N}/{current.RunEpoch}/{current.Generation}/{current.CommandId:N}");

                var invalid = new List<string>();
                foreach (var channel in expected.FrozenChannels)
                {
                    var runtime = _channelRuntimeStateStore.Get(channel);
                    var timerActive = _timers.ContainsKey(channel) || _timerCache.ContainsKey(channel);
                    var runnerActive = _runners.ContainsKey(channel) || _runnerCache.ContainsKey(channel);
                    var formal = runtime?.FormalPhaseCommitted == true || IsFormalPhaseCommitted;
                    var recoveryOwned = runtime?.State == ChannelRuntimeState.Recovering ||
                                        RecoveryOwnershipPolicy.HasExplicitOwner(runtime);
                    var energized = IsChannelEnergized(channel) || runtime?.Energized == true;
                    var identityCurrent = runtime != null &&
                                          runtime.RunId == expected.RunId &&
                                          runtime.RunEpoch == expected.RunEpoch &&
                                          runtime.PauseGeneration == expected.Generation &&
                                          runtime.PauseCommandId == expected.CommandId;
                    if (!timerActive || !runnerActive || !formal || recoveryOwned ||
                        energized || !identityCurrent)
                        invalid.Add(
                            $"EPB{channel}:Timer={timerActive},Runner={runnerActive},Formal={formal}," +
                            $"RecoveryOwner={recoveryOwned},Energized={energized},Identity={identityCurrent}");
                }

                if (invalid.Count > 0)
                    throw new InvalidOperationException(
                        "暂停提交前复核失败：" + string.Join(";", invalid));
            }
        }

        private async Task WaitForBatchPauseRecoveryOwnersAsync(
            BatchPauseSnapshot expected,
            CancellationToken token)
        {
            if (expected == null || expected.CommandId == Guid.Empty)
                throw new InvalidOperationException("暂停事务身份缺失，不能等待恢复 Owner 收口。");

            var progress = CaptureManualPauseProgress();
            var deadlineUtc = progress.HardDeadlineUtc > DateTime.UtcNow
                ? progress.HardDeadlineUtc
                : DateTime.UtcNow;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pendingOwners = new List<int>();
                lock (_daqRecoveryCommitGate)
                {
                    var current = CaptureBatchPauseSnapshot();
                    if (current.State != BatchPauseState.PausePending ||
                        current.Generation != expected.Generation ||
                        current.RunId != expected.RunId ||
                        current.RunEpoch != expected.RunEpoch ||
                        current.CommandId != expected.CommandId)
                        throw new InvalidOperationException(
                            "等待恢复 Owner 收口期间暂停事务已被替换，拒绝提交 Paused。");

                    foreach (var channel in expected.FrozenChannels)
                    {
                        var runtime = _channelRuntimeStateStore.Get(channel);
                        var timerActive = _timers.ContainsKey(channel) || _timerCache.ContainsKey(channel);
                        var runnerActive = _runners.ContainsKey(channel) || _runnerCache.ContainsKey(channel);
                        var formal = runtime?.FormalPhaseCommitted == true || IsFormalPhaseCommitted;
                        var identityCurrent = runtime != null &&
                                              runtime.RunId == expected.RunId &&
                                              runtime.RunEpoch == expected.RunEpoch &&
                                              runtime.PauseGeneration == expected.Generation &&
                                              runtime.PauseCommandId == expected.CommandId;
                        if (!timerActive || !runnerActive || !formal || !identityCurrent)
                            throw new InvalidOperationException(
                                $"EPB[{channel}] 暂停运行载体或事务身份已丢失，禁止发布假 Paused。" +
                                $" Timer={timerActive},Runner={runnerActive},Formal={formal},Identity={identityCurrent}");
                        if (IsChannelEnergized(channel) || runtime.Energized)
                            throw new InvalidOperationException(
                                $"EPB[{channel}] 暂停安全边界后仍有上电证据，禁止发布 Paused。");
                        if (runtime.State == ChannelRuntimeState.Recovering ||
                            RecoveryOwnershipPolicy.HasExplicitOwner(runtime))
                            pendingOwners.Add(channel);
                    }
                }

                if (pendingOwners.Count == 0)
                    return;
                if (DateTime.UtcNow >= deadlineUtc)
                    throw new TimeoutException(
                        $"暂停安全边界到达硬截止，恢复 Owner 未收口：EPB[{string.Join(",", pendingOwners)}]。");

                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        private void BeginManualPauseProgress(
            BatchPauseSnapshot pause,
            DateTime startedUtc,
            DateTime hardDeadlineUtc)
        {
            ManualPauseProgressSnapshot published;
            lock (_manualPauseProgressGate)
            {
                _manualPauseProgress = new ManualPauseProgressSnapshot
                {
                    Active = true,
                    Stage = ManualPauseStage.CurrentCycleDrain,
                    ProgressVersion = _manualPauseProgress.ProgressVersion + 1,
                    StartedUtc = startedUtc,
                    StageStartedUtc = startedUtc,
                    HardDeadlineUtc = hardDeadlineUtc,
                    Channels = pause?.FrozenChannels?.Distinct().OrderBy(channel => channel).ToArray() ?? Array.Empty<int>(),
                    EnergizedChannels = Array.Empty<int>(),
                    RunId = pause?.RunId ?? Guid.Empty,
                    RunEpoch = pause?.RunEpoch ?? 0,
                    PauseCommandId = pause?.CommandId ?? Guid.Empty,
                    Detail = "等待所有通道完成当前圈"
                };
                published = _manualPauseProgress.Clone();
            }
            PublishManualPauseProgress(published);
        }

        private void AdvanceManualPauseProgress(ManualPauseStage stage, string detail)
        {
            ManualPauseProgressSnapshot published;
            lock (_manualPauseProgressGate)
            {
                _manualPauseProgress.Active = stage != ManualPauseStage.Completed &&
                                              stage != ManualPauseStage.Resumed;
                _manualPauseProgress.Stage = stage;
                _manualPauseProgress.StageStartedUtc = DateTime.UtcNow;
                _manualPauseProgress.ProgressVersion++;
                _manualPauseProgress.Detail = detail ?? string.Empty;
                if (stage == ManualPauseStage.Completed)
                {
                    _manualPauseProgress.EnergizedChannels = Array.Empty<int>();
                    _manualPauseProgress.MotorsOff = true;
                    _manualPauseProgress.PowerOff = true;
                    _manualPauseProgress.PressureSafe = true;
                    _manualPauseProgress.PersistenceDrained = true;
                }
                if (stage == ManualPauseStage.PersistenceDrain)
                {
                    _manualPauseProgress.MotorsOff = true;
                    _manualPauseProgress.PowerOff = true;
                    _manualPauseProgress.PressureSafe = true;
                }
                published = _manualPauseProgress.Clone();
            }
            PublishManualPauseProgress(published);
        }

        private void MarkManualPauseSafetyFault(string reason)
        {
            ManualPauseProgressSnapshot published;
            lock (_manualPauseProgressGate)
            {
                _manualPauseProgress.Active = true;
                _manualPauseProgress.Stage = ManualPauseStage.SafetyFault;
                _manualPauseProgress.StageStartedUtc = DateTime.UtcNow;
                _manualPauseProgress.ProgressVersion++;
                _manualPauseProgress.SafetyFault = true;
                _manualPauseProgress.Detail = reason ?? "人工暂停安全边界失败";
                published = _manualPauseProgress.Clone();
            }
            PublishManualPauseProgress(published);
        }

        private void PublishManualPauseProgress(ManualPauseProgressSnapshot snapshot)
        {
            NonCriticalObserver.Invoke(
                ManualPauseProgressChanged,
                snapshot?.Clone(),
                ex => _log?.Warn($"人工暂停进度观察者异常，已隔离：{ex.Message}", "EPB"));
        }

        public bool CanAcknowledgeChannelAlarm(int channel, out string rejectionReason)
        {
            var state = _channelRuntimeStateStore.Get(channel);
            return CanOperateConfiguredChannel(
                IsChannelEnabled(channel),
                state?.State ?? ChannelRuntimeState.NotEnabled,
                out rejectionReason);
        }

        internal static bool CanOperateConfiguredChannel(
            bool configuredEnabled,
            ChannelRuntimeState state,
            out string rejectionReason)
        {
            if (!configuredEnabled)
            {
                rejectionReason = "当前项目已取消启用该通道；请先在试验设置中重新勾选并保存。";
                return false;
            }
            if (!IsChannelStateRestartable(state))
            {
                rejectionReason = "通道当前不处于可重新开始的停机状态。";
                return false;
            }
            // 旧故障的作用只是说明上次为什么停止，不再作为新一次启动的许可条件。
            // 重新开始会完整执行DAQ、电源、启动定位和资格圈实时复核；
            // 如果硬件问题仍存在，它会在新运行中被再次发现并停止。
            rejectionReason = string.Empty;
            return true;
        }

        internal static bool IsChannelStateRestartable(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.AlarmStopped ||
                   state == ChannelRuntimeState.InterlockStopped ||
                   state == ChannelRuntimeState.ManualStopped ||
                   state == ChannelRuntimeState.StartBlocked ||
                   state == ChannelRuntimeState.SystemFault;
        }

        internal static bool CanResumeWithoutQualification(DateTime pausedUtc, DateTime nowUtc)
        {
            // 同进程暂停的可恢复性由运行对象、DAQ、电源和模型实时状态决定，
            // 不再使用易受墙钟跳变影响的 5 分钟历史门禁。
            return true;
        }

        internal static bool IsChannelAlarmCodeRecoverable(string code)
        {
            // 保留此方法供旧调用方兼容；故障码不再锁死单通道重启。
            return true;
        }

        /// <summary>
        /// 单通道重新开始。旧的报警、联锁、启动受阻、系统故障或人工停止状态只作历史证据；
        /// 本次点击始终重新执行定位和2圈资格复核，仍存在的实时故障会在本次运行中再次停止。
        /// </summary>
        /// <summary>
        /// Manual and unattended alarm recovery entry point.  The actual
        /// recovery body is registered and bound before the first Recovering
        /// publication; the caller's batch/reconnect task is never used as
        /// this channel's owner.
        /// </summary>
        public async Task ResumeAlarmStoppedChannelAsync(
            int channel,
            bool operatorAcknowledged,
            CancellationToken token = default,
            bool unattendedRecovery = false)
        {
            if (!operatorAcknowledged)
                throw new InvalidOperationException("必须由操作员确认故障原因已排除后才能恢复。");
            if (!CanRunStandaloneAlarmRecovery(IsBatchSessionActive, IsFormalPhaseCommitted))
                throw new InvalidOperationException(
                    "批量启动、学习或资格复核尚未提交正式阶段；" +
                    "单通道恢复不得越过批次协调器创建正式Timer。");
            if (unattendedRecovery && _nonRecoverableChannelFaultLatch.ContainsKey(channel))
                throw new InvalidOperationException("卡钳硬件故障已锁存，禁止无人值守自动拉起。");
            if (unattendedRecovery && _operatorFullRelearningRequired.ContainsKey(channel))
                throw new InvalidOperationException(
                    "ForwardUnderTargetHighLoadStall 已锁存，禁止无人值守自动拉起；" +
                    "必须由操作员确认并完成完整重学习与两圈资格复核。");

            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var recoveryCorrelation = Guid.NewGuid();
            // A standalone recovery is still a run-scoped operation.  The
            // normal UI path reaches here only after a run context exists;
            // fail closed rather than publishing an owner that cannot be
            // correlated with the current run.
            if (runId == Guid.Empty || runEpoch <= 0)
                throw new InvalidOperationException(
                    "单通道恢复缺少当前运行身份，拒绝创建孤儿恢复事务。");

            RecoveryIncidentHandle recoveryIncident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return () => ResumeAlarmStoppedChannelBodyAsync(
                    channel,
                    operatorAcknowledged,
                    token,
                    unattendedRecovery,
                    recoveryCorrelation);
            }

            var started = TryBeginRecoveryIncident(
                "AlarmChannelRecovery",
                runId,
                runEpoch,
                RecoveryOwnerKind.AffectedGroupRecovery,
                RecoveryTargetPhase.Formal,
                recoveryCorrelation,
                new[] { channel },
                _ => BuildRecoveryWorker(),
                contract =>
                {
                    PublishRecoveryIncidentState(
                        channel,
                        ChannelRuntimeState.Recovering,
                        "AlarmResumeIncidentStarted",
                        unattendedRecovery
                            ? "无人值守报警恢复已建立真实执行任务，正在执行全量恢复预检。"
                            : "人工确认报警恢复已建立真实执行任务，正在执行全量恢复预检。",
                        affectedChannels: contract.Channels,
                        correlationId: contract.IncidentId,
                        recoveryOwnerKind: contract.OwnerKind,
                        recoveryTargetPhase: contract.TargetPhase,
                        recoveryOwnerId: contract.OwnerId,
                        recoveryOwnerGeneration: contract.RunEpoch);
                },
                out recoveryIncident);
            if (!started || recoveryIncident == null)
                throw new InvalidOperationException(
                    "报警恢复事务建立失败，已保持通道安全终态。");

            try
            {
                _taskSupervisor.Observe(
                    recoveryIncident.WorkerTask,
                    "AlarmChannelRecovery",
                    _activeBatchId,
                    channel);
            }
            catch (Exception observeError)
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(channel, "AlarmRecoveryObserveFailed"); }
                    catch { }
                    PublishRecoverySafeTerminal(
                        contract,
                        "AlarmRecoveryObserveFailed",
                        $"报警恢复任务登记失败，已保持安全终态：{observeError.Message}");
                });
                throw;
            }

            if (!recoveryIncident.Start())
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(channel, "AlarmRecoveryStartRejected"); }
                    catch { }
                    PublishRecoverySafeTerminal(
                        contract,
                        "AlarmRecoveryStartRejected",
                        "报警恢复启动许可被拒绝，已保持安全终态。 ");
                });
                throw new InvalidOperationException("报警恢复启动许可被拒绝。");
            }

            try
            {
                await recoveryIncident.WorkerTask.ConfigureAwait(false);
            }
            finally
            {
                // Terminal publication remains inside the contract gate until
                // the worker's final state is observable.  If the body exits
                // without a terminal state, CompleteRecoveryIncident emits
                // exactly one OFF -> StartBlocked fallback.
                recoveryIncident.CompleteAfterTerminal(contract =>
                    CommitRecoveryIncidentStateForRelease(
                        contract,
                        "AlarmRecoveryTerminalWithoutRejoin",
                        "报警恢复未完成重新入网，已保持安全终态。 "));
            }
        }

        private async Task ResumeAlarmStoppedChannelBodyAsync(
            int channel,
            bool operatorAcknowledged,
            CancellationToken token = default,
            bool unattendedRecovery = false,
            Guid recoveryCorrelation = default)
        {
            if (!operatorAcknowledged)
                throw new InvalidOperationException("必须由操作员确认故障原因已排除后才能恢复。");
            if (!CanRunStandaloneAlarmRecovery(IsBatchSessionActive, IsFormalPhaseCommitted))
                throw new InvalidOperationException(
                    "批量启动、学习或资格复核尚未提交正式阶段；" +
                    "单通道恢复不得越过批次协调器创建正式Timer。");
            if (unattendedRecovery && _nonRecoverableChannelFaultLatch.ContainsKey(channel))
                throw new InvalidOperationException("卡钳硬件故障已锁存，禁止无人值守自动拉起。");
            var requiresFullRelearning =
                _operatorFullRelearningRequired.ContainsKey(channel);
            if (unattendedRecovery && requiresFullRelearning)
                throw new InvalidOperationException(
                    "ForwardUnderTargetHighLoadStall 已锁存，禁止无人值守自动拉起。");
            if (!unattendedRecovery)
            {
                _manualStopRequestedChannels.TryRemove(channel, out _);
                _nonRecoverableChannelFaultLatch.TryRemove(channel, out _);
            }
            if (!CanAcknowledgeChannelAlarm(channel, out var rejection))
                throw new InvalidOperationException(rejection);

            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            CancellationTokenSource resumeCts = null;
            CancellationTokenSource hardDeadline = null;
            CancellationTokenSource deadlineLinked = null;
            CancellationTokenSource ownershipLinked = null;
            HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease ownership = null;
            var hardDeadlineReached = false;
            var resetOnHardDeadline = IsBatchSessionActive;
            var recoveryRunId = _activeBatchId;
            var recoveryRunEpoch = Interlocked.Read(ref _runEpoch);
            try
            {
                if (!CanAcknowledgeChannelAlarm(channel, out rejection))
                    throw new InvalidOperationException(rejection);
                hardDeadline = new CancellationTokenSource(RecoveryGroupHardDeadlineMs);
                deadlineLinked = CancellationTokenSource.CreateLinkedTokenSource(
                    token,
                    hardDeadline.Token);
                ownership = await _recoveryOwnership.AcquireAsync(
                        channel <= 6 ? 1 : 2,
                        $"ALARM:{channel}:{_activeBatchId:N}",
                        RecoveryOwnerPriority.AlarmChannel,
                        RecoveryOwnershipTakeoverTimeoutMs,
                        deadlineLinked.Token)
                    .ConfigureAwait(false);
                ownershipLinked = CancellationTokenSource.CreateLinkedTokenSource(
                    deadlineLinked.Token,
                    ownership.Token);
                var recoveryToken = ownershipLinked.Token;
                var remaining = GetRemainingMechanicalTargetCycles(channel);
                if (remaining <= 0)
                    throw new InvalidOperationException($"EPB[{channel}] 已无剩余正式圈数。");

                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.ResumeChecking,
                    "AlarmResumeChecking",
                    "操作员已确认故障排除，正在执行完整恢复预检",
                    correlationId: _activeBatchId,
                    allowTerminalReset: true);
                ResetTransientFaultStateForRestart(new[] { channel }, "AlarmStoppedChannelRestart");

                if (!IsBatchSessionActive)
                {
                    EpbTestCycle[channel] = remaining;
                    if (requiresFullRelearning)
                        ResetAdaptiveProfileForOperatorRelearning(channel);
                    var learnCycles = requiresFullRelearning
                        ? Math.Max(5, _cfg.Test?.LearnCycles ?? 5)
                        : 0;
                    await RecoveryStageDeadline.RunAsync(
                            "AlarmResumeFreshBatch",
                            RecoveryGroupHardDeadlineMs,
                            ct => requiresFullRelearning
                                ? StartBatchCoreAsync(
                                    new[] { channel },
                                    learnCycles,
                                    qualificationCycles: 2,
                                    reuseStableProfiles: false,
                                    ct,
                                    chainIdentity: null,
                                    operatorFullRelearningAuthorized: true)
                                : StartBatchFromGracefulCheckpointAsync(
                                    new[] { channel },
                                    2,
                                    ct),
                            recoveryToken)
                        .ConfigureAwait(false);
                    if (requiresFullRelearning &&
                        !PersistOperatorFullRelearningState(
                            channel,
                            required: false,
                            reason: string.Empty,
                            utc: DateTime.UtcNow,
                            correlationId: recoveryCorrelation))
                        throw new InvalidOperationException(
                            $"EPB[{channel}] 重学习已通过，但门禁持久化清除失败，保持安全停机。");
                    ClearAlarmIndicatorAfterRecoveryBestEffort(channel);
                    return;
                }

                // 报警停止时原通道令牌已移除。恢复预检前建立新令牌，
                // 这样操作员在DAQ/电源自愈期间点击停止也能立即取消。
                RenewStopCts(channel);
                resumeCts = CreateResumeLinkedTokenSource(
                    recoveryToken,
                    new[] { channel },
                    includeBatchSession: true);
                var resumeToken = resumeCts.Token;

                await RecoveryStageDeadline.RunAsync(
                        "AlarmResumeDaqReady",
                        RecoveryStageTimeoutMs,
                        ct => EnsureDaqReadyBeforeStartAsync(new[] { channel }, ct),
                        resumeToken)
                    .ConfigureAwait(false);
                EnsureStrictCurveControl(new[] { channel });
                EnsureAdaptiveProfilesReady(new[] { channel });
                await RecoveryStageDeadline.RunAsync(
                        "AlarmResumePowerReady",
                        RecoveryStageTimeoutMs,
                        ct => EnsurePowerSupplyReadyBeforeStartAsync(new[] { channel }, ct),
                        resumeToken)
                    .ConfigureAwait(false);

                var plan = GetCompatibleStaggerPlan(new[] { channel });
                // 人工确认后开启新的报警代次；资格复核期间若再次触发故障，必须重新锁存并停机。
                _alarmStopLatch.BeginRun(channel);
                StartupPositioningResult[] positioningFailures = null;
                await RecoveryStageDeadline.RunAsync(
                        "AlarmResumeMechanicalRelease",
                        RecoveryMechanicalReleaseTimeoutMs,
                        async ct =>
                        {
                            positioningFailures = await PreReleaseBatchWithPlanAsync(
                                    new[] { channel },
                                    null,
                                    plan,
                                    ct)
                                .ConfigureAwait(false);
                        },
                        resumeToken)
                    .ConfigureAwait(false);
                if (positioningFailures.Length > 0)
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 恢复定位失败：{positioningFailures[0].Code}/" +
                        positioningFailures[0].Reason);

                if (requiresFullRelearning)
                {
                    var learnCycles = Math.Max(5, _cfg.Test?.LearnCycles ?? 5);
                    ResetAdaptiveProfileForOperatorRelearning(channel);
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Learning,
                        "OperatorConfirmedFullRelearning",
                        $"操作员已确认，正在执行完整{learnCycles}圈重学习",
                        correlationId: _activeBatchId);
                    var learningGroups = GroupByPressure(new[] { channel });
                    var learningAnchor = CeilToBoundary(
                        DateTime.UtcNow.AddMilliseconds(Math.Max(2, AnchorWarmupMs)),
                        PeriodMs);
                    var learningAnchors = learningGroups.Keys.ToDictionary(
                        hydraulicId => hydraulicId,
                        _ => learningAnchor);
                    int[] learningFailed = null;
                    await RecoveryStageDeadline.RunAsync(
                            "AlarmResumeFullRelearning",
                            RecoveryGroupHardDeadlineMs,
                            async ct =>
                            {
                                learningFailed = await RunLearningPhaseAsync(
                                        learningGroups,
                                        learningAnchors,
                                        learnCycles,
                                        plan,
                                        ct)
                                    .ConfigureAwait(false);
                            },
                            resumeToken)
                        .ConfigureAwait(false);
                    if (learningFailed.Contains(channel))
                        throw new InvalidOperationException(
                            $"EPB[{channel}] 完整重学习失败，保持报警停机和输出OFF。");
                    EnsureAdaptiveProfilesReady(new[] { channel });
                }

                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Qualification,
                    "AlarmQualification",
                    "报警恢复资格复核（2圈，不计入正式目标）",
                    correlationId: _activeBatchId);
                int[] qualificationFailed = null;
                await RecoveryStageDeadline.RunAsync(
                        "AlarmResumeQualification",
                        RecoveryGroupHardDeadlineMs,
                        async ct =>
                        {
                            qualificationFailed = await RunPausedQualificationAsync(
                                    new[] { channel },
                                    2,
                                    ct)
                                .ConfigureAwait(false);
                        },
                        resumeToken)
                    .ConfigureAwait(false);
                if (qualificationFailed.Contains(channel))
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 资格复核确认仍有实时故障，保持报警停机。");
                if (requiresFullRelearning &&
                    !PersistOperatorFullRelearningState(
                        channel,
                        required: false,
                        reason: string.Empty,
                        utc: DateTime.UtcNow,
                        correlationId: recoveryCorrelation))
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 重学习资格已通过，但门禁持久化清除失败，保持安全停机。");
                RejoinFormalChannelsAtSharedFutureSlot(
                    new[] { channel },
                    plan,
                    "AlarmResumed",
                    "报警恢复完成，已按当前公共节律槽重新加入",
                    allowTerminalReset: true);
                ClearAlarmIndicatorAfterRecoveryBestEffort(channel);
            }
            catch (OperationCanceledException) when (
                hardDeadline?.IsCancellationRequested == true &&
                !token.IsCancellationRequested)
            {
                hardDeadlineReached = true;
                PublishRecoveryIncidentState(
                    channel,
                    resetOnHardDeadline
                        ? ChannelRuntimeState.Recovering
                        : ChannelRuntimeState.AlarmStopped,
                    "AlarmResumeHardDeadline",
                    $"单通道恢复超过{RecoveryGroupHardDeadlineMs}ms，" +
                    (resetOnHardDeadline
                        ? "转入受影响液压组Stop→Start等价清场。"
                        : "保持安全停机。"),
                    correlationId: recoveryCorrelation,
                    allowTerminalReset: true,
                    recoveryOwnerKind: RecoveryOwnerKind.AffectedGroupRecovery,
                    recoveryTargetPhase: RecoveryTargetPhase.Formal,
                    recoveryOwnerId: recoveryCorrelation,
                    recoveryOwnerGeneration: recoveryRunEpoch);
                try { CommandEpbOffHighPriority(channel, "AlarmResumeHardDeadline"); }
                catch { }
            }
            catch (OperationCanceledException) when (
                token.IsCancellationRequested ||
                (resumeCts?.IsCancellationRequested ?? false))
            {
                throw;
            }
            catch
            {
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.AlarmStopped,
                    "AlarmResumeRejected",
                    "恢复预检或资格复核失败，报警保持锁存",
                    correlationId: _activeBatchId,
                    allowTerminalReset: true);
                throw;
            }
            finally
            {
                resumeCts?.Dispose();
                ownershipLinked?.Dispose();
                deadlineLinked?.Dispose();
                hardDeadline?.Dispose();
                ownership?.Dispose();
                _pauseResumeGate.Release();
            }
            if (hardDeadlineReached && resetOnHardDeadline)
                await ExecuteAffectedGroupResetAsync(
                        new[] { channel },
                        "AlarmResumeHardDeadline",
                        recoveryCorrelation,
                        recoveryRunId,
                        recoveryRunEpoch,
                        RecoveryTargetPhase.Formal)
                    .ConfigureAwait(false);
        }

        private void ResetAdaptiveProfileForOperatorRelearning(int channel)
        {
            var empty = new EpbAdaptiveProfile { Channel = channel };
            if (_adaptiveProfileStore == null)
                throw new InvalidOperationException("自适应模型存储未初始化，不能执行完整重学习。");
            _adaptiveProfileStore.SaveWithReceipt(empty);
            if (_runnerCache.TryGetValue(channel, out var runner))
                runner.RestoreAdaptiveProfile(empty);
            _log.Warn(
                $"EPB[{channel}] 已清除旧自适应模型；完整学习与资格复核成功前禁止正式重入。",
                "EPB");
        }

        private void ClearAlarmIndicatorAfterRecoveryBestEffort(int channel)
        {
            var alarm = Alarm;
            if (alarm == null) return;
            var expectedRunId = _activeBatchId;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                var attempt = 0;
                while (true)
                {
                    var beforeAttempt = _channelRuntimeStateStore.Get(channel);
                    if (beforeAttempt == null ||
                        !CanRetryAlarmIndicatorClear(
                            expectedRunId,
                            _activeBatchId,
                            beforeAttempt.State))
                        return;
                    attempt++;
                    try
                    {
                        await alarm.SetAlarmAsync(
                                channel,
                                false,
                                "人工确认并通过资格复核",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        // 首次 SetAlarmAsync 可能已移除内部锁存但在串口写指示灯时失败；
                        // 后续重试 changed=false，不会再自动写灯，因此必须显式强制 OFF。
                        await alarm.SetIndicatorOutputAsync(channel, false, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (attempt > 1)
                            _log?.Info(
                                $"EPB[{channel}] 报警显示清除自愈完成，Attempt={attempt}。",
                                "报警");
                        return;
                    }
                    catch (Exception ex)
                    {
                        var state = _channelRuntimeStateStore.Get(channel);
                        var stillRecoveredRun = state != null &&
                                                CanRetryAlarmIndicatorClear(
                                                    expectedRunId,
                                                    _activeBatchId,
                                                    state.State);
                        if (!stillRecoveredRun)
                        {
                            _log?.Info(
                                $"EPB[{channel}] 报警显示清除自愈因运行代次或状态已变化而作废。",
                                "报警");
                            return;
                        }

                        var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                        _log?.Warn(
                            $"EPB[{channel}] 已恢复试验，但报警显示清除失败；" +
                            $"不反向停止控制，{delayMs}ms后自动重试。" +
                            $"Attempt={attempt} Error={ex.Message}",
                            "报警");
                        await Task.Delay(delayMs).ConfigureAwait(false);
                    }
                }
            }), "ClearAlarmIndicatorAfterRecovery", channel);
        }

        internal static bool CanRetryAlarmIndicatorClear(
            Guid expectedRunId,
            Guid currentRunId,
            ChannelRuntimeState state)
        {
            return expectedRunId == currentRunId &&
                   (state == ChannelRuntimeState.Running ||
                    state == ChannelRuntimeState.WarningRunning);
        }

        private CancellationTokenSource CreateResumeLinkedTokenSource(
            CancellationToken externalToken,
            IEnumerable<int> channels,
            bool includeBatchSession)
        {
            var tokens = new List<CancellationToken> { externalToken };
            if (includeBatchSession)
            {
                var session = Volatile.Read(ref _batchSessionCts);
                try
                {
                    if (session != null) tokens.Add(session.Token);
                }
                catch (ObjectDisposedException) { }
            }

            foreach (var channel in (channels ?? Array.Empty<int>()).Distinct())
            {
                if (!_stopCtsByChannel.TryGetValue(channel, out var stopCts)) continue;
                try { tokens.Add(stopCts.Token); }
                catch (ObjectDisposedException) { }
            }
            return CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray());
        }

        private async Task EnsureMotorReleasedBeforeFormalRejoinAsync(
            IEnumerable<int> channels,
            ElectricalStaggerPlan staggerPlan,
            string reason,
            CancellationToken token)
        {
            var selected = (channels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0) return;
            var rejoinPermits = selected.ToDictionary(
                channel => channel,
                channel => _channelExecutionFence.Capture(channel));
            if (rejoinPermits.Any(pair =>
                    !IsChannelExecutionPermitCurrent(pair.Key, pair.Value)))
                throw new InvalidOperationException(
                    $"FormalRejoinRejected StaleExecutionPermit " +
                    $"Channels=[{string.Join(",", selected)}]");

            await InvokeAfterCycleExecutionQuiescenceAsync(
                    selected,
                    WaitForPreviousCycleExecutionAsync,
                    ct => EnsureMotorReleasedBeforeFormalRejoinCoreAsync(
                        selected,
                        staggerPlan,
                        reason,
                        ct),
                    "MotorReleaseBeforeFormalRejoin",
                    token)
                .ConfigureAwait(false);
        }

        private async Task EnsureMotorReleasedBeforeFormalRejoinCoreAsync(
            int[] selected,
            ElectricalStaggerPlan staggerPlan,
            string reason,
            CancellationToken token)
        {
            var failures = await PreReleaseBatchWithPlanAsync(
                    selected,
                    null,
                    staggerPlan,
                    token)
                .ConfigureAwait(false);
            if (failures.Length > 0)
                throw new InvalidOperationException(
                    $"正式节律重新加入前的机械释放失败：EPB[{string.Join(",", failures.Select(x => x.Channel))}] " +
                    $"Reason={reason} Detail={failures[0].Code}/{failures[0].Reason}");

            _log?.Info(
                $"正式节律重新加入前机械释放确认完成：Channels=[{string.Join(",", selected)}] " +
                $"Reason={reason}",
                "EPB");
        }

        private void RejoinFormalChannelsAtSharedFutureSlot(
            IEnumerable<int> channels,
            ElectricalStaggerPlan staggerPlan,
            string runtimeCode,
            string runtimeReason,
            bool allowTerminalReset,
            bool allowSystemFaultReset = false,
            bool ownedByActiveDaqRecovery = false,
            long recoveryEpoch = 0)
        {
            if (!CanRunStandaloneAlarmRecovery(IsBatchSessionActive, IsFormalPhaseCommitted))
                throw new InvalidOperationException(
                    $"FormalRejoinRejected Batch={_activeBatchId:N} " +
                    $"FormalCommitted={IsFormalPhaseCommitted}; 批次正式阶段尚未提交。");
            var selected = (channels ?? Array.Empty<int>())
                .Distinct()
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => !IsAlarmStopRequested(channel) || allowTerminalReset)
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0) return;
            var rejoinPermits = selected.ToDictionary(
                channel => channel,
                channel => _channelExecutionFence.Capture(channel));
            if (rejoinPermits.Any(pair =>
                    !IsChannelExecutionPermitCurrent(pair.Key, pair.Value)))
                throw new InvalidOperationException(
                    $"FormalRejoinRejected StaleExecutionPermit " +
                    $"Channels=[{string.Join(",", selected)}]");

            // A channel-level warning, hydraulic self-heal or power self-heal may complete while
            // the owning DAQ group is still in its device-level recovery.  Letting that path
            // replace/restart a timer here can re-pressurize the group before DAQ freshness and
            // power restoration have committed.  The DAQ recovery owns the eventual group rejoin.
            if (!ownedByActiveDaqRecovery)
            {
                var deferredForDaq = selected
                    .Where(channel => ShouldDeferIndependentRejoinForDaq(
                        IsDaqRecoveryActiveForChannel(channel)))
                    .ToArray();
                if (deferredForDaq.Length > 0)
                {
                    _log?.Info(
                        $"通道级恢复已移交DAQ组统一恢复：Channels=[{string.Join(",", deferredForDaq)}] " +
                        $"Reason={runtimeCode}",
                        "AI");
                    var deferred = new HashSet<int>(deferredForDaq);
                    selected = selected.Where(channel => !deferred.Contains(channel)).ToArray();
                    if (selected.Length == 0) return;
                }
            }
            else if (recoveryEpoch <= 0)
                throw new InvalidOperationException("DAQ恢复所有者重入缺少有效 RecoveryEpoch。");

            var nowUtc = DateTime.UtcNow;
            foreach (var group in selected.GroupBy(channel => channel <= 6 ? 1 : 2))
            {
                var members = group.ToArray();
                lock (_formalRejoinGates[group.Key])
                {
                    if (!_activeFormalT0ByPressureGroup.TryGetValue(group.Key, out var t0))
                        t0 = CeilToBoundary(nowUtc.AddMilliseconds(AnchorWarmupMs), PeriodMs);
                    _activeFormalT0ByPressureGroup[group.Key] = t0;
                    var sharedFirstSlot = SelectSharedFormalRejoinSlot(t0, nowUtc, PeriodMs);
                    var remainingByChannel = members.ToDictionary(
                        channel => channel,
                        GetRemainingMechanicalTargetCycles);
                    var restartMembers = members
                        .Where(channel => remainingByChannel[channel] > 0)
                        .ToArray();

                    foreach (var channel in members)
                        RemoveTimerRuntime(channel, "FormalSharedSlotRejoin");

                    // 先把本组全部成员登记到同一个未来槽，再启动任何一个 Timer。
                    // 因此首个 Timer 即使立刻拍摄成员快照，也不可能只看到部分成员。
                    foreach (var channel in restartMembers)
                        MarkHydraulicParticipantFromFormalSlot(channel, sharedFirstSlot);

                    try
                    {
                        if (restartMembers.Any(channel =>
                                !IsChannelExecutionPermitCurrent(
                                    channel,
                                    rejoinPermits[channel])))
                            throw new InvalidOperationException(
                                $"FormalRejoinRejected ExecutionPermitRevoked " +
                                $"Hydraulic={group.Key}");
                        StartFormalPhaseTimers(
                            new Dictionary<int, List<int>>
                            {
                                [group.Key] = restartMembers.ToList()
                            },
                            new Dictionary<int, DateTime>
                            {
                                [group.Key] = t0
                            },
                            staggerPlan,
                            GetBatchSessionTokenOr(CancellationToken.None),
                            CycleAttemptKind.FormalRecovery,
                            new Dictionary<int, long>
                            {
                                [group.Key] = sharedFirstSlot
                            },
                            registerParticipants: false,
                            timerTaskName: "RejoinedChannelTimer");

                        if (!ownedByActiveDaqRecovery)
                        {
                            foreach (var channel in restartMembers)
                            {
                                PublishChannelRuntimeState(
                                    channel,
                                    ChannelRuntimeState.Running,
                                    runtimeCode,
                                    $"{runtimeReason}；FutureSlot={sharedFirstSlot}",
                                    correlationId: _activeBatchId,
                                    allowTerminalReset: allowTerminalReset,
                                    allowSystemFaultReset: allowSystemFaultReset);
                                NonCriticalObserver.Invoke(
                                    ChannelResumed,
                                    channel,
                                    ex => _log?.Warn(
                                        $"单通道继续观察者异常，已隔离：{ex.Message}",
                                        "EPB"));
                            }
                        }

                        var candidates = group.Key == 1
                            ? Enumerable.Range(1, 6).ToArray()
                            : Enumerable.Range(7, 6).ToArray();
                        var participants = GetHydraulicParticipantsInPressureGroupSnapshot(
                            group.Key,
                            candidates,
                            sharedFirstSlot);
                        var missing = restartMembers
                            .Where(channel => !participants.Contains(channel))
                            .ToArray();
                        if (missing.Length > 0)
                            throw new InvalidOperationException(
                                $"共同重入不变量失败 Hydraulic={group.Key} Slot={sharedFirstSlot} " +
                                $"Expected=[{string.Join(",", restartMembers)}] " +
                                $"Actual=[{string.Join(",", participants)}] " +
                                $"Missing=[{string.Join(",", missing)}]");

                        _log?.Info(
                            $"通道按公共正式槽原子重新加入：Hydraulic={group.Key} " +
                            $"Slot={sharedFirstSlot} Members=[{string.Join(",", restartMembers)}] " +
                            $"Participants=[{string.Join(",", participants)}] " +
                            $"RecoveryEpoch={recoveryEpoch} Invariant=Passed Reason={runtimeCode}",
                            "液压协调");
                    }
                    catch
                    {
                        foreach (var channel in restartMembers)
                        {
                            RemoveTimerRuntime(channel, "FormalSharedSlotRejoinRollback");
                            UnmarkHydraulicParticipant(channel);
                        }
                        throw;
                    }
                }
            }
        }

        internal static long SelectSharedFormalRejoinSlot(
            DateTime formalT0Utc,
            DateTime nowUtc,
            int periodMs)
        {
            return CalculateFirstFutureFormalSlot(formalT0Utc, nowUtc, periodMs);
        }

    }
}
