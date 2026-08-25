using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;

namespace Controller
{
    /// <summary>
    /// A stop material event is accepted only when its source supplies a
    /// positive monotonic sequence/boundary.  The same source/version is
    /// deliberately idempotent: changing the prose detail cannot renew the
    /// stop watchdog grace window.
    /// </summary>
    internal sealed class StopSafetyMaterialEvidenceGate
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _lastVersionBySource =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public bool TryAccept(string source, long version)
        {
            if (string.IsNullOrWhiteSpace(source) || version <= 0) return false;
            lock (_gate)
            {
                if (_lastVersionBySource.TryGetValue(source, out var previous) &&
                    version <= previous)
                    return false;
                _lastVersionBySource[source] = version;
                return true;
            }
        }

        public void Reset()
        {
            lock (_gate) _lastVersionBySource.Clear();
        }
    }

    public partial class EpbManager
    {
        // StopAll has one process-wide escape deadline, but every stage exposes its
        // own physical/safety deadline to the sidecar.  In particular, hydraulic
        // release and persistence are allowed to exceed the old five-second
        // watchdog heuristic without weakening the immediate OFF stages.
        internal const int StopAllHardDeadlineMs = 45000;
        private const int StopImmediateStageDeadlineMs = 2000;
        private const int StopShortStageDeadlineMs = 5000;
        private const int StopReleaseHydraulicsStageDeadlineMs = 15000;
        private const int StopPersistenceStageDeadlineMs = 15000;
        internal const int StopStageNoProgressGraceMs = 5000;

        private readonly object _stopProgressGate = new object();
        private readonly StopSafetyMaterialEvidenceGate _stopMaterialEvidenceGate =
            new StopSafetyMaterialEvidenceGate();
        private StopSafetyProgressSnapshot _stopSafetyProgress = new StopSafetyProgressSnapshot();
        // Compatibility mirror only.  All live progress clocks and material
        // evidence are owned by the active transaction runner.
        private StopSafetyTransactionRunner _activeStopSafetyRunner;
        // The production path uses the system clock and the existing NI/
        // hydraulic adapters.  These fields are replaceable only before the
        // first StopAll transaction so deterministic acceptance tests can
        // drive the same manager entry point without duplicating the runner.
        private IStopSafetyClock _stopSafetyClock = new SystemStopSafetyClock();
        private IStopSafetyHydraulicAdapter _stopSafetyHydraulicAdapter;
        private long _stopSafetyGeneration;
        private int _energizationRevoked;
        private int _processRestartRequired;

        /// <summary>
        /// Real runner progress observation boundary.  The event is raised
        /// after the immutable snapshot has been published; observers must
        /// remain non-blocking and cannot alter the stop decision.
        /// </summary>
        internal event Action<StopSafetyProgressSnapshot> StopSafetyProgressObserved;

        /// <summary>
        /// Diagnostic edge emitted immediately before a rejected-channel
        /// fallback is scheduled. It is observation-only and does not own the
        /// physical action or alter the runner decision.
        /// </summary>
        internal event Action<int> StopSafetyFallbackIssued;

        // Explicit production-facing names used by acceptance observers.
        internal event Action<StopSafetyProgressSnapshot> StopSafetyProgressPublished
        {
            add { StopSafetyProgressObserved += value; }
            remove { StopSafetyProgressObserved -= value; }
        }

        internal int SafeIdleIssueCount =>
            Volatile.Read(ref _activeStopSafetyRunner)?.SafeIdleIssueCount ?? 0;

        internal bool HasOrphanCore =>
            Volatile.Read(ref _activeStopSafetyRunner)?.HasOrphanCore == true;

        internal int StopSafetySafeIdleIssueCount => SafeIdleIssueCount;

        internal bool StopSafetyHasOrphanCore => HasOrphanCore;

        public bool RequiresProcessRestart => Volatile.Read(ref _processRestartRequired) != 0;
        internal bool IsEnergizationRevoked => Volatile.Read(ref _energizationRevoked) != 0;

        /// <summary>
        /// 仅供全新 Watchdog 恢复进程使用：硬件暂不可用期间本进程从未获得过上电授权；
        /// 当一次新的完整 StopAll 预检已证明物理安全、持久化和逻辑清场全部成立时，
        /// 可清除先前失败探测留下的进程重启锁存。旧运行进程不得调用此入口。
        /// </summary>
        public bool TryClearRecoveryProcessRestartLatchAfterVerifiedPreflight(
            StopSafetyResult safety,
            out string rejectionReason)
        {
            if (safety == null || !safety.CanRestartInProcess || safety.TimedOut)
            {
                rejectionReason = "最新安全预检尚未完整确认，不能清除恢复进程锁存。";
                return false;
            }
            lock (_stopSafetyGate)
            {
                if (_stopSafetyTask != null && !_stopSafetyTask.IsCompleted)
                {
                    rejectionReason = "仍有在途 StopAll，不能清除恢复进程锁存。";
                    return false;
                }
                if (_lastStopSafetyResult == null ||
                    !string.Equals(
                        _lastStopSafetyResult.CorrelationId,
                        safety.CorrelationId,
                        StringComparison.Ordinal) ||
                    !_lastStopSafetyResult.CanRestartInProcess)
                {
                    rejectionReason = "最新 StopAll 身份或结果不匹配，不能清除恢复进程锁存。";
                    return false;
                }
                if (!CaptureLogicalQuiescenceSnapshot().IsQuiescent)
                {
                    rejectionReason = "控制对象尚未完全清场，不能清除恢复进程锁存。";
                    return false;
                }
                Interlocked.Exchange(ref _processRestartRequired, 0);
                _recoveryAggregateStore.ClearStopSafetyStickyTerminal(
                    Math.Max(1, Interlocked.Read(ref _stopSafetyGeneration) + 1));
            }
            rejectionReason = string.Empty;
            return true;
        }

        public void RevokeExecutionForExternalRecovery(string reason)
        {
            Interlocked.Exchange(ref _processRestartRequired, 1);
            Volatile.Write(ref _energizationRevoked, 1);
            Interlocked.Increment(ref _runEpoch);
            foreach (var channel in Enumerable.Range(1, 12))
            {
                try { TrySubmitEpbOffHighPriority(channel, null, out _); }
                catch { }
            }
            if (_powerSupply != null)
                ObserveBackgroundTask(
                    Task.Run(() => _powerSupply.DisableAllForSafetyAsync(
                        "ExternalRecoveryRequired:" + (reason ?? string.Empty),
                        CancellationToken.None)),
                    "ExternalRecoveryEmergencyPowerOff");
            _log.Error(
                "已到达外部恢复边界，当前进程永久撤销上电授权。Reason=" +
                (reason ?? string.Empty),
                "EPB");
        }

        public StopSafetyProgressSnapshot CaptureStopSafetyProgress()
        {
            var runner = Volatile.Read(ref _activeStopSafetyRunner);
            if (runner != null)
                return runner.CaptureProgress();
            lock (_stopProgressGate) return _stopSafetyProgress.Clone();
        }

        private void ThrowIfProcessRestartRequired()
        {
            if (RequiresProcessRestart)
                throw new InvalidOperationException(
                    "上一次停止事务未完整清场，当前进程已永久撤销上电授权；必须由 Watchdog 重启软件。");
        }

    private void AuthorizeFreshRunAfterSafetyPreflight()
    {
        ThrowIfProcessRestartRequired();
        Volatile.Write(ref _energizationRevoked, 0);
        _recoveryAggregateStore.ClearStopSafetyStickyTerminal(
            Math.Max(1, Interlocked.Read(ref _stopSafetyGeneration) + 1));
    }

        private void AdvanceStopSafetyProgress(
            long generation,
            StopSafetyStage stage,
            string detail,
            bool? active = null,
            bool? offSubmitted = null,
            bool? powerStarted = null,
            bool? physicalSafe = null)
        {
            var runner = Volatile.Read(ref _activeStopSafetyRunner);
            if (runner == null || generation != Interlocked.Read(ref _stopSafetyGeneration))
                return;
            runner.TryAdvanceProgress(
                stage,
                detail,
                active,
                offSubmitted,
                powerStarted,
                physicalSafe);
        }

        /// <summary>
        /// Records a real pressure/DAQ/persistence event without changing the
        /// current stage's start time or hard deadline.  A heartbeat/diagnostic
        /// refresh must never count as material progress.
        /// </summary>
        private void RecordStopSafetyMaterialProgress(long generation, string detail)
        {
            // Legacy/detail-only callers are intentionally inert.  A material
            // event must go through the overload carrying an auditable source
            // and a monotonic sequence/boundary.
        }

        private bool RecordStopSafetyMaterialProgress(
            long generation,
            string source,
            long evidenceVersion,
            string detail)
        {
            var runner = Volatile.Read(ref _activeStopSafetyRunner);
            return runner != null &&
                   generation == Interlocked.Read(ref _stopSafetyGeneration) &&
                   runner.TryRecordMaterialProgress(source, evidenceVersion, detail);
        }

        private static int SelectStopStageDeadlineMs(StopSafetyStage stage)
        {
            switch (stage)
            {
                case StopSafetyStage.FreezeActiveWork:
                case StopSafetyStage.RevokeExecutionAuthorization:
                case StopSafetyStage.SubmitPhysicalOff:
                    return StopImmediateStageDeadlineMs;
                case StopSafetyStage.StartPowerDisable:
                case StopSafetyStage.ClearTimerAndRunner:
                case StopSafetyStage.ClearRecoveryOwners:
                case StopSafetyStage.StopAcquisition:
                case StopSafetyStage.VerifyLogicalQuiescence:
                    return StopShortStageDeadlineMs;
                case StopSafetyStage.ReleaseHydraulics:
                    // 现场完整释压约 10.4s；15s 是该阶段独立截止，不是
                    // 全流程的统一等待时间。
                    return StopReleaseHydraulicsStageDeadlineMs;
                case StopSafetyStage.ClosePersistenceBoundary:
                    return StopPersistenceStageDeadlineMs;
                default:
                    return StopImmediateStageDeadlineMs;
            }
        }

        private static int SelectStopStageNoProgressGraceMs(StopSafetyStage stage)
        {
            return stage == StopSafetyStage.None
                ? 0
                : StopStageNoProgressGraceMs;
        }

        // Legacy outer implementation retained only as a diagnostic fallback
        // while the production entry point is owned by
        // StopSafetyTransactionRunner (see EpbManager.StopSafetyOperations).
        private async Task<StopSafetyResult> RunBoundedStopSafetyLegacyAsync(
            StopContext context,
            CancellationToken callerToken,
            long generation)
        {
            var transactionId = Guid.NewGuid();
            var startedUtc = DateTime.UtcNow;
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            lock (_stopProgressGate)
            {
                var previousProgressVersion = _stopSafetyProgress?.ProgressVersion ?? 0;
                _stopMaterialEvidenceGate.Reset();
                _stopSafetyProgress = new StopSafetyProgressSnapshot
                {
                    TransactionId = transactionId,
                    RunId = runId,
                    RunEpoch = runEpoch,
                    Generation = generation,
                    // Keep the Controller-owned version monotonic across repeated
                    // StopAll transactions in the same process; the Watchdog still
                    // uses the transaction identity/stage fields to scope evidence.
                    ProgressVersion = Math.Max(1, previousProgressVersion + 1),
                    Stage = StopSafetyStage.FreezeActiveWork,
                    StartedUtc = startedUtc,
                    StageStartedUtc = startedUtc,
                    HardDeadlineUtc = startedUtc.AddMilliseconds(StopAllHardDeadlineMs),
                    StageHardDeadlineUtc = startedUtc.AddMilliseconds(
                        SelectStopStageDeadlineMs(StopSafetyStage.FreezeActiveWork)),
                    StageNoProgressGraceMs = SelectStopStageNoProgressGraceMs(
                        StopSafetyStage.FreezeActiveWork),
                    LastMaterialProgressUtc = startedUtc,
                    Active = true,
                    Detail = "已冻结停止事务身份与活动集合"
                };
            }
            _recoveryAggregateStore.PublishStopSource(CaptureStopSafetyProgress());
            _recoveryAggregateStore.PublishLogicalSource(CaptureLogicalQuiescenceSnapshot());
            lock (_recoveryContractGate)
                PublishRecoveryOperationalSourcesLocked();

            // 安全方向命令使用独立 owner，不继承 UI 取消令牌。先装栅栏，再启动任何
            // 可能受 Timer、恢复 owner、日志或持久化锁影响的清场工作。
            Volatile.Write(ref _energizationRevoked, 1);
            foreach (var channel in Enumerable.Range(1, 12))
                RevokeChannelExecutionPermit(channel, "StopAll");
            Interlocked.Increment(ref _runEpoch);
            // 在启动通用清场前先撤销旧液压代次。该操作不等待缺员屏障，防止一个
            // 永不到达的成员不能占满整个 StopAll 45 秒总预算；压力安全仍在后续独立确认。
            var forceAbortedHydraulicObjects = _hydCoordinator?.ForceAbortRun(
                runId,
                $"StopAll:{context.Source}:{transactionId:N}") ?? 0;
            foreach (var pair in _hydraulicLeaseByChannel.ToArray())
            {
                if (pair.Value?.Key?.TestRunId != runId) continue;
                pair.Value.ForceCloseWithoutWait();
                ((ICollection<KeyValuePair<int, HydraulicChannelLeaseScope>>)_hydraulicLeaseByChannel)
                    .Remove(pair);
            }
            foreach (var channel in _hydraulicParticipants.Keys.ToArray())
                _hydraulicParticipants.TryRemove(channel, out _);
            AdvanceStopSafetyProgress(
                generation,
                StopSafetyStage.RevokeExecutionAuthorization,
                $"已撤销旧 RunEpoch、液压代次并安装禁止再上电栅栏；" +
                $"ForceAbortedHydraulicObjects={forceAbortedHydraulicObjects}");

            var offCompletions = new List<Task<HighPriorityDoTelemetry>>();
            foreach (var channel in Enumerable.Range(1, 12))
            {
                var completion = new TaskCompletionSource<HighPriorityDoTelemetry>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    if (TrySubmitEpbOffHighPriority(
                            channel,
                            telemetry => completion.TrySetResult(telemetry),
                            out _))
                        offCompletions.Add(completion.Task);
                    else
                        QueueElectricalGroupEmergencyShutdownFromDaqControl(
                            channel,
                            $"StopAll admission rejected Transaction={transactionId:N}");
                }
                catch
                {
                    try
                    {
                        QueueElectricalGroupEmergencyShutdownFromDaqControl(
                            channel,
                            $"StopAll submission failed Transaction={transactionId:N}");
                    }
                    catch { }
                }
            }
            AdvanceStopSafetyProgress(
                generation,
                StopSafetyStage.SubmitPhysicalOff,
                $"已提交全部高优先级 DO OFF，Accepted={offCompletions.Count}/12",
                offSubmitted: true);

            Task powerDisableTask;
            try
            {
                powerDisableTask = _powerSupply == null
                    ? Task.CompletedTask
                    : Task.Run(async () =>
                    {
                        var results = await _powerSupply.DisableAllForSafetyAsync(
                        $"StopAll emergency owner Transaction={transactionId:N}",
                        CancellationToken.None).ConfigureAwait(false);
                        if (results.Any(item => !item.ConfirmedOff))
                            throw new InvalidOperationException(string.Join("; ", results
                                .Where(item => !item.ConfirmedOff)
                                .Select(item => $"Group{item.ElectricalGroupId}:{item.Outcome}:{item.Error}")));
                    });
            }
            catch (Exception ex)
            {
                powerDisableTask = Task.FromException(ex);
            }
            AdvanceStopSafetyProgress(
                generation,
                StopSafetyStage.StartPowerDisable,
                "已并行启动全部电源组 Disable",
                powerStarted: true);

            // 物理 OFF 与总电源 Disable 均已提交，此处执行不可等待的软件撤权。
            // 正常 RunStopSafety 会再次幂等清理并闭合数据边界，但不再有 Timer、
            // Runner 或通道 CTS 能占用外层硬截止并推进新动作。
            var forceRevokedRuntimes = 0;
            foreach (var channel in Enumerable.Range(1, 12))
            {
                try
                {
                    if (_timers.TryGetValue(channel, out var timer))
                        timer.Pause("StopAllForceQuiesce");
                }
                catch { }
                try { CancelCyclePauseCts(channel); } catch { }
                try { CancelStopCts(channel); } catch { }
                try
                {
                    var existed = _timers.ContainsKey(channel) || _timerCache.ContainsKey(channel);
                    RemoveTimerRuntime(channel, "StopAllForceQuiesce");
                    if (existed) forceRevokedRuntimes++;
                }
                catch { }
                try
                {
                    var existed = _runners.ContainsKey(channel) || _runnerCache.ContainsKey(channel);
                    RemoveRunnerRuntime(channel, "StopAllForceQuiesce");
                    if (existed) forceRevokedRuntimes++;
                }
                catch { }
            }
            AdvanceStopSafetyProgress(
                generation,
                StopSafetyStage.ClearTimerAndRunner,
                $"ForceQuiesce已撤销Timer/Runner/CTS；Removed={forceRevokedRuntimes}");

            // StartNew 把同步前段也移出调用线程；无论它卡在何处，对外 owner 都由
            // WhenAny 的硬截止终态化，后续人工停止不会复用一个永不完成的旧任务。
            var coreTask = Task.Factory.StartNew(
                    () => RunStopSafetyLegacyCoreAsync(context, CancellationToken.None, generation),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();
            AdvanceStopSafetyProgress(
                generation,
                StopSafetyStage.ClearTimerAndRunner,
                "正在清理 Timer、Runner、恢复 owner、DAQ 与持久化边界");

            var deadline = Task.Delay(StopAllHardDeadlineMs, CancellationToken.None);
            var completed = await Task.WhenAny(coreTask, deadline).ConfigureAwait(false);
            if (completed == coreTask)
            {
                var result = await coreTask.ConfigureAwait(false);
                result.Outcome = result.CanRestartInProcess
                    ? StopSafetyOutcome.CompletedSafe
                    : result.PhysicalSafetyConfirmed
                        ? StopSafetyOutcome.SafeButRestartRequired
                        : StopSafetyOutcome.PhysicalSafetyUnconfirmed;
                result.RequiresProcessRestart = result.Outcome != StopSafetyOutcome.CompletedSafe;
                if (result.RequiresProcessRestart)
                    Interlocked.Exchange(ref _processRestartRequired, 1);
                result.LastStage = StopSafetyStage.Completed;
                result.PhysicalOffSubmitted = true;
                result.PowerDisableStarted = true;
                AdvanceStopSafetyProgress(
                    generation,
                    StopSafetyStage.Completed,
                    result.Outcome.ToString(),
                    active: false,
                    physicalSafe: result.PhysicalSafetyConfirmed);
                lock (_stopSafetyGate)
                {
                    if (generation == Interlocked.Read(ref _stopSafetyGeneration))
                        _lastStopSafetyResult = result.Clone();
                }
                return result;
            }

            Interlocked.Exchange(ref _processRestartRequired, 1);
            ObserveLateSafetyTask(coreTask, "StopAllCoreAfterHardDeadline", transactionId);
            AdvanceStopSafetyProgress(
                generation,
                StopSafetyStage.TimedOut,
                $"停止事务超过总逃逸期限 {StopAllHardDeadlineMs}ms；旧 owner 已隔离，等待 Watchdog 接管",
                active: false);

            // 超时后再次走独立幂等物理安全路径。这里只观察已经发出的命令，不等待
            // 旧 core；所有观察也有自己的截止。
            var offConfirmed = false;
            if (offCompletions.Count > 0)
            {
                var allOff = Task.WhenAll(offCompletions);
                var offDone = await Task.WhenAny(allOff, Task.Delay(1000)).ConfigureAwait(false);
                if (offDone != allOff)
                    ObserveLateSafetyTask(
                        allOff,
                        "StopAllPhysicalOffAfterObservationDeadline",
                        transactionId);
                offConfirmed = offDone == allOff && allOff.Status == TaskStatus.RanToCompletion &&
                               allOff.Result.All(item => item?.Result == true);
            }
            var powerDone = await Task.WhenAny(powerDisableTask, Task.Delay(1000)).ConfigureAwait(false);
            if (powerDone != powerDisableTask)
                ObserveLateSafetyTask(
                    powerDisableTask,
                    "StopAllPowerDisableAfterObservationDeadline",
                    transactionId);
            var powerConfirmed = powerDone == powerDisableTask &&
                                 powerDisableTask.Status == TaskStatus.RanToCompletion;
            try
            {
                foreach (var channel in Enumerable.Range(1, 12))
                    TrySubmitEpbOffHighPriority(channel, null, out _);
                if (_powerSupply != null)
                    ObserveBackgroundTask(
                        Task.Run(() => _powerSupply.DisableAllForSafetyAsync(
                            $"StopAll timeout independent retry Transaction={transactionId:N}",
                            CancellationToken.None)),
                        "StopAllTimeoutPowerDisableRetry");
            }
            catch { }

            var physicalSafe = offConfirmed && powerConfirmed;
            var logical = CaptureLogicalQuiescenceSnapshotForStop(context);
            var timeoutResult = new StopSafetyResult
            {
                Outcome = physicalSafe
                    ? StopSafetyOutcome.SafeButRestartRequired
                    : StopSafetyOutcome.PhysicalSafetyUnconfirmed,
                LastStage = StopSafetyStage.TimedOut,
                TimedOut = true,
                RequiresProcessRestart = true,
                PhysicalOffSubmitted = true,
                PowerDisableStarted = true,
                StageError = $"StopAll total escape deadline exceeded: {StopAllHardDeadlineMs}ms",
                Source = context.Source,
                CorrelationId = context.CorrelationId ?? transactionId.ToString("N"),
                RunId = runId,
                MotorOffCommandSucceeded = offConfirmed,
                PowerOffConfirmed = powerConfirmed,
                PressureSafeConfirmed = false,
                PersistenceBoundaryConfirmed = false,
                RawStorageFlushed = false,
                StartedUtc = startedUtc,
                CompletedUtc = DateTime.UtcNow,
                MotorError = offConfirmed ? string.Empty : "DO OFF completion unconfirmed before hard deadline",
                PowerError = powerConfirmed ? string.Empty : "Power OFF unconfirmed before hard deadline",
                PressureError = "StopAll timed out before pressure confirmation",
                PersistenceError = "StopAll timed out before durable boundary confirmation",
                LogicalQuiescenceConfirmed = logical.IsQuiescent,
                LogicalError = logical.IsQuiescent ? string.Empty : logical.ToString(),
                LogicalState = logical
            };
            lock (_stopSafetyGate)
            {
                if (generation == Interlocked.Read(ref _stopSafetyGeneration))
                    _lastStopSafetyResult = timeoutResult.Clone();
            }
            _log.Error(
                $"StopAll 超过硬截止，当前进程永久禁止重新开始。" +
                $" Transaction={transactionId:N}; RunId={runId:N}; RunEpoch={runEpoch}; " +
                $"Outcome={timeoutResult.Outcome}; Logical={logical}",
                "EPB");
            return timeoutResult;
        }

        private void ObserveLateSafetyTask(Task task, string operation, Guid transactionId)
        {
            if (task == null || task.IsCompleted) return;
            _taskSupervisor.Observe(task, operation, _activeBatchId);
            var observedUtc = DateTime.UtcNow;
            var logTask = task.ContinueWith(
                completed =>
                {
                    var status = completed.IsCanceled
                        ? "Canceled"
                        : completed.IsFaulted
                            ? "Faulted"
                            : "Completed";
                    var error = completed.Exception?.GetBaseException().Message ?? string.Empty;
                    _log.Warn(
                        $"硬截止后的迟到安全任务已终态化：Task={operation}; " +
                        $"Transaction={transactionId:N}; Status={status}; " +
                        $"LateMs={(DateTime.UtcNow - observedUtc).TotalMilliseconds:F0}; Error={error}",
                        "EPB-STOP");
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _taskSupervisor.Observe(logTask, operation + ".LateCompletionLog", _activeBatchId);
        }
    }
}
