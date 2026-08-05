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

        public event Action<BatchPauseStateChangedEvent> BatchPauseStateChanged;

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
                    try { ChannelPaused?.Invoke(channel); } catch { }
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
        /// 恢复暂停批次。5分钟内直接从统一未来锚点继续；超过5分钟先执行2圈资格复核。
        /// </summary>
        public async Task ResumeBatchAsync(CancellationToken token = default)
        {
            await _pauseResumeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!IsBatchSessionActive || CurrentBatchPauseState != BatchPauseState.Paused)
                    throw new InvalidOperationException("当前没有处于安全暂停状态的批次。");

                var channels = _batchPausedChannels
                    .Where(channel => _timers.ContainsKey(channel) && _runners.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                if (channels.Length == 0)
                    throw new InvalidOperationException("暂停运行对象已丢失，不能快速恢复；请重新开始并完整学习。");

                SetBatchPauseState(BatchPauseState.ResumeChecking, channels, "正在执行恢复安全预检");
                foreach (var channel in channels)
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.ResumeChecking,
                        "ResumeChecking",
                        "正在执行DAQ、电源、配置和模型预检",
                        affectedChannels: channels,
                        correlationId: _activeBatchId);

                await EnsureDaqReadyBeforeStartAsync(channels, token).ConfigureAwait(false);
                EnsureStrictCurveControl(channels);
                EnsureAdaptiveProfilesReady(channels);
                if (_powerSupply != null)
                    await _powerSupply.RevalidateEnabledAsync(channels, token).ConfigureAwait(false);

                if (!CanResumeWithoutQualification(_batchPausedUtc, DateTime.UtcNow))
                {
                    SetBatchPauseState(BatchPauseState.Qualification, channels, "暂停超过5分钟，执行2圈资格复核");
                    foreach (var channel in channels)
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Qualification,
                            "Qualification",
                            "旧模型资格复核（2圈，不计入正式目标）",
                            affectedChannels: channels,
                            correlationId: _activeBatchId);
                    await RunPausedQualificationAsync(channels, 2, token).ConfigureAwait(false);
                }

                var boundaryUtc = CeilToBoundary(
                    DateTime.UtcNow.AddMilliseconds(Math.Max(1000, AnchorWarmupMs)),
                    PeriodMs);
                foreach (var channel in channels)
                {
                    MarkHydraulicParticipant(channel);
                    if (_timers.TryGetValue(channel, out var timer))
                        timer.ResumeAtUtcBoundary(boundaryUtc);
                    _channelPausedUtc.TryRemove(channel, out _);
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Running,
                        "Resumed",
                        $"已从未来统一锚点 {boundaryUtc:O} 恢复",
                        affectedChannels: channels,
                        correlationId: _activeBatchId);
                    try { ChannelResumed?.Invoke(channel); } catch { }
                }

                _batchPausedUtc = DateTime.MinValue;
                _batchPausedChannels = Array.Empty<int>();
                SetBatchPauseState(BatchPauseState.Running, channels, "试验已恢复");
            }
            catch
            {
                // 预检或资格失败时保持定时器暂停，不允许带故障恢复。
                SetBatchPauseState(BatchPauseState.Paused, _batchPausedChannels, "恢复失败，保持安全暂停");
                throw;
            }
            finally
            {
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
                ChannelPaused?.Invoke(channel);
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
            try
            {
                if (!_channelPausedUtc.TryGetValue(channel, out var pausedUtc) ||
                    !_timers.TryGetValue(channel, out var timer) ||
                    !_runners.ContainsKey(channel))
                    throw new InvalidOperationException($"EPB[{channel}] 不处于可恢复的安全暂停状态。");

                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.ResumeChecking,
                    "ChannelResumeChecking",
                    "正在执行通道恢复预检",
                    correlationId: _activeBatchId);
                await EnsureDaqReadyBeforeStartAsync(new[] { channel }, token).ConfigureAwait(false);
                EnsureStrictCurveControl(new[] { channel });
                EnsureAdaptiveProfilesReady(new[] { channel });
                if (_powerSupply != null)
                    await _powerSupply.RevalidateEnabledAsync(new[] { channel }, token).ConfigureAwait(false);

                if (!CanResumeWithoutQualification(pausedUtc, DateTime.UtcNow))
                {
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Qualification,
                        "ChannelQualification",
                        "暂停超过5分钟，执行2圈资格复核",
                        correlationId: _activeBatchId);
                    await RunPausedQualificationAsync(new[] { channel }, 2, token).ConfigureAwait(false);
                }

                var boundaryUtc = CeilToBoundary(
                    DateTime.UtcNow.AddMilliseconds(Math.Max(1000, AnchorWarmupMs)),
                    PeriodMs);
                MarkHydraulicParticipant(channel);
                timer.ResumeAtUtcBoundary(boundaryUtc);
                _channelPausedUtc.TryRemove(channel, out _);
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Running,
                    "ChannelResumed",
                    $"通道已从未来锚点 {boundaryUtc:O} 恢复",
                    correlationId: _activeBatchId);
                ChannelResumed?.Invoke(channel);
            }
            finally
            {
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
                throw new TimeoutException("暂停时等待DAQ持久化队列排空超时。");

            await Task.Run(() =>
            {
                foreach (var channel in selected)
                    Recorder?.FlushRecent(channel, 10);
            }, token).ConfigureAwait(false);
        }

        private async Task RunPausedQualificationAsync(
            int[] channels,
            int cycles,
            CancellationToken token)
        {
            var selected = channels.Distinct().OrderBy(x => x).ToArray();
            EnsureAdaptiveProfilesReady(selected);
            var groups = GroupByPressure(selected);
            var staggerPlan = _activeStaggerPlan ??
                              ElectricalStaggerPlanner.Build(selected, _cfg.Test.Groups, PeriodMs);

            for (var ordinal = 1; ordinal <= cycles; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                var tasks = new List<Task>();
                foreach (var pair in groups.Where(pair => pair.Value.Count > 0))
                {
                    var groupChannels = pair.Value.OrderBy(x => x).ToArray();
                    var slot = Interlocked.Increment(ref _qualificationGeneration);
                    var key = new HydraulicGenerationKey(
                        _activeBatchId,
                        pair.Key,
                        HydraulicPhaseKind.Qualification,
                        slot);
                    var anchorTask = HydraulicEnterAtGroupAnchorAsync(key, groupChannels, token);
                    var maxPhase = groupChannels.Max(channel => staggerPlan.Get(channel).PhaseMs);
                    var groupTimelineUtc = CeilToBoundary(
                        DateTime.UtcNow.AddMilliseconds(Math.Max(2, AnchorWarmupMs)),
                        PeriodMs);
                    foreach (var channel in groupChannels)
                    {
                        var capturedChannel = channel;
                        var capturedOrdinal = ordinal;
                        var phase = staggerPlan.Get(channel).PhaseMs;
                        tasks.Add(Task.Run(async () =>
                        {
                            var lease = await anchorTask.ConfigureAwait(false);
                            var window = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                                lease?.ActuationAnchorUtc ?? DateTime.UtcNow.AddMilliseconds(2),
                                groupTimelineUtc,
                                PeriodMs,
                                maxPhase);
                            var dueUtc = window.GetDueUtc(phase);
                            var delay = dueUtc - DateTime.UtcNow;
                            if (delay.TotalMilliseconds > 1)
                                await Task.Delay(delay, token).ConfigureAwait(false);

                            var cycleNumber = Recorder?.BeginLearningCycle(capturedChannel, DateTime.UtcNow) ?? 0;
                            if (cycleNumber != 0) MarkCurrentCycleNumber(capturedChannel, cycleNumber);
                            try
                            {
                                if (!_runners.TryGetValue(capturedChannel, out var runner))
                                    throw new InvalidOperationException($"EPB[{capturedChannel}] Runner 已丢失。");
                                var outcome = await runner.RunOneAdaptiveLearningAsync(PeriodMs, token)
                                    .ConfigureAwait(false);
                                if (!outcome.IsSuccess)
                                    throw new InvalidOperationException(
                                        $"EPB[{capturedChannel}] 资格圈失败：{outcome.Stage}/{outcome.Reason}");
                                await SealLearningCycleAsync(
                                        capturedChannel,
                                        cycleNumber,
                                        _activeBatchId,
                                        capturedOrdinal,
                                        "qualification_completed")
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                                if (!IsAlarmStopRequested(capturedChannel))
                                    await SealLearningCycleAsync(
                                            capturedChannel,
                                            cycleNumber,
                                            _activeBatchId,
                                            capturedOrdinal,
                                            "qualification_failed")
                                        .ConfigureAwait(false);
                                throw;
                            }
                        }, token));
                    }
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
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
            try { BatchPauseStateChanged?.Invoke(update); } catch { }
        }

        public bool CanAcknowledgeChannelAlarm(int channel, out string rejectionReason)
        {
            rejectionReason = string.Empty;
            var state = _channelRuntimeStateStore.Get(channel);
            if (state == null || state.State != ChannelRuntimeState.AlarmStopped)
            {
                rejectionReason = "通道不处于报警停机状态。";
                return false;
            }
            if ((state.AffectedChannels ?? Array.Empty<int>()).Distinct().Count() != 1)
            {
                rejectionReason = "该故障影响多个通道，禁止单通道恢复。";
                return false;
            }

            var code = state.ReasonCode ?? string.Empty;
            if (!IsChannelAlarmCodeRecoverable(code))
            {
                rejectionReason = $"{code} 属于共享资源、数据链或断电确认故障，禁止单通道恢复。";
                return false;
            }

            return true;
        }

        internal static bool CanResumeWithoutQualification(DateTime pausedUtc, DateTime nowUtc)
        {
            var elapsed = nowUtc.ToUniversalTime() - pausedUtc.ToUniversalTime();
            return elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromMinutes(5);
        }

        internal static bool IsChannelAlarmCodeRecoverable(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            var blockedFragments = new[]
            {
                "Daq", "PowerSupply", "Hydraulic", "Shared", "Interlock",
                "OffCurrent", "OverCurrent", "Persistence", "Storage", "System"
            };
            return !blockedFragments.Any(fragment =>
                code.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 人工确认后的通道级报警恢复。仅允许单通道可恢复故障；始终重新定位并做2圈资格复核。
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
            try
            {
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

                if (!IsBatchSessionActive)
                {
                    EpbTestCycle[channel] = remaining;
                    await StartBatchFromGracefulCheckpointAsync(
                            new[] { channel },
                            2,
                            token)
                        .ConfigureAwait(false);
                    if (Alarm != null)
                        await Alarm.SetAlarmAsync(channel, false, "人工确认并通过资格复核", token)
                            .ConfigureAwait(false);
                    return;
                }

                await EnsureDaqReadyBeforeStartAsync(new[] { channel }, token).ConfigureAwait(false);
                EnsureStrictCurveControl(new[] { channel });
                EnsureAdaptiveProfilesReady(new[] { channel });
                if (_powerSupply != null)
                    await _powerSupply.RevalidateEnabledAsync(new[] { channel }, token).ConfigureAwait(false);

                var plan = _activeStaggerPlan ??
                           ElectricalStaggerPlanner.Build(new[] { channel }, _cfg.Test.Groups, PeriodMs);
                // 人工确认后开启新的报警代次；资格复核期间若再次触发故障，必须重新锁存并停机。
                _alarmStopLatch.BeginRun(channel);
                var positioningFailures = await PreReleaseBatchWithPlanAsync(
                        new[] { channel },
                        null,
                        plan,
                        token)
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
                await RunPausedQualificationAsync(new[] { channel }, 2, token).ConfigureAwait(false);
                StartRejoinedFormalChannel(channel, remaining, plan);
                if (Alarm != null)
                    await Alarm.SetAlarmAsync(channel, false, "人工确认并通过资格复核", token)
                        .ConfigureAwait(false);
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
                _pauseResumeGate.Release();
            }
        }

        private void StartRejoinedFormalChannel(
            int channel,
            int remainingRuns,
            ElectricalStaggerPlan staggerPlan)
        {
            var pressureGroup = channel <= 6 ? 1 : 2;
            if (!_activeFormalT0ByPressureGroup.TryGetValue(pressureGroup, out var t0))
                t0 = CeilToBoundary(DateTime.UtcNow.AddMilliseconds(AnchorWarmupMs), PeriodMs);
            _activeFormalT0ByPressureGroup[pressureGroup] = t0;

            var firstSlot = CalculateFirstFutureFormalSlot(t0, DateTime.UtcNow, PeriodMs);
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
            MarkHydraulicParticipant(channel);

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
                    candidates);
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
                Recorder?.BeginCycle(channel, cycleNumber, DateTime.UtcNow);
                MarkCurrentCycleNumber(channel, cycleNumber);
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

                var recorder = Recorder;
                if (recorder != null)
                {
                    var finalN = recorder.GetCurrentCycleSampleCount(channel);
                    if (IsAlarmStopRequested(channel))
                    {
                        // 报警后台取得封存权。
                    }
                    else if (runner.LastCycleOutcome.IsSuccess)
                        CompleteCycleAndScheduleEvidence(
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
                if (!IsAlarmStopRequested(channel)) ClearCurrentCycleNumber(channel);

                if (runner.LastCycleOutcome.IsSuccess &&
                    Interlocked.Increment(ref successfulCycles) >= remainingRuns)
                {
                    FinalizeChannelAfterNaturalCompletion(channel);
                    timer.Stop();
                }
                ReleaseCyclePauseCts(channel, cyclePauseCts);
                return ok;
            });

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Running,
                "AlarmResumed",
                $"报警恢复完成，从未来正式槽 {firstSlot} 重新加入",
                correlationId: _activeBatchId,
                allowTerminalReset: true);
            try { ChannelResumed?.Invoke(channel); } catch { }
        }
    }
}
