using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MTTFTest.Watchdog.Protocol;
using Config;
using Controller;

namespace MTEmbTest
{
    internal sealed class WatchdogHardwareUnavailableException : InvalidOperationException
    {
        internal WatchdogHardwareUnavailableException(string fingerprint, string detail)
            : base(detail)
        {
            Fingerprint = fingerprint ?? "HardwareUnavailable";
            Detail = detail ?? string.Empty;
        }

        internal string Fingerprint { get; }
        internal string Detail { get; }
    }

    public partial class FrmEpbMainMonitor
    {
        private const int UnattendedQuiesceTotalTimeoutMs = 30000;
        private int _watchdogTakeoverExit;
        private long _watchdogRecoveryProgressVersion;
        private long _watchdogRecoveryBatchCommitGeneration;
        private readonly object _watchdogProgressGate = new object();
        private string _watchdogProgressSignature = string.Empty;
        private long _watchdogStageStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        private readonly object _hardwareRecoveryGate = new object();
        private bool _hardwareRecoveryActive;
        private string _hardwareFailureFingerprint = string.Empty;
        private string _hardwareFailureDetail = string.Empty;
        private int _hardwareProbeAttempt;
        private DateTime _hardwareNextProbeUtc = DateTime.MinValue;

        internal bool IsOperatorStopRequested =>
            Volatile.Read(ref _operatorStopRequested) != 0 || IsDisposed || Disposing;

        internal void PublishHardwareUnavailable(
            string fingerprint,
            string detail,
            int attempt,
            DateTime nextProbeUtc)
        {
            lock (_hardwareRecoveryGate)
            {
                _hardwareRecoveryActive = true;
                _hardwareFailureFingerprint = fingerprint ?? "HardwareUnavailable";
                _hardwareFailureDetail = detail ?? string.Empty;
                _hardwareProbeAttempt = Math.Max(1, attempt);
                _hardwareNextProbeUtc = nextProbeUtc;
            }
            if (IsDisposed || Disposing) return;
            void Apply()
            {
                ApplyBatchActionButton("硬件不可用，禁止开始", false, false);
                var state = attempt >= RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit
                    ? "SafeIdleHardwareUnavailable"
                    : "HardwareSafetyProbePending";
                PostSafetyStatus(
                    $"{state}：所有输出保持 OFF；第{attempt}次安全预检未通过。" +
                    $" 下一次探测={nextProbeUtc:HH:mm:ss}；{detail}",
                    true);
            }
            if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
        }

        internal void ClearHardwareUnavailable()
        {
            lock (_hardwareRecoveryGate)
            {
                _hardwareRecoveryActive = false;
                _hardwareFailureFingerprint = string.Empty;
                _hardwareFailureDetail = string.Empty;
                _hardwareProbeAttempt = 0;
                _hardwareNextProbeUtc = DateTime.MinValue;
            }
        }

        internal void PrepareForWatchdogRetryExit()
        {
            // Watchdog 恢复子进程失败退出时保留授权；不得发布普通关闭终态。
            Interlocked.Exchange(ref _watchdogTakeoverExit, 1);
        }

        private void AttachUnattendedRecovery()
        {
            if (_epb == null || _cfg == null) return;
            // Keep any checkpoint-authorized root out of retention even if a
            // worker scan raced monitor construction.  This is an explicit UI /
            // recovery-coordinator decision; Controller does not inspect files.
            var pending = UnattendedRunCheckpointStore.Load();
            if (pending?.Armed == true && Guid.TryParse(pending.RootRunId, out var pendingRoot))
                _epb.ProtectLearningRoot(pendingRoot);
            UnattendedRecoveryCoordinator.Attach(_epb, _cfg);
            UnattendedRecoveryCoordinator.RegisterQuiesceAndFlush(
                QuiesceAndFlushForUnattendedRestartAsync);
            WatchdogRuntime.StopAllRequested -= OnWatchdogStopAllRequested;
            WatchdogRuntime.StopAllRequested += OnWatchdogStopAllRequested;
            WatchdogRuntime.SetHeartbeatProvider(CreateWatchdogHeartbeat);
            WatchdogRuntime.TransportLost -= OnWatchdogTransportLost;
            WatchdogRuntime.TransportLost += OnWatchdogTransportLost;
            WatchdogRuntime.TransportError -= OnWatchdogTransportError;
            WatchdogRuntime.TransportError += OnWatchdogTransportError;
        }

        private void OnWatchdogTransportLost(string reason, string detail)
        {
            ProjectLogHub.Write(
                ProjectLogLevel.Error,
                $"FIELD WatchdogTransportLost Reason={reason};Detail={detail}",
                "独立看门狗");
        }

        private void OnWatchdogTransportError(string reason, string detail)
        {
            ProjectLogHub.Write(
                ProjectLogLevel.Warning,
                $"FIELD WatchdogTransportError Reason={reason};Detail={detail}",
                "独立看门狗");
        }

