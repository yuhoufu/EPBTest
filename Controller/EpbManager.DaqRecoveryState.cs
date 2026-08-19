using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;

namespace Controller
{
    public sealed partial class EpbManager
    {
        internal static bool ShouldAutoRecoverExternalEquipmentFault(FaultScope scope)
        {
            // 只有卡钳本体的通道级、已定义硬件故障允许保持锁存停机。
            // DAQ、液压、电源等外部设备即使硬件暂时离线，也应保持安全断电并持续探测，
            // 条件恢复后自动续测。
            return scope != FaultScope.Channel;
        }

        private bool IsCurrentRecovery(DaqAutoRecoveryContext context)
        {
            if (context == null || context.Cancellation.IsCancellationRequested) return false;
            if (context.RunEpoch != Interlocked.Read(ref _runEpoch)) return false;
            return _daqAutoRecovery.TryGetValue(context.Device, out var active) &&
                   ReferenceEquals(active, context) &&
                   context.Terminal.Current == DaqRecoveryTerminal.None;
        }

        private void CompleteCancelledRecovery(DaqAutoRecoveryContext context, string reason)
        {
            if (context == null) return;
            lock (_daqRecoveryCommitGate)
            {
                if (!context.Terminal.TryCommit(DaqRecoveryTerminal.Cancelled)) return;
                context.Phase.MarkTerminal();
                MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
                TryRemoveExactDaqRecoveryContext(context);
            }
            MarkDaqRecoveryBatchTerminal(context);
            ReleaseDaqRecoveryOwnerships(context);
            try { context.Cancellation.Cancel(); } catch { }
            var result = new DaqRecoveryResult
            {
                Device = context.Device ?? string.Empty,
                Recovered = false,
                PreviousGeneration = context.PreviousGeneration,
                RecoveredGeneration = context.RecoveredGeneration,
                FailureReason = reason?.IndexOf(
                                    "ManualUi",
                                    StringComparison.OrdinalIgnoreCase) >= 0
                    ? "人工停止，DAQ自动恢复已取消"
                    : reason ?? "RecoveryCancelled",
                FailureKind = reason?.IndexOf(
                                  "ManualUi",
                                  StringComparison.OrdinalIgnoreCase) >= 0
                    ? "ManualCancelled"
                    : "Cancelled",
                Classification = FaultClassification.SoftwareTransient
            };
            context.Completion.TrySetResult(result);
            LogDaqRecoveryFieldMetric(context, "Cancelled", reason);
            NonCriticalObserver.Invoke(
                DaqRecoveryStateChanged,
                result,
                ex => _log?.Warn($"DAQ取消恢复观察者异常，已隔离：{ex.Message}", "AI"));
        }

