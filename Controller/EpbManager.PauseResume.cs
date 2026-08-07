using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Timing;

namespace Controller
{
    public sealed partial class EpbManager
    {
        private readonly SemaphoreSlim _pauseResumeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, DateTime> _channelPausedUtc =
            new ConcurrentDictionary<int, DateTime>();
        private int _batchPauseStateValue = (int)BatchPauseState.Idle;
        private DateTime _batchPausedUtc = DateTime.MinValue;
        private int[] _batchPausedChannels = Array.Empty<int>();
        private long _qualificationGeneration;
        private Func<CancellationToken, Task> _pausePersistenceFlush;

        public event Action<BatchPauseStateChangedEvent> BatchPauseStateChanged;

        /// <summary>
        /// 由宿主注册Raw发布/文件写入排空回调。优雅暂停只有在圈数据与原始数据链均排空后才完成。
        /// </summary>
        public void RegisterPausePersistenceFlush(Func<CancellationToken, Task> flush)
        {
            Interlocked.Exchange(ref _pausePersistenceFlush, flush);
        }

        public BatchPauseState CurrentBatchPauseState =>
            (BatchPauseState)Volatile.Read(ref _batchPauseStateValue);

        public bool IsBatchPaused => CurrentBatchPauseState == BatchPauseState.Paused;

        public DateTime? BatchPausedUtc => _batchPausedUtc == DateTime.MinValue
            ? (DateTime?)null
            : _batchPausedUtc;

        /// <summary>
        /// 批次优雅暂停：先同时封住所有通道的下一圈，等待在途圈自然结束，
        /// 再确认电机关闭、液压释放、持久化排空并导出最近10圈。
        /// </summary>
        public async Task PauseBatchGracefullyAsync(CancellationToken token = default)
        {
            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!IsBatchSessionActive)
                    throw new InvalidOperationException("当前没有可暂停的批量试验。");
                if (CurrentBatchPauseState == BatchPauseState.Paused)
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
                foreach (var channel in channels)
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.PausePending,
                        "PausePending",
                        "已收到暂停请求，等待当前圈完成",
                        affectedChannels: channels,
                        correlationId: _activeBatchId);

                var pauseAll = Task.WhenAll(timers.Select(pair => pair.Value.PauseAfterCurrentCycleAsync()));
                var timeoutMs = Math.Max(30000, Math.Min(180000, PeriodMs * 2 + 30000));
                var timeout = Task.Delay(timeoutMs, token);
                if (await Task.WhenAny(pauseAll, timeout).ConfigureAwait(false) != pauseAll)
                    throw new TimeoutException($"优雅暂停等待当前圈结束超时（{timeoutMs}ms）。");
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

                await CompletePauseSafetyBoundaryAsync(channels, forceHydraulicGroups: true, token)
                    .ConfigureAwait(false);
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