        private int[] CapturePermanentAlarmedChannels()
        {
            return (_epb?.CaptureWatchdogPermanentAlarmedChannels() ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        private WatchdogHeartbeat CreateWatchdogHeartbeat()
        {
            ChannelRuntimeStateChangedEvent[] states;
            lock (_channelRuntimeStates)
                states = _channelRuntimeStates.Values.Select(x => x.Clone()).ToArray();
            var enabled = states.Where(x => x.Enabled).Select(x => x.Channel).Distinct().OrderBy(x => x).ToArray();
            var completed = states.Where(x => x.State == ChannelRuntimeState.Completed).Select(x => x.Channel).ToArray();
            var alarmed = states.Where(x => x.State == ChannelRuntimeState.AlarmStopped ||
                                            x.State == ChannelRuntimeState.InterlockStopped ||
                                            x.State == ChannelRuntimeState.StartBlocked)
                .Select(x => x.Channel).Distinct().OrderBy(x => x).ToArray();
            var permanentAlarmed = CapturePermanentAlarmedChannels();
            var manuallyDisabled = states.Where(x => !x.Enabled ||
                                                      x.State == ChannelRuntimeState.ManualStopped ||
                                                      x.State == ChannelRuntimeState.NotEnabled)
                .Select(x => x.Channel).Distinct().OrderBy(x => x).ToArray();
            var recoveryEvidence = CaptureWatchdogRecoveryEvidence();
            // AlarmedChannels is diagnostic only.  The sidecar must not infer
            // permanence from a UI state; the controller publishes the
            // structured hardware latch through the controller-owned snapshot API.
            // RecoveryEligibleChannels is the complete candidate set for a
            // process takeover: all selected enabled channels minus durable
            // completion, permanent hardware latches and explicit manual
            // exclusions.  ExpectedRecoveryChannels in the DAQ snapshot is
            // only the context that triggered recovery, not a reason to drop
            // unrelated healthy channels from the new process.
            var eligible = enabled
                .Except(completed)
                .Except(permanentAlarmed)
                .Except(manuallyDisabled)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var recovering = states.Where(x => x.State == ChannelRuntimeState.Recovering ||
                                                x.State == ChannelRuntimeState.ResumeChecking)
                .ToArray();
            var logical = _epb?.CaptureWatchdogLogicalSnapshot();
            var stop = _epb?.CaptureStopSafetyProgress();
            var storage = _epb?.CaptureWatchdogStorageSnapshot();
            var manualPauseActive =
                _epb?.CurrentBatchPauseState == BatchPauseState.Paused;
            var manualPausePending =
                _epb?.CurrentBatchPauseState == BatchPauseState.PausePending;
            var manualPauseCommanded =
                WatchdogTakeoverPolicy.IsManualPauseCommanded(
                    manualPauseActive,
                    manualPausePending);
            var manualPause = _epb?.CaptureManualPauseProgress();
            bool hardwareUnavailable;
            string hardwareFingerprint;
            string hardwareDetail;
            int hardwareAttempt;
            DateTime hardwareNextProbeUtc;
            lock (_hardwareRecoveryGate)
            {
                hardwareUnavailable = _hardwareRecoveryActive;
                hardwareFingerprint = _hardwareFailureFingerprint;
                hardwareDetail = _hardwareFailureDetail;
                hardwareAttempt = _hardwareProbeAttempt;
                hardwareNextProbeUtc = _hardwareNextProbeUtc;
            }
            var orphanPaused = !manualPauseCommanded && recoveryEvidence.OrphanPaused;
            var powerDisablePending = !manualPauseCommanded && recoveryEvidence.PowerDisablePending;
            var pauseSince = recoveryEvidence.PauseSince;
            var recoveryIncident = recoveryEvidence.Incident;
            var recoveryContext = recoveryEvidence.Context;
            var stageOrdinal = recoveryEvidence.StageOrdinal;
            var progressSignature = RecoveryProgressSignature.Build(
                recovering.Select(state =>
                        $"{state.Channel}:{state.State}:{state.ReasonCode}:{state.Revision}")
                    .Concat(new[]
                    {
                        $"Stop:{stop?.ProgressVersion ?? 0}:{stop?.Stage}:{stop?.Active}:{stop?.PhysicalSafe}"
                    }),
                logical?.DaqRecoveryCount ?? 0,
                logical?.SoftwareRecoveryCount ?? 0,
                logical?.RecoveryOwnerCount ?? 0,
                stageOrdinal,
                recoveryIncident,
                recoveryContext,
                orphanPaused,
                powerDisablePending);
            lock (_watchdogProgressGate)
            {
                if (!string.Equals(
                        progressSignature,
                        _watchdogProgressSignature,
                        StringComparison.Ordinal))
                {
                    _watchdogProgressSignature = progressSignature;
                    _watchdogStageStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _watchdogRecoveryProgressVersion);
                }
            }
            var phase = hardwareUnavailable ? "SafeIdleHardwareUnavailable" :
                manualPausePending ? "ManualPausePending" :
                manualPauseActive ? "ManualPaused" :
                states.Any(x => x.State == ChannelRuntimeState.Learning) ? "Learning" :
                states.Any(x => x.State == ChannelRuntimeState.Running || x.State == ChannelRuntimeState.WarningRunning) ? "Formal" :
                recovering.Length > 0 ? "Recovering" :
                (_epb?.IsBatchSessionActive ?? false) ? "Paused" : "Idle";
            return new WatchdogHeartbeat
            {
                RunId = _epb?.WatchdogRunId.ToString("N") ?? string.Empty,
                RunEpoch = _epb?.WatchdogRunEpoch ?? 0,
                Phase = phase,
                EnabledChannels = enabled,
                EligibleChannels = eligible,
                RecoveryEligibleChannels = eligible,
                CompletedChannels = completed,
                AlarmedChannels = alarmed,
                PermanentAlarmedChannels = permanentAlarmed,
                ManuallyDisabledChannels = manuallyDisabled,
                ManualPauseActive = manualPauseActive,
                ManualPausePending = manualPausePending,
                ManualPauseStage = manualPause?.Stage.ToString() ?? string.Empty,
                ManualPauseProgressVersion = manualPause?.ProgressVersion ?? 0,
                ManualPauseStageStartedUtc = manualPause?.StageStartedUtc.Ticks ?? 0,
                ManualPauseHardDeadlineUtc = manualPause?.HardDeadlineUtc.Ticks ?? 0,
                ManualPauseSafetyFault = manualPause?.SafetyFault == true,
                ManualPauseSafetyFaultReason = manualPause?.Detail ?? string.Empty,
                ManualPauseEnergizedChannels = manualPause?.EnergizedChannels ?? Array.Empty<int>(),
                RecoveryActive = !manualPauseCommanded &&
                                 (recoveryEvidence.Active ||
                                  (logical?.DaqRecoveryCount ?? 0) > 0 ||
                                  (logical?.SoftwareRecoveryCount ?? 0) > 0 ||
                                  (logical?.RecoveryOwnerCount ?? 0) > 0),
                RecoveryCode = manualPauseCommanded
                    ? "ManualGracefulPause"
                    : recovering.FirstOrDefault()?.ReasonCode ?? string.Empty,
                RecoveryStage = manualPauseCommanded
                    ? (manualPausePending
                        ? "ManualPausePending"
                        : "ManualPaused")
                    : recovering.FirstOrDefault()?.State.ToString() ?? string.Empty,
                RecoveryIncident = recoveryIncident,
                RecoveryContext = recoveryContext,
                StageOrdinal = stageOrdinal,
                OrphanPaused = orphanPaused,
                PowerDisablePending = powerDisablePending,
                OutputsConfirmedOff = recoveryEvidence.OutputsConfirmedOff,
                PowerOffUnconfirmed = recoveryEvidence.PowerOffUnconfirmed,
                PauseSince = manualPauseCommanded ? 0 : pauseSince,
                PowerDisableSince = recoveryEvidence.PowerDisableSince,
                RecoveryProgressVersion = Interlocked.Read(ref _watchdogRecoveryProgressVersion),
                RecoveryHardDeadlineUtc = recoveryEvidence.RecoveryHardDeadlineUtc,
                RecoveryBatchCommitGeneration = Interlocked.Read(
                    ref _watchdogRecoveryBatchCommitGeneration),
                StageStartedMonotonic = Interlocked.Read(ref _watchdogStageStartedTicks),
                DaqRecoveryCount = logical?.DaqRecoveryCount ?? 0,
                SoftwareRecoveryCount = logical?.SoftwareRecoveryCount ?? 0,
                RecoveryOwnerCount = logical?.RecoveryOwnerCount ?? 0,
                StopAllActive = stop?.Active == true,
                StopStage = stop?.Stage.ToString() ?? string.Empty,
                StopStartedUtc = stop?.StartedUtc.Ticks ?? 0,
                StopStageStartedUtc = stop?.StageStartedUtc.Ticks ?? 0,
                StopProgressVersion = stop?.ProgressVersion ?? 0,
                StopPhysicalSafe = stop?.PhysicalSafe == true,
                TimerCount = logical?.TimerCount ?? 0,
                RunnerCount = logical?.RunnerCount ?? 0,
                EnergizedChannelCount = logical?.EnergizedChannelCount ?? 0,
                CompletedCycleCount = (_cfg?.Test?.EpbRecords ?? Enumerable.Empty<Config.EpbTestRecord>())
                    .Sum(record => Math.Max(record.MechanicalCycleCount, record.RunCount)),
                ExpectedCyclePeriodMs = Math.Max(1, _cfg?.Test?.PeriodMs ?? 1),
                StopCtsCount = logical?.StopCtsCount ?? 0,
                CyclePauseCtsCount = logical?.CycleCtsCount ?? 0,
                ChannelProgress = (logical?.ChannelProgress ??
                                   Array.Empty<Controller.WatchdogChannelProgressSnapshot>())
                    .Select(item => new WatchdogChannelProgress
                    {
                        Channel = item.Channel,
                        State = item.State,
                        StateRevision = item.StateRevision,
                        StateSinceUtcTicks = item.StateSinceUtcTicks,
                        TimerActive = item.TimerActive,
                        RunnerActive = item.RunnerActive,
                        Energized = item.Energized,
                        LastMechanicalCompletedUtcTicks = item.LastMechanicalCompletedUtcTicks,
                        MechanicalCompletedCount = item.MechanicalCompletedCount,
                        ConsecutiveSoftwareAbortCount = item.ConsecutiveSoftwareAbortCount,
                        DoCommandSequence = item.DoCommandSequence,
                        PeakCutoffGeneration = item.PeakCutoffGeneration,
                        PeakCutoffSequence = item.PeakCutoffSequence
                    })
                    .ToArray(),
                Dev1CallbackGapCount = storage?.Dev1?.CallbackGapCount ?? 0,
                Dev2CallbackGapCount = storage?.Dev2?.CallbackGapCount ?? 0,
                Dev1Generation = storage?.Dev1?.Generation ?? 0,
                Dev2Generation = storage?.Dev2?.Generation ?? 0,
                FrozenBoundary = new Dictionary<string, long>
                {
                    ["Dev1"] = storage?.Dev1?.FrozenBoundary ?? 0,
                    ["Dev2"] = storage?.Dev2?.FrozenBoundary ?? 0
                },
                Persisted = new Dictionary<string, long>
                {
                    ["Dev1"] = storage?.Dev1?.Persisted ?? 0,
                    ["Dev2"] = storage?.Dev2?.Persisted ?? 0
                },
                Head = new Dictionary<string, long>
                {
                    ["Dev1"] = storage?.Dev1?.Head ?? 0,
                    ["Dev2"] = storage?.Dev2?.Head ?? 0
                },
                InFlight = new Dictionary<string, long>
                {
                    ["Dev1"] = storage?.Dev1?.InFlight ?? 0,
                    ["Dev2"] = storage?.Dev2?.InFlight ?? 0
                },
                QueueDepth = new Dictionary<string, int>
                {
                    ["Dev1"] = storage?.Dev1?.QueueDepth ?? 0,
                    ["Dev2"] = storage?.Dev2?.QueueDepth ?? 0
                },
                PersistenceState = $"Dev1={storage?.Dev1?.PersistenceState ?? "Unavailable"};" +
                                   $"Dev2={storage?.Dev2?.PersistenceState ?? "Unavailable"}",
                LogicalState = logical?.ToString() ?? "Unavailable",
                ManualStopRequested = Volatile.Read(ref _operatorStopRequested) != 0,
                HardwareUnavailable = hardwareUnavailable,
                HardwareFailureFingerprint = hardwareFingerprint,
                HardwareFailureDetail = hardwareDetail,
                HardwareProbeAttempt = hardwareAttempt,
                HardwareNextProbeUtc = hardwareNextProbeUtc.Ticks,
                RunActive = _epb?.IsBatchSessionActive ?? false
            };
        }

        private sealed class WatchdogRecoveryEvidence
        {
            public bool OrphanPaused;
            public bool PowerDisablePending;
            public bool OutputsConfirmedOff;
            public bool PowerOffUnconfirmed;
            public long RecoveryHardDeadlineUtc;
            public long PauseSince;
            public long PowerDisableSince;
            public string Incident = string.Empty;
            public string Context = string.Empty;
            public int StageOrdinal;
            public bool Active;
        }

        private WatchdogRecoveryEvidence CaptureWatchdogRecoveryEvidence()
        {
            var result = new WatchdogRecoveryEvidence();
            try
            {
                var snapshot = _epb?.CaptureWatchdogRecoverySnapshot();
                if (snapshot == null) return result;
                result.OrphanPaused = snapshot.OrphanPaused;
                result.PowerDisablePending = snapshot.PowerDisablePending;
                result.OutputsConfirmedOff = snapshot.OutputsConfirmedOff;
                result.PowerOffUnconfirmed = snapshot.PowerOffUnconfirmed;
                result.RecoveryHardDeadlineUtc = snapshot.RecoveryHardDeadlineUtcTicks;
                result.PauseSince = snapshot.PauseSinceUtcTicks;
                result.PowerDisableSince = snapshot.PowerDisableSinceUtcTicks;
                result.Incident = snapshot.RecoveryIncident ?? string.Empty;
                result.Context = snapshot.RecoveryContext ?? string.Empty;
                result.StageOrdinal = snapshot.StageOrdinal;
                result.Active = snapshot.ActiveRecovery;
            }
            catch (Exception ex)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Warning,
                    "读取结构化Watchdog恢复快照失败；不根据界面状态猜测孤儿暂停或电源待断。",
                    "独立看门狗",
                    ex);
            }
            return result;
        }


