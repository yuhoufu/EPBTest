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
                _daqAutoRecovery.TryRemove(context.Device, out _);
            }
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
            NonCriticalObserver.Invoke(
                DaqRecoveryStateChanged,
                result,
                ex => _log?.Warn($"DAQ取消恢复观察者异常，已隔离：{ex.Message}", "AI"));
        }

        private void MarkDaqRecoveryTerminal(Guid correlationId, string device)
        {
            if (correlationId == Guid.Empty) return;
            _daqRecoveryTerminalCorrelations[correlationId] = DateTime.UtcNow;
            _daqIncidentLatch.Complete(device, correlationId);
            if (_daqRecoveryTerminalCorrelations.Count <= 1024) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var item in _daqRecoveryTerminalCorrelations)
                if (item.Value < cutoff)
                    _daqRecoveryTerminalCorrelations.TryRemove(item.Key, out _);
        }

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
            foreach (var channel in channels)
            {
                try { CommandEpbOffSafetyImmediate(channel); } catch { }
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Recovering,
                    code,
                    reason,
                    affectedChannels: channels,
                    correlationId: fault.CorrelationId);
            }
            _log.Error(
                $"隔离软件故障（健康设备继续运行，不触发硬件报警/全局重启） [{code}]：{reason}",
                "AI");
            NonCriticalObserver.Invoke(
                ControlFaultRaised,
                fault,
                ex => _log?.Warn($"DAQ隔离故障观察者异常，已隔离：{ex.Message}", "AI"));
            ScheduleIsolatedInfrastructureRecovery(channels, code, fault.CorrelationId);
        }
    }
}