                SetBatchPauseState(BatchPauseState.Paused, channels, "全部通道已安全暂停");
                FlushPersistentLog();
            }
            catch (Exception ex)
            {
                SetBatchPauseState(BatchPauseState.Stopping, _batchPausedChannels, ex.Message);
                await StopAllAsync(
                        new StopContext
                        {
                            Source = StopSource.ManualUi,
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
            try
            {
                if (!IsBatchSessionActive || CurrentBatchPauseState != BatchPauseState.Paused)
                    throw new InvalidOperationException("当前没有处于安全暂停状态的批次。");

                channels = _batchPausedChannels
                    .Where(channel => _timers.ContainsKey(channel) && _runners.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                if (channels.Length == 0)
                    throw new InvalidOperationException("暂停运行对象已丢失，不能快速恢复；请重新开始并完整学习。");
                resumeCts = CreateResumeLinkedTokenSource(token, channels, includeBatchSession: true);
                var resumeToken = resumeCts.Token;

                SetBatchPauseState(BatchPauseState.ResumeChecking, channels, "正在执行恢复安全预检");
                ResetTransientFaultStateForRestart(channels, "BatchResume");
                foreach (var channel in channels)
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.ResumeChecking,
                        "ResumeChecking",
                        "正在执行DAQ、电源、配置和模型预检",
                        affectedChannels: channels,
                        correlationId: _activeBatchId);

                await EnsureDaqReadyBeforeStartAsync(channels, resumeToken).ConfigureAwait(false);
                EnsureStrictCurveControl(channels);
                EnsureAdaptiveProfilesReady(channels);
                await EnsurePowerSupplyReadyBeforeStartAsync(channels, resumeToken).ConfigureAwait(false);

                var plan = _activeStaggerPlan ??
                           ElectricalStaggerPlanner.Build(channels, _cfg.Test.Groups, PeriodMs);
                RejoinFormalChannelsAtSharedFutureSlot(
                    channels,
                    plan,
                    "Resumed",
                    "批次已从安全暂停边界按当前公共节律槽重新加入",
                    allowTerminalReset: false);
                foreach (var channel in channels)
                    _channelPausedUtc.TryRemove(channel, out _);

                _batchPausedUtc = DateTime.MinValue;
                _batchPausedChannels = Array.Empty<int>();
                SetBatchPauseState(BatchPauseState.Running, channels, "试验已恢复");
            }
            catch (OperationCanceledException) when (
                token.IsCancellationRequested ||
                (resumeCts?.IsCancellationRequested ?? false))
            {
                if (!IsBatchSessionActive)
                    SetBatchPauseState(BatchPauseState.Idle, Array.Empty<int>(), "恢复自愈已由停止请求取消");
                else
                {
                    SetBatchPauseState(BatchPauseState.Paused, _batchPausedChannels, "恢复自愈已取消，保持安全暂停");
                    PublishChannelsHeldAfterResumeFailure(channels, "BatchResumeCanceled");
                }
                throw;
            }
            catch
            {
                // 预检或资格失败时保持定时器暂停，不允许带故障恢复。
                SetBatchPauseState(BatchPauseState.Paused, _batchPausedChannels, "恢复失败，保持安全暂停");
                PublishChannelsHeldAfterResumeFailure(channels, "BatchResumeFailed");
                throw;
            }
            finally
            {
                resumeCts?.Dispose();
                _pauseResumeGate.Release();
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
                var pause = timer.PauseAfterCurrentCycleAsync();
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
            catch
            {
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

                var plan = _activeStaggerPlan ??
                           ElectricalStaggerPlanner.Build(new[] { channel }, _cfg.Test.Groups, PeriodMs);
                RejoinFormalChannelsAtSharedFutureSlot(
                    new[] { channel },
                    plan,
                    "ChannelResumed",
                    "通道已从安全暂停边界按当前公共节律槽重新加入",
                    allowTerminalReset: false);
                _channelPausedUtc.TryRemove(channel, out _);
            }
            catch
            {
                if (_channelPausedUtc.ContainsKey(channel) && _timers.ContainsKey(channel))
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

            var drained = await _persistence.DrainAsync(10000).ConfigureAwait(false);
            if (!drained)
                _log.Warn(
                    "暂停时圈数据持久化队列10秒内未完全排空；电机和液压已处于安全态，" +
                    "不升级全局停机，DAQ自维护继续处理，必要时允许作废受影响圈。",
                    "落盘");

            var flushRaw = Volatile.Read(ref _pausePersistenceFlush);
            if (flushRaw != null)
            {
                try
                {
                    await flushRaw(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"暂停时Raw数据链排空/刷新未完成：{ex.Message}；" +
                        "不升级全局停机，后台自维护继续运行。",
                        "落盘");
                }
            }

            try
            {
                await Task.Run(() =>
                {
                    foreach (var channel in selected)
                        Recorder?.FlushRecent(channel, 10);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"暂停时最近10圈证据导出失败：{ex.Message}；" +
                    "不升级全局停机，正式圈索引和后台写盘保持运行。",
                    "落盘");
            }
        }

        private async Task<int[]> RunPausedQualificationAsync(
            int[] channels,
            int cycles,
            CancellationToken token)
        {
            var selected = channels.Distinct().OrderBy(x => x).ToArray();
            EnsureAdaptiveProfilesReady(selected);
            var groups = GroupByPressure(selected);
            var staggerPlan = _activeStaggerPlan ??
                              ElectricalStaggerPlanner.Build(selected, _cfg.Test.Groups, PeriodMs);
            var quarantined = new ConcurrentDictionary<int, string>();

            for (var ordinal = 1; ordinal <= cycles; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                var tasks = new List<Task>();
                foreach (var pair in groups.Where(pair => pair.Value.Count > 0))
                {
                    var groupChannels = pair.Value
                        .Where(channel => !quarantined.ContainsKey(channel))
                        .OrderBy(x => x)
                        .ToArray();
                    if (groupChannels.Length == 0) continue;
                    var slot = Interlocked.Increment(ref _qualificationGeneration);
                    var key = new HydraulicGenerationKey(
                        _activeBatchId,
                        pair.Key,
                        HydraulicPhaseKind.Qualification,
                        slot);
                    var anchorTask = EnterHydraulicStartupPhaseWithSelfHealingAsync(
                        key,
                        groupChannels,
                        token);
                    var maxPhase = groupChannels.Max(channel => staggerPlan.Get(channel).PhaseMs);
                    var groupTimelineUtc = CeilToBoundary(
                        DateTime.UtcNow.AddMilliseconds(Math.Max(2, AnchorWarmupMs)),
                        PeriodMs);
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
                                    // 资格阶段的共享液压锚点异常只能隔离本压力组，不能越过
                                    // WhenAll 把另一压力组和整个重新开始流程一起回滚。
                                    var lease = await anchorTask.ConfigureAwait(false);
                                    channelToken.ThrowIfCancellationRequested();
                                    var window = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                                        lease?.ActuationAnchorUtc ?? DateTime.UtcNow.AddMilliseconds(2),
                                        groupTimelineUtc,
                                        PeriodMs,
                                        maxPhase);
                                    var dueUtc = window.GetDueUtc(phase);
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
            var modelBeforeLogicalCycle = runner.CaptureAdaptiveProfile();
            var cycleNumber = 0;
            var attempts = await SoftwareSelfHealingLoop.RunAsync(
                    async (attempt, attemptToken) =>
                    {
                        if (attempt > 1)
                            await EnterHydraulicStartupPhaseWithSelfHealingAsync(
                                    new HydraulicGenerationKey(
                                        runId,
                                        pressureGroup,
                                        HydraulicPhaseKind.Recovery,
                                        Interlocked.Increment(ref _qualificationGeneration)),
                                    new[] { channel },
                                    attemptToken)
                                .ConfigureAwait(false);
                        try
                        {
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
                            if (cycleNumber != 0) MarkCurrentCycleNumber(channel, cycleNumber);

                            var outcome = await runner.RunOneAdaptiveLearningAsync(PeriodMs, attemptToken)
                                .ConfigureAwait(false);
                            if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.Canceled)
                                throw new OperationCanceledException(attemptToken);
                            if (outcome.Kind == Adaptive.EpbCycleOutcomeKind.SoftwareRecovery)
                                throw new SoftwareSelfHealingRetryException(
                                    $"EPB[{channel}] 资格圈遇到软件瞬态；" +
                                    $"本次尝试作废后重做。Reason={outcome.Reason}");
                            if (!outcome.IsSuccess &&
                                outcome.Reason?.IndexOf(
                                    "DaqSampleStale",
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                await SealLearningCycleAsync(
                                        channel,
                                        cycleNumber,
                                        runId,
                                        qualificationOrdinal,
                                        "qualification_failed",
                                        requireValidEvidence: true,
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                                cycleNumber = 0;
                                runner.RestoreAdaptiveProfile(modelBeforeLogicalCycle);
                                await WaitForDaqRecoveryAsync(channel, attemptToken).ConfigureAwait(false);
                                throw new SoftwareSelfHealingRetryException(
                                    $"EPB[{channel}] 资格圈遇到DAQ陈旧数据；已作废并等待恢复后重做。");
                            }
                            if (!outcome.IsSuccess)
                                throw new InvalidOperationException(
                                    $"EPB[{channel}] 资格圈失败：{outcome.Stage}/{outcome.Reason}");

                            await SealLearningCycleAsync(
                                    channel,
                                    cycleNumber,
                                    runId,
                                    qualificationOrdinal,
                                    "qualification_completed",
                                    requireValidEvidence: true,
                                    softwareAttempt: attempt)
                                .ConfigureAwait(false);
                            cycleNumber = 0;
                        }
                        catch (SoftwareSelfHealingRetryException)
                        {
                            cycleNumber = 0;
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        cycleNumber,
                                        runId,
                                        qualificationOrdinal,
                                        "qualification_canceled",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            cycleNumber = 0;
                            throw;
                        }
                        catch
                        {
                            if (!IsAlarmStopRequested(channel))
                                await SealLearningCycleAsync(
                                        channel,
                                        cycleNumber,
                                        runId,
                                        qualificationOrdinal,
                                        "qualification_failed",
                                        softwareAttempt: attempt)
                                    .ConfigureAwait(false);
                            cycleNumber = 0;
                            throw;
                        }
                    },
                    (attempt, ex, attemptToken) =>
                    {
                        runner.RestoreAdaptiveProfile(modelBeforeLogicalCycle);
                        RequireSoftwareRecoveryOutputOff(
                            channel,
                            "QualificationPersistenceSelfHealing");
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Qualification,
                            "QualificationPersistenceSelfHealing",
                            $"资格圈证据软件自愈第{attempt}次：本次尝试已作废，随后重做同一资格圈。",
                            affectedChannels: new[] { channel },
                            correlationId: runId,
                            allowTerminalReset: true);
                        _log.Warn(
                            $"EPB[{channel}] 资格圈证据失败已作废，不停止批次。" +
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
                    ChannelRuntimeState.Qualification,
                    "QualificationPersistenceSelfHealed",
                    $"资格圈证据自愈完成，共尝试{attempts}次；只保留最后一次有效资格结果。",
                    affectedChannels: new[] { channel },
                    correlationId: runId,
                    allowTerminalReset: true);
        }

        private void SetBatchPauseState(BatchPauseState state, int[] channels, string reason)
        {
            Interlocked.Exchange(ref _batchPauseStateValue, (int)state);
            var update = new BatchPauseStateChangedEvent
            {
                State = state,
                Channels = channels?.Distinct().OrderBy(x => x).ToArray() ?? Array.Empty<int>(),
                TimestampUtc = DateTime.UtcNow,
                Reason = reason ?? string.Empty,
                RunId = _activeBatchId
            };
            _log?.Info(
                $"批次暂停状态={state} Channels=[{string.Join(",", update.Channels)}] Reason={update.Reason}",
                "EPB");
            NonCriticalObserver.Invoke(
                BatchPauseStateChanged,
                update,
                ex => _log?.Warn($"批次暂停状态观察者异常，已隔离：{ex.Message}", "EPB"));
        }

        public bool CanAcknowledgeChannelAlarm(int channel, out string rejectionReason)
        {
            rejectionReason = string.Empty;
            var state = _channelRuntimeStateStore.Get(channel);
            if (state == null || !IsChannelStateRestartable(state.State))
            {
                rejectionReason = "通道当前不处于可重新开始的停机状态。";
                return false;
            }
            // 旧故障的作用只是说明上次为什么停止，不再作为新一次启动的许可条件。
            // 重新开始会完整执行DAQ、电源、启动定位和资格圈实时复核；
            // 如果硬件问题仍存在，它会在新运行中被再次发现并停止。
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
        public async Task ResumeAlarmStoppedChannelAsync(
            int channel,
            bool operatorAcknowledged,
            CancellationToken token = default)
        {
            if (!operatorAcknowledged)
                throw new InvalidOperationException("必须由操作员确认故障原因已排除后才能恢复。");
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
            var recoveryRunEpoch = Interlocked.Read(ref _runEpoch);
            var recoveryCorrelation = _activeBatchId == Guid.Empty
                ? Guid.NewGuid()
                : _activeBatchId;
            try
            {
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
                var remaining = Math.Max(
                    0,
                    _cfg.Test.GetEpbRecord(channel).TotalCount -
                    _cfg.Test.GetEpbRecord(channel).RunCount);
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
                    await RecoveryStageDeadline.RunAsync(
                            "AlarmResumeFreshBatch",
                            RecoveryGroupHardDeadlineMs,
                            ct => StartBatchFromGracefulCheckpointAsync(
                                new[] { channel },
                                2,
                                ct),
                            recoveryToken)
                        .ConfigureAwait(false);
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

                var plan = _activeStaggerPlan ??
                           ElectricalStaggerPlanner.Build(new[] { channel }, _cfg.Test.Groups, PeriodMs);
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
                PublishChannelRuntimeState(
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
                    allowTerminalReset: true);
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
                        recoveryRunEpoch)
                    .ConfigureAwait(false);
        }

        private void ClearAlarmIndicatorAfterRecoveryBestEffort(int channel)
        {
            var alarm = Alarm;
            if (alarm == null) return;
            var expectedRunId = _activeBatchId;
            _ = Task.Run(async () =>
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
            });
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
            bool allowTerminalReset)
        {
            var selected = (channels ?? Array.Empty<int>())
                .Distinct()
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => !IsAlarmStopRequested(channel) || allowTerminalReset)
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0) return;

            // A channel-level warning, hydraulic self-heal or power self-heal may complete while
            // the owning DAQ group is still in its device-level recovery.  Letting that path
            // replace/restart a timer here can re-pressurize the group before DAQ freshness and
            // power restoration have committed.  The DAQ recovery owns the eventual group rejoin.
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

            var nowUtc = DateTime.UtcNow;
            foreach (var group in selected.GroupBy(channel => channel <= 6 ? 1 : 2))
            {
                if (!_activeFormalT0ByPressureGroup.TryGetValue(group.Key, out var t0))
                    t0 = CeilToBoundary(nowUtc.AddMilliseconds(AnchorWarmupMs), PeriodMs);
                _activeFormalT0ByPressureGroup[group.Key] = t0;
                var sharedFirstSlot = SelectSharedFormalRejoinSlot(t0, nowUtc, PeriodMs);

                var members = group.ToArray();
                foreach (var channel in members)
                    RemoveTimerRuntime(channel, "FormalSharedSlotRejoin");

                foreach (var channel in members)
                {
                    var remaining = Math.Max(
                        0,
                        _cfg.Test.GetEpbRecord(channel).TotalCount -
                        _cfg.Test.GetEpbRecord(channel).RunCount);
                    if (remaining <= 0) continue;
                    StartRejoinedFormalChannel(
                        channel,
                        remaining,
                        staggerPlan,
                        t0,
                        sharedFirstSlot,
                        runtimeCode,
                        runtimeReason,
                        allowTerminalReset);
                }

                _log?.Info(
                    $"通道按公共正式槽重新加入：Hydraulic={group.Key} Slot={sharedFirstSlot} " +
                    $"Members=[{string.Join(",", members)}] Reason={runtimeCode}",
                    "液压协调");
            }
        }

        internal static long SelectSharedFormalRejoinSlot(
            DateTime formalT0Utc,
            DateTime nowUtc,
            int periodMs)
        {
            return CalculateFirstFutureFormalSlot(formalT0Utc, nowUtc, periodMs);
        }

        private void StartRejoinedFormalChannel(
            int channel,
            int remainingRuns,
            ElectricalStaggerPlan staggerPlan,
            DateTime t0,
            long firstSlot,
            string runtimeCode,
            string runtimeReason,
            bool allowTerminalReset)
        {
            var pressureGroup = channel <= 6 ? 1 : 2;
            _activeFormalT0ByPressureGroup[pressureGroup] = t0;
            var firstCallbackUtc = t0.AddMilliseconds(firstSlot * (double)PeriodMs);
            var initialDelay = Math.Max(
                1,
                (int)Math.Ceiling((firstCallbackUtc - DateTime.UtcNow).TotalMilliseconds));
            var phase = staggerPlan.Get(channel).PhaseMs;
            var runner = (EpbCycleRunner)GetRunner(channel);
            PrepareRunnerForNoHeadAndTailCompensation(channel);
            var stopCts = RenewStopCts(channel);
            var timer = GetTimer(channel, PeriodMs, OverrunPolicy.AlignToWallClock);
            var baseCycle = Recorder?.GetLastCycleNumber(channel) ?? 0;
            var successfulCycles = 0;
            EpbTestCycle[channel] = remainingRuns;
            // 先登记“首个合格正式槽”再暴露 participant。正在运行的上一槽即使此刻
            // 重新拍摄成员快照，也会按槽过滤掉本通道。
            MarkHydraulicParticipantFromFormalSlot(channel, firstSlot);

            _ = timer.StartAsync(null, initialDelay, async (cycleIndex, timerToken) =>
            {
                var cyclePauseCts = RenewCyclePauseCts(channel);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timerToken,
                    stopCts.Token,
                    cyclePauseCts.Token);
                var ct = linked.Token;
                var phaseSlot = firstSlot + cycleIndex - 1L;
                await WaitForDaqRecoveryAsync(channel, ct).ConfigureAwait(false);

                var candidates = pressureGroup == 1
                    ? Enumerable.Range(1, 6).ToArray()
                    : Enumerable.Range(7, 6).ToArray();
                var participants = GetHydraulicParticipantsInPressureGroupSnapshot(
                    pressureGroup,
                    candidates,
                    phaseSlot);
                var key = new HydraulicGenerationKey(
                    _activeBatchId,
                    pressureGroup,
                    HydraulicPhaseKind.Formal,
                    phaseSlot);
                var lease = await HydraulicEnterAtGroupAnchorAsync(key, participants, ct)
                    .ConfigureAwait(false);
                var maxPhase = participants.Count == 0
                    ? phase
                    : participants.Max(member => staggerPlan.Get(member).PhaseMs);
                var window = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                    lease?.ActuationAnchorUtc ?? DateTime.UtcNow.AddMilliseconds(2),
                    t0,
                    PeriodMs,
                    maxPhase);
                var plannedUtc = window.GetDueUtc(phase);
                var delay = plannedUtc - DateTime.UtcNow;
                if (delay.TotalMilliseconds > 1)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var cycleNumber = baseCycle + cycleIndex;
                MarkElectricalPhaseDue(channel, plannedUtc);
                var recorder = Recorder;
                if (!TryBeginFormalCycle(recorder, channel, cycleNumber, DateTime.UtcNow))
                {
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }
                var ok = false;
                try
                {
                    ok = await runner.RunOneAlignedAsync(
                            PeriodMs,
                            T8BaseMs,
                            phase,
                            T8MinMs,
                            window.DeadlineUtc,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { ok = false; }
                catch { ok = false; }
                var controlSucceeded = IsFormalControlSucceeded(
                    ok,
                    runner.LastCycleOutcome.IsSuccess);
                var controlNeedsSoftwareRecovery =
                    runner.LastCycleOutcome.Kind ==
                    Adaptive.EpbCycleOutcomeKind.SoftwareRecovery;
                if (controlNeedsSoftwareRecovery)
                    ReportFormalControlSoftwareRecovery(
                        channel,
                        cycleNumber,
                        runner.LastCycleOutcome.Reason);

                var persistenceCommitted = recorder == null;
                if (recorder != null)
                {
                    try
                    {
                        var finalN = recorder.GetCurrentCycleSampleCount(channel);
                        if (IsAlarmStopRequested(channel))
                        {
                            // 报警后台取得封存权。
                        }
                        else if (controlNeedsSoftwareRecovery)
                            AbortCycleAfterPersistence(
                                recorder,
                                channel,
                                cycleNumber,
                                DateTime.UtcNow,
                                "AbortedBySoftwareRecovery");
                        else if (controlSucceeded)
                            persistenceCommitted = CompleteCycleAndScheduleEvidence(
                                recorder,
                                channel,
                                cycleNumber,
                                finalN,
                                DateTime.UtcNow);
                        else
                            AbortCycleAfterPersistence(
                                recorder,
                                channel,
                                cycleNumber,
                                DateTime.UtcNow,
                                runner.LastCycleOutcome.Kind == Adaptive.EpbCycleOutcomeKind.Canceled
                                    ? "canceled"
                                    : "failed");
                    }
                    catch (Exception ex)
                    {
                        if (controlSucceeded)
                            AbortFormalCycleWithoutPersistenceBarrier(
                                recorder,
                                channel,
                                cycleNumber,
                                DateTime.UtcNow,
                                0,
                                ex);
                        else
                            _log.Warn(
                                $"EPB[{channel}] 报警恢复后的失败圈封存异常 " +
                                $"Cycle={cycleNumber}: {ex.Message}",
                                "落盘");
                    }
                }
                if (IsFormalCycleCountable(
                        controlSucceeded,
                        persistenceCommitted))
                {
                    var committedCycles = Interlocked.Increment(ref successfulCycles);
                    var nonRecoverableAlarm =
                        OnFormalCycleCommittedAndEvaluateClampFault(
                            runner,
                            channel,
                            cycleNumber,
                            committedCycles);
                    if (!nonRecoverableAlarm && committedCycles >= remainingRuns)
                    {
                        FinalizeChannelAfterNaturalCompletion(channel);
                        timer.Stop();
                    }
                }
                if (!IsAlarmStopRequested(channel)) ClearCurrentCycleNumber(channel);
                ReleaseCyclePauseCts(channel, cyclePauseCts);
                return controlSucceeded && persistenceCommitted;
            });

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Running,
                runtimeCode,
                $"{runtimeReason}；FutureSlot={firstSlot}",
                correlationId: _activeBatchId,
                allowTerminalReset: allowTerminalReset);
            NonCriticalObserver.Invoke(
                ChannelResumed,
                channel,
                ex => _log?.Warn($"单通道继续观察者异常，已隔离：{ex.Message}", "EPB"));
        }
    }
}