        private void OnWatchdogStopAllRequested(string reason, string correlationId)
        {
            if (Volatile.Read(ref _operatorStopRequested) != 0) return;
            try
            {
                BeginInvoke((Action)(async () =>
                {
                    if (Volatile.Read(ref _operatorStopRequested) != 0) return;
                    try
                    {
                        var safety = await _epb.PrepareForFreshRestartAsync(
                            new StopContext
                            {
                                Source = StopSource.SystemFault,
                                Reason = "独立看门狗整批接管：" + reason,
                                Initiator = "MTTFTest.Watchdog",
                                CorrelationId = string.IsNullOrWhiteSpace(correlationId)
                                    ? Guid.NewGuid().ToString("N")
                                    : correlationId,
                                RequestedUtc = DateTime.UtcNow
                            }).ConfigureAwait(true);
                        WatchdogRuntime.NotifyStopCompleted(ToWatchdogStopSummary(safety), reason);
                        Interlocked.Exchange(ref _watchdogTakeoverExit, 1);
                        ProjectLogHub.Flush(true);
                        System.Windows.Forms.Application.Exit();
                    }
                    catch (Exception ex)
                    {
                        ProjectLogHub.Write(ProjectLogLevel.Error,
                            "独立看门狗请求 StopAll 未能收口，外部进程将在15秒期限后强制接管：" + ex.Message,
                            "独立看门狗", ex);
                    }
                }));
            }
            catch { }
        }

