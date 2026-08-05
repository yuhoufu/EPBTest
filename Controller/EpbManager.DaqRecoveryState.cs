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
            if (!context.Terminal.TryCommit(DaqRecoveryTerminal.Cancelled)) return;
            MarkDaqRecoveryTerminal(context.CorrelationId);
            _daqAutoRecovery.TryRemove(context.Device, out _);
            try { context.Cancellation.Cancel(); } catch { }
            var result = new DaqRecoveryResult
            {
                Device = context.Device ?? string.Empty,
                Recovered = false,
                PreviousGeneration = context.PreviousGeneration,
                RecoveredGeneration = context.RecoveredGeneration,
                FailureReason = reason ?? "RecoveryCancelled",
                FailureKind = "Cancelled",
                Classification = FaultClassification.SoftwareTransient
            };
            context.Completion.TrySetResult(result);
            try { DaqRecoveryStateChanged?.Invoke(result); } catch { }
        }

        private void MarkDaqRecoveryTerminal(Guid correlationId)
        {
            if (correlationId == Guid.Empty) return;
            _daqRecoveryTerminalCorrelations[correlationId] = DateTime.UtcNow;
            if (_daqRecoveryTerminalCorrelations.Count <= 1024) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var item in _daqRecoveryTerminalCorrelations)
                if (item.Value < cutoff)
                    _daqRecoveryTerminalCorrelations.TryRemove(item.Key, out _);
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
            try
            {
                DaqPersistenceStateChanged?.Invoke(new DaqPersistenceStateChanged
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
                });
            }
            catch { }
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

        private void PublishStandaloneSystemFault(
            string code,
            string reason,
            int[] affectedChannels,
            Guid correlationId)
        {
            var channels = (affectedChannels ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray();
            var fault = new ControlFault(
                code,
                reason,
                FaultScope.DaqGroup,
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
                    ChannelRuntimeState.SystemFault,
                    code,
                    reason,
                    affectedChannels: channels,
                    correlationId: fault.CorrelationId);
            }
            _log.Error($"系统故障（不触发硬件报警） [{code}]：{reason}", "AI");
            try { ControlFaultRaised?.Invoke(fault); } catch { }
            try { SystemFaultRaised?.Invoke(fault); } catch { }
        }
    }
}
