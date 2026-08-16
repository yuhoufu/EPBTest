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
    internal sealed class DaqDataContinuityCompromisedException : InvalidOperationException
    {
        internal DaqDataContinuityCompromisedException(string message) : base(message) { }
    }

    public sealed partial class EpbManager
    {
        private readonly SemaphoreSlim _pauseResumeGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, DateTime> _channelPausedUtc =
            new ConcurrentDictionary<int, DateTime>();
        private int _batchPauseStateValue = (int)BatchPauseState.Idle;
        private DateTime _batchPausedUtc = DateTime.MinValue;
        private int[] _batchPausedChannels = Array.Empty<int>();
        private long _qualificationGeneration;
        private Func<IReadOnlyDictionary<string, long>, CancellationToken, Task> _pausePersistenceFlush;

        public event Action<BatchPauseStateChangedEvent> BatchPauseStateChanged;

        /// <summary>
        /// 由宿主注册Raw发布/文件写入排空回调。优雅暂停只有在圈数据与原始数据链均排空后才完成。
        /// </summary>
        public void RegisterPausePersistenceFlush(
            Func<IReadOnlyDictionary<string, long>, CancellationToken, Task> flush)
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

                var pauseAll = Task.WhenAll(timers.Select(pair =>
                    pair.Value.PauseAfterCurrentCycleAsync("BatchGracefulPause")));
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
                EnsureNoPermanentDataContinuityGap("批次恢复预检");
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
                // DAQ健康回调只证明采集硬件仍在工作，不能消除已锁存的
                // 工程/Raw数据空洞。上电前再检一次，封住预检期间的迟到故障。
                EnsureNoPermanentDataContinuityGap("批次恢复上电门禁");

                var plan = GetCompatibleStaggerPlan(channels);
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
            BeginLearningProfileTransaction(channel);
            int attempts;
            try
            {
                attempts = await SoftwareSelfHealingLoop.RunAsync(
                    async (attempt, attemptToken) =>
                    {
                        await EnsurePowerSupplyReadyForChannelsAsync(new[] { channel }, attemptToken)
                            .ConfigureAwait(false);
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
                                RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
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
                            try
                            {
                                SaveAdaptiveProfileWithReceipt(runner.CaptureAdaptiveProfile());
                                UpdateLearningAttemptReceiptStatus(
                                    channel, qualificationOrdinal, attempt, "Successful", string.Empty,
                                    qualification: true);
                            }
                            catch (EpbAdaptiveProfilePersistenceFatalException fatalEx)
                            {
                                // Preserve the non-retryable persistence fatal
                                // identity even when failed-receipt I/O also
                                // fails.  Runner state is restored first.
                                RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                                TryUpdateLearningAttemptReceiptStatusBestEffort(
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
            var recoveryCorrelation = Guid.NewGuid();
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
                        recoveryRunId,
                        recoveryRunEpoch)
                    .ConfigureAwait(false);
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
                        channel => Math.Max(
                            0,
                            _cfg.Test.GetEpbRecord(channel).TotalCount -
                            _cfg.Test.GetEpbRecord(channel).RunCount));
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
                        foreach (var channel in restartMembers)
                            StartRejoinedFormalChannel(
                                channel,
                                remainingByChannel[channel],
                                staggerPlan,
                                t0,
                                sharedFirstSlot,
                                runtimeCode,
                                runtimeReason,
                                allowTerminalReset,
                                allowSystemFaultReset,
                                publishRuntimeStateAndObserver: !ownedByActiveDaqRecovery);

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

        private void StartRejoinedFormalChannel(
            int channel,
            int remainingRuns,
            ElectricalStaggerPlan staggerPlan,
            DateTime t0,
            long firstSlot,
            string runtimeCode,
            string runtimeReason,
            bool allowTerminalReset,
            bool allowSystemFaultReset,
            bool publishRuntimeStateAndObserver)
        {
            var pressureGroup = channel <= 6 ? 1 : 2;
            _activeFormalT0ByPressureGroup[pressureGroup] = t0;
            var firstCallbackUtc = t0.AddMilliseconds(firstSlot * (double)PeriodMs);
            var initialDelay = Math.Max(
                1,
                (int)Math.Ceiling((firstCallbackUtc - DateTime.UtcNow).TotalMilliseconds));
            var phase = staggerPlan.Get(channel).PhaseMs;
            var stopCts = RenewStopCts(channel);
            var timer = GetTimer(channel, PeriodMs, OverrunPolicy.AlignToWallClock);
            var baseCycle = Recorder?.GetLastCycleNumber(channel) ?? 0;
            var successfulCycles = 0;
            EpbTestCycle[channel] = remainingRuns;
            ObserveBackgroundTask(timer.StartAsync(null, initialDelay, async (cycleIndex, timerToken) =>
            {
                var cyclePauseCts = RenewCyclePauseCts(channel);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timerToken,
                    stopCts.Token,
                    cyclePauseCts.Token);
                var ct = linked.Token;
                var phaseSlot = firstSlot + cycleIndex - 1L;
                if (!await WaitForPreviousCycleExecutionAsync(channel, ct)
                        .ConfigureAwait(false))
                {
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }

                // 只有旧 execution 已完全退出后，才允许取得并重新配置共享 Runner。
                var runner = (EpbCycleRunner)GetRunner(channel);
                PrepareRunnerForNoHeadAndTailCompensation(channel);
                await WaitForDaqRecoveryAsync(channel, ct).ConfigureAwait(false);
                await EnsurePowerSupplyReadyForChannelsAsync(new[] { channel }, ct)
                    .ConfigureAwait(false);

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
                if (!TryBeginFormalCycleAttempt(
                        recorder,
                        channel,
                        cycleNumber,
                        DateTime.UtcNow,
                        _activeBatchId,
                        CycleAttemptKind.FormalRecovery,
                        ct,
                        out var cycleAttempt))
                {
                    await AbortHydraulicLeaseForChannelAsync(
                            channel,
                            "RejoinedFormalPersistenceBoundaryRejected")
                        .ConfigureAwait(false);
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }
                if (!cycleAttempt.MarkExecutionStarted())
                {
                    if (!cycleAttempt.IsExecutionStarted)
                        CompleteCycleAttemptExecution(cycleAttempt);
                    await AbortHydraulicLeaseForChannelAsync(
                            channel,
                            "RejoinedFormalExecutionOwnershipRejected")
                        .ConfigureAwait(false);
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }
                using var executionScope =
                    CompleteCycleAttemptExecutionOnCallbackExit(cycleAttempt);

                var ok = false;
                Adaptive.EpbCycleOutcome cycleOutcome;
                try
                {
                    ok = await runner.RunOneAlignedAsync(
                            PeriodMs,
                            T8BaseMs,
                            phase,
                            T8MinMs,
                            window.DeadlineUtc,
                            cycleAttempt.AttemptCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { ok = false; }
                catch { ok = false; }
                finally
                {
                    cycleOutcome = runner.LastCycleOutcome;
                    if (_hydraulicLeaseByChannel.TryGetValue(channel, out var activeScope) &&
                        !activeScope.IsClosed)
                        await AbortHydraulicLeaseForChannelAsync(
                                channel,
                                "RejoinedFormalAttemptFinalizer")
                            .ConfigureAwait(false);
                }
                var controlSucceeded = IsFormalControlSucceeded(
                    ok,
                    cycleOutcome.IsSuccess);
                var controlNeedsSoftwareRecovery =
                    cycleOutcome.Kind ==
                    Adaptive.EpbCycleOutcomeKind.SoftwareRecovery;
                if (controlNeedsSoftwareRecovery)
                    ReportFormalControlSoftwareRecovery(
                        channel,
                        cycleNumber,
                        cycleOutcome.Reason);

                var persistenceCommitted = false;
                try
                {
                    if (TryConsumeDaqClockCycleAbort(
                            cycleAttempt.RunId,
                            cycleAttempt.RunEpoch,
                            channel,
                            cycleNumber))
                    {
                        AbortFormalCycleAttempt(
                            cycleAttempt,
                            recorder,
                            DateTime.UtcNow,
                            "AbortedBySoftwareRecovery");
                        _log?.Warn(
                            $"EPB[{channel}] 恢复正式周期 {cycleNumber} 已由DAQ流程封存，" +
                            "跳过重复终态提交。",
                            "落盘");
                    }
                    else
                    {
                        var finalN = recorder?.GetCurrentCycleSampleCount(channel) ?? 0;
                        if (IsAlarmStopRequested(channel))
                        {
                            // 报警后台取得封存权；活动 context 保留到报警耐久终态。
                        }
                        else if (controlNeedsSoftwareRecovery)
                            AbortFormalCycleAttempt(
                                cycleAttempt,
                                recorder,
                                DateTime.UtcNow,
                                "AbortedBySoftwareRecovery");
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
                }
                catch (Exception ex)
                {
                    PreserveFormalCycleForPersistenceRecovery(
                        channel,
                        cycleNumber,
                        "CycleFinalizer",
                        ex);
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
                        FinalizeChannelAfterNaturalCompletion(channel, cycleNumber);
                        timer.Stop();
                    }
                }
                ReleaseCyclePauseCts(channel, cyclePauseCts);
                return controlSucceeded && persistenceCommitted;
            }), "RejoinedChannelTimer", channel);

            if (publishRuntimeStateAndObserver)
            {
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Running,
                    runtimeCode,
                    $"{runtimeReason}；FutureSlot={firstSlot}",
                    correlationId: _activeBatchId,
                    allowTerminalReset: allowTerminalReset,
                    allowSystemFaultReset: allowSystemFaultReset);
                NonCriticalObserver.Invoke(
                    ChannelResumed,
                    channel,
                    ex => _log?.Warn($"单通道继续观察者异常，已隔离：{ex.Message}", "EPB"));
            }
        }
    }
}