        private static WatchdogStopSummary ToWatchdogStopSummary(StopSafetyResult safety)
        {
            return new WatchdogStopSummary
            {
                MotorOffConfirmed = safety?.MotorOffCommandSucceeded == true,
                PowerOffConfirmed = safety?.PowerOffConfirmed == true,
                PressureSafeConfirmed = safety?.PressureSafeConfirmed == true,
                RawDrained = safety?.RawStorageFlushed == true,
                PersistenceConfirmed = safety?.PersistenceBoundaryConfirmed == true,
                ContinuityConfirmed = safety?.DataContinuityCompromised == false,
                LogicalQuiescenceConfirmed = safety?.LogicalQuiescenceConfirmed == true,
                RequiresProcessRestart = safety?.RequiresProcessRestart == true,
                TimedOut = safety?.TimedOut == true,
                Outcome = safety?.Outcome.ToString() ?? string.Empty,
                LastStage = safety?.LastStage.ToString() ?? string.Empty,
                Detail = safety == null ? "StopSafetyResultUnavailable" :
                    $"CanRestart={safety.CanRestartInProcess};Motor={safety.MotorError};Power={safety.PowerError};" +
                    $"Pressure={safety.PressureError};Persistence={safety.PersistenceError};Logical={safety.LogicalError}"
            };
        }

