using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Timing;

namespace Controller
{
    internal enum TimerRuntimeHealthAction
    {
        None = 0,
        PublishPausePending = 1,
        PublishPaused = 2,
        BeginSelfHealing = 3
    }

    internal sealed class TimerRuntimeHealthDecision
    {
        internal TimerRuntimeHealthAction Action { get; set; }
        internal string ReasonCode { get; set; } = string.Empty;
        internal string ReasonText { get; set; } = string.Empty;
    }

    internal sealed class TimerRuntimeEventGateDecision
    {
        internal bool Accepted { get; set; }
        internal string ReasonCode { get; set; } = string.Empty;
    }

    internal enum RecoveryInvariantEscalationAction
    {
        ReturnToBatchOwner = 0,
        FormalGroupReset = 1,
        FailSafeTerminal = 2
    }

    public sealed partial class EpbManager
    {
        private readonly System.Threading.Timer _timerRuntimeWatchdog;
        private readonly int _timerRuntimeWatchdogIntervalMs;
        private readonly int _timerRuntimeSilenceThresholdMs;
        private readonly int _recoveryOrphanGraceMs;
        private readonly ConcurrentDictionary<int, byte> _timerRuntimeRecoveries =
            new ConcurrentDictionary<int, byte>();
        // 以 RunId/RunEpoch/Device/Correlation/Channel 组成一次性孤儿暂停身份。
        // 旧任务迟到或新 Run 复用通道号时，不能再次创建第二个恢复 owner。
        private readonly ConcurrentDictionary<string, byte> _orphanPauseRecoveryAttempts =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Recovering 的连续进入时刻独立于状态文本刷新保存。恢复任务即使被取消、
        // owner 被移除或重复发布 Recovering，也不能重置 60 秒绝对期限。
        private readonly ConcurrentDictionary<int, long> _recoveringSinceUtcTicks =
            new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<int, long> _recoveringSinceMonotonicTicks =
            new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<string, byte> _orphanRecoveryEscalations =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, long> _timerRuntimeGenerations =
            new ConcurrentDictionary<int, long>();
        private readonly ConcurrentDictionary<int, TimerRuntimeObserverRegistration>
            _timerRuntimeObservers =
                new ConcurrentDictionary<int, TimerRuntimeObserverRegistration>();
        private int _timerRuntimeWatchdogBusy;

        private void TrackRecoveringRuntimeTransition(
            ChannelRuntimeStateChangedEvent previous,
            ChannelRuntimeStateChangedEvent current)
        {
            if (current == null || current.Channel < 1 || current.Channel > 12) return;
            if (current.State != ChannelRuntimeState.Recovering)
            {
                _recoveringSinceUtcTicks.TryRemove(current.Channel, out _);
                _recoveringSinceMonotonicTicks.TryRemove(current.Channel, out _);
                return;
            }

            var newRecoveryIdentity = previous == null ||
                                      previous.State != ChannelRuntimeState.Recovering ||
                                      previous.RunId != current.RunId ||
                                      previous.RunEpoch != current.RunEpoch ||
                                      previous.CorrelationId != current.CorrelationId;
            if (newRecoveryIdentity)
            {
                _recoveringSinceUtcTicks[current.Channel] = current.TimestampUtc.Ticks;
                _recoveringSinceMonotonicTicks[current.Channel] = Stopwatch.GetTimestamp();
            }
            else
            {
                _recoveringSinceUtcTicks.TryAdd(current.Channel, current.TimestampUtc.Ticks);
                _recoveringSinceMonotonicTicks.TryAdd(
                    current.Channel,
                    Stopwatch.GetTimestamp());
            }
        }

        private sealed class TimerRuntimeObserverRegistration
        {
            internal HighPrecisionTimer Timer { get; set; }
            internal long Generation { get; set; }
            internal long RunEpoch { get; set; }
            internal Action<HighPrecisionTimerStateChangedEvent> Handler { get; set; }
        }

        private void AttachTimerRuntimeObserver(int channel, HighPrecisionTimer timer)
        {
            if (timer == null) return;
            var generation = _timerRuntimeGenerations.AddOrUpdate(
                channel,
                1,
                (_, current) => checked(current + 1));
            var runEpoch = Interlocked.Read(ref _runEpoch);
            Action<HighPrecisionTimerStateChangedEvent> handler = update =>
                OnTimerRuntimeStateChanged(
                    channel,
                    timer,
                    generation,
                    runEpoch,
                    update);
            var registration = new TimerRuntimeObserverRegistration
            {
                Timer = timer,
                Generation = generation,
                RunEpoch = runEpoch,
                Handler = handler
            };

            while (true)
            {
                if (_timerRuntimeObservers.TryGetValue(channel, out var previous))
                {
                    if (!_timerRuntimeObservers.TryUpdate(channel, registration, previous))
                        continue;
                    try { previous.Timer.StateChanged -= previous.Handler; } catch { }
                    break;
                }
                if (_timerRuntimeObservers.TryAdd(channel, registration)) break;
            }
            timer.StateChanged += handler;
            _log?.Info(
                $"TimerRuntimeObserverAttached Channel={channel} Generation={generation} " +
                $"RunEpoch={runEpoch} Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(timer)}",
                "Timer");
        }

        private void DetachTimerRuntimeObserver(int channel, HighPrecisionTimer timer)
        {
            if (timer == null ||
                !_timerRuntimeObservers.TryGetValue(channel, out var registration) ||
                !ReferenceEquals(registration.Timer, timer))
                return;
            var pair = new KeyValuePair<int, TimerRuntimeObserverRegistration>(
                channel,
                registration);
            if (!((ICollection<KeyValuePair<int, TimerRuntimeObserverRegistration>>)
                    _timerRuntimeObservers).Remove(pair))
                return;
            try { timer.StateChanged -= registration.Handler; } catch { }
            _log?.Info(
                $"TimerRuntimeObserverDetached Channel={channel} " +
                $"Generation={registration.Generation} RunEpoch={registration.RunEpoch} " +
                $"Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(timer)}",
                "Timer");
        }

