using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;

namespace Controller
{
    /// <summary>
    /// Hydraulic physical-release boundary.  The production adapter is the
    /// manager's existing pressure/ForceRelease implementation; no deadline
    /// is owned here—the outer stop runner remains the sole clock authority.
    /// </summary>
    internal interface IStopSafetyHydraulicAdapter
    {
        Task<(bool ok, string error)> ConfirmPressureSafeAsync(
            StopContext context,
            Task<(bool ok, string error)> powerOffTask,
            long stopGeneration);
    }

    /// <summary>
    /// Adapter boundary between the transaction runner and the existing EPB
    /// stop algorithms.  The adapter contains no timing or retry policy: all
    /// ownership/deadline decisions remain in StopSafetyTransactionRunner.
    /// </summary>
    internal sealed class EpbManagerStopSafetyExecutionPort : IStopSafetyExecutionPort
    {
        private readonly Func<StopSafetyStage, StopSafetyTransactionContext, CancellationToken,
            Task<StopSafetyPortResult>> _executeStage;
        private readonly Action<StopSafetyTransactionContext, string> _safeIdle;

        internal EpbManagerStopSafetyExecutionPort(
            Func<StopSafetyStage, StopSafetyTransactionContext, CancellationToken,
                Task<StopSafetyPortResult>> executeStage,
            Action<StopSafetyTransactionContext, string> safeIdle)
        {
            _executeStage = executeStage ?? throw new ArgumentNullException(nameof(executeStage));
            _safeIdle = safeIdle ?? throw new ArgumentNullException(nameof(safeIdle));
        }

        public Task<StopSafetyPortResult> ExecuteStageAsync(
            StopSafetyStage stage,
            StopSafetyTransactionContext transaction,
            CancellationToken safetyCancellationToken)
        {
            return _executeStage(stage, transaction, safetyCancellationToken);
        }

        public void EnterSafeIdleOnce(
            StopSafetyTransactionContext transaction,
            string reason)
        {
            _safeIdle(transaction, reason);
        }
    }

    public partial class EpbManager
    {
        private readonly object _stopSafetyProductionGate = new object();
        private StopSafetyProductionState _stopSafetyProductionState;

        // A late durable completion belongs to the original stopped session. It may authorize
        // exit, but must never resurrect the expired runner or authorize another trial.
        private bool TryResumeStopPersistenceForClose(out Task<StopSafetyResult> task)
        {
            task = null;
            var state = Volatile.Read(ref _stopSafetyProductionState);
            if (_lastStopSafetyResult?.PersistenceBoundaryConfirmed != false ||
                !IsEnergizationRevoked || IsBatchSessionActive || state?.PersistenceTask == null ||
                state.Generation != Interlocked.Read(ref _stopSafetyGeneration) ||
                state.FinalPersistenceBoundaries.Count != 2) return false;
            lock (state.PersistenceGate)
            {
                if (state.ClosePersistenceTask == null || state.ClosePersistenceTask.IsFaulted ||
                    state.ClosePersistenceTask.IsCanceled ||
                    state.ClosePersistenceTask.IsCompleted && !state.ClosePersistenceTask.Result.CanCloseApplication)
                    state.ClosePersistenceTask = Task.Run(async () =>
                    {
                        await state.PersistenceTask.ConfigureAwait(false);
                        while (state.PersistenceTask.Result?.Succeeded != true && IsEnergizationRevoked &&
                               state.Generation == Interlocked.Read(ref _stopSafetyGeneration))
                        {
                            await Task.Delay(2000).ConfigureAwait(false);
                            _log?.Warn($"StopPersistenceCloseRetry Transaction={state.TransactionId:N}; " +
                                $"RunId={state.RunId:N}; Error={state.PersistenceTask.Result?.Detail}", "EPB-Safety");
                            state.PersistenceErrors.Clear();
                            state.PersistenceTask = ExecuteStopPersistenceStageAsync(state);
                            await state.PersistenceTask.ConfigureAwait(false);
                        }
                        if (!IsEnergizationRevoked || state.Generation != Interlocked.Read(ref _stopSafetyGeneration))
                            throw new InvalidOperationException("StopPersistenceCloseIdentityChanged");
                        var result = ExecuteStopVerificationStage(state).Result;
                        result.RequiresProcessRestart = true;
                        return await CompleteFinalPersistenceAsync(Task.FromResult(result)).ConfigureAwait(false);
                    });
                task = state.ClosePersistenceTask;
                return true;
            }
        }

        /// <summary>
        /// Builds the production runner boundary while keeping the legacy
        /// hardware implementation behind an explicit port.  Callers that
        /// need to exercise the runner use the same factory with a fake port;
        /// no runner state is duplicated in UI/watchdog code.
        /// </summary>
        internal StopSafetyTransactionRunner CreateStopSafetyTransactionRunner(
            IStopSafetyExecutionPort port,
            IStopSafetyClock clock = null,
            StopSafetyTransactionOptions options = null)
        {
            return new StopSafetyTransactionRunner(
                port,
                clock,
                options,
                snapshot => _recoveryAggregateStore.PublishStopSource(snapshot),
                () => _activeBatchId,
                () => Interlocked.Read(ref _runEpoch));
        }

        /// <summary>
        /// Single production seam for the real EpbManager stage algorithms.
        /// Tests and the public stop path must obtain the port through this
        /// factory; no UI/watchdog adapter is allowed to reproduce the
        /// physical ordering or SafeIdle behavior.
        /// </summary>
        internal IStopSafetyExecutionPort CreateStopSafetyExecutionPortForProduction()
        {
            return new EpbManagerStopSafetyExecutionPort(
                (stage, transaction, safetyToken) =>
                    ExecuteExistingStopSafetyStageAsync(stage, transaction, safetyToken),
                EnterStopSafetySafeIdle);
        }

        /// <summary>
        /// Installs the narrowly-scoped production acceptance seams.  This is
        /// intentionally internal and can only be called before the manager
        /// owns a StopAll task/runner; regular UI/watchdog code has exactly one
        /// entry point, <see cref="EpbManager.StopAllAsync(StopContext, CancellationToken)"/>.
        /// </summary>
        internal void ConfigureStopSafetyProductionSeams(
            IStopSafetyClock clock = null,
            IStopSafetyHydraulicAdapter hydraulicAdapter = null)
        {
            lock (_stopSafetyGate)
            {
                if (_stopSafetyTask != null || _activeStopSafetyRunner != null)
                    throw new InvalidOperationException(
                        "Stop safety seams must be configured before StopAll starts.");
                _stopSafetyClock = clock ?? new SystemStopSafetyClock();
                _stopSafetyHydraulicAdapter = hydraulicAdapter;
            }
        }

        private bool TrySubmitStopSafetyPhysicalOff(
            int channel,
            Action<HighPriorityDoTelemetry> completion,
            out Guid commandId)
        {
            // Physical admission, worker registration, NI write and receipt
            // completion are owned by the real DoController.  EpbManager has
            // no replacement writer seam, so every stop action follows this
            // one production chain.
            return TrySubmitEpbOffHighPriority(channel, completion, out commandId);
        }