        private bool TryRemoveExactDaqRecoveryContext(DaqAutoRecoveryContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Device)) return false;
            // ConcurrentDictionary.TryRemove(key) can remove a newer context if an old terminal
            // cleanup races with remove+replace. ICollection.Remove(KeyValuePair) compares both
            // key and reference value atomically, so a late old Run cannot delete a new Run.
            return ((ICollection<KeyValuePair<string, DaqAutoRecoveryContext>>)_daqAutoRecovery)
                .Remove(new KeyValuePair<string, DaqAutoRecoveryContext>(
                    context.Device,
                    context));
        }

        private void MarkDaqRecoveryTerminal(Guid correlationId, string device)
        {
            if (correlationId == Guid.Empty) return;
            _daqRecoveryTerminalCorrelations[BuildDaqRecoveryTerminalKey(device, correlationId)] =
                DateTime.UtcNow;
            _daqIncidentLatch.Complete(device, correlationId);
            if (_daqRecoveryTerminalCorrelations.Count <= 1024) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var item in _daqRecoveryTerminalCorrelations)
                if (item.Value < cutoff)
                    _daqRecoveryTerminalCorrelations.TryRemove(item.Key, out _);
        }

        private bool IsDaqRecoveryTerminalCorrelation(string device, Guid correlationId)
        {
            return correlationId != Guid.Empty &&
                   _daqRecoveryTerminalCorrelations.ContainsKey(
                       BuildDaqRecoveryTerminalKey(device, correlationId));
        }

        internal static string BuildDaqRecoveryTerminalKey(string device, Guid correlationId)
            => $"{(device ?? string.Empty).Trim().ToUpperInvariant()}:{correlationId:N}";

        private void StartDaqRecoveryWatchdog(DaqAutoRecoveryContext context)
        {
            if (context == null) return;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                try
                {
                    // 看门狗必须在任何同步断电、写盘封存或诊断动作之前启动。
                    // 这些步骤中的任意一个即使意外阻塞，也不能让通道永久停留在旧状态。
                    await Task.Delay(_daqPersistenceRecoveryTimeoutMs, context.Cancellation.Token)
                        .ConfigureAwait(false);
                    await EscalateDaqAutoRecoveryAsync(
                            context.Device,
                            "DaqRecoveryPipelineStalled",
                            $"恢复流水线在{_daqPersistenceRecoveryTimeoutMs}ms内未进入终态；" +
                            "保持受影响组安全隔离并启动软件自维护。",
                            context.CorrelationId)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }), "DaqRecoveryWatchdog");
        }

        private void StartDaqPowerDisableDeadline(DaqAutoRecoveryContext context)
        {
            if (context == null || _powerSupply == null ||
                ((context.PowerDisableTasksByGroup == null ||
                  context.PowerDisableTasksByGroup.Count == 0) &&
                 (context.PowerDisableTasks == null ||
                  context.PowerDisableTasks.Length == 0)))
                return;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(
                            PowerDisableHardDeadlineMs,
                            context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;
                    // 截止阶段一旦提交，后续 Validation/Rejoin 中的 Active 或重新上电
                    // 都不再属于“OFF 未确认”；不能用过期的 5 秒切断计时器越级接管。
                    if (context.Phase.Current >= DaqRecoveryPhase.CutoffCompleted) return;

                    var pendingGroups = (context.PowerDisableTasksByGroup ??
                                         new Dictionary<int, Task>())
                        .Where(pair => pair.Key > 0 &&
                                       pair.Value != null &&
                                       !pair.Value.IsCompleted)
                        .Select(pair => pair.Key)
                        .ToHashSet();
                    // Backward-compatible safety for contexts built by an older call
                    // site that only filled Task[].  New cutoff contexts always carry
                    // the group map, so no group identity is invented here.
                    var pendingTaskCount = pendingGroups.Count > 0
                        ? pendingGroups.Count
                        : (context.PowerDisableTasks ?? Array.Empty<Task>())
                            .Count(task => task != null && !task.IsCompleted);
                    var energizedGroups = (context.PowerDisableTasksByGroup ??
                                           new Dictionary<int, Task>())
                        .Where(pair => pair.Key > 0)
                        .Select(pair => pair.Key)
                        .Where(id =>
                        {
                            var state = _powerSupply.GetRuntimeState(id);
                            return pendingGroups.Contains(id) ||
                                   state.ExpectedOutputEnabled ||
                                   state.TelemetryOutputEnabled;
                        })
                        .OrderBy(id => id)
                        .ToArray();
                    if (energizedGroups.Length == 0 && pendingTaskCount == 0) return;

                    if (Interlocked.Exchange(ref context.PowerDisableDeadlineLogged, 1) == 0)
                    {
                        _log.Error(
                            $"DaqPowerDisableDeadlineExceeded Device={context.Device} " +
                            $"RunId={context.RunId:N} RunEpoch={context.RunEpoch} " +
                            $"CorrelationId={context.CorrelationId:N} " +
                            $"PendingTasks={pendingTaskCount} " +
                            $"EnergizedGroups=[{string.Join(",", energizedGroups)}] " +
                            $"DeadlineMs={PowerDisableHardDeadlineMs} " +
                            "ExternalRecoveryRequired=true；禁止继续在本进程重建DAQ。",
                            "程控电源");
                    }

                    TryEscalateSoftwareRecoveryCircuitOpen(
                        "DaqPowerDisableDeadline",
                        $"Device={context.Device}; PendingTasks={pendingTaskCount}; " +
                        $"EnergizedGroups=[{string.Join(",", energizedGroups)}]; " +
                        "ExternalRecoveryRequired=true",
                        context.AffectedChannels,
                        context.RunId,
                        context.RunEpoch,
                        SoftwareRecoveryEscalationAttempts,
                        "ExternalRecoveryRequired");
                }
                catch (OperationCanceledException) { }
            }), "DaqPowerDisableDeadline");
        }

        private async Task CancelAllDaqRecoveriesAsync(string reason)
        {
            DaqAutoRecoveryContext[] contexts;
            lock (_daqRecoveryCommitGate)
            {
                Interlocked.Increment(ref _runEpoch);
                contexts = _daqAutoRecovery.Values.ToArray();
                foreach (var context in contexts)
                    CompleteCancelledRecovery(context, reason);
            }
            if (contexts.Length == 0) return;
            var completions = contexts.Select(x => (Task)x.Completion.Task).ToArray();
            var all = Task.WhenAll(completions);
            await Task.WhenAny(all, Task.Delay(2000)).ConfigureAwait(false);
        }

        private void PublishRecoveryProgress(DaqAutoRecoveryContext context, string reason)
        {
            var queue = _persistence.GetSnapshot(context.Device);
            LogDaqRecoveryFieldMetric(context, "Progress", reason);
            NonCriticalObserver.Invoke(
                DaqPersistenceStateChanged,
                new DaqPersistenceStateChanged
                {
                    Device = context.Device,
                    State = DaqPersistenceState.Recovering,
                    Code = context.TriggerCode,
                    Reason = reason,
                    QueueDepth = queue.QueueDepth,
                    OldestBatchAgeMs = queue.OldestBatchAgeMs,
                    Generation = _acq.GetCurrentGeneration(context.Device),
                    TimestampUtc = DateTime.UtcNow,
                    CorrelationId = context.CorrelationId
                },
                ex => _log?.Warn($"DAQ恢复进度观察者异常，已隔离：{ex.Message}", "AI"));
        }

        private async Task<HardwareEvidence[]> ConfirmDaqHardwareFailureAsync(
            DaqAutoRecoveryContext context,
            DaqRecoveryResult recoveryResult)
        {
            if (context == null || recoveryResult == null ||
                !string.Equals(recoveryResult.FailureKind, "DaqDeviceMissing", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<HardwareEvidence>();

            var evidence = new List<HardwareEvidence>(2);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation.Token))
            {
                timeout.CancelAfter(3000);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (!IsCurrentRecovery(context)) return Array.Empty<HardwareEvidence>();
                    DaqHardwareProbeResult probe;
                    try
                    {
                        probe = await _daqHardwareProbe.ProbeAsync(context.Device, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return Array.Empty<HardwareEvidence>();
                    }
                    evidence.Add(probe.ToEvidence());
                    if (!probe.IndependentFailureConfirmed) return evidence.ToArray();
                    if (attempt == 0)
                        await Task.Delay(500, timeout.Token).ConfigureAwait(false);
                }
            }
            return evidence.ToArray();
        }

        /// <summary>
        /// Isolates an infrastructure fault while keeping it recoverable. Healthy independent
        /// groups continue running; affected channels remain safely de-energized in Recovering
        /// and are handed to the affected-group restart loop instead of a permanent SystemFault.
        /// </summary>
        private void PublishIsolatedSoftwareFault(
            string code,
            string reason,
            int[] affectedChannels,
            Guid correlationId)
        {
            var channels = (affectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var fault = new ControlFault(
                code,
                reason,
                channels.Length == 1 ? FaultScope.Channel : FaultScope.DaqGroup,
                channels,
                null,
                DateTime.UtcNow,
                correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
                FaultClassification.SystemFault);
            Dictionary<int, string> rejectedOff = null;
            ExecuteNonBlockingSafetyIsolationOrder(
                () => FreezeAndCancelSafetyChannels(
                    channels,
                    $"IsolatedSoftwareFault:{code}",
                    cancelStopTokens: false),
                () => rejectedOff = SubmitEpbOffHighPriorityBatch(
                    channels,
                    "IsolatedSoftwareFaultOffAdmissionRejected",
                    "IsolatedSoftwareFaultOffSubmissionException"),
                () => StartElectricalGroupSafetyDisables(
                    channels,
                    $"隔离软件故障安全断电 Code={code} CorrelationId={fault.CorrelationId:N}",
                    "IsolatedSoftwareFaultPowerDisable"),
                () =>
                {
                    // 先登记局部恢复任务，再发布逐通道/UI诊断；慢观察者不能阻止
                    // 受影响组进入有界恢复协调。
                    ScheduleIsolatedInfrastructureRecovery(
                        channels,
                        code,
                        fault.CorrelationId,
                        code);
                    foreach (var channel in channels)
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            code,
                            reason,
                            affectedChannels: channels,
                            correlationId: fault.CorrelationId);
                    _log.Error(
                        $"隔离软件故障（健康设备继续运行，不触发硬件报警/全局重启） [{code}]：{reason}",
                        "AI");
                    NonCriticalObserver.Invoke(
                        ControlFaultRaised,
                        fault,
                        ex => _log?.Warn(
                            $"DAQ隔离故障观察者异常，已隔离：{ex.Message}",
                            "AI"));
                },
                () => ScheduleRejectedOffFallbacks(
                    rejectedOff,
                    "IsolatedSoftwareFaultImmediateOffFallback"));
        }
    }
}
