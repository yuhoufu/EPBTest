using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    public sealed partial class EpbManager
    {
        private const int RecoveryOwnershipTakeoverTimeoutMs = 10_000;
        private const int RecoveryStageTimeoutMs = 15_000;
        private const int RecoveryMechanicalReleaseTimeoutMs = 20_000;
        internal const int RecoveryGroupHardDeadlineMs = 60_000;

        private readonly HydraulicRecoveryOwnershipCoordinator _recoveryOwnership =
            new HydraulicRecoveryOwnershipCoordinator();
        private readonly ConcurrentDictionary<int, byte> _affectedGroupResetInProgress = new();
        private readonly ConcurrentDictionary<long, byte> _activeCycleLimitRecoveries = new();

        private static int GetHydraulicGroupForChannel(int channel)
        {
            return channel >= 1 && channel <= 6 ? 1 :
                channel >= 7 && channel <= 12 ? 2 : 0;
        }

        private async Task<HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease[]>
            AcquireRecoveryOwnershipsAsync(
                string ownerId,
                RecoveryOwnerPriority priority,
                IEnumerable<int> channels,
                CancellationToken token)
        {
            var leases = new List<HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease>();
            try
            {
                foreach (var groupId in (channels ?? Array.Empty<int>())
                             .Select(GetHydraulicGroupForChannel)
                             .Where(groupId => groupId > 0)
                             .Distinct()
                             .OrderBy(groupId => groupId))
                {
                    leases.Add(await _recoveryOwnership.AcquireAsync(
                            groupId,
                            ownerId,
                            priority,
                            RecoveryOwnershipTakeoverTimeoutMs,
                            token)
                        .ConfigureAwait(false));
                }
                return leases.ToArray();
            }
            catch
            {
                foreach (var lease in leases) lease.Dispose();
                throw;
            }
        }

        private async Task AcquireDaqRecoveryOwnershipsAsync(DaqAutoRecoveryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            context.ValidationPhase = "RecoveryOwnership";
            var leases = await AcquireRecoveryOwnershipsAsync(
                    $"DAQ:{context.Device}:{context.CorrelationId:N}",
                    RecoveryOwnerPriority.Daq,
                    context.AffectedChannels,
                    context.Cancellation.Token)
                .ConfigureAwait(false);
            context.Ownerships = leases;
            if (Volatile.Read(ref context.OwnershipReleased) != 0)
            {
                foreach (var lease in leases) lease.Dispose();
                return;
            }
            context.OwnershipCancellationRegistrations = leases
                .Select(lease => lease.Token.Register(() =>
                {
                    try { context.Cancellation.Cancel(); }
                    catch { }
                    // 所有权抢占必须有一个确定的退出提交点。仅取消令牌不够：
                    // 自维护任务可能正处在下一次定时重试之前，没有活动 await 负责释放 lease。
                    _ = Task.Run(() =>
                        CompleteCancelledRecovery(context, "RecoveryOwnershipPreempted"));
                }))
                .ToArray();
        }

        private void ReleaseDaqRecoveryOwnerships(DaqAutoRecoveryContext context)
        {
            if (context == null || Interlocked.Exchange(ref context.OwnershipReleased, 1) != 0)
                return;
            foreach (var registration in context.OwnershipCancellationRegistrations ??
                         Array.Empty<CancellationTokenRegistration>())
            {
                try { registration.Dispose(); }
                catch { }
            }
            foreach (var lease in context.Ownerships ??
                         Array.Empty<HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease>())
            {
                try { lease.Dispose(); }
                catch { }
            }
        }

        private void StartAffectedGroupRecoveryDeadline(DaqAutoRecoveryContext context)
        {
            if (context == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RecoveryGroupHardDeadlineMs, context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;

                    var channels = (context.PreviouslyRunningChannels ?? context.AffectedChannels ??
                                    Array.Empty<int>())
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray();
                    _log.Error(
                        $"DAQ恢复超过{RecoveryGroupHardDeadlineMs}ms未提交，" +
                        $"执行受影响组Stop→Start等价清场。Device={context.Device} " +
                        $"Channels=[{string.Join(",", channels)}] " +
                        $"Phase={context.ValidationPhase} CorrelationId={context.CorrelationId:N}",
                        "AI");

                    CompleteCancelledRecovery(context, "AffectedGroupHardDeadlineReset");
                    await ExecuteAffectedGroupResetAsync(
                            channels,
                            $"DaqRecoveryHardDeadline:{context.Device}",
                            context.CorrelationId,
                            context.RunEpoch)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.Error(
                        $"DAQ受影响组自动清场失败 Device={context.Device}: {ex.Message}",
                        "AI",
                        ex);
                    PublishIsolatedSoftwareFault(
                        "AffectedGroupResetFailed",
                        ex.Message,
                        context.AffectedChannels,
                        context.CorrelationId);
                }
            });
        }

        private async Task ExecuteAffectedGroupResetAsync(
            int[] requestedChannels,
            string reason,
            Guid correlationId,
            long expectedRunEpoch)
        {
            var requested = (requestedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            foreach (var requestedGroup in requested.GroupBy(GetHydraulicGroupForChannel))
            {
                var hydraulicGroupId = requestedGroup.Key;
                if (hydraulicGroupId <= 0 ||
                    !_affectedGroupResetInProgress.TryAdd(hydraulicGroupId, 0))
                    continue;

                // Stop→Start 等价清场的边界是共享液压组，而不是最初上报故障的单通道。
                // 将同组仍有运行对象/成员资格的兄弟通道一并清场，避免旧 lease 或旧代次
                // 在恢复通道重新加入后继续污染下一槽。
                var channels = requestedGroup
                    .Concat(_timers.Keys)
                    .Concat(_runners.Keys)
                    .Concat(_hydraulicParticipants.Keys)
                    .Where(channel => GetHydraulicGroupForChannel(channel) == hydraulicGroupId)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease lease = null;
                try
                {
                    if (expectedRunEpoch != Interlocked.Read(ref _runEpoch)) return;
                    lease = await _recoveryOwnership.AcquireAsync(
                            hydraulicGroupId,
                            $"GROUP-RESET:{hydraulicGroupId}:{correlationId:N}",
                            RecoveryOwnerPriority.AffectedGroupReset,
                            RecoveryOwnershipTakeoverTimeoutMs,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    // 先完成与人工Stop相同的内存清场和安全断电。此时即使后续预检失败，
                    // 通道也已经处于明确隔离态，不会继续显示一个永不结束的旧恢复。
                    foreach (var channel in channels)
                    {
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "AffectedGroupReset",
                            "恢复超过硬期限，正在执行受影响组Stop→Start等价清场。",
                            affectedChannels: channels,
                            correlationId: correlationId,
                            allowTerminalReset: true);
                        try { CancelCyclePauseCts(channel); } catch { }
                        try { CancelStopCts(channel); } catch { }
                        RemoveTimerRuntime(channel, "AffectedGroupReset");
                        RemoveRunnerRuntime(channel, "AffectedGroupReset");
                        UnmarkHydraulicParticipant(channel);
                        try { CommandEpbOffHighPriority(channel, "AffectedGroupReset"); } catch { }
                        DiscardCurrentCycleForSoftwareRecovery(
                            channel,
                            DateTime.UtcNow,
                            reason);
                    }

                    await RecoveryStageDeadline.RunAsync(
                            "AffectedGroupForceRelease",
                            RecoveryMechanicalReleaseTimeoutMs,
                            _ => _hydCoordinator.ForceReleaseAsync(
                                hydraulicGroupId,
                                "AffectedGroupReset:" + reason),
                            lease.Token)
                        .ConfigureAwait(false);

                    if (expectedRunEpoch != Interlocked.Read(ref _runEpoch)) return;
                    foreach (var device in channels
                                 .Select(channel => _acq.GetDeviceForEpbChannel(channel))
                                 .Where(device => !string.IsNullOrWhiteSpace(device))
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        IO.NI.DaqRecoveryResult[] ready = null;
                        await RecoveryStageDeadline.RunAsync(
                                "AffectedGroupDaqValidation",
                                RecoveryStageTimeoutMs,
                                async ct =>
                                {
                                    ready = await _acq.EnsureChannelsReadyAsync(
                                            channels.Where(channel => string.Equals(
                                                    _acq.GetDeviceForEpbChannel(channel),
                                                    device,
                                                    StringComparison.OrdinalIgnoreCase))
                                                .ToArray(),
                                            RecoveryStageTimeoutMs,
                                            _daqPersistenceRequiredFreshBatches,
                                            (int)_daqPersistenceResumeAgeMs,
                                            ct)
                                        .ConfigureAwait(false);
                                },
                                lease.Token)
                            .ConfigureAwait(false);
                        if (ready == null || ready.Any(status => !status.Recovered))
                            throw new InvalidOperationException(
                                $"AffectedGroupDaqValidationFailed Device={device}");
                        _persistence.AcceptGeneration(device, _acq.GetCurrentGeneration(device));
                        _persistence.ResumeAdmission(device);
                    }

                    if (_powerSupply != null)
                        await RecoveryStageDeadline.RunAsync(
                                "AffectedGroupPowerEnable",
                                RecoveryStageTimeoutMs,
                                ct => _powerSupply.PrepareAndEnableAsync(channels, ct),
                                lease.Token)
                            .ConfigureAwait(false);

                    var plan = _activeStaggerPlan ??
                               ElectricalStaggerPlanner.Build(
                                   channels,
                                   _cfg.Test.Groups,
                                   PeriodMs);
                    await RecoveryStageDeadline.RunAsync(
                            "AffectedGroupMechanicalRelease",
                            RecoveryMechanicalReleaseTimeoutMs,
                            ct => EnsureMotorReleasedBeforeFormalRejoinAsync(
                                channels,
                                plan,
                                reason,
                                ct),
                            lease.Token)
                        .ConfigureAwait(false);

                    if (expectedRunEpoch != Interlocked.Read(ref _runEpoch)) return;
                    ResetTransientFaultStateForRestart(channels, "AffectedGroupResetRejoin");
                    RejoinFormalChannelsAtSharedFutureSlot(
                        channels,
                        plan,
                        "AffectedGroupResetRecovered",
                        "受影响组已完成Stop→Start等价清场并从未来完整槽重新加入",
                        allowTerminalReset: true);
                    _log.Info(
                        $"受影响液压组{hydraulicGroupId}自动清场完成，" +
                        $"Channels=[{string.Join(",", channels)}] Reason={reason}",
                        "液压协调");
                }
                catch (Exception ex)
                {
                    foreach (var channel in channels)
                    {
                        try { CommandEpbOffHighPriority(channel, "AffectedGroupResetFailed"); }
                        catch { }
                    }
                    PublishIsolatedSoftwareFault(
                        "AffectedGroupResetFailed",
                        $"Hydraulic={hydraulicGroupId} Reason={reason} Error={ex.Message}",
                        channels,
                        correlationId);
                }
                finally
                {
                    lease?.Dispose();
                    _affectedGroupResetInProgress.TryRemove(hydraulicGroupId, out _);
                }
            }
        }

        private async Task HandleActiveCycleDataLimitExceededAsync(
            DaqPersistenceStateChanged update)
        {
            if (update == null) return;
            var channel = update.EpbId;
            if (channel < 1 || channel > 12)
            {
                PublishIsolatedSoftwareFault(
                    "ActiveCycleIdentityMissing",
                    $"活动圈达到上限但缺少有效EPB标识。Device={update.Device} " +
                    $"Cycle={update.CycleNumber} Limit={update.RecordLimit}",
                    GetDaqGroupChannels(update.Device),
                    update.CorrelationId);
                return;
            }

            var lifecycleKey = DaqAbortedCycleKey(channel, update.CycleNumber);
            if (!_activeCycleLimitRecoveries.TryAdd(lifecycleKey, 0)) return;

            var expectedRunEpoch = Interlocked.Read(ref _runEpoch);
            var hydraulicGroupId = GetHydraulicGroupForChannel(channel);
            HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease lease = null;
            try
            {
                lease = await _recoveryOwnership.AcquireAsync(
                        hydraulicGroupId,
                        $"ACTIVE-CYCLE:{channel}:{update.CycleNumber}:{update.CorrelationId:N}",
                        RecoveryOwnerPriority.Hydraulic,
                        RecoveryOwnershipTakeoverTimeoutMs,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (expectedRunEpoch != Interlocked.Read(ref _runEpoch)) return;
                if (!_currentCycleNumberByChannel.TryGetValue(channel, out var currentCycle) ||
                    currentCycle != update.CycleNumber)
                {
                    _log.Warn(
                        $"忽略已过期的活动圈上限事件。EPB={channel} " +
                        $"EventCycle={update.CycleNumber} CurrentCycle={currentCycle} " +
                        $"Limit={update.RecordLimit}",
                        "落盘");
                    return;
                }
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Recovering,
                    "ActiveCycleDataLimitExceeded",
                    $"活动圈数据达到上限，正在作废 EPB={channel} " +
                    $"Cycle={update.CycleNumber} Limit={update.RecordLimit} 并重置液压代次。",
                    affectedChannels: new[] { channel },
                    correlationId: update.CorrelationId);
                if (_timers.TryGetValue(channel, out var timer))
                    timer.Pause("ActiveCycleDataLimitExceeded");
                CancelCyclePauseCts(channel);
                try { CommandEpbOffHighPriority(channel, "ActiveCycleDataLimitExceeded"); }
                catch { }
                UnmarkHydraulicParticipant(channel);
                if (!DiscardCurrentCycleForSoftwareRecovery(
                    channel,
                    update.TimestampUtc,
                    $"ActiveCycleDataLimitExceeded EPB={channel} " +
                    $"Cycle={update.CycleNumber} Limit={update.RecordLimit}",
                    update.CycleNumber))
                    throw new InvalidOperationException(
                        $"ActiveCycleCleanupCommitFailed EPB={channel} " +
                        $"Cycle={update.CycleNumber} Limit={update.RecordLimit}");

                await RecoveryStageDeadline.RunAsync(
                        "ActiveCycleHydraulicGenerationReset",
                        RecoveryMechanicalReleaseTimeoutMs,
                        _ => _hydCoordinator.ForceReleaseAsync(
                            hydraulicGroupId,
                            $"ActiveCycleDataLimitExceeded:EPB={channel}:Cycle={update.CycleNumber}"),
                        lease.Token)
                    .ConfigureAwait(false);

                var freshness = _acq.GetDaqFreshnessSnapshot(
                    update.Device,
                    _daqPersistenceResumeAgeMs);
                if (freshness == null || !freshness.IsFresh)
                {
                    // 活动圈上限只负责圈生命周期清理。仅有独立的DAQ陈旧证据时，
                    // 才以真实DAQ故障码进入设备恢复，绝不拿容量异常冒充DAQ故障。
                    _log.Warn(
                        $"活动圈已作废且液压代次已重置；另有DAQ陈旧证据，" +
                        $"转入DaqSampleStale恢复。EPB={channel} Device={update.Device}",
                        "AI");
                    _ = BeginDaqAutoRecoveryAsync(
                        update.Device,
                        "DaqSampleStale",
                        $"ActiveCycle cleanup completed; CallbackAge={freshness?.CallbackAgeMs:F1}ms " +
                        $"ControlAge={freshness?.ControlProcessedAgeMs:F1}ms",
                        update.CorrelationId,
                        restartDaq: true,
                        eventUtc: DateTime.UtcNow);
                    return;
                }

                var plan = _activeStaggerPlan ??
                           ElectricalStaggerPlanner.Build(
                               new[] { channel },
                               _cfg.Test.Groups,
                               PeriodMs);
                await RecoveryStageDeadline.RunAsync(
                        "ActiveCycleMechanicalRelease",
                        RecoveryMechanicalReleaseTimeoutMs,
                        ct => EnsureMotorReleasedBeforeFormalRejoinAsync(
                            new[] { channel },
                            plan,
                            "ActiveCycleDataLimitExceeded",
                            ct),
                        lease.Token)
                    .ConfigureAwait(false);
                if (expectedRunEpoch != Interlocked.Read(ref _runEpoch)) return;

                ResetTransientFaultStateForRestart(
                    new[] { channel },
                    "ActiveCycleDataLimitExceededRejoin");
                RejoinFormalChannelsAtSharedFutureSlot(
                    new[] { channel },
                    plan,
                    "ActiveCycleLifecycleRecovered",
                    $"EPB={channel} Cycle={update.CycleNumber} 已作废；" +
                    "DAQ保持原代次，液压代次重置后从未来完整槽重试",
                    allowTerminalReset: false);
                _log.Warn(
                    $"ActiveCycleDataLimitExceeded 已按圈生命周期故障处理，未重建DAQ。" +
                    $"EPB={channel} Cycle={update.CycleNumber} Limit={update.RecordLimit} " +
                    $"Device={update.Device} DaqFresh=true",
                    "落盘");
            }
            catch (Exception ex)
            {
                try { CommandEpbOffHighPriority(channel, "ActiveCycleRecoveryFailed"); }
                catch { }
                PublishIsolatedSoftwareFault(
                    "ActiveCycleRecoveryFailed",
                    $"EPB={channel} Cycle={update.CycleNumber} Limit={update.RecordLimit} " +
                    $"Error={ex.Message}",
                    new[] { channel },
                    update.CorrelationId);
            }
            finally
            {
                lease?.Dispose();
                _activeCycleLimitRecoveries.TryRemove(lifecycleKey, out _);
            }
        }
    }
}