        private Task<(bool ok, string error)> ConfirmPressureSafeForStopAsync(
            StopContext context,
            Task<(bool ok, string error)> powerOffTask,
            long stopGeneration)
        {
            var adapter = Volatile.Read(ref _stopSafetyHydraulicAdapter);
            return adapter == null
                ? ConfirmPressureSafeForStopCoreAsync(
                    context,
                    powerOffTask,
                    stopGeneration)
                : adapter.ConfirmPressureSafeAsync(
                    context,
                    powerOffTask,
                    stopGeneration);
        }

        /// <summary>
        /// Production StopAll entry.  Each physical action is dispatched by
        /// the stage-specific execution port below; the legacy monolith is not
        /// part of the production path.
        /// </summary>
        private Task<StopSafetyResult> RunBoundedStopSafetyAsync(
            StopContext context,
            CancellationToken callerToken,
            long generation)
        {
            var port = CreateStopSafetyExecutionPortForProduction();
            // The runner is process-scoped.  StopAll, final-exit cleanup and
            // late UI re-entry must share the same lease/cache/orphan slot;
            // creating a runner per call would reset the SafeIdle and timeout
            // guards and allow a second stop core to be launched.
            var runner = Volatile.Read(ref _activeStopSafetyRunner);
            if (runner == null)
            {
                lock (_stopSafetyGate)
                {
                    runner = _activeStopSafetyRunner;
                    if (runner == null)
                    {
                        runner = new StopSafetyTransactionRunner(
                            port,
                            Volatile.Read(ref _stopSafetyClock) ?? new SystemStopSafetyClock(),
                            new StopSafetyTransactionOptions(),
                            PublishStopSafetyRunnerProgress,
                            () => _activeBatchId,
                            () => Interlocked.Read(ref _runEpoch),
                            () => Interlocked.Read(ref _stopSafetyGeneration));
                        Volatile.Write(ref _activeStopSafetyRunner, runner);
                    }
                }
            }
            var task = runner.StopAsync(context, callerToken);
            return task.ContinueWith(
                    completed =>
                    {
                        try
                        {
                            var result = completed.GetAwaiter().GetResult();
                            if ((result.RequiresProcessRestart && !result.LogicalCleanupPending) || result.TimedOut)
                                Interlocked.Exchange(ref _processRestartRequired, 1);
                            lock (_stopSafetyGate)
                            {
                                if (generation == Interlocked.Read(ref _stopSafetyGeneration))
                                    _lastStopSafetyResult = result.Clone();
                            }
                            return result;
                        }
                        finally
                        {
                            // Keep the process-scoped runner alive.  A normal
                            // completed transaction may be re-entered, while
                            // a timed-out runner retains its cached result and
                            // its single orphan until a fresh process starts.
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private async Task<StopSafetyPortResult> ExecuteExistingStopSafetyStageAsync(
            StopSafetyStage stage,
            StopSafetyTransactionContext transaction,
            CancellationToken safetyToken)
        {
            if (transaction == null) return StopSafetyPortResult.Failure("Stop transaction missing");
            var state = GetOrCreateStopSafetyProductionState(transaction);
            switch (stage)
            {
                case StopSafetyStage.AdmitAndSubmitSafety:
                    return await ExecuteImmediateStopSafetyBoundaryAsync(state).ConfigureAwait(false);
                case StopSafetyStage.FreezeActiveWork:
                    return ExecuteStopFreezeStage(state);
                case StopSafetyStage.RevokeExecutionAuthorization:
                    return ExecuteStopRevokeStage(state);
                case StopSafetyStage.SubmitPhysicalOff:
                    return ExecuteStopPhysicalOffStage(state);
                case StopSafetyStage.StartPowerDisable:
                    return await ExecuteStopPowerDisableStageAsync(state).ConfigureAwait(false);
                case StopSafetyStage.ClearTimerAndRunner:
                    return ExecuteStopTimerRunnerStage(state);
                case StopSafetyStage.ClearRecoveryOwners:
                    return await ExecuteStopRecoveryOwnerStageAsync(state, safetyToken).ConfigureAwait(false);
                case StopSafetyStage.ReleaseHydraulics:
                    return await ExecuteStopHydraulicReleaseStageAsync(state, safetyToken).ConfigureAwait(false);
                case StopSafetyStage.StopAcquisition:
                    return ExecuteStopAcquisitionStage(state);
                case StopSafetyStage.ClosePersistenceBoundary:
                    lock (state.PersistenceGate)
                        state.PersistenceTask = state.PersistenceTask ?? ExecuteStopPersistenceStageAsync(state);
                    return await state.PersistenceTask.ConfigureAwait(false);
                case StopSafetyStage.VerifyLogicalQuiescence:
                    return ExecuteStopVerificationStage(state);
                default:
                    return StopSafetyPortResult.Success(stage.ToString());
            }
        }

        private async Task<StopSafetyPortResult> ExecuteImmediateStopSafetyBoundaryAsync(
            StopSafetyProductionState state)
        {
            var revoke = ExecuteStopRevokeStage(state);
            var off = ExecuteStopPhysicalOffStage(state);
            var power = await ExecuteStopPowerDisableStageAsync(state).ConfigureAwait(false);
            if (!revoke.Succeeded || !off.Succeeded || !power.Succeeded)
                return StopSafetyPortResult.Failure(
                    string.Join(";", new[] { revoke.Error, off.Error, power.Error }
                        .Where(item => !string.IsNullOrWhiteSpace(item))),
                    physicalOffSubmitted: off.PhysicalOffSubmitted,
                    powerDisableStarted: power.PowerDisableStarted);
            return StopSafetyPortResult.Success(
                "已撤权并立即提交DO OFF及电源Disable；后续阶段仅负责证据收口。",
                true,
                "StopImmediateSafetyBoundary",
                1,
                physicalOffSubmitted: true,
                powerDisableStarted: true);
        }

        private StopSafetyProductionState GetOrCreateStopSafetyProductionState(
            StopSafetyTransactionContext transaction)
        {
            lock (_stopSafetyProductionGate)
            {
                if (_stopSafetyProductionState == null ||
                    _stopSafetyProductionState.TransactionId != transaction.TransactionId)
                {
                    var channels = _timers.Keys
                        .Concat(_runners.Keys)
                        .Concat(_hydraulicParticipants.Keys)
                        .Concat(_hydraulicLeaseByChannel.Keys)
                        .Concat(_stopCtsByChannel.Keys)
                        .Concat(_cyclePauseCtsByChannel.Keys)
                        .Concat(_currentCycleNumberByChannel.Keys)
                        .Concat(Enumerable.Range(1, 12).Where(IsChannelEnergized))
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray();
                    _stopSafetyProductionState =
                        new StopSafetyProductionState(transaction, channels,
                            CaptureSoftwareRecoveryCycles(Enumerable.Range(1, 12)));
                }
                return _stopSafetyProductionState;
            }
        }

        private StopSafetyPortResult ExecuteStopFreezeStage(
            StopSafetyProductionState state)
        {
            var context = state.Context;
            if (context.Source == StopSource.ManualUi ||
                context.Source == StopSource.ApplicationClosing ||
                context.Source == StopSource.ProgramExit ||
                context.Source == StopSource.UnknownLegacy)
            {
                foreach (var channel in Enumerable.Range(1, 12))
                    _manualStopRequestedChannels[channel] = 0;
            }
            if (context.Source != StopSource.SystemFault)
                NotifyRunAuthorizationRevoking(
                    context.Source,
                    context.Reason,
                    context.Initiator,
                    Guid.TryParse(context.CorrelationId, out var requestedCorrelation)
                        ? requestedCorrelation
                        : Guid.NewGuid(),
                    context.FaultScope);
            // Freeze is a non-blocking producer barrier.  The process-wide
            // energization fence and execution-permit cancellation were
            // installed synchronously at StopAll admission.  Here we only
            // pause/cancel producers and freeze cycle evidence; runtime-map
            // removal is deliberately deferred until after DO OFF and PSU
            // Disable have been submitted so a channel/recovery lock cannot
            // consume the two-second immediate safety deadline.
            FreezeStopRuntimeObjects(state);
            return StopSafetyPortResult.Success(
                "停止事务已冻结活动圈与身份；等待后续安全动作。",
                materialProgress: true,
                evidenceSource: "StopFreeze",
                evidenceVersion: 1);
        }

        private void FreezeStopRuntimeObjects(
            StopSafetyProductionState state)
        {
            if (state.RuntimeProducersFrozen)
                return;

            var reason = "StopAll:" + state.Context.Source;
            // Pause every producer first.  This path is deliberately free of
            // StateChanged/log/cancellation callbacks.  The cycle identity is
            // then sampled again before any CTS/runtime participant is removed.
            foreach (var channel in state.Channels)
            {
                try
                {
                    if (_timers.TryGetValue(channel, out var timer))
                        timer.RequestSafetyPauseNonBlocking(reason);
                }
                catch (Exception ex)
                {
                    state.FreezeErrors.Add($"EPB{channel}:暂停Timer:{ex.Message}");
                }
            }

            foreach (var pair in CaptureSoftwareRecoveryCycles(Enumerable.Range(1, 12)))
            {
                if (!state.StopCycles.TryGetValue(pair.Key, out var frozen) ||
                    frozen != pair.Value)
                {
                    state.CyclesSealed = false;
                    state.FreezeErrors.Add(
                        $"StopAll暂停后二次圈身份变化 EPB={pair.Key};" +
                        $"Frozen={(state.StopCycles.TryGetValue(pair.Key, out var old) ? old : 0)};" +
                        $"Observed={pair.Value}");
                }
            }

            // Runtime objects and cancellation sources are atomically removed
            // from the authoritative maps after the second identity sample.
            // No user callback is invoked here and channel execution gates are
            // intentionally not acquired.  Slow Cancel/Dispose/Stop/event
            // detach work is queued by ClearTimerAndRunner after physical OFF.
            state.DetachedBatchSessionCts =
                Interlocked.Exchange(ref _batchSessionCts, null);
            foreach (var channel in state.Channels)
            {
                try
                {
                    if (_cyclePauseCtsByChannel.TryRemove(channel, out var pauseCts))
                        state.CleanupItems.Add(StopSafetyCleanupWorkItem.ForCancellationSource(
                            channel,
                            "CyclePauseCts.CancelDispose",
                            pauseCts));
                }
                catch (Exception ex)
                {
                    state.FreezeErrors.Add($"EPB{channel}:摘除PauseCTS:{ex.Message}");
                }
                try
                {
                    if (_stopCtsByChannel.TryRemove(channel, out var stopCts))
                        state.CleanupItems.Add(StopSafetyCleanupWorkItem.ForCancellationSource(
                            channel,
                            "StopCts.CancelDispose",
                            stopCts));
                }
                catch (Exception ex)
                {
                    state.FreezeErrors.Add($"EPB{channel}:摘除StopCTS:{ex.Message}");
                }
                try
                {
                    foreach (var timer in _timerRuntime.Remove(channel))
                        state.CleanupItems.Add(new StopSafetyCleanupWorkItem
                        {
                            Channel = channel,
                            Operation = "Timer.ObserverDetachAndStop",
                            Execute = () =>
                            {
                                DetachTimerRuntimeObserver(channel, timer);
                                timer.Stop();
                            }
                        });
                }
                catch (Exception ex)
                {
                    state.FreezeErrors.Add($"EPB{channel}:摘除Timer:{ex.Message}");
                }
                try
                {
                    foreach (var runner in _runnerRuntime.Remove(channel))
                        state.CleanupItems.Add(new StopSafetyCleanupWorkItem
                        {
                            Channel = channel,
                            Operation = "Runner.EventDetach",
                            Execute = () => DetachRunnerEvents(runner)
                        });
                }
                catch (Exception ex)
                {
                    state.FreezeErrors.Add($"EPB{channel}:摘除Runner:{ex.Message}");
                }
            }
            if (state.DetachedBatchSessionCts != null)
                state.CleanupItems.Add(StopSafetyCleanupWorkItem.ForCancellationSource(
                    0,
                    "BatchSessionCts.CancelDispose",
                    state.DetachedBatchSessionCts));
            state.RuntimeProducersFrozen = true;
        }

        private StopSafetyPortResult ExecuteStopRevokeStage(
            StopSafetyProductionState state)
        {
            if (state.AuthorizationRevoked)
                return StopSafetyPortResult.Success(
                    "执行授权已由立即安全阶段撤销。",
                    true,
                    "StopRevoke",
                    Math.Max(1, state.ForceAbortedHydraulicObjects + 1L));
            var context = state.Context;
            // 两秒快速隔离阶段只撤销准入和代次。EndBatchSession 会同步触发
            // CancellationToken 回调、清理全局液压槽和会话对象，现场四通道曾
            // 因该长尾让本阶段误超时并升级“需重启软件”。这些耗时清场统一移到
            // 具有 5 秒预算的 ClearTimerAndRunner 阶段。
            Interlocked.Exchange(ref _batchSessionActive, 0);
            Interlocked.Exchange(ref _formalPhaseCommitted, 0);
            Volatile.Write(ref _energizationRevoked, 1);
            Interlocked.Increment(ref _runEpoch);
            foreach (var channel in Enumerable.Range(1, 12))
            {
                try { RevokeChannelExecutionPermit(channel, "StopSafetyTransaction"); }
                catch (Exception ex) { state.Errors.Add($"EPB{channel}:撤权:{ex.Message}"); }
            }
            state.AuthorizationRevoked = true;
            try
            {
                state.ForceAbortedHydraulicObjects = _hydCoordinator?.ForceAbortRun(
                    state.RunId,
                    $"StopAll:{context.Source}:{state.TransactionId:N}") ?? 0;
            }
            catch (Exception ex)
            {
                state.Errors.Add("HydraulicAbort:" + ex.Message);
            }
            return state.Errors.Count == 0
                ? StopSafetyPortResult.Success(
                    "已撤销执行授权、旧RunEpoch与液压代次。",
                    true,
                    "StopRevoke",
                    state.ForceAbortedHydraulicObjects + 1L)
                : StopSafetyPortResult.Failure(string.Join(";", state.Errors));
        }

        private StopSafetyPortResult ExecuteStopPhysicalOffStage(
            StopSafetyProductionState state)
        {
            if (state.OffSubmissionStarted)
                return StopSafetyPortResult.Success(
                    $"高优先级DO OFF已提交，Accepted={state.OffCompletions.Count}/{state.Channels.Length}",
                    true,
                    "StopPhysicalOff",
                    Math.Max(1, state.OffCompletions.Count),
                    physicalOffSubmitted: true);
            state.OffSubmissionStarted = true;
            foreach (var channel in state.Channels)
            {
                var completion = new TaskCompletionSource<HighPriorityDoTelemetry>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    if (TrySubmitStopSafetyPhysicalOff(
                            channel,
                            telemetry => completion.TrySetResult(telemetry),
                            out _))
                    {
                        state.OffCompletions[channel] = completion;
                    }
                    else
                    {
                        state.OffFallbackStages[channel] = "StopAllAdmissionRejected";
                    }
                }
                catch (Exception ex)
                {
                    state.Errors.Add($"EPB{channel}:DO OFF提交:{ex.Message}");
                    state.OffFallbackStages[channel] = "StopAllSubmissionException";
                }
            }
            // Admission rejection is not a reason to stop the sequence before
            // the PSU owner is started.  The next stage starts the group-wide
            // Disable first, then schedules each rejected-channel fallback.
            // This preserves the physical safety ordering and lets the runner
            // report a fast PSU failure at its own stage.
            return StopSafetyPortResult.Success(
                $"已提交高优先级DO OFF，Accepted={state.OffCompletions.Count}/{state.Channels.Length}",
                true,
                "StopPhysicalOff",
                Math.Max(1, state.OffCompletions.Count),
                physicalOffSubmitted: true);
        }

        private async Task<StopSafetyPortResult> ExecuteStopPowerDisableStageAsync(
            StopSafetyProductionState state)
        {
            if (state.PowerTask == null)
            {
                state.PowerTask = ConfirmPowerOffForStopAsync(
                    state.Context,
                    CancellationToken.None,
                    state.Generation);
            }
            // The PSU action is now in flight.  Only after this point may
            // rejected DO admissions receive their independent fallback.
            if (state.OffFallbackStages.Count != 0)
                StartStopOffFallbacks(state);
            CancelStopRecoverableRestartJobs(state);
            try
            {
                state.CyclesSealed = TrySealSoftwareRecoveryCycleWindows(
                    state.StopCycles,
                    state.StartedUtc,
                    $"StopAll:{state.Context.Source}:AfterSafetyActionsStarted") &&
                    state.CyclesSealed;
            }
            catch (Exception ex)
            {
                state.CyclesSealed = false;
                state.Errors.Add("CycleSeal:" + ex.Message);
            }
            // Seal the cycle evidence before exposing channel terminal state;
            // MarkBatchIdle is the final logical action in this stage.  This
            // ordering keeps a partially sealed old core from being reported
            // idle and avoids duplicate terminal publication on re-entry.
            PublishStopTerminalStates(state);
            try { MarkBatchIdle("批次已取消"); }
            catch (Exception ex) { state.Errors.Add("MarkBatchIdle:" + ex.Message); }
            // A synchronous/fast PSU rejection is an immediate safety fault;
            // a slow action is covered by the runner's named stage deadline
            // and the process-level hard escape (the port has no timer).
            var immediatePowerFailure = GetImmediatePowerDisableFailure(state.PowerTask);
            if (!string.IsNullOrWhiteSpace(immediatePowerFailure))
                return StopSafetyPortResult.Failure(
                    "PowerDisable:" + immediatePowerFailure,
                    powerDisableStarted: true);
            await Task.Yield();
            // Give a synchronously-failing coordinator one continuation turn
            // to publish its result, but never wait here for the physical
            // 15-second confirmation barrier.
            immediatePowerFailure = GetImmediatePowerDisableFailure(state.PowerTask);
            if (!string.IsNullOrWhiteSpace(immediatePowerFailure))
                return StopSafetyPortResult.Failure(
                    "PowerDisable:" + immediatePowerFailure,
                    powerDisableStarted: true);
            return StopSafetyPortResult.Success(
                "已启动全部电源组Disable，等待独立回读。",
                true,
                "StopPowerDisable",
                1,
                powerDisableStarted: true);
        }

        private StopSafetyPortResult ExecuteStopTimerRunnerStage(
            StopSafetyProductionState state)
        {
            if (!state.RuntimeObjectsFrozen)
            {
                try
                {
                    var terminalStatus = state.Context.Source == StopSource.TargetCompleted
                        ? "Successful"
                        : state.Context.Source == StopSource.AlarmInterlock
                            ? "Failed"
                            : "Cancelled";
                    EndBatchSession(
                        cancel: false,
                        publishIdleState: false,
                        terminalStatus: terminalStatus,
                        terminalReason: state.Context.Reason);
                }
                catch (Exception ex)
                {
                    state.FreezeErrors.Add("EndBatchSession:" + ex.Message);
                }
                foreach (var channel in state.Channels)
                {
                    // Capture any producer that raced with Freeze.  The global
                    // energization fence makes the late owner harmless; only
                    // callback-free map removal is performed in this stage.
                    try
                    {
                        foreach (var timer in _timerRuntime.Remove(channel))
                            state.CleanupItems.Add(new StopSafetyCleanupWorkItem
                            {
                                Channel = channel,
                                Operation = "LateTimer.ObserverDetachAndStop",
                                Execute = () =>
                                {
                                    DetachTimerRuntimeObserver(channel, timer);
                                    timer.Stop();
                                }
                            });
                    }
                    catch (Exception ex) { state.FreezeErrors.Add($"EPB{channel}:摘除迟到Timer:{ex.Message}"); }
                    try
                    {
                        foreach (var runner in _runnerRuntime.Remove(channel))
                            state.CleanupItems.Add(new StopSafetyCleanupWorkItem
                            {
                                Channel = channel,
                                Operation = "LateRunner.EventDetach",
                                Execute = () => DetachRunnerEvents(runner)
                            });
                    }
                    catch (Exception ex) { state.FreezeErrors.Add($"EPB{channel}:摘除迟到Runner:{ex.Message}"); }
                    try { UnmarkHydraulicParticipant(channel); }
                    catch (Exception ex) { state.FreezeErrors.Add($"EPB{channel}:移除液压参与者:{ex.Message}"); }
                }
                QueueStopSafetyCleanup(state);
                state.RuntimeObjectsFrozen = true;
            }
            return state.FreezeErrors.Count == 0
                ? StopSafetyPortResult.Success(
                    $"已摘除Timer、Runner、CTS并移除液压参与者；" +
                    $"隔离清理已排队 {state.CleanupItems.Count} 项。",
                    materialProgress: true,
                    evidenceSource: "StopTimerRunner",
                    evidenceVersion: 1)
                : StopSafetyPortResult.Failure(
                    "运行对象清理失败:" + string.Join(";", state.FreezeErrors));
        }

        private void QueueStopSafetyCleanup(StopSafetyProductionState state)
        {
            if (Interlocked.Exchange(ref state.CleanupScheduled, 1) != 0)
                return;
            foreach (var item in state.CleanupItems.ToArray())
            {
                var captured = item;
                captured.ExecutionTask = Task.Factory.StartNew(
                    () => ExecuteStopSafetyCleanupItem(state, captured),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
        }

        private void ExecuteStopSafetyCleanupItem(
            StopSafetyProductionState state,
            StopSafetyCleanupWorkItem item)
        {
            var stopwatch = Stopwatch.StartNew();
            _log?.Info(
                $"StopCleanupOperationStarted Transaction={state.TransactionId:N} " +
                $"Channel={item.Channel} Operation={item.Operation}",
                "停止事务");
            try
            {
                item.Execute?.Invoke();
                _log?.Info(
                    $"StopCleanupOperationCompleted Transaction={state.TransactionId:N} " +
                    $"Channel={item.Channel} Operation={item.Operation} " +
                    $"ElapsedMs={stopwatch.ElapsedMilliseconds} Result=Success",
                    "停止事务");
            }
            catch (Exception ex)
            {
                item.Failure = ex.GetType().Name + ":" + ex.Message;
                Interlocked.Exchange(ref item.Failed, 1);
                _log?.Warn(
                    $"StopCleanupOperationFailed Transaction={state.TransactionId:N} " +
                    $"Channel={item.Channel} Operation={item.Operation} " +
                    $"ElapsedMs={stopwatch.ElapsedMilliseconds} Result={ex.GetType().Name}:{ex.Message}",
                    "停止事务");
            }
        }

        private async Task<StopSafetyPortResult> ExecuteStopRecoveryOwnerStageAsync(
            StopSafetyProductionState state,
            CancellationToken safetyToken)
        {
            try
            {
                var stageStarted = Stopwatch.GetTimestamp();
                var cancelDaq = CancelAllDaqRecoveriesAsync(
                    $"StopAll:{state.Context.Source}:{state.Context.CorrelationId}");
                await cancelDaq.ConfigureAwait(false);

                safetyToken.ThrowIfCancellationRequested();
                var elapsedMs = (int)Math.Ceiling(
                    (Stopwatch.GetTimestamp() - stageStarted) * 1000d / Stopwatch.Frequency);
                // Leave margin for the transaction runner to publish its stage
                // outcome before the five-second watchdog deadline.
                var remainingMs = Math.Max(1, 4400 - elapsedMs);
                var ownersTask = _recoveryOwnership.CancelAllAsync(
                    remainingMs,
                    safetyToken);
                var pendingTask = _recoveryTaskRegistry.DrainThroughEpochAsync(
                    state.RunEpoch,
                    remainingMs,
                    safetyToken);
                await Task.WhenAll(ownersTask, pendingTask).ConfigureAwait(false);
                var ownersExited = ownersTask.Result;
                var drain = pendingTask.Result;
                if (ownersExited != true || !drain.Drained)
                {
                    var residue = string.Join(",", drain.Residues.Select(item =>
                        $"{item.Operation}[{item.Kind};EPB={string.Join("/", item.Channels)}]"));
                    var supersededHydraulicOwners = ownersExited
                        ? 0
                        : _recoveryOwnership.SupersedeAll(
                            "StopSafetyRunEpochRevoked:" + state.RunEpoch);
                    var quarantined = _recoveryTaskRegistry.SupersedeThroughEpoch(
                        state.RunEpoch,
                        "StopSafetyRunEpochRevoked");
                    return StopSafetyPortResult.Success(
                        "恢复owner已按旧RunEpoch隔离，不再阻塞安全收口。" +
                        $" HydraulicOwners={supersededHydraulicOwners};" +
                        $" RegistryLeases={quarantined.Length};Residue={residue}",
                        true,
                        "StopRecoveryOwnersSuperseded",
                        Math.Max(1, quarantined.Length + supersededHydraulicOwners));
                }
                return StopSafetyPortResult.Success("DAQ与软件恢复owner已退出。", true, "StopRecoveryOwners", 1);
            }
            catch (OperationCanceledException)
            {
                return StopSafetyPortResult.Failure("恢复owner清理已到达事务截止。 ");
            }
            catch (Exception ex)
            {
                return StopSafetyPortResult.Failure("恢复owner清理失败: " + ex.Message);
            }
        }

        private async Task<StopSafetyPortResult> ExecuteStopHydraulicReleaseStageAsync(
            StopSafetyProductionState state, CancellationToken safetyToken)
        {
            try
            {
                await AwaitStopEvidenceWithProgressAsync(AwaitStopOffEvidenceAsync(state), state,
                    "DoOffReceipt", safetyToken).ConfigureAwait(false);
                state.PressureTask ??= ConfirmPressureSafeForStopAsync(
                    state.Context,
                    state.PowerTask,
                    state.Generation);
                await AwaitStopEvidenceWithProgressAsync(state.PressureTask, state,
                    "PressureFreshAndReleased", safetyToken).ConfigureAwait(false);
                var pressure = await state.PressureTask.ConfigureAwait(false);
                if (state.PowerTask != null)
                    await AwaitStopEvidenceWithProgressAsync(state.PowerTask, state,
                        "PowerOffReadback", safetyToken).ConfigureAwait(false);
                state.Power = state.PowerTask == null
                    ? (true, string.Empty)
                    : await state.PowerTask.ConfigureAwait(false);
                state.Pressure = pressure;
                state.MotorOk = state.OffErrors.Count == 0;
                if (!state.MotorOk || !state.Power.ok || !state.Pressure.ok)
                    return StopSafetyPortResult.Failure(
                        string.Join(";", state.OffErrors.Concat(new[]
                        {
                            state.Power.ok ? string.Empty : "Power:" + state.Power.error,
                            state.Pressure.ok ? string.Empty : "Pressure:" + state.Pressure.error
                        }).Where(item => !string.IsNullOrWhiteSpace(item))));
                return StopSafetyPortResult.Success(
                    "液压释放、DO完成与电源/压力回读均已取得。",
                    true,
                    "StopHydraulicRelease",
                    Math.Max(1, state.OffCompletions.Count),
                    physicalSafe: true);
            }
            catch (Exception ex)
            {
                return StopSafetyPortResult.Failure("液压/物理安全确认失败: " + ex.Message);
            }
        }

        private async Task AwaitStopEvidenceWithProgressAsync(Task task,
            StopSafetyProductionState state, string predicate, CancellationToken token)
        {
            while (!task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (await Task.WhenAny(task, Task.Delay(1000, token)).ConfigureAwait(false) == task) break;
                token.ThrowIfCancellationRequested();
                _log?.Warn($"StopSafetyPending Transaction={state.TransactionId:N} " +
                    $"Run={state.RunId:N}/{state.RunEpoch} Predicate={predicate} " +
                    $"Power={state.PowerTask?.Status} Pressure={state.PressureTask?.Status} " +
                    $"DoPending=[{string.Join(",", state.OffCompletions.Where(p => !p.Value.Task.IsCompleted).Select(p => p.Key))}] " +
                    $"Dev1={_acq?.GetDaqFreshnessSnapshot("Dev1").RejectionReason} " +
                    $"Dev2={_acq?.GetDaqFreshnessSnapshot("Dev2").RejectionReason}", "EPB-Safety");
            }
            await task.ConfigureAwait(false);
        }

        private StopSafetyPortResult ExecuteStopAcquisitionStage(
            StopSafetyProductionState state)
        {
            try
            {
                if (ShouldStopAcquisitionBeforeFinalPersistence(state.Context.Source))
                    _acq.Stop();
                foreach (var device in new[] { "Dev1", "Dev2" })
                {
                    var gap = _acq.TryGetDataContinuityGap(
                        device,
                        out var firstGap,
                        out var lastObserved);
                    var accepted = _acq.GetLastProcessRecycleBoundary(device);
                    var persistence = _persistence.GetSnapshot(device);
                    var existing = persistence.SuppressAfterSequence;
                    var finalBoundary = persistence.SuppressThroughSequence != 0
                        ? Math.Min(accepted, existing)
                        : accepted;
                    state.PersistenceBoundaries[device] = finalBoundary;
                    state.FinalPersistenceBoundaries[device] = accepted;
                    if (gap)
                    {
                        state.DataGaps.Add(
                            $"{device}:FirstGap={firstGap},LastObserved={lastObserved}");
                        _persistence.SuppressAfter(
                            device,
                            state.StartedUtc,
                            finalBoundary,
                            state.CorrelationId,
                            state.RunId,
                            state.RunEpoch);
                    }
                }
                return StopSafetyPortResult.Success(
                    "DAQ已停止并冻结最终接纳边界。",
                    true,
                    "StopAcquisition",
                    Math.Max(1, state.PersistenceBoundaries.Values.DefaultIfEmpty(0).Max()));
            }
            catch (Exception ex)
            {
                return StopSafetyPortResult.Failure("DAQ停止/边界冻结失败: " + ex.Message);
            }
        }

        private async Task<StopSafetyPortResult> ExecuteStopPersistenceStageAsync(
            StopSafetyProductionState state)
        {
            try
            {
                var flush = Volatile.Read(ref _pausePersistenceFlush);
                if (flush == null)
                    state.RawStorageFlushed = false;
                else
                {
                    try
                    {
                        await flush(state.PersistenceBoundaries, CancellationToken.None)
                            .ConfigureAwait(false);
                        state.RawStorageFlushed = true;
                    }
                    catch (Exception ex)
                    {
                        state.RawStorageFlushed = false;
                        state.PersistenceErrors.Add("RawStorage:" + ex.Message);
                    }
                }
                var boundaries = await WaitForStopPersistenceBoundariesAsync(
                        state.PersistenceBoundaries,
                        RequiresRecoveredPersistenceStateForStop(state.Context.Source),
                        state.FinalPersistenceBoundaries)
                    .ConfigureAwait(false);
                foreach (var boundary in boundaries.Where(item => item.Closed))
                {
                    var evidence = Math.Max(boundary.Boundary, boundary.Persisted);
                    if (evidence > 0)
                        RecordStopSafetyMaterialProgress(
                            state.Generation,
                            "PersistenceBoundary:" + boundary.Device,
                            evidence,
                            "持久化边界已推进 " + boundary.Device);
                }
                state.PersistenceBoundaryConfirmed = state.RawStorageFlushed &&
                    boundaries.Length == state.PersistenceBoundaries.Count &&
                    boundaries.All(item => item.Closed);
                if (state.PersistenceBoundaryConfirmed)
                {
                    var sealedAtBoundary = TrySealSoftwareRecoveryCycleWindows(
                        state.StopCycles,
                        state.StartedUtc,
                        "StopSafetyTransaction");
                    // A Pause/Cancel identity mismatch is sticky for this
                    // transaction.  A later seal attempt may add evidence but
                    // must not turn an untrusted cycle into a restartable one.
                    state.CyclesSealed = state.CyclesSealed && sealedAtBoundary;
                    if (state.CyclesSealed)
                        state.CyclesSealed = TryFinalizeStoppedCyclesAfterDurableBoundary(
                            state.StopCycles,
                            state.StartedUtc,
                            "canceled");
                    if (state.CyclesSealed)
                        state.CyclesSealed = FlushRecentStopEvidence(state);
                }
                if (!state.PersistenceBoundaryConfirmed || !state.CyclesSealed)
                    return StopSafetyPortResult.Failure(
                        string.Join(";", state.PersistenceErrors.Concat(new[]
                        {
                            state.PersistenceBoundaryConfirmed ? string.Empty : "PersistenceBoundaryUnconfirmed",
                            state.CyclesSealed ? string.Empty : "CycleWindowUnsealed"
                        }).Where(item => !string.IsNullOrWhiteSpace(item))));
                if (ShouldShutdownPersistenceForStop(
                        state.Context.Source,
                        state.PersistenceBoundaryConfirmed))
                    await _persistence.ShutdownAsync(int.MaxValue)
                        .ConfigureAwait(false);
                return StopSafetyPortResult.Success(
                    "Raw、SQLite与圈证据边界已闭合。",
                    true,
                    "StopPersistenceBoundary",
                    Math.Max(1, state.PersistenceBoundaries.Values.DefaultIfEmpty(0).Max()));
            }
            catch (Exception ex)
            {
                return StopSafetyPortResult.Failure("持久化边界关闭失败: " + ex.Message);
            }
        }

        private StopSafetyPortResult ExecuteStopVerificationStage(
            StopSafetyProductionState state)
        {
            state.Logical = CaptureLogicalQuiescenceSnapshotForStop(state.Context);
            var powerDisposition = GetStopPowerDisposition(
                state.Generation,
                state.Power.ok);
            var result = new StopSafetyResult
            {
                Source = state.Context.Source,
                CorrelationId = state.Context.CorrelationId ?? state.Correlation,
                SafetyTransactionId = state.TransactionId,
                RunId = state.RunId,
                RunEpoch = state.RunEpoch,
                SafetyBoundaryGeneration = state.Generation,
                MotorOffCommandSucceeded = state.MotorOk,
                PowerOffConfirmed = powerDisposition == PowerShutdownDisposition.ConfirmedOff,
                PowerDisposition = powerDisposition,
                PressureSafeConfirmed = state.Pressure.ok,
                PersistenceBoundaryConfirmed = state.PersistenceBoundaryConfirmed,
                RawStorageFlushed = state.RawStorageFlushed,
                DataContinuityCompromised = state.DataGaps.Count != 0,
                DataContinuityError = string.Join(";", state.DataGaps),
                StartedUtc = state.StartedUtc,
                CompletedUtc = DateTime.UtcNow,
                MotorError = string.Join(";", state.OffErrors),
                PowerError = state.Power.error,
                PressureError = state.Pressure.error,
                PersistenceError = string.Join(";", state.PersistenceErrors),
                LogicalQuiescenceConfirmed = state.Logical.IsQuiescent,
                LogicalError = state.Logical.IsQuiescent ? string.Empty : state.Logical.ToString(),
                LogicalState = state.Logical
            };
            result.Outcome = result.CanRestartInProcess
                ? StopSafetyOutcome.CompletedSafe
                : result.PhysicalSafetyConfirmed
                    ? StopSafetyOutcome.SafeButRestartRequired
                    : StopSafetyOutcome.PhysicalSafetyUnconfirmed;
            result.RequiresProcessRestart = !result.CanRestartInProcess;
            result.LogicalCleanupPending = result.FullyConfirmed &&
                !result.DataContinuityCompromised && !result.LogicalQuiescenceConfirmed;
            state.Result = result;
            ReportStopCleanupStatusAtSafetyBoundary(state, result);
            if (result.RequiresProcessRestart && !result.LogicalCleanupPending)
                Interlocked.Exchange(ref _processRestartRequired, 1);
            lock (_stopSafetyGate)
            {
                if (state.Generation == Interlocked.Read(ref _stopSafetyGeneration))
                    _lastStopSafetyResult = result.Clone();
            }
            EndPowerSupplyTelemetryRecording();
            FlushPersistentLog(true);
            return new StopSafetyPortResult
            {
                Succeeded = true,
                Result = result,
                // These are nullable edge observations.  A failed/partial
                // verification cannot erase a previously published one-way
                // command edge; only a positive physical observation is
                // emitted here.
                PhysicalOffSubmitted = state.OffCompletions.Count > 0 ||
                                       state.OffFallbackStages.Count > 0 ||
                                       state.OffFallbackTasks.Count > 0
                    ? true
                    : (bool?)null,
                PowerDisableStarted = state.PowerTask != null ? true : (bool?)null,
                PhysicalSafe = result.PhysicalSafetyConfirmed ? true : (bool?)null,
                Detail = "停止安全事务已完成逻辑清场验证"
            };
        }

        private void ReportStopCleanupStatusAtSafetyBoundary(
            StopSafetyProductionState state,
            StopSafetyResult result)
        {
            var abnormal = state.CleanupItems
                .Where(item =>
                    Volatile.Read(ref item.Failed) != 0 ||
                    item.ExecutionTask != null && !item.ExecutionTask.IsCompleted)
                .ToArray();
            if (abnormal.Length == 0) return;
            var detail = string.Join(
                ",",
                abnormal.Select(item =>
                    $"Channel={item.Channel}/Operation={item.Operation}/" +
                    (Volatile.Read(ref item.Failed) != 0
                        ? "Failed=" + item.Failure
                        : "Pending")));
            if (result.PhysicalSafetyConfirmed &&
                result.PersistenceBoundaryConfirmed)
            {
                _log?.Warn(
                    $"设备已安全，后台收口异常；不要求重启。" +
                    $"Transaction={state.TransactionId:N};{detail}",
                    "停止事务");
                return;
            }
            _log?.Error(
                $"后台收口异常且物理或持久化事实未完全证明，保持原有接管策略。" +
                $"Transaction={state.TransactionId:N};{detail}",
                "停止事务");
        }

        private void StartStopOffFallbacks(StopSafetyProductionState state)
        {
            foreach (var fallback in state.OffFallbackStages)
            {
                try
                {
                    try { StopSafetyFallbackIssued?.Invoke(fallback.Key); }
                    catch { /* diagnostic observers cannot affect safety */ }
                    var task = Task.Run(() => TryExecuteImmediateOffFallback(
                        fallback.Key,
                        fallback.Value,
                        out _));
                    state.OffFallbackTasks[fallback.Key] = task;
                    ObserveBackgroundTask(task, "StopSafetyImmediateOffFallback", fallback.Key);
                }
                catch (Exception ex)
                {
                    state.OffErrors.Add($"EPB{fallback.Key}:OFF兜底调度:{ex.Message}");
                }
            }
        }

        private void CancelStopRecoverableRestartJobs(
            StopSafetyProductionState state)
        {
            foreach (var recovery in _recoverableChannelRestartJobs.Values)
            {
                try { recovery.Cancel(); }
                catch (Exception ex) { state.Errors.Add("RestartJob:" + ex.Message); }
            }
        }

        private static string GetImmediatePowerDisableFailure(
            Task<(bool ok, string error)> powerTask)
        {
            if (powerTask == null || !powerTask.IsCompleted)
                return string.Empty;
            if (powerTask.IsCanceled)
                return "PowerDisableCanceled";
            if (powerTask.IsFaulted)
                return powerTask.Exception?.GetBaseException().Message ??
                       "PowerDisableFailed";
            return powerTask.Result.ok
                ? string.Empty
                : (powerTask.Result.error ?? "PowerDisableFailed");
        }

        private void PublishStopTerminalStates(StopSafetyProductionState state)
        {
            foreach (var channel in state.Channels)
            {
                try
                {
                    var stoppedState = state.Context.Source == StopSource.ManualUi ||
                                       state.Context.Source == StopSource.ApplicationClosing ||
                                       state.Context.Source == StopSource.ProgramExit ||
                                       state.Context.Source == StopSource.UnknownLegacy
                        ? ChannelRuntimeState.ManualStopped
                        : state.Context.Source == StopSource.AlarmInterlock
                            ? ChannelRuntimeState.InterlockStopped
                            : ChannelRuntimeState.SystemFault;
                    PublishChannelRuntimeState(
                        channel,
                        stoppedState,
                        "StopAll",
                        state.Context.Reason ?? "停止全部",
                        affectedChannels: state.Channels,
                        correlationId: state.CorrelationId,
                        allowTerminalReset: state.Context.Source == StopSource.ManualUi,
                        runIdOverride: state.RunId,
                        runEpochOverride: state.RunEpoch);
                }
                catch (Exception ex)
                {
                    state.Errors.Add($"EPB{channel}:停止终态发布:{ex.Message}");
                }
            }
        }

        private bool FlushRecentStopEvidence(StopSafetyProductionState state)
        {
            var channels = state.Channels
                .Concat(state.StopCycles.Keys)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (channels.Length == 0) return true;
            try
            {
                var recorder = Recorder;
                if (recorder == null) return false;
                foreach (var channel in channels)
                {
                    state.StopCycles.TryGetValue(channel, out var cycle);
                    if (recorder is DataOperation.IStopRecentCycleEvidenceExporter exporter)
                        exporter.FlushRecentForStop(channel, 10, cycle);
                    else
                        recorder.FlushRecent(channel, 10);
                }
                return true;
            }
            catch (Exception ex)
            {
                state.PersistenceErrors.Add("RecentCycles:" + ex.Message);
                return false;
            }
        }

        private async Task AwaitStopOffEvidenceAsync(
            StopSafetyProductionState state)
        {
            if (state.OffCompletions.Count != 0)
            {
                var completed = await Task.WhenAll(
                        state.OffCompletions.Values.Select(item => item.Task))
                    .ConfigureAwait(false);
                foreach (var telemetry in completed)
                    if (telemetry?.Result != true)
                        state.OffErrors.Add($"EPB{telemetry?.Channel ?? 0}:DO物理写失败");
            }
            if (state.OffFallbackTasks.Count != 0)
            {
                var pairs = state.OffFallbackTasks.ToArray();
                var completed = await Task.WhenAll(pairs.Select(pair => pair.Value))
                    .ConfigureAwait(false);
                for (var index = 0; index < completed.Length; index++)
                    if (!completed[index])
                        state.OffErrors.Add($"EPB{pairs[index].Key}:OFF兜底失败");
            }
        }

        private sealed class StopSafetyCleanupWorkItem
        {
            internal int Channel { get; set; }
            internal string Operation { get; set; } = string.Empty;
            internal Action Execute { get; set; }
            internal Task ExecutionTask { get; set; }
            internal int Failed;
            internal string Failure { get; set; } = string.Empty;

            internal static StopSafetyCleanupWorkItem ForCancellationSource(
                int channel,
                string operation,
                CancellationTokenSource source)
            {
                return new StopSafetyCleanupWorkItem
                {
                    Channel = channel,
                    Operation = operation ?? "CancellationSource.CancelDispose",
                    Execute = () =>
                    {
                        try { source?.Cancel(); }
                        finally { source?.Dispose(); }
                    }
                };
            }
        }

        private sealed class StopSafetyProductionState
        {
            internal StopSafetyProductionState(
                StopSafetyTransactionContext transaction,
                int[] channels,
                Dictionary<int, int> stopCycles)
            {
                Transaction = transaction;
                Context = transaction.StopContext;
                TransactionId = transaction.TransactionId;
                Generation = transaction.Generation;
                RunId = transaction.RunId;
                RunEpoch = transaction.RunEpoch;
                StartedUtc = transaction.StartedUtc;
                Correlation = Context.CorrelationId ?? transaction.TransactionId.ToString("N");
                CorrelationId = Guid.TryParse(Correlation, out var parsedCorrelation)
                    ? parsedCorrelation
                    : transaction.TransactionId;
                Channels = channels ?? Array.Empty<int>();
                StopCycles = stopCycles ?? new Dictionary<int, int>();
            }

            internal StopSafetyTransactionContext Transaction { get; }
            internal StopContext Context { get; }
            internal Guid TransactionId { get; }
            internal long Generation { get; }
            internal Guid RunId { get; }
            internal long RunEpoch { get; }
            internal DateTime StartedUtc { get; }
            internal string Correlation { get; }
            internal Guid CorrelationId { get; }
            internal int[] Channels { get; }
            internal Dictionary<int, int> StopCycles { get; }
            internal Dictionary<int, TaskCompletionSource<HighPriorityDoTelemetry>> OffCompletions { get; } =
                new Dictionary<int, TaskCompletionSource<HighPriorityDoTelemetry>>();
            internal Dictionary<int, string> OffFallbackStages { get; } =
                new Dictionary<int, string>();
            internal Dictionary<int, Task<bool>> OffFallbackTasks { get; } =
                new Dictionary<int, Task<bool>>();
            internal Dictionary<string, long> PersistenceBoundaries { get; } =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            internal Dictionary<string, long> FinalPersistenceBoundaries { get; } =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            internal object PersistenceGate { get; } = new object();
            internal Task<StopSafetyPortResult> PersistenceTask { get; set; }
            internal Task<StopSafetyResult> ClosePersistenceTask { get; set; }
            internal List<string> Errors { get; } = new List<string>();
            internal List<string> OffErrors { get; } = new List<string>();
            internal List<string> DataGaps { get; } = new List<string>();
            internal List<string> PersistenceErrors { get; } = new List<string>();
            internal List<string> FreezeErrors { get; } = new List<string>();
            internal List<StopSafetyCleanupWorkItem> CleanupItems { get; } =
                new List<StopSafetyCleanupWorkItem>();
            internal CancellationTokenSource DetachedBatchSessionCts { get; set; }
            internal int CleanupScheduled;
            internal Task<(bool ok, string error)> PowerTask { get; set; }
            internal Task<(bool ok, string error)> PressureTask { get; set; }
            internal (bool ok, string error) Power { get; set; } = (true, string.Empty);
            internal (bool ok, string error) Pressure { get; set; } = (true, string.Empty);
            internal bool MotorOk { get; set; } = true;
            internal bool RawStorageFlushed { get; set; } = true;
            internal bool PersistenceBoundaryConfirmed { get; set; }
            internal bool CyclesSealed { get; set; } = true;
            internal bool AuthorizationRevoked { get; set; }
            internal bool OffSubmissionStarted { get; set; }
            internal bool RuntimeProducersFrozen { get; set; }
            internal bool RuntimeObjectsFrozen { get; set; }
            internal int ForceAbortedHydraulicObjects { get; set; }
            internal LogicalQuiescenceSnapshot Logical { get; set; } =
                new LogicalQuiescenceSnapshot();
            internal StopSafetyResult Result { get; set; }
        }

        /// <summary>
        /// Runner snapshots are accepted only for the current generation and
        /// never regress an already-published real hardware stage.  This lets
        /// the legacy algorithm remain the source of physical transitions
        /// while the runner owns terminal identity/timeout fields.
        /// </summary>
        private void PublishStopSafetyRunnerProgress(StopSafetyProgressSnapshot candidate)
        {
            if (candidate == null) return;
            StopSafetyProgressSnapshot published = null;
            lock (_stopProgressGate)
            {
                if (candidate.Generation != Interlocked.Read(ref _stopSafetyGeneration))
                    return;
                if (_stopSafetyProgress.Generation == candidate.Generation &&
                    _stopSafetyProgress.Stage != StopSafetyStage.None &&
                    (int)candidate.Stage < (int)_stopSafetyProgress.Stage)
                    return;
                if (_stopSafetyProgress.Generation == candidate.Generation &&
                    _stopSafetyProgress.Stage != StopSafetyStage.None)
                {
                    // Preserve physical evidence from the real core when the
                    // runner emits its post-core observation barriers.
                    candidate.PhysicalOffSubmitted |= _stopSafetyProgress.PhysicalOffSubmitted;
                    candidate.PowerDisableStarted |= _stopSafetyProgress.PowerDisableStarted;
                    candidate.PhysicalSafe |= _stopSafetyProgress.PhysicalSafe;
                }
                _stopSafetyProgress = candidate.Clone();
                published = _stopSafetyProgress.Clone();
            }
            _recoveryAggregateStore.PublishStopSource(published);
            lock (_recoveryContractGate)
                PublishRecoveryOperationalSourcesLocked();
            try
            {
                StopSafetyProgressObserved?.Invoke(published.Clone());
            }
            catch
            {
                // Progress observers are diagnostic only; they can never
                // delay or change the production stop transaction.
            }
        }

        /// <summary>
        /// Emergency action used by the runner's deadline path.  It is
        /// intentionally synchronous and idempotent: caller cancellation and
        /// late asynchronous cores cannot revoke the one safe-idle command.
        /// </summary>
        internal void EnterStopSafetySafeIdle(
            StopSafetyTransactionContext transaction,
            string reason)
        {
            Volatile.Write(ref _energizationRevoked, 1);
            Interlocked.Exchange(ref _processRestartRequired, 1);
            foreach (var channel in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
            {
                try { TrySubmitStopSafetyPhysicalOff(channel, null, out _); }
                catch { }
            }
            try
            {
                if (_powerSupply != null)
                    ObserveBackgroundTask(
                        Task.Run(() => _powerSupply.DisableAllForSafetyAsync(
                            "StopSafetyRunnerSafeIdle:" + (reason ?? string.Empty),
                            CancellationToken.None)),
                        "StopSafetyRunnerSafeIdle");
            }
            catch { }
        }

        /// <summary>
        /// Stable forwarding point for material evidence.  The legacy detail
        /// overload is deliberately not used by the runner.
        /// </summary>
        internal bool RecordStopSafetyMaterialFromRunner(
            long generation,
            string source,
            long evidenceVersion,
            string detail)
        {
            return RecordStopSafetyMaterialProgress(
                generation,
                source,
                evidenceVersion,
                detail);
        }
    }
}
