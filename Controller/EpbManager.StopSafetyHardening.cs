using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;

namespace Controller
{
    public partial class EpbManager
    {
        internal const int StopAllHardDeadlineMs = 15000;

        private readonly object _stopProgressGate = new object();
        private StopSafetyProgressSnapshot _stopSafetyProgress = new StopSafetyProgressSnapshot();
        private long _stopSafetyGeneration;
        private int _energizationRevoked;
        private int _processRestartRequired;

        public bool RequiresProcessRestart => Volatile.Read(ref _processRestartRequired) != 0;

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
            if (generation != Interlocked.Read(ref _stopSafetyGeneration)) return;
            StopSafetyStage previousStage;
            DateTime previousStartedUtc;
            StopSafetyProgressSnapshot snapshot;
            lock (_stopProgressGate)
            {
                if (generation != Interlocked.Read(ref _stopSafetyGeneration)) return;
                previousStage = _stopSafetyProgress.Stage;
                previousStartedUtc = _stopSafetyProgress.StageStartedUtc;
                _stopSafetyProgress.Stage = stage;
                _stopSafetyProgress.StageStartedUtc = DateTime.UtcNow;
                _stopSafetyProgress.ProgressVersion++;
                _stopSafetyProgress.Detail = detail ?? string.Empty;
                if (active.HasValue) _stopSafetyProgress.Active = active.Value;
                if (offSubmitted.HasValue) _stopSafetyProgress.PhysicalOffSubmitted = offSubmitted.Value;
                if (powerStarted.HasValue) _stopSafetyProgress.PowerDisableStarted = powerStarted.Value;
                if (physicalSafe.HasValue) _stopSafetyProgress.PhysicalSafe = physicalSafe.Value;
                snapshot = _stopSafetyProgress.Clone();
            }
            // 阶段日志从电源 Disable 已经启动后才异步发布，绝不让日志观察者占用
            // DO OFF/电源关闭的前置安全预算。
            if (stage >= StopSafetyStage.StartPowerDisable)
                ObserveBackgroundTask(
                    Task.Run(() => _log.Info(
                        $"StopStage Exit={previousStage} " +
                        $"ElapsedMs={(DateTime.UtcNow - previousStartedUtc).TotalMilliseconds:F0}; " +
                        $"Enter={stage}; Transaction={snapshot.TransactionId:N}; " +
                        $"RunId={snapshot.RunId:N}; RunEpoch={snapshot.RunEpoch}; " +
                        $"ProgressVersion={snapshot.ProgressVersion}; Detail={snapshot.Detail}",
                        "EPB-STOP")),
                    "StopSafetyStageLog");
        }

        private async Task<StopSafetyResult> RunBoundedStopSafetyAsync(
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
                _stopSafetyProgress = new StopSafetyProgressSnapshot
                {
                    TransactionId = transactionId,
                    RunId = runId,
                    RunEpoch = runEpoch,
                    ProgressVersion = 1,
                    Stage = StopSafetyStage.FreezeActiveWork,
                    StartedUtc = startedUtc,
                    StageStartedUtc = startedUtc,
                    Active = true,
                    Detail = "已冻结停止事务身份与活动集合"
                };
            }

            // 安全方向命令使用独立 owner，不继承 UI 取消令牌。先装栅栏，再启动任何
            // 可能受 Timer、恢复 owner、日志或持久化锁影响的清场工作。
            Volatile.Write(ref _energizationRevoked, 1);
            Interlocked.Increment(ref _runEpoch);
            AdvanceStopSafetyProgress(
                generation,
                StopSafetyStage.RevokeExecutionAuthorization,
                "已撤销旧 RunEpoch，并安装禁止再上电栅栏");

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

            // StartNew 把同步前段也移出调用线程；无论它卡在何处，对外 owner 都由
            // WhenAny 的硬截止终态化，后续人工停止不会复用一个永不完成的旧任务。
            var coreTask = Task.Factory.StartNew(
                    () => RunStopSafetyAsync(context, CancellationToken.None, generation),
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
                $"停止事务超过 {StopAllHardDeadlineMs}ms；旧 owner 已隔离，等待 Watchdog 接管",
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
            var logical = CaptureLogicalQuiescenceSnapshot();
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
                StageError = $"StopAll hard deadline exceeded: {StopAllHardDeadlineMs}ms",
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