        private void OnTimerRuntimeStateChanged(
            int channel,
            HighPrecisionTimer timer,
            long sourceGeneration,
            long sourceRunEpoch,
            HighPrecisionTimerStateChangedEvent update)
        {
            if (update == null) return;
            _log?.Info(
                $"TimerRuntimeState Channel={channel} " +
                $"Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(timer)} " +
                $"Generation={sourceGeneration} RunEpoch={sourceRunEpoch} " +
                $"State={update.State} IsRunning={update.IsRunning} IsPaused={update.IsPaused} " +
                $"Reason={update.Reason} LastStart={update.LastCycleStartedUtc:O} " +
                $"LastCompleted={update.LastCycleCompletedUtc:O}",
                "Timer");

            var current = _channelRuntimeStateStore.Get(channel);
            if (current == null) return;
            var sourceActive = _timers.TryGetValue(channel, out var activeTimer) &&
                               ReferenceEquals(activeTimer, timer);
            var registrationMatches =
                _timerRuntimeObservers.TryGetValue(channel, out var registration) &&
                ReferenceEquals(registration.Timer, timer) &&
                registration.Generation == sourceGeneration &&
                registration.RunEpoch == sourceRunEpoch;
            var currentGeneration = _timerRuntimeGenerations.TryGetValue(
                channel,
                out var observedGeneration)
                ? observedGeneration
                : 0;
            var gate = EvaluateTimerRuntimeEventGate(
                sourceActive,
                registrationMatches,
                sourceGeneration,
                currentGeneration,
                sourceRunEpoch,
                Interlocked.Read(ref _runEpoch),
                current.State,
                update.State);
            if (!gate.Accepted)
            {
                _log?.Warn(
                    $"StaleTimerEventIgnored Channel={channel} Code={gate.ReasonCode} " +
                    $"SourceGeneration={sourceGeneration} CurrentGeneration={currentGeneration} " +
                    $"SourceRunEpoch={sourceRunEpoch} CurrentRunEpoch={Interlocked.Read(ref _runEpoch)} " +
                    $"RuntimeState={current.State} TimerState={update.State}",
                    "Timer");
                return;
            }
            var pauseTransitionOwned =
                current.State == ChannelRuntimeState.PausePending &&
                update.State == HighPrecisionTimerRuntimeState.Paused;
            if (!IsDisplayedAsRunning(current.State) && !pauseTransitionOwned) return;

            if (ShouldAutoRecoverTimerAnomaly(
                    current.State,
                    update.State,
                    update.Reason,
                    IsBatchSessionActive,
                    CurrentBatchPauseState,
                    _channelPausedUtc.ContainsKey(channel) ||
                    _manualStopRequestedChannels.ContainsKey(channel),
                    IsAlarmStopRequested(channel),
                    IsChannelEnabled(channel),
                    IsEnergizationRevoked))
            {
                BeginTimerRuntimeSelfHealing(
                    channel,
                    timer,
                    "TimerRuntimeStateLost",
                    $"Timer状态异常：State={update.State} Reason={update.Reason}");
                return;
            }

            if (update.State == HighPrecisionTimerRuntimeState.PausePending)
            {
                _adaptiveLifecyclePort.PublishTimerPause(
                    channel,
                    update.Reason ?? "TimerPausePending",
                    current.RunId,
                    current.RunEpoch);
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.PausePending,
                    "TimerPausePending",
                    $"控制定时器已收到暂停请求。Reason={update.Reason}",
                    correlationId: ResolveTimerRecoveryCorrelation(channel));
            }
            else if (update.State == HighPrecisionTimerRuntimeState.Paused)
            {
                _adaptiveLifecyclePort.PublishTimerPause(
                    channel,
                    update.Reason ?? "TimerPaused",
                    current.RunId,
                    current.RunEpoch);
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Paused,
                    "TimerPaused",
                    $"控制定时器已暂停。Reason={update.Reason}",
                    correlationId: ResolveTimerRecoveryCorrelation(channel));
            }
        }

        internal static TimerRuntimeEventGateDecision EvaluateTimerRuntimeEventGate(
            bool sourceActive,
            bool registrationMatches,
            long sourceGeneration,
            long currentGeneration,
            long sourceRunEpoch,
            long currentRunEpoch,
            ChannelRuntimeState currentState,
            HighPrecisionTimerRuntimeState timerState)
        {
            if (!sourceActive)
                return new TimerRuntimeEventGateDecision
                    { Accepted = false, ReasonCode = "TimerInstanceNotActive" };
            if (!registrationMatches)
                return new TimerRuntimeEventGateDecision
                    { Accepted = false, ReasonCode = "TimerObserverRegistrationStale" };
            if (sourceGeneration <= 0 || sourceGeneration != currentGeneration)
                return new TimerRuntimeEventGateDecision
                    { Accepted = false, ReasonCode = "TimerGenerationStale" };
            if (sourceRunEpoch != currentRunEpoch)
                return new TimerRuntimeEventGateDecision
                    { Accepted = false, ReasonCode = "TimerRunEpochStale" };
            if (currentState == ChannelRuntimeState.Recovering &&
                (timerState == HighPrecisionTimerRuntimeState.PausePending ||
                 timerState == HighPrecisionTimerRuntimeState.Paused))
                return new TimerRuntimeEventGateDecision
                    { Accepted = false, ReasonCode = "RecoveryOwnsChannelLifecycle" };
            return new TimerRuntimeEventGateDecision
                { Accepted = true, ReasonCode = "CurrentTimerEvent" };
        }

        private Guid ResolveTimerRecoveryCorrelation(int channel)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            return !string.IsNullOrWhiteSpace(device) &&
                   _daqAutoRecovery.TryGetValue(device, out var context) &&
                   context != null && context.CorrelationId != Guid.Empty
                ? context.CorrelationId
                : _activeBatchId;
        }

        private void InspectTimerRuntimeHealth(object state)
        {
            if (Interlocked.Exchange(ref _timerRuntimeWatchdogBusy, 1) != 0)
                return;
            try
            {
                // The immutable watchdog aggregate must have its own current
                // logical source.  Heartbeat capture intentionally never scans
                // live controller dictionaries, so publish here as the bounded
                // periodic backstop for event-driven progress publications.
                PublishWatchdogLogicalSourceBestEffort("TimerRuntimeHealth");
                if (!IsRecoveryMemoryAdmissionAllowed()) return;
                if (!IsBatchSessionActive) return;
                TryLogFieldRuntimeMetrics();
                var nowUtc = DateTime.UtcNow;
                InspectRecoveringRuntimeInvariants();
                InspectExpectedExecutionProgress();
                foreach (var pair in _timers.ToArray())
                {
                    var channel = pair.Key;
                    var timer = pair.Value;
                    var runtime = _channelRuntimeStateStore.Get(channel);
                    if (TryRecoverOrphanDaqPause(channel, timer, runtime, nowUtc))
                        continue;
                    var decision = EvaluateTimerRuntimeHealth(
                        runtime?.State ?? ChannelRuntimeState.NotEnabled,
                        timer,
                        nowUtc,
                        _timerRuntimeSilenceThresholdMs);
                    if (decision.Action == TimerRuntimeHealthAction.None) continue;

                    if (decision.Action == TimerRuntimeHealthAction.PublishPausePending)
                    {
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.PausePending,
                            decision.ReasonCode,
                            decision.ReasonText,
                            correlationId: _activeBatchId);
                        continue;
                    }
                    if (decision.Action == TimerRuntimeHealthAction.PublishPaused)
                    {
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Paused,
                            decision.ReasonCode,
                            decision.ReasonText,
                            correlationId: _activeBatchId);
                        continue;
                    }

                    BeginTimerRuntimeSelfHealing(
                        channel,
                        timer,
                        decision.ReasonCode,
                        decision.ReasonText);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"Timer运行一致性看门狗异常已隔离：{ex.Message}", "Timer");
            }
            finally
            {
                Volatile.Write(ref _timerRuntimeWatchdogBusy, 0);
            }
        }

        private int _watchdogLogicalPublishQueued;

        private void RequestWatchdogLogicalSourcePublish()
        {
            if (Interlocked.CompareExchange(ref _watchdogLogicalPublishQueued, 1, 0) != 0)
                return;
            var queued = ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    PublishWatchdogLogicalSourceBestEffort("MechanicalCycleCompleted");
                }
                finally
                {
                    Volatile.Write(ref _watchdogLogicalPublishQueued, 0);
                }
            });
            if (!queued)
                Volatile.Write(ref _watchdogLogicalPublishQueued, 0);
        }

        private void PublishWatchdogLogicalSourceBestEffort(string source)
        {
            try
            {
                CaptureLogicalQuiescenceSnapshot();
            }
            catch (Exception ex)
            {
                _log?.Warn(
                    $"Watchdog逻辑源发布异常已隔离：Source={source};Error={ex.Message}",
                    "Watchdog");
            }
        }

        private void InspectRecoveringRuntimeInvariants()
        {
            if (!IsBatchSessionActive) return;
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            foreach (var runtime in _channelRuntimeStateStore.Snapshot()
                         .Where(item => item != null &&
                                        item.State == ChannelRuntimeState.Recovering &&
                                        item.RunId == runId &&
                                        item.RunEpoch == runEpoch &&
                                        item.Enabled))
            {
                var startedTicks = _recoveringSinceUtcTicks.GetOrAdd(
                    runtime.Channel,
                    runtime.TimestampUtc.Ticks);
                var startedMonotonic = _recoveringSinceMonotonicTicks.GetOrAdd(
                    runtime.Channel,
                    Stopwatch.GetTimestamp());
                var ageMs = Math.Max(
                    0d,
                    (Stopwatch.GetTimestamp() - startedMonotonic) * 1000d /
                    Stopwatch.Frequency);
                var hasCoverage = HasRecoveryExecutionCoverage(
                    runtime,
                    runEpoch,
                    out var taskCoverage);
                var lastProgressUtc = taskCoverage?.LastProgressUtc ?? runtime.TimestampUtc;
                var progressAgeMs = lastProgressUtc == default
                    ? ageMs
                    : Math.Max(0d, (DateTime.UtcNow - lastProgressUtc).TotalMilliseconds);
                var hardDeadlineReached = ageMs >= RecoveryGroupHardDeadlineMs &&
                                          progressAgeMs >= RecoveryGroupHardDeadlineMs;
                var ownerOrTaskMissing = ageMs >= _recoveryOrphanGraceMs &&
                                         progressAgeMs >= _recoveryOrphanGraceMs &&
                                         !hasCoverage;
                if (!hardDeadlineReached && !ownerOrTaskMissing) continue;

                ScheduleRecoveryInvariantEscalation(
                    runtime,
                    runId,
                    runEpoch,
                    startedTicks,
                    hardDeadlineReached
                        ? "RecoveryHardDeadlineExceeded"
                        : "OrphanRecoveryOwnerOrTaskMissing");
            }
        }

        private bool HasRecoveryExecutionCoverage(
            ChannelRuntimeStateChangedEvent runtime,
            long runEpoch,
            out RecoveryTaskRegistry.RecoveryTaskLeaseSnapshot exactTask)
        {
            exactTask = null;
            var channel = runtime.Channel;
            if (_recoveryTaskRegistry.TryGetActiveIncidentCoverage(
                    channel,
                    runEpoch,
                    runtime.CorrelationId,
                    out exactTask))
            {
                if (runtime.RecoveryOwnerKind != exactTask.OwnerKind ||
                    runtime.RecoveryTargetPhase != exactTask.TargetPhase ||
                    runtime.RecoveryOwnerId != exactTask.OwnerId ||
                    runtime.RecoveryOwnerGeneration != exactTask.RunEpoch)
                    RepairRecoveryOwnerProjection(runtime, exactTask);
                return true;
            }

            if (!RecoveryOwnershipPolicy.IsOwnerCurrent(runtime) ||
                runtime.RecoveryOwnerGeneration != runEpoch)
                return false;
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (runtime.RecoveryOwnerKind == RecoveryOwnerKind.DaqRecovery &&
                !string.IsNullOrWhiteSpace(device) &&
                _daqAutoRecovery.TryGetValue(device, out var daq) &&
                daq != null && daq.RunEpoch == runEpoch &&
                daq.Terminal.Current == DaqRecoveryTerminal.None &&
                daq.AffectedChannels.Contains(channel) &&
                daq.CorrelationId == runtime.RecoveryOwnerId)
                return true;

            if (_batchLifecycleGate.IsBusy &&
                (runtime.RecoveryOwnerKind == RecoveryOwnerKind.BatchStartup ||
                 runtime.RecoveryOwnerKind == RecoveryOwnerKind.BatchLearning ||
                 runtime.RecoveryOwnerKind == RecoveryOwnerKind.BatchQualification) &&
                runtime.RecoveryTargetPhase != RecoveryTargetPhase.Formal)
                return true;

            var hydraulicGroup = GetHydraulicGroupForChannel(channel);
            var owner = hydraulicGroup > 0
                ? _recoveryOwnership.GetOwner(hydraulicGroup)
                : string.Empty;
            var electricalGroup = GetElectricalGroupId(channel);
            switch (runtime.RecoveryOwnerKind)
            {
                case RecoveryOwnerKind.FormalTimer:
                    return _timerRuntimeRecoveries.ContainsKey(channel) ||
                           _recoverableChannelRestartJobs.ContainsKey(channel);
                case RecoveryOwnerKind.HydraulicGroupRecovery:
                    return !string.IsNullOrWhiteSpace(owner) ||
                           (hydraulicGroup > 0 &&
                            _hydraulicSoftwareRecoveryGroups.ContainsKey(hydraulicGroup));
                case RecoveryOwnerKind.PowerRecovery:
                    return electricalGroup > 0 &&
                           _powerSoftwareRecoveryGroups.ContainsKey(electricalGroup);
                case RecoveryOwnerKind.AffectedGroupRecovery:
                    return hydraulicGroup > 0 &&
                           _affectedGroupResetInProgress.ContainsKey(
                               GetAffectedGroupResetKey(runEpoch, hydraulicGroup));
                case RecoveryOwnerKind.Watchdog:
                    return false;
                default:
                    return false;
            }
        }

        private void RepairRecoveryOwnerProjection(
            ChannelRuntimeStateChangedEvent runtime,
            RecoveryTaskRegistry.RecoveryTaskLeaseSnapshot task)
        {
            ChannelRuntimeStateChangedEvent repaired;
            lock (_recoveryContractGate)
            {
                if (!_channelRuntimeStateStore.TryRepairRecoveryOwnerIfCurrent(
                        runtime.Channel,
                        runtime.Revision,
                        runtime.RunId,
                        runtime.RunEpoch,
                        runtime.CorrelationId,
                        task,
                        out repaired))
                    return;
                PublishRecoveryAggregateOwnershipSourceLocked();
                CaptureLogicalQuiescenceSnapshotLocked();
            }
            _log?.Warn(
                $"RecoveryOwnerMetadataMismatchRepaired EPB={runtime.Channel} " +
                $"IncidentId={runtime.CorrelationId:N} TaskId={task.Id} " +
                $"Owner={task.OwnerKind}/{task.OwnerId:N} Target={task.TargetPhase}",
                "Timer");
            TrackRecoveringRuntimeTransition(runtime, repaired);
            _adaptiveLifecyclePort.PublishRuntimeState(repaired);
        }

        internal static bool IsBatchLifecycleOwnedRecoveryReason(string reasonCode)
        {
            var reason = reasonCode ?? string.Empty;
            return reason.StartsWith("Startup", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "DaqStartSelfHealing", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "PowerStartSelfHealing", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "LearningPersistenceSelfHealing", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "HydraulicGenerationSelfHealing", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "PowerSupplyRecoveredForStartup", StringComparison.OrdinalIgnoreCase);
        }

        internal static RecoveryInvariantEscalationAction SelectRecoveryInvariantEscalationAction(
            RecoveryTargetPhase targetPhase,
            bool formalPhaseCommitted,
            bool batchLifecycleOwned,
            bool hardDeadlineReached)
        {
            if (targetPhase == RecoveryTargetPhase.Formal && formalPhaseCommitted)
                return RecoveryInvariantEscalationAction.FormalGroupReset;
            if (!hardDeadlineReached && batchLifecycleOwned &&
                (targetPhase == RecoveryTargetPhase.Startup ||
                 targetPhase == RecoveryTargetPhase.Learning ||
                 targetPhase == RecoveryTargetPhase.Qualification))
                return RecoveryInvariantEscalationAction.ReturnToBatchOwner;
            return RecoveryInvariantEscalationAction.FailSafeTerminal;
        }

        internal static string GetRecoveryInvariantIncidentKey(
            Guid runId,
            long runEpoch,
            int hydraulicGroup,
            long sourceStateRevision,
            RecoveryTargetPhase targetPhase)
        {
            return $"{runId:N}:{runEpoch}:{hydraulicGroup}:" +
                   $"{sourceStateRevision}:{targetPhase}";
        }

        private void ScheduleRecoveryInvariantEscalation(
            ChannelRuntimeStateChangedEvent runtime,
            Guid runId,
            long runEpoch,
            long startedUtcTicks,
            string code)
        {
            var hydraulicGroup = GetHydraulicGroupForChannel(runtime.Channel);
            if (hydraulicGroup <= 0) return;
            var hardDeadlineReached = string.Equals(
                code,
                "RecoveryHardDeadlineExceeded",
                StringComparison.OrdinalIgnoreCase);
            var action = SelectRecoveryInvariantEscalationAction(
                runtime.RecoveryTargetPhase,
                runtime.FormalPhaseCommitted,
                _batchLifecycleGate.IsBusy &&
                (runtime.RecoveryOwnerKind == RecoveryOwnerKind.BatchStartup ||
                 runtime.RecoveryOwnerKind == RecoveryOwnerKind.BatchLearning ||
                 runtime.RecoveryOwnerKind == RecoveryOwnerKind.BatchQualification),
                hardDeadlineReached);
            if (action == RecoveryInvariantEscalationAction.ReturnToBatchOwner)
            {
                _log?.Warn(
                    $"RecoveryInvariantReturnedToBatchOwner EPB={runtime.Channel} " +
                    $"Owner={runtime.RecoveryOwnerKind} Target={runtime.RecoveryTargetPhase} " +
                    $"Revision={runtime.Revision} Code={code}",
                    "Timer");
                return;
            }
            var sourceStateRevision = runtime.SourceStateRevision > 0
                ? runtime.SourceStateRevision
                : runtime.Revision;
            var key = GetRecoveryInvariantIncidentKey(
                runId,
                runEpoch,
                hydraulicGroup,
                sourceStateRevision,
                runtime.RecoveryTargetPhase);
            if (!_orphanRecoveryEscalations.TryAdd(key, 0)) return;
            var channel = runtime.Channel;
            var correlationId = runtime.CorrelationId == Guid.Empty
                ? Guid.NewGuid()
                : runtime.CorrelationId;
            var taskCovered = _recoveryTaskRegistry.TryGetActiveIncidentCoverage(
                channel,
                runEpoch,
                runtime.CorrelationId,
                out var coveredTask);
            var reason =
                $"Code={code}; EPB={channel}; StateReason={runtime.ReasonCode}; " +
                $"RecoveringSinceUtc={new DateTime(startedUtcTicks, DateTimeKind.Utc):O}; " +
                $"RunId={runId:N}; RunEpoch={runEpoch}; " +
                $"Owner={_recoveryOwnership.GetOwner(hydraulicGroup)}; " +
                $"TaskCovered={taskCovered};" +
                $"TaskId={coveredTask?.Id ?? 0};" +
                $"LastProgressUtc={(coveredTask == null ? "none" : coveredTask.LastProgressUtc.ToString("O"))};" +
                $"ProgressStage={coveredTask?.ProgressStage ?? "none"}";
            _log?.Error(
                $"RecoveryInvariantViolation {reason}; Action={action}; " +
                (action == RecoveryInvariantEscalationAction.FormalGroupReset
                    ? "仅在已提交正式阶段执行受影响组Stop→Start等价清场。"
                    : "预正式阶段禁止重入正式节拍，转入终态安全清场。"),
                "Timer");

            ObserveNonRecoveryLifecycleTask(Task.Run(async () =>
            {
                try
                {
                    if (!IsAffectedGroupResetRunCurrent(runId, runEpoch)) return;
                    var current = _channelRuntimeStateStore.Get(channel);
                    if (current == null || current.State != ChannelRuntimeState.Recovering ||
                        current.RunId != runId || current.RunEpoch != runEpoch)
                        return;
                    if (action == RecoveryInvariantEscalationAction.FormalGroupReset)
                    {
                        await ExecuteAffectedGroupResetIncidentAsync(
                                new[] { channel },
                                code,
                                correlationId,
                                runId,
                                runEpoch,
                                RecoveryTargetPhase.Formal)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        PublishStartBlockedAfterCleanup(
                            channel,
                            code,
                            "恢复所有权/硬期限异常；已阻止预正式阶段进入组级正式重入。",
                            correlationId,
                            new[] { channel });
                        TryEscalateSoftwareRecoveryCircuitOpen(
                            code,
                            reason,
                            new[] { channel },
                            runId,
                            runEpoch,
                            SoftwareRecoveryEscalationAttempts,
                            "ExternalRecoveryRequired");
                    }
                }
                catch (Exception ex)
                {
                    _log?.Error(
                        $"RecoveryInvariantEscalationFailed {reason}; Error={ex.Message}",
                        "Timer",
                        ex);
                    TryEscalateSoftwareRecoveryCircuitOpen(
                        code,
                        reason + "; Error=" + ex.Message,
                        new[] { channel },
                        runId,
                        runEpoch,
                        SoftwareRecoveryEscalationAttempts,
                        "ExternalRecoveryRequired");
                }
                // 同一 RunEpoch/组/生命周期 Revision 的事故永久锁存；只有新 run 或
                // 新状态 Revision 才可再次调度，避免现场每秒重复复位风暴。
            }), "RecoveryInvariantAffectedGroupReset", new[] { channel });
        }

        private bool TryRecoverOrphanDaqPause(
            int channel,
            HighPrecisionTimer timer,
            ChannelRuntimeStateChangedEvent runtime,
            DateTime nowUtc)
        {
            if (timer == null || runtime == null || !IsBatchSessionActive ||
                CurrentBatchPauseState != BatchPauseState.Running ||
                !IsChannelEnabled(channel) || IsAlarmStopRequested(channel) ||
                _channelPausedUtc.ContainsKey(channel) ||
                _manualStopRequestedChannels.ContainsKey(channel))
                return false;
            if (timer.RuntimeState != HighPrecisionTimerRuntimeState.PausePending &&
                timer.RuntimeState != HighPrecisionTimerRuntimeState.Paused)
                return false;
            if (!IsIntentionalOrOwnedTimerPauseReason(timer.PauseReason) ||
                timer.PauseReason.IndexOf("Daq", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            var pausedUtc = timer.PauseUtc;
            if (!pausedUtc.HasValue ||
                (nowUtc - pausedUtc.Value.ToUniversalTime()).TotalMilliseconds <
                Math.Max(1000, _timerRuntimeSilenceThresholdMs))
                return false;

            var device = _acq.GetDeviceForEpbChannel(channel);
            if (string.IsNullOrWhiteSpace(device)) return false;
            if (_daqAutoRecovery.TryGetValue(device, out var existing) &&
                existing != null && existing.Terminal.Current == DaqRecoveryTerminal.None)
                return false;

            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var correlationId = runtime.CorrelationId == Guid.Empty
                ? runId
                : runtime.CorrelationId;
            var key =
                $"{runId:N}:{runEpoch}:{device}:{correlationId:N}:{channel}";
            if (!_orphanPauseRecoveryAttempts.TryAdd(key, 0)) return true;

            var reason =
                $"孤儿DAQ暂停：EPB[{channel}] Timer={timer.RuntimeState} " +
                $"PauseReason={timer.PauseReason} PauseUtc={pausedUtc:O} " +
                $"RunId={runId:N} RunEpoch={runEpoch} Device={device} " +
                $"CorrelationId={correlationId:N}";
            _log?.Error(
                $"TimerOrphanPauseDetected Code=OrphanPauseRecoveryRequired {reason}",
                "Timer");

            try
            {
                ObserveNonRecoveryLifecycleTask(
                    BeginDaqAutoRecoveryAsync(
                        device,
                        "DaqOrphanPause",
                        reason,
                        correlationId,
                        restartDaq: true,
                        pausedUtc.Value),
                    "DaqOrphanPauseRecovery",
                    channel);

                // 补建也可能在安全事务或恢复 owner 上永久等待。独立于恢复任务的
                // 一次性期限观察者将其升级为 SystemFault/UnattendedBatchRecycle，
                // 由主程序转发 ExternalRecoveryRequired 给进程看门狗。
                ObserveBackgroundTask(Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(
                                Math.Max(_daqPersistenceRecoveryTimeoutMs,
                                    _timerRuntimeSilenceThresholdMs),
                                _batchSessionCts?.Token ?? CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!IsBatchSessionActive || runId != _activeBatchId ||
                            runEpoch != Interlocked.Read(ref _runEpoch)) return;
                        var current = _channelRuntimeStateStore.Get(channel);
                        if (current == null ||
                            (current.State != ChannelRuntimeState.PausePending &&
                             current.State != ChannelRuntimeState.Paused &&
                             current.State != ChannelRuntimeState.Recovering)) return;
                        if (!_daqAutoRecovery.TryGetValue(device, out var recovery) ||
                            recovery == null || recovery.Terminal.Current != DaqRecoveryTerminal.None)
                            return;
                        TryEscalateSoftwareRecoveryCircuitOpen(
                            "TimerOrphanPauseDeadline",
                            $"{reason}; ExternalRecoveryRequired=true; " +
                            $"DeadlineMs={_daqPersistenceRecoveryTimeoutMs}",
                            recovery.AffectedChannels,
                            runId,
                            runEpoch,
                            SoftwareRecoveryEscalationAttempts,
                            "ExternalRecoveryRequired");
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        _orphanPauseRecoveryAttempts.TryRemove(key, out _);
                    }
                }), "DaqOrphanPauseDeadline", channel);
            }
            catch (Exception ex)
            {
                _orphanPauseRecoveryAttempts.TryRemove(key, out _);
                _log?.Error(
                    $"Timer孤儿暂停补建投递失败；ExternalRecoveryRequired=true " +
                    $"{reason} Error={ex.Message}",
                    "Timer",
                    ex);
                TryEscalateSoftwareRecoveryCircuitOpen(
                    "TimerOrphanPauseDispatchFailed",
                    reason + "; ExternalRecoveryRequired=true; Error=" + ex.Message,
                    new[] { channel },
                    runId,
                    runEpoch,
                    SoftwareRecoveryEscalationAttempts,
                    "ExternalRecoveryRequired");
            }
            return true;
        }

        internal static TimerRuntimeHealthDecision EvaluateTimerRuntimeHealth(
            ChannelRuntimeState runtimeState,
            HighPrecisionTimer timer,
            DateTime nowUtc,
            int silenceThresholdMs)
        {
            var none = new TimerRuntimeHealthDecision();
            if (!IsDisplayedAsRunning(runtimeState) || timer == null) return none;

            if (timer.RuntimeState == HighPrecisionTimerRuntimeState.PausePending)
                return new TimerRuntimeHealthDecision
                {
                    Action = IsIntentionalOrOwnedTimerPauseReason(timer.PauseReason)
                        ? TimerRuntimeHealthAction.PublishPausePending
                        : TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerPausePendingMismatch",
                    ReasonText = $"通道仍显示运行，但Timer已等待暂停。Reason={timer.PauseReason}"
                };
            if (timer.RuntimeState == HighPrecisionTimerRuntimeState.Paused || timer.IsPaused)
                return new TimerRuntimeHealthDecision
                {
                    Action = IsIntentionalOrOwnedTimerPauseReason(timer.PauseReason)
                        ? TimerRuntimeHealthAction.PublishPaused
                        : TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerPausedMismatch",
                    ReasonText = $"通道仍显示运行，但Timer已暂停。Reason={timer.PauseReason}"
                };

            if (!timer.IsRunning ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Completed ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Faulted)
                return new TimerRuntimeHealthDecision
                {
                    Action = TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerNotRunning",
                    ReasonText = $"通道仍显示运行，但Timer状态为{timer.RuntimeState}。"
                };

            var thresholdMs = Math.Max(1000, silenceThresholdMs);
            var activityUtc = timer.LastCycleCompletedUtc ?? timer.LastCycleStartedUtc ?? timer.StartedUtc;
            if (activityUtc.HasValue && (nowUtc - activityUtc.Value).TotalMilliseconds > thresholdMs)
                return new TimerRuntimeHealthDecision
                {
                    Action = TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerHeartbeatStale",
                    ReasonText = $"Timer超过{thresholdMs}ms没有新循环心跳；" +
                                 $"LastStart={timer.LastCycleStartedUtc:O} " +
                                 $"LastCompleted={timer.LastCycleCompletedUtc:O}。"
                };
            return none;
        }

        private static bool IsDisplayedAsRunning(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.Running ||
                   state == ChannelRuntimeState.WarningRunning;
        }

        internal static bool IsIntentionalOrOwnedTimerPauseReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var ownedPrefixes = new[]
            {
                "BatchGracefulPause",
                "ChannelGracefulPause",
                "ManualPause",
                "Daq",
                "Hydraulic",
                "PowerSupply",
                "RecoverableWarning",
                "FormalPersistence",
                "FormalControl",
                "ActiveCycle",
                "ElectricalGroup",
                "TimerSelfHealing",
                "Watchdog",
                "StopAll",
                "StopSafety",
                "ApplicationClosing"
            };
            return ownedPrefixes.Any(prefix =>
                reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool ShouldAutoRecoverTimerAnomaly(
            ChannelRuntimeState runtimeState,
            HighPrecisionTimerRuntimeState timerState,
            string reason,
            bool batchActive,
            BatchPauseState batchPauseState,
            bool channelPaused,
            bool alarmStopRequested,
            bool channelEnabled,
            bool stopInProgress = false)
        {
            if (stopInProgress || !batchActive || !channelEnabled ||
                channelPaused || alarmStopRequested)
                return false;
            if (batchPauseState != BatchPauseState.Running)
                return false;
            if (!IsDisplayedAsRunning(runtimeState))
                return false;

            if (timerState == HighPrecisionTimerRuntimeState.PausePending ||
                timerState == HighPrecisionTimerRuntimeState.Paused)
                return !IsIntentionalOrOwnedTimerPauseReason(reason);

            return timerState == HighPrecisionTimerRuntimeState.Stopped ||
                   timerState == HighPrecisionTimerRuntimeState.Completed ||
                   timerState == HighPrecisionTimerRuntimeState.Faulted;
        }

        private bool CanContinueTimerRuntimeSelfHealing(int channel, Guid runId)
        {
            if (IsEnergizationRevoked || !IsBatchSessionActive ||
                runId == Guid.Empty || runId != _activeBatchId)
                return false;
            if (CurrentBatchPauseState != BatchPauseState.Running ||
                _channelPausedUtc.ContainsKey(channel) ||
                _manualStopRequestedChannels.ContainsKey(channel) ||
                IsAlarmStopRequested(channel) ||
                !IsChannelEnabled(channel))
                return false;
            var runtime = _channelRuntimeStateStore.Get(channel);
            return runtime != null && runtime.State == ChannelRuntimeState.Recovering;
        }

        private void BeginTimerRuntimeSelfHealing(
            int channel,
            HighPrecisionTimer failedTimer,
            string reasonCode,
            string reasonText)
        {
            if (IsEnergizationRevoked) return;
            if (!_timerRuntimeRecoveries.TryAdd(channel, 0)) return;

            if (IsEnergizationRevoked)
            {
                _timerRuntimeRecoveries.TryRemove(channel, out _);
                return;
            }

            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var sessionToken = _batchSessionCts?.Token ?? CancellationToken.None;
            _log?.Error(
                $"EPB[{channel}] Timer运行异常，进入无人值守自恢复。" +
                $"Code={reasonCode} Detail={reasonText}",
                "Timer");

            try { failedTimer?.Pause($"TimerSelfHealing:{reasonCode}"); } catch { }
            try { CancelCyclePauseCts(channel); } catch { }
            UnmarkHydraulicParticipant(channel);
            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = CaptureSoftwareRecoveryCycles(new[] { channel });
            TrySealSoftwareRecoveryCycleWindows(
                cutoffCycles,
                cutoffUtc,
                $"TimerRuntimeSelfHealing:{reasonCode}");

            // TryBeginRecoveryIncident creates and binds a non-running wrapper.
            // The caller explicitly starts that wrapper only after the owner and
            // Recovering state have been published.
            RecoveryIncidentHandle recoveryIncident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                    var attempt = 0;
                    try
                    {
                        while (CanContinueTimerRuntimeSelfHealing(channel, runId))
                        {
                            attempt++;
                            HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease ownership = null;
                            try
                            {
                                ownership = await _recoveryOwnership.AcquireAsync(
                                        GetHydraulicGroupForChannel(channel),
                                        $"TIMER:{channel}:{runId:N}",
                                        RecoveryOwnerPriority.Hydraulic,
                                        RecoveryOwnershipTakeoverTimeoutMs,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                                var recoveryToken = ownership.Token;
                                RequireSoftwareRecoveryOutputOff(channel, "TimerRuntimeSelfHealing");
                                await HydraulicMarkReleaseAsync(channel).ConfigureAwait(false);
                                if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;

                            if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                                    cutoffCycles,
                                    cutoffUtc,
                                    $"TimerRuntimeSelfHealing:{reasonCode}",
                                    _daqPersistenceRecoveryTimeoutMs,
                                    recoveryToken)
                                .ConfigureAwait(false))
                                throw new SoftwareSelfHealingRetryException(
                                    "Timer异常圈 Raw/耐久边界尚未闭合；保持断能并继续重试。");

                            await EnsureDaqReadyBeforeStartAsync(
                                    new[] { channel },
                                    recoveryToken)
                                .ConfigureAwait(false);
                            await EnsurePowerSupplyReadyForChannelsAsync(
                                    new[] { channel },
                                    recoveryToken)
                                .ConfigureAwait(false);
                            if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;

                            var plan = GetCompatibleStaggerPlan(new[] { channel });
                            await EnsureMotorReleasedBeforeFormalRejoinAsync(
                                    new[] { channel },
                                    plan,
                                    "TimerRuntimeSelfHealing",
                                    recoveryToken)
                                .ConfigureAwait(false);
                            if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;

                            RejoinFormalChannelsAtSharedFutureSlot(
                                new[] { channel },
                                plan,
                                "TimerRuntimeSelfHealed",
                                $"Timer异常已完成安全重建并自动续测（Attempt={attempt}）",
                                allowTerminalReset: false);

                            if (!_timers.TryGetValue(channel, out var replacement) ||
                                ReferenceEquals(replacement, failedTimer) ||
                                !replacement.IsRunning)
                                throw new SoftwareSelfHealingRetryException(
                                    "新Timer未进入运行态。");

                            _log?.Info(
                                $"EPB[{channel}] Timer已自动重建并继续测试。Attempt={attempt}",
                                "Timer");
                            return;
                            }
                            catch (Exception ex)
                            {
                                if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;
                                if (TryEscalateSoftwareRecoveryCircuitOpen(
                                    "TimerRuntimeSelfHealing",
                                    $"Code={reasonCode}; Error={ex.Message}",
                                    new[] { channel },
                                    runId,
                                    runEpoch,
                                    attempt))
                                    return;
                                PublishRecoveryIncidentState(
                                channel,
                                ChannelRuntimeState.Recovering,
                                "TimerRuntimeSelfHealingRetry",
                                $"Timer自动重建第{attempt}次未完成；三次失败将整批重建：{ex.Message}",
                                correlationId: runId,
                                recoveryOwnerKind: RecoveryOwnerKind.FormalTimer,
                                recoveryTargetPhase: RecoveryTargetPhase.Formal,
                                recoveryOwnerId: runId,
                                recoveryOwnerGeneration: runEpoch);
                                _log?.Warn(
                                $"EPB[{channel}] Timer自动重建第{attempt}次失败；三次失败将整批重建：{ex.Message}",
                                "Timer");
                                ownership?.Dispose();
                                ownership = null;
                                await Task.Delay(
                                    SelectTimerRecoveryRetryDelayMs(attempt),
                                    sessionToken)
                                .ConfigureAwait(false);
                            }
                            finally
                            {
                                ownership?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        // The state transition is the terminal commit point.  If
                        // no earlier path published Running/StartBlocked, publish
                        // a safe terminal while the incident lease is still held,
                        // then remove the owner/task contract.
                        recoveryIncident?.CompleteAfterTerminal(contract =>
                        {
                            var current = _channelRuntimeStateStore.Get(channel);
                            if (current?.State == ChannelRuntimeState.Recovering)
                                PublishChannelRuntimeState(
                                    channel,
                                    ChannelRuntimeState.StartBlocked,
                                    "TimerRecoveryTerminalWithoutRejoin",
                                    "Timer自恢复未完成重入，已保持安全终态。",
                                    correlationId: contract.IncidentId,
                                    allowTerminalReset: true,
                                    allowSystemFaultReset: true);
                        });
                        _timerRuntimeRecoveries.TryRemove(channel, out _);
                    }
                };
            }

            var started = TryBeginRecoveryIncident(
                "TimerRuntimeSelfHealing",
                runId,
                runEpoch,
                RecoveryOwnerKind.FormalTimer,
                RecoveryTargetPhase.Formal,
                runId,
                new[] { channel },
                _ => BuildRecoveryWorker(),
                contract =>
                {
                    PublishRecoveryIncidentState(
                        channel,
                        ChannelRuntimeState.Recovering,
                        "TimerRuntimeSelfHealing",
                        $"{reasonText}；已安全断电，正在自动重建Timer并继续测试。",
                        correlationId: contract.IncidentId,
                        recoveryOwnerKind: contract.OwnerKind,
                        recoveryTargetPhase: contract.TargetPhase,
                        recoveryOwnerId: contract.OwnerId,
                        recoveryOwnerGeneration: contract.RunEpoch);
                },
                out recoveryIncident);
            if (!started)
            {
                _timerRuntimeRecoveries.TryRemove(channel, out _);
                return;
            }
            try
            {
                _taskSupervisor.Observe(
                    recoveryIncident.WorkerTask,
                    "TimerRuntimeSelfHealing",
                    _activeBatchId,
                    channel);
            }
            catch (Exception observeError)
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(channel, "TimerRecoveryObserveFailed"); }
                    catch { }
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.StartBlocked,
                        "TimerRecoveryObserveFailed",
                        $"Timer自恢复任务登记失败，已保持安全终态：{observeError.Message}",
                        correlationId: contract.IncidentId,
                        allowTerminalReset: true,
                        allowSystemFaultReset: true);
                });
                return;
            }
            if (!recoveryIncident.Start())
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(channel, "TimerRecoveryStartRejected"); }
                    catch { }
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.StartBlocked,
                        "TimerRecoveryStartRejected",
                        "Timer自恢复启动许可被拒绝，已保持安全终态。",
                        correlationId: contract.IncidentId,
                        allowTerminalReset: true,
                        allowSystemFaultReset: true);
                });
            }
        }

        internal static int SelectTimerRecoveryRetryDelayMs(int attempt)
        {
            if (attempt <= 1) return 1000;
            if (attempt == 2) return 2000;
            if (attempt == 3) return 5000;
            if (attempt == 4) return 10000;
            return 30000;
        }
    }
}
