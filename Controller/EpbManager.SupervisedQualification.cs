using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;
using MTTFTest.Watchdog.Protocol;

namespace Controller
{
    public sealed partial class EpbManager
    {
        private readonly SupervisedFormalContinuation _supervisedFormal = new SupervisedFormalContinuation();

        public Task<BatchStartResult> PrepareBatchQualificationAsync(int[] channels,
            RecoveryCommand command, RunChainIdentity chainIdentity, CancellationToken token)
        {
            if (command?.IsStructurallyValid() != true || command.Kind != RecoveryCommandKind.RunQualificationCycle ||
                command.DeadlineUtcTicks <= DateTime.UtcNow.Ticks)
                throw new InvalidOperationException("QualificationCommandInvalid");
            var selected = (channels ?? Array.Empty<int>()).Distinct().OrderBy(channel => channel).ToArray();
            if (selected.Length == 0 || selected.Any(channel => channel < 1 || channel > 12))
                throw new ArgumentException("QualificationChannelsInvalid", nameof(channels));
            var reuseStableProfiles = selected.All(channel => GetAdaptiveProfile(channel).IsStable);
            // New models still need two separate qualification circles after learning.
            return StartBatchCoreAsync(selected, reuseStableProfiles ? 0 : Math.Max(5, _cfg.Test?.LearnCycles ?? 5),
                2, reuseStableProfiles, token, chainIdentity, supervisedQualification: command);
        }

        public Task<int[]> CommitPreparedFormalRunAsync(RecoveryCommand command, CancellationToken token)
        {
            return _batchLifecycleGate.RunAsync(() => _supervisedFormal.CommitAsync(command, token), token);
        }

        public void InvalidatePreparedFormalRun()
        {
            _supervisedFormal.Invalidate();
        }

        private void PrepareSupervisedFormalContinuation(long version, int[] channels,
            ElectricalStaggerPlan staggerPlan, Guid incidentId)
        {
            var selected = channels.ToArray();
            var batchId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var permits = selected.ToDictionary(channel => channel, channel =>
                RequireCurrentChannelExecutionPermit(channel, "资格完成等待授权"));
            var devices = selected.Select(_acq.GetDeviceForEpbChannel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (devices.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("QualifiedDaqMappingMissing");
            var daqGenerations = devices.ToDictionary(device => device, device => _acq.GetDaqFreshnessSnapshot(device, 100).Generation);
            _supervisedFormal.CompleteQualification(version, async (ensureCurrent, token) =>
            {
                using (var linked = CreateResumeLinkedTokenSource(token, selected, includeBatchSession: true))
                using (var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    permits.Values.Select(permit => permit.RevocationToken).Concat(new[] { linked.Token }).ToArray()))
                {
                    var resumeToken = executionCancellation.Token;
                    Action validate = () =>
                    {
                        ensureCurrent();
                        resumeToken.ThrowIfCancellationRequested();
                        if (!IsBatchSessionActive || IsFormalPhaseCommitted || IsEnergizationRevoked ||
                            batchId != _activeBatchId || runEpoch != Interlocked.Read(ref _runEpoch) ||
                            selected.Any(channel => !IsChannelExecutionPermitCurrent(channel, permits[channel]) ||
                                IsAlarmStopRequested(channel) || _nonRecoverableChannelFaultLatch.ContainsKey(channel)))
                            throw new InvalidOperationException("QualifiedFormalExecutionContextRevoked");
                        EnsureNoPermanentDataContinuityGap("资格后正式启动");
                    };
                    validate();
                    await _pauseResumeGate.WaitAsync(resumeToken).ConfigureAwait(false);
                    try
                    {
                        validate();
                        // This is a read-only health probe, not another private recovery loop.
                        await Task.WhenAll(daqGenerations.Select(pair => SupervisedFormalDaqProbe.VerifyAsync(pair.Value,
                            () => _acq.GetDaqFreshnessSnapshot(pair.Key, 100), resumeToken))).ConfigureAwait(false);
                        EnsureStrictCurveControl(selected);
                        EnsureAdaptiveProfilesReady(selected);
                        var hydraulics = _cfg.Test.Hydraulics.Where(group => group.Enabled &&
                            selected.Any(channel => group.Members.Count > 0 ? group.Members.Contains(channel) : (channel <= 6 ? 1 : 2) == group.Id)).ToArray();
                        if (hydraulics.Length == 0) throw new InvalidOperationException("QualifiedFormalHydraulicScopeMissing");
                        foreach (var group in hydraulics)
                        {
                            var sample = _acq.ReadPressureSample(group.Id);
                            if (!sample.IsFinite || sample.AgeMs < 0 || sample.AgeMs > group.PressureSampleMaxAgeMs ||
                                sample.ValueBar > group.ReleaseSafePressureBar)
                                throw new InvalidOperationException("QualifiedFormalPressureNotSafeOrFresh:" + group.Id);
                        }
                        validate();
                        if (_powerSupply == null) throw new InvalidOperationException("QualifiedFormalPowerMissing");
                        await _powerSupply.PrepareAndEnableAsync(selected, resumeToken).ConfigureAwait(false);
                        validate();
                        var groups = GroupByPressure(selected);
                        var t0 = CeilToBoundary(DateTime.UtcNow.AddMilliseconds(AnchorWarmupMs), PeriodMs);
                        var anchors = groups.Keys.ToDictionary(group => group, group => t0);
                        StartFormalPhaseTimers(groups, anchors, staggerPlan, _batchSessionCts.Token);
                        // Recheck after timer creation. EngineHost also fences its final
                        // snapshot publication against the priority Stop command.
                        validate();
                        MarkBatchRunning(selected, "Supervisor 已授权正式运行");
                        foreach (var channel in selected)
                            PublishChannelRuntimeState(channel, ChannelRuntimeState.Running, "SupervisorFormalAuthorized",
                                "资格复核完成，Supervisor 已授权正式运行", affectedChannels: selected, correlationId: incidentId);
                        LogFieldSessionMetric("Start", batchId, selected, false, "SupervisorFormal");
                        return selected;
                    }
                    catch
                    {
                        _supervisedFormal.Invalidate();
                        foreach (var channel in selected)
                        {
                            try { StopChannelForInternalCleanup(channel); }
                            catch (Exception cleanup) { _log?.Error("资格后正式启动清理失败：" + cleanup.Message, "EPB", cleanup); }
                        }
                        await DisableBatchResumePowerBestEffortAsync(selected, "SupervisorFormalCommitFailed").ConfigureAwait(false);
                        // Failure is not a safety proof; the Supervisor must still run its OFF transaction.
                        throw;
                    }
                    finally { _pauseResumeGate.Release(); }
                }
            });
        }
    }
}