        private async Task QuiesceAndFlushForUnattendedRestartAsync()
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(UnattendedQuiesceTotalTimeoutMs / 1000.0 * Stopwatch.Frequency);
            var daqDev1 = _daqDev1;
            var daqDev2 = _daqDev2;
            var acquirer = twoDeviceAiAcquirer;
            try
            {
                // 先刷新末端Raw队列以释放背压槽，再停止并排空上游，最后二次Flush。
                // Flush API没有调用方CancellationToken，因此每一步都必须由共享总期限
                // 的Task.WhenAny隔离，禁止某个文件锁/磁盘I/O永久占住进程重启单飞门。
                if (daqDev1 != null)
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev1.FlushRawToDiskAsync(),
                            "Dev1首次Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                if (daqDev2 != null)
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev2.FlushRawToDiskAsync(),
                            "Dev2首次Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                if (acquirer != null)
                {
                    var drainTimeoutMs = Math.Min(
                        10000,
                        RequireUnattendedQuiesceTimeRemaining("DAQ采集/Raw发布链排空", deadline));
                    await AwaitUnattendedQuiesceStageAsync(
                            async () =>
                            {
                                if (!await acquirer.StopAndDrainAsync(drainTimeoutMs)
                                        .ConfigureAwait(false))
                                    throw new TimeoutException(
                                        $"DAQ采集/Raw发布链{drainTimeoutMs}ms内未排空。");
                            },
                            "DAQ采集/Raw发布链排空",
                            deadline)
                        .ConfigureAwait(false);
                }
                if (daqDev1 != null)
                {
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev1.FlushRawToDiskAsync(),
                            "Dev1最终Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev1.FlushStatToDiskAsync(),
                            "Dev1最终Stat落盘",
                            deadline)
                        .ConfigureAwait(false);
                }
                if (daqDev2 != null)
                {
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev2.FlushRawToDiskAsync(),
                            "Dev2最终Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev2.FlushStatToDiskAsync(),
                            "Dev2最终Stat落盘",
                            deadline)
                        .ConfigureAwait(false);
                }
                if (acquirer != null)
                    await AwaitUnattendedQuiesceStageAsync(
                            () => Task.Run(() => acquirer.Dispose()),
                            "DAQ资源释放",
                            deadline)
                        .ConfigureAwait(false);
            }
            catch
            {
                // StopAll已经确认执行器和压力安全；此处再异步冻结新采样，避免一个
                // 卡死的NI驱动调用突破总期限。失败会回到RestartAsync并释放单飞门，
                // 检查点保留Armed，后续仍可在重启预算内重试。
                RequestAcquirerStopAfterQuiesceFailure(acquirer);
                throw;
            }
        }

        private static async Task AwaitUnattendedQuiesceStageAsync(
            Func<Task> operation,
            string stage,
            long deadline)
        {
            var remainingMs = RequireUnattendedQuiesceTimeRemaining(stage, deadline);
            var operationTask = operation?.Invoke() ??
                                throw new ArgumentNullException(nameof(operation));
            using (var delayCancellation = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(remainingMs, delayCancellation.Token);
                var completed = await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false);
                if (!ReferenceEquals(completed, operationTask))
                {
                    ObserveLateUnattendedQuiesceTask(operationTask, stage);
                    throw new TimeoutException(
                        $"无人值守静默/落盘超过总期限{UnattendedQuiesceTotalTimeoutMs}ms，" +
                        $"阶段={stage}。");
                }

                delayCancellation.Cancel();
                await operationTask.ConfigureAwait(false);
            }
        }

        private static int RequireUnattendedQuiesceTimeRemaining(string stage, long deadline)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                throw new TimeoutException(
                    $"无人值守静默/落盘超过总期限{UnattendedQuiesceTotalTimeoutMs}ms，" +
                    $"阶段={stage}。");

            var remainingMs = (long)Math.Ceiling(
                remainingTicks * 1000.0 / Stopwatch.Frequency);
            return (int)Math.Max(1L, Math.Min((long)int.MaxValue, remainingMs));
        }

        private static void ObserveLateUnattendedQuiesceTask(Task operationTask, string stage)
        {
            _ = operationTask.ContinueWith(
                faulted => ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    $"无人值守静默超时后后台阶段最终失败。Stage={stage}",
                    "无人值守恢复",
                    faulted.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void RequestAcquirerStopAfterQuiesceFailure(IO.NI.TwoDeviceAiAcquirer acquirer)
        {
            if (acquirer == null) return;
            Task stopTask;
            try
            {
                stopTask = Task.Run(() => acquirer.Stop());
            }
            catch (Exception ex)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "无人值守静默失败后无法调度DAQ停止。",
                    "无人值守恢复",
                    ex);
                return;
            }

            ObserveLateUnattendedQuiesceTask(stopTask, "失败后DAQ停止");
        }

        private void UpdateUnattendedRunAuthorization(ChannelRuntimeStateChangedEvent state)
        {
            if (state == null || _cfg?.Test == null) return;
            if (state.State == ChannelRuntimeState.Starting)
            {
                var pending = UnattendedRunCheckpointStore.Load();
                if (pending?.Armed == true &&
                    (pending.RecoveryChainPendingStart || pending.InProcessRecoveryPending))
                {
                    // 自动恢复的新 EpbManager 会先发布各通道 Starting。此时学习/资格
                    // 和正式 Timer 尚未全部成功，不能提前覆盖父 RunId 或消耗恢复代次；
                    // 完整通道集合验证通过后由 ConfirmRecoveryBatchStarted 原子提交。
                    return;
                }
                var selected = Enumerable.Range(1, 12)
                    .Where(channel => EpbGroup[channel - 1]?.CtrlJoinTest?.Checked == true)
                    .ToArray();
                UnattendedRecoveryCoordinator.Arm(_cfg, selected, state.RunId, state.RunEpoch);
                return;
            }

            if (state.State != ChannelRuntimeState.Completed) return;
            var checkpoint = UnattendedRunCheckpointStore.Load();
            if (checkpoint == null || !checkpoint.Armed || checkpoint.SelectedChannels == null) return;
            lock (_channelRuntimeStates)
            {
                var permanent = new HashSet<int>(CapturePermanentAlarmedChannels());
                var manuallyExcluded = new HashSet<int>(
                    _channelRuntimeStates.Values
                        .Where(current => !current.Enabled ||
                                          current.State == ChannelRuntimeState.ManualStopped ||
                                          current.State == ChannelRuntimeState.NotEnabled)
                        .Select(current => current.Channel));
                var completionSet = checkpoint.SelectedChannels
                    .Where(channel => !permanent.Contains(channel) && !manuallyExcluded.Contains(channel))
                    .ToArray();
                if (completionSet.Length > 0 && completionSet.All(channel =>
                        _channelRuntimeStates.TryGetValue(channel, out var current) &&
                        current.State == ChannelRuntimeState.Completed))
                    UnattendedRunCheckpointStore.DisarmIfRunMatches(
                        state.RunId.ToString("N"),
                        "FormalRunCompleted");
                if (completionSet.Length > 0 && completionSet.All(channel =>
                        _channelRuntimeStates.TryGetValue(channel, out var finished) &&
                        finished.State == ChannelRuntimeState.Completed))
                {
                    WatchdogRuntime.NotifyRunCompleted();
                    WatchdogRuntime.ShutdownLocalClient();
                }
            }
        }

        internal async Task ResumeFromWatchdogCheckpointAsync(
            UnattendedRunCheckpoint checkpoint,
            WatchdogRecoveryIntent intent)
        {
            for (var attempt = 0; attempt < 100 && (_epb == null || _cfg == null); attempt++)
                await Task.Delay(100).ConfigureAwait(true);
            if (_epb == null || _cfg?.Test == null)
                throw new InvalidOperationException("安全接管硬件与控制对象初始化超时。");

            // 主进程完成硬件初始化后先由控制主站显式写入全断能。Watchdog 本身不持有 NI。
            if (_do == null || !_do.AllOff())
                throw new InvalidOperationException("安全接管无法写入全部 DO 关闭。");
            _ao?.ResetAll();

            var safety = await _epb.StopAllAsync(new StopContext
            {
                Source = StopSource.SystemFault,
                Reason = "独立看门狗恢复新进程 SafetyTakeover",
                Initiator = "WatchdogSafetyTakeover",
                CorrelationId = Guid.NewGuid().ToString("N"),
                RequestedUtc = DateTime.UtcNow
            }, CancellationToken.None).ConfigureAwait(true);
            var latchRejection = string.Empty;
            if (!safety.CanRestartInProcess ||
                !_epb.TryClearRecoveryProcessRestartLatchAfterVerifiedPreflight(
                    safety,
                    out latchRejection))
            {
                var detail =
                    "安全接管预检未完整确认，禁止重新学习。" +
                    $" Motor={safety.MotorError}; Power={safety.PowerError}; " +
                    $"Pressure={safety.PressureError}; Persistence={safety.PersistenceError}; " +
                    $"Logical={safety.LogicalError}; Latch={latchRejection}";
                var fingerprint = RecoveryFailurePolicy.BuildFingerprint(
                    $"SafetyTakeover|Motor={safety.MotorOffCommandSucceeded}|" +
                    $"Power={safety.PowerOffConfirmed}|Pressure={safety.PressureSafeConfirmed}|" +
                    $"Persistence={safety.PersistenceBoundaryConfirmed}|" +
                    $"Logical={safety.LogicalQuiescenceConfirmed}|{detail}");
                throw new WatchdogHardwareUnavailableException(fingerprint, detail);
            }
            ClearHardwareUnavailable();

            var abortedOrphanCycles = _diskWriter?.AbortInterruptedCyclesForSoftwareRecovery(
                DateTime.UtcNow) ?? 0;
            WatchdogRuntime.NotifySafetyPreflightPassed(
                $"MotorOff={safety.MotorOffCommandSucceeded};PowerOff={safety.PowerOffConfirmed};" +
                $"PressureSafe={safety.PressureSafeConfirmed};Logical={safety.LogicalQuiescenceConfirmed};" +
                $"AbortedOrphanCycles={abortedOrphanCycles}");

            if (intent.ExcludedChannels?.Length > 0)
            {
                var excluded = new HashSet<int>(intent.ExcludedChannels);
                checkpoint.SelectedChannels = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                    .Where(channel => !excluded.Contains(channel))
                    .ToArray();
            }

            ProjectLogHub.Write(ProjectLogLevel.Info,
                $"Watchdog SafetyTakeover 已确认。Session={intent.SessionId};Attempt={intent.RecoveryAttempt};" +
                $"PreviousPid={intent.PreviousPid};Persistence={safety.PersistenceBoundaryConfirmed};" +
                $"Logical={safety.LogicalQuiescenceConfirmed};AbortedOrphanCycles={abortedOrphanCycles}",
                "独立看门狗");
            await ResumeFromUnattendedCheckpointAsync(checkpoint).ConfigureAwait(true);
        }

        internal async Task PrepareSafeIdleAfterWatchdogAsync(string sessionId, int previousPid)
        {
            UnattendedRecoveryCoordinator.Disarm("ManualStopWatchdogIdleRestart");
            UnattendedRunCheckpointStore.ClearGracefulPause("ManualStopWatchdogIdleRestart");
            for (var attempt = 0; attempt < 100 && (_epb == null || _cfg == null); attempt++)
                await Task.Delay(100).ConfigureAwait(true);
            if (_epb == null || _cfg?.Test == null)
                throw new InvalidOperationException("空闲重启硬件与控制对象初始化超时。");
            if (_do == null || !_do.AllOff())
                throw new InvalidOperationException("空闲重启无法写入全部 DO OFF。");
            _ao?.ResetAll();
            var safety = await _epb.StopAllAsync(new StopContext
            {
                Source = StopSource.ManualUi,
                Reason = "人工停止超时后的 Watchdog 空闲重启安全预检",
                Initiator = "WatchdogIdleRestart",
                CorrelationId = Guid.NewGuid().ToString("N"),
                RequestedUtc = DateTime.UtcNow
            }, CancellationToken.None).ConfigureAwait(true);
            if (!safety.MotorOffCommandSucceeded || !safety.PowerOffConfirmed)
            {
                BtnStartTest.Enabled = false;
                throw new InvalidOperationException(
                    "空闲重启全断能预检未通过，开始试验保持禁用。" +
                    $" Motor={safety.MotorError}; Power={safety.PowerError}");
            }
            Interlocked.Exchange(ref _operatorStopRequested, 1);
            ProjectLogHub.Write(
                ProjectLogLevel.Info,
                $"人工停止超时后已安全重启到空闲模式。Session={sessionId};PreviousPid={previousPid};AutoResume=false",
                "独立看门狗");
            ApplyBatchPauseState(BatchPauseState.Idle);
        }

        internal async Task ResumeFromUnattendedCheckpointAsync(UnattendedRunCheckpoint checkpoint)
        {
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            for (var attempt = 0; attempt < 100 && (_epb == null || _cfg == null); attempt++)
                await Task.Delay(100);
            if (_epb == null || _cfg?.Test == null)
                throw new InvalidOperationException("实时监视硬件与控制对象初始化超时。所有输出保持关闭。");

            var authorized = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => _cfg.Test.GetEpbRecord(channel)?.Enabled != false)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (authorized.Length == 0)
                throw new InvalidOperationException("检查点没有有效的测试通道。所有输出保持关闭。");

            // InitializeEpbRecords 已在控制对象创建前同时回填正式证据圈和 index.db
            // 的 mechanical_completed 物理完成事实。进程恢复必须以机械耐久事实计算剩余圈，
            // 同时用检查点证明进度没有倒退；不能仅靠旧 XML，也不能让 Remaining=0
            // 的已完成通道在新进程中再多跑一圈。
            var durableRemaining = authorized.ToDictionary(
                channel => channel,
                channel =>
                {
                    var record = _cfg.Test.GetEpbRecord(channel);
                    return record.GetRemainingMechanicalCycles(_cfg.Test.TestTarget);
                });
            var remainingPlan = EpbManager.BuildUnattendedRemainingCyclePlan(
                authorized,
                checkpoint.RemainingFormalCycles,
                durableRemaining);
            if (!remainingPlan.IsValid)
                throw new InvalidOperationException(
                    remainingPlan.Error + "；拒绝在正式圈进度证据不一致时自动上电。");

            var selected = remainingPlan.Channels;
            foreach (var channel in authorized)
            {
                var key = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var checkpointValue = checkpoint.RemainingFormalCycles[key];
                var durableValue = remainingPlan.RemainingCycles[channel];
                if (durableValue < checkpointValue)
                    ProjectLogHub.Write(
                        ProjectLogLevel.Info,
                        $"FieldMetric RECOVERY_PROGRESS Result=DurableAhead EPB={channel} " +
                        $"DurableRemaining={durableValue} CheckpointRemaining={checkpointValue}",
                        "FIELD");
            }

            if (selected.Length == 0)
            {
                UnattendedRunCheckpointStore.Disarm("FormalRunAlreadyCompletedAtRecoveryStartup");
                UnattendedRecoveryCoordinator.LogRecoveryStartupRecovered(
                    Guid.TryParse(checkpoint.RunId, out var completedRunId)
                        ? completedRunId
                        : Guid.Empty,
                    Array.Empty<int>());
                PostSafetyStatus(
                    "无人值守进程交接确认所有授权通道均已达到目标正式圈；保持全断能并关闭恢复授权。",
                    false);
                return;
            }

            _cfg.Test.LearnCycles = Math.Max(5, checkpoint.LearnCycles);
            for (var channel = 1; channel <= 12; channel++)
            {
                var control = EpbGroup[channel - 1]?.CtrlJoinTest;
                if (control != null) control.Checked = selected.Contains(channel);
            }
            _epb.EpbTestCycle = remainingPlan.RemainingCycles
                .Where(pair => pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            PostSafetyStatus(
                $"系统恢复预检：项目={checkpoint.TestName}，待续通道=[{string.Join(",", selected)}]，" +
                $"根RunId={checkpoint.RootRunId}，父RunId={checkpoint.RunId}，" +
                $"恢复代次={checkpoint.RestartGeneration + 1}；重新学习={_cfg.Test.LearnCycles}圈；" +
                $"剩余正式圈=[{string.Join(",", selected.Select(channel => $"EPB{channel}:{remainingPlan.RemainingCycles[channel]}"))}]；" +
                "正式计数从SQLite成功提交圈数的下一完整圈继续。",
                false);
            await Task.Delay(250);
            var startResult = await StartUnattendedBatchAsync(selected).ConfigureAwait(true);
            if (startResult.StartedChannels.Length == 0 &&
                startResult.CompletedDuringStartChannels.Length > 0)
            {
                UnattendedRunCheckpointStore.Disarm(
                    "MechanicalTargetCompletedDuringRecoveryLearning");
                UnattendedRecoveryCoordinator.LogRecoveryStartupRecovered(
                    startResult.TestRunId,
                    Array.Empty<int>());
                PostSafetyStatus(
                    $"无人值守恢复的学习/资格阶段已完成全部剩余机械目标圈；" +
                    $"完成通道=[{string.Join(",", startResult.CompletedDuringStartChannels)}]，" +
                    "保持安全停止并关闭恢复授权。",
                    false);
                return;
            }
            UnattendedRecoveryCoordinator.ConfirmRecoveryBatchStarted(
                _cfg,
                startResult.StartedChannels,
                startResult.TestRunId,
                _epb.WatchdogRunEpoch);
            var commitGeneration = Math.Max(
                checkpoint.Revision,
                Interlocked.Read(ref _watchdogRecoveryBatchCommitGeneration) + 1);
            Interlocked.Exchange(
                ref _watchdogRecoveryBatchCommitGeneration,
                commitGeneration);
            WatchdogRuntime.NotifyRecoveryBatchCommitted(
                $"RunId={startResult.TestRunId:N};RunEpoch={_epb.WatchdogRunEpoch};" +
                $"Channels=[{string.Join(",", startResult.StartedChannels.OrderBy(x => x))}]",
                commitGeneration);
            try
            {
                // 新 Run 身份已原子提交；从这里开始只允许观察性动作。日志或 UI
                // 状态提示失败不能向外冒泡并触发对健康新批次的再次进程回收。
                UnattendedRecoveryCoordinator.LogRecoveryStartupRecovered(
                    startResult.TestRunId,
                    startResult.StartedChannels);
                PostSafetyStatus(
                    $"无人值守进程交接启动已确认：RunId={startResult.TestRunId:N}，" +
                    $"通道=[{string.Join(",", startResult.StartedChannels.OrderBy(x => x))}]。",
                    false);
            }
            catch (Exception observerError)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "无人值守进程交接已提交新批次，但提交后观察性处理失败；" +
                    "保留新批次继续运行。",
                    "无人值守恢复",
                    observerError);
            }
        }

        protected override void OnFormClosing(System.Windows.Forms.FormClosingEventArgs e)
        {
            WatchdogRuntime.StopAllRequested -= OnWatchdogStopAllRequested;
            WatchdogRuntime.TransportLost -= OnWatchdogTransportLost;
            WatchdogRuntime.TransportError -= OnWatchdogTransportError;
            // Watchdog recovery children may close after a failed takeover and must leave
            // the session armed so the sidecar can retry. Manual stop/application closing
            // have already revoked authorization through their explicit lifecycle paths.
            if (Volatile.Read(ref _watchdogTakeoverExit) == 0 &&
                !WatchdogRuntime.IsAttached &&
                !UnattendedRunCheckpointStore.IsGracefulPauseArmed())
                UnattendedRecoveryCoordinator.Disarm("MonitorClosing");
            try
            {
                if (!UnattendedRecoveryCoordinator.DrainBackgroundTasksAsync(2000)
                        .GetAwaiter()
                        .GetResult())
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        "窗口关闭时无人值守恢复任务未在2秒内收口；任务仍受监督，进程退出后不会继续控制硬件。",
                        "无人值守恢复");
            }
            catch (Exception ex)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "窗口关闭等待无人值守恢复任务异常：" + ex.Message,
                    "无人值守恢复");
            }
            base.OnFormClosing(e);
        }
    }
}
