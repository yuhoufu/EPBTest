using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;

namespace Controller
{
    internal enum StartupPositioningOffState
    {
        CompletedWithinDeadline = 0,
        CompletedLateAfterGroupIsolation = 1,
        FailedCommandRecoveredByGroup = 2,
        AdmissionRejectedRecoveredByGroup = 3
    }

    internal sealed class StartupPositioningOffReceipt
    {
        internal StartupPositioningOffState State { get; set; }
        internal Guid CommandId { get; set; }
        internal HighPriorityDoTelemetry Telemetry { get; set; }
        internal Guid GroupCorrelationId { get; set; }
    }

    internal static class StartupPositioningOffRecoveryJoin
    {
        internal static async Task<EmergencyPowerGroupCompletion> WaitAsync(
            Task exactOffCompletion,
            Task<EmergencyPowerGroupCompletion> groupCompletion,
            int hardDeadlineMs,
            Func<bool> isRunCurrent,
            CancellationToken token)
        {
            if (exactOffCompletion == null)
                throw new ArgumentNullException(nameof(exactOffCompletion));
            if (groupCompletion == null)
                throw new ArgumentNullException(nameof(groupCompletion));
            if (isRunCurrent == null)
                throw new ArgumentNullException(nameof(isRunCurrent));

            var joined = Task.WhenAll(exactOffCompletion, groupCompletion);
            var deadline = Task.Delay(Math.Max(1, hardDeadlineMs), token);
            var completed = await Task.WhenAny(joined, deadline).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(completed, joined))
                throw new TimeoutException("StartupPositioningOffRecoveryHardDeadline");
            await joined.ConfigureAwait(false);
            if (!isRunCurrent())
                throw new OperationCanceledException(
                    "StartupPositioningOffRecoverySuperseded");
            return await groupCompletion.ConfigureAwait(false);
        }
    }

    public sealed partial class EpbManager
    {
        private async Task<StartupPositioningOffReceipt>
            EnsureStartupPositioningOutputOffAsync(
                int channel,
                Guid runId,
                long runEpoch,
                string reason,
                CancellationToken token)
        {
            var completion = new TaskCompletionSource<HighPriorityDoTelemetry>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Guid commandId;
            var accepted = _do.TrySubmitEpbOffHighPriority(
                channel,
                telemetry => completion.TrySetResult(telemetry),
                out commandId);
            if (accepted)
            {
                var immediate = await Task.WhenAny(
                        completion.Task,
                        Task.Delay(100, token))
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (ReferenceEquals(immediate, completion.Task))
                {
                    var telemetry = await completion.Task.ConfigureAwait(false);
                    if (telemetry?.Result == true)
                    {
                        SetChannelEnergized(channel, false);
                        return new StartupPositioningOffReceipt
                        {
                            State = StartupPositioningOffState.CompletedWithinDeadline,
                            CommandId = commandId,
                            Telemetry = telemetry
                        };
                    }
                }
            }

            var registration = RequestElectricalGroupEmergencyShutdown(
                channel,
                reason + (accepted
                    ? " AcceptedOffPendingOrFailed"
                    : " OffAdmissionRejected"));
            if (!registration.IsValid)
                throw new SoftwareSelfHealingRetryException(
                    $"EPB[{channel}] 无法建立电源组失效安全恢复事务。Reason={reason}");

            Task exactOffCompletion = accepted
                ? completion.Task
                : Task.CompletedTask;
            EmergencyPowerGroupCompletion groupCompletion;
            try
            {
                groupCompletion = await StartupPositioningOffRecoveryJoin.WaitAsync(
                        exactOffCompletion,
                        registration.Completion,
                        RecoveryGroupHardDeadlineMs,
                        () => _activeBatchId == runId &&
                              Interlocked.Read(ref _runEpoch) == runEpoch,
                        token)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new SoftwareSelfHealingRetryException(
                    $"EPB[{channel}] 电源组恢复超过{RecoveryGroupHardDeadlineMs}ms。" +
                    $"CorrelationId={registration.CorrelationId:N}");
            }
            if (groupCompletion?.Recovered != true)
                throw new SoftwareSelfHealingRetryException(
                    $"EPB[{channel}] 电源组恢复未获得重新上电许可。" +
                    $"Status={groupCompletion?.Status} Reason={groupCompletion?.Reason}");

            var finalTelemetry = accepted
                ? await completion.Task.ConfigureAwait(false)
                : null;
            if (finalTelemetry?.Result == true)
                SetChannelEnergized(channel, false);
            return new StartupPositioningOffReceipt
            {
                State = !accepted
                    ? StartupPositioningOffState.AdmissionRejectedRecoveredByGroup
                    : finalTelemetry?.Result == true
                        ? StartupPositioningOffState.CompletedLateAfterGroupIsolation
                        : StartupPositioningOffState.FailedCommandRecoveredByGroup,
                CommandId = commandId,
                Telemetry = finalTelemetry,
                GroupCorrelationId = registration.CorrelationId
            };
        }

        /// <summary>
        /// A RetryReady transition must preserve these resources. If a legacy
        /// terminal path revoked them, repair only while the same startup
        /// run/epoch is still authoritative and every output is confirmed OFF.
        /// Framework repair attempts are not mechanical positioning attempts.
        /// </summary>
        private bool TryEnsureStartupPositioningExecutionResources(
            int channel,
            Guid runId,
            ref EpbCycleRunner runner,
            out string failure)
        {
            failure = string.Empty;
            var permit = _channelExecutionFence.Capture(channel);
            if (IsChannelExecutionPermitCurrent(channel, permit) &&
                runner != null &&
                runner.IsBoundToExecutionPermit(permit))
                return true;

            var runEpoch = Interlocked.Read(ref _runEpoch);
            var lifecycle = _channelRuntimeStateStore.Get(channel);
            if (runId == Guid.Empty || _activeBatchId != runId ||
                lifecycle == null || lifecycle.RunId != runId ||
                lifecycle.RunEpoch != runEpoch ||
                lifecycle.State != ChannelRuntimeState.Starting ||
                Volatile.Read(ref _energizationRevoked) != 0 ||
                RequiresProcessRestart || !IsChannelEnabled(channel))
            {
                failure =
                    $"StartupExecutionRepairRejected Run={runId:N} Active={_activeBatchId:N} " +
                    $"Epoch={runEpoch} State={lifecycle?.State} " +
                    $"StateRun={lifecycle?.RunId:N}/{lifecycle?.RunEpoch} " +
                    $"Revoked={Volatile.Read(ref _energizationRevoked)} Restart={RequiresProcessRestart}";
                return false;
            }

            if (!CommandEpbOffSafetyImmediate(channel))
            {
                failure = "StartupExecutionRepairOffNotConfirmed";
                return false;
            }

            try
            {
                // Authorize creates a fresh permit generation and cancels any
                // legacy permit. Recreate the Runner so it cannot retain the
                // canceled generation in its private command fence.
                AuthorizeChannelExecution(channel, runEpoch);
                RemoveRunnerRuntime(channel, "StartupPositioningExecutionRepair");
                runner = GetRunner(channel) as EpbCycleRunner;
                permit = _channelExecutionFence.Capture(channel);
                if (runner == null ||
                    !IsChannelExecutionPermitCurrent(channel, permit) ||
                    !runner.IsBoundToExecutionPermit(permit))
                {
                    failure = "StartupExecutionRepairPermitOrRunnerMissing";
                    return false;
                }

                _log?.Warn(
                    $"EPB[{channel}] 已在同一启动 run/epoch 内修复 Runner/执行许可；" +
                    "本次框架修复不计入启动定位尝试次数。",
                    "EPB");
                return true;
            }
            catch (Exception ex)
            {
                failure = "StartupExecutionRepairFailed:" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Performs one startup-positioning retry under a real recovery
        /// worker.  The worker owns the output-off confirmation and backoff;
        /// no caller/stagger executor Task is registered as the owner.
        /// </summary>
        private async Task RunStartupPositioningRetryIncidentAsync(
            int channel,
            Guid runId,
            int attempt,
            string reasonCode,
            string reasonText,
            int delayMs,
            CancellationToken token,
            string offReason)
        {
            var runEpoch = Interlocked.Read(ref _runEpoch);
            await EnsureStartupPositioningOutputOffAsync(
                    channel,
                    runId,
                    runEpoch,
                    offReason,
                    token)
                .ConfigureAwait(false);
            if (_channelRuntimeStateStore.Get(channel)?.State ==
                ChannelRuntimeState.Recovering)
            {
                await Task.Delay(delayMs, token).ConfigureAwait(false);
                return;
            }

            var ownerId = Guid.NewGuid();
            RecoveryIncidentHandle recoveryIncident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                };
            }

            if (!TryBeginRecoveryIncident(
                    "StartupPositioningSelfHealing",
                    runId,
                    runEpoch,
                    RecoveryOwnerKind.BatchStartup,
                    RecoveryTargetPhase.Startup,
                    ownerId,
                    new[] { channel },
                    _ => BuildRecoveryWorker(),
                    contract =>
                    {
                        PublishRecoveryIncidentState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            reasonCode,
                            reasonText,
                            affectedChannels: contract.Channels,
                            correlationId: contract.IncidentId,
                            allowTerminalReset: true,
                            recoveryOwnerKind: contract.OwnerKind,
                            recoveryTargetPhase: contract.TargetPhase,
                            recoveryOwnerId: contract.OwnerId,
                            recoveryOwnerGeneration: contract.RunEpoch);
                    },
                    out recoveryIncident))
                throw new InvalidOperationException(
                    $"EPB[{channel}] 启动定位恢复事务建立失败，已保持安全终态。");

            try
            {
                _taskSupervisor.Observe(
                    recoveryIncident.WorkerTask,
                    "StartupPositioningSelfHealing",
                    _activeBatchId,
                    channel);
                if (!recoveryIncident.Start())
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 启动定位恢复worker启动许可被拒绝。");
                await recoveryIncident.WorkerTask.ConfigureAwait(false);
            }
            finally
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                    CommitRecoveryIncidentStateForRetry(
                        contract,
                        ChannelRuntimeState.Starting,
                        "StartupPositioningRetryReady",
                        "启动定位重试前安全断电已确认，继续当前定位流程。"));
            }
        }

        internal async Task PublishStartupPositioningFailureAsync(StartupPositioningResult result)
        {
            if (result == null || result.Succeeded) return;
            RefreshStartupPositioningEvidence(result);
            var openCircuit = StartupPositioningFaultPolicy.IsOpenCircuit(result);
            var classification = ClassifyStartupPositioningFailure(
                IsStartupPositioningOverCurrent(result),
                HasFreshStartupPowerEvidence(result?.Channel ?? 0),
                IsStartupPositioningOutputControlFailure(result),
                openCircuit,
                result.ForwardCommandAccepted,
                result.DaqEvidenceFresh,
                result.PowerEnergizationPermitted,
                result.InfrastructureTransitionObserved);
            var reason = BuildStartupPositioningFailureReason(result, openCircuit, classification);
            if (classification != FaultClassification.HardwareConfirmed &&
                !TryEnsureSoftwareRecoveryOutputOff(
                    result.Channel,
                    "StartupPositioningSelfHealing"))
            {
                reason += " OutputOffCommandFailed：保持电源组安全自恢复，断电确认后继续启动定位。";
            }
            var fault = new ControlFault(
                string.IsNullOrWhiteSpace(result.Code) ? "StartupPositioningFailed" : result.Code,
                reason,
                FaultScope.Channel,
                new[] { result.Channel },
                null,
                DateTime.UtcNow,
                Guid.NewGuid(),
                classification,
                classification == FaultClassification.HardwareConfirmed
                    ? FaultRecoveryPolicy.CurrentRunDisableChannel
                    : FaultRecoveryPolicy.Recoverable);

            if (classification != FaultClassification.HardwareConfirmed)
            {
                await RunStartupPositioningFailureIncidentAsync(
                        result,
                        fault,
                        "StartupPositioningSelfHealing",
                        "启动定位未获得硬件故障双证据；已安全断电，按软件瞬态继续自愈。" + reason)
                    .ConfigureAwait(false);
                _log?.Warn(
                    $"EPB[{result.Channel}] 启动定位未获得硬件故障双证据，保持自愈。" +
                    $"CorrelationId={fault.CorrelationId:N} {reason}",
                    "EPB");
                FlushPersistentLog();
                NonCriticalObserver.Invoke(
                    ControlFaultRaised,
                    fault,
                    ex => _log?.Warn($"启动定位自愈观察者异常，已隔离：{ex.Message}", "EPB"));
                try { ExportStartupPositioningSnapshot(result, fault); }
                catch (Exception ex)
                {
                    _log?.Warn($"EPB[{result.Channel}] 启动定位快照导出失败：{ex.Message}", "落盘");
                }
                return;
            }

            NotifyRunAuthorizationRevoking(
                StopSource.AlarmInterlock,
                reason,
                nameof(PublishStartupPositioningFailureAsync),
                fault.CorrelationId,
                FaultScope.Channel);

            _alarmStopLatch.TryRequestStop(result.Channel);
            _log?.Error(
                $"EPB[{result.Channel}] 启动定位失败并隔离。CorrelationId={fault.CorrelationId:N} {reason}",
                "报警");
            FlushPersistentLog(true);
            PublishFaultRuntimeStates(fault, result.Channel);
            NonCriticalObserver.Invoke(
                ControlFaultRaised,
                fault,
                ex => _log?.Warn($"启动定位故障观察者异常，已隔离：{ex.Message}", "EPB"));
            NonCriticalObserver.Invoke(
                ChannelAlarmRaised,
                result.Channel,
                reason,
                ex => _log?.Warn($"启动定位报警观察者异常，已隔离：{ex.Message}", "EPB"));
            try
            {
                if (Alarm != null)
                    await Alarm.SetAlarmAsync(result.Channel, true, reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{result.Channel}] 启动定位失败后报警灯输出失败：{ex.Message}", "报警");
            }

            try
            {
                ExportStartupPositioningSnapshot(result, fault);
            }
            catch (Exception ex)
            {
                var message = $"EPB[{result.Channel}] 启动定位快照导出失败：{ex.Message}";
                _log?.Warn(message, "落盘");
                NonCriticalObserver.Invoke(
                    SnapshotExportFailed,
                    message,
                    observerEx => _log?.Warn(
                        $"启动定位快照失败观察者异常，已隔离：{observerEx.Message}",
                        "落盘"));
            }
        }

        private async Task RunStartupPositioningFailureIncidentAsync(
            StartupPositioningResult result,
            ControlFault fault,
            string reasonCode,
            string reasonText)
        {
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var ownerId = fault?.CorrelationId ?? Guid.NewGuid();
            var channel = result?.Channel ?? 0;
            if (runId == Guid.Empty || runEpoch <= 0 || channel < 1 || channel > 12)
                throw new InvalidOperationException(
                    "启动定位软件故障缺少当前运行身份，拒绝创建孤儿恢复事务。");

            RecoveryIncidentHandle recoveryIncident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return () =>
                {
                    if (!TryEnsureSoftwareRecoveryOutputOff(
                            channel,
                            "StartupPositioningFailureIncident"))
                    {
                        RequestElectricalGroupEmergencyShutdown(
                            channel,
                            "StartupPositioningFailureIncident");
                        throw new SoftwareSelfHealingRetryException(
                            $"EPB[{channel}] 启动定位故障后的断电确认失败。");
                    }
                    return Task.CompletedTask;
                };
            }

            if (!TryBeginRecoveryIncident(
                    "StartupPositioningFailureIncident",
                    runId,
                    runEpoch,
                    RecoveryOwnerKind.BatchStartup,
                    RecoveryTargetPhase.Startup,
                    ownerId,
                    new[] { channel },
                    _ => BuildRecoveryWorker(),
                    contract =>
                    {
                        PublishRecoveryIncidentState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            reasonCode,
                            reasonText,
                            affectedChannels: contract.Channels,
                            correlationId: contract.IncidentId,
                            allowTerminalReset: true,
                            recoveryOwnerKind: contract.OwnerKind,
                            recoveryTargetPhase: contract.TargetPhase,
                            recoveryOwnerId: contract.OwnerId,
                            recoveryOwnerGeneration: contract.RunEpoch);
                    },
                    out recoveryIncident))
                throw new InvalidOperationException(
                    $"EPB[{channel}] 启动定位故障恢复事务建立失败，已保持安全终态。");

            try
            {
                _taskSupervisor.Observe(
                    recoveryIncident.WorkerTask,
                    "StartupPositioningFailureIncident",
                    _activeBatchId,
                    channel);
                if (!recoveryIncident.Start())
                    throw new InvalidOperationException(
                        $"EPB[{channel}] 启动定位故障恢复worker启动许可被拒绝。");
                await recoveryIncident.WorkerTask.ConfigureAwait(false);
            }
            finally
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                    CommitRecoveryIncidentStateForRelease(
                        contract,
                        "StartupPositioningFailureRetryReady",
                        "启动定位软件故障已安全断电，等待原流程有界重试。"));
            }
        }

        internal bool IsStartupPositioningHardwareConfirmed(StartupPositioningResult result)
        {
            if (result == null || result.Succeeded) return false;
            RefreshStartupPositioningEvidence(result);
            return ClassifyStartupPositioningFailure(
                       IsStartupPositioningOverCurrent(result),
                       HasFreshStartupPowerEvidence(result.Channel),
                       IsStartupPositioningOutputControlFailure(result),
                       StartupPositioningFaultPolicy.IsOpenCircuit(result),
                       result.ForwardCommandAccepted,
                       result.DaqEvidenceFresh,
                       result.PowerEnergizationPermitted,
                       result.InfrastructureTransitionObserved) ==
                   FaultClassification.HardwareConfirmed;
        }

        internal static FaultClassification ClassifyStartupPositioningFailure(
            bool overCurrent,
            bool freshIndependentPowerEvidence,
            bool outputControlFailure = false,
            bool openCircuit = false,
            bool forwardCommandAccepted = false,
            bool daqEvidenceFresh = false,
            bool powerEnergizationPermitted = false,
            bool infrastructureTransition = false)
        {
            // 输出关闭失败属于控制链/外部设备故障，必须保持安全断电重试；
            // 过流需要独立电源证据；近零电流需要命令、DAQ、供电许可三项
            // 同时有效，且不能发生在DAQ/电源计划恢复窗口内。
            var hardwareConfirmed = overCurrent && freshIndependentPowerEvidence ||
                                    openCircuit && forwardCommandAccepted &&
                                    daqEvidenceFresh && powerEnergizationPermitted &&
                                    !infrastructureTransition;
            return hardwareConfirmed
                ? FaultClassification.HardwareConfirmed
                : FaultClassification.SoftwareTransient;
        }

        private void RefreshStartupPositioningEvidence(StartupPositioningResult result)
        {
            if (result == null) return;
            result.RootFaultCode = StartupPositioningFaultPolicy.NormalizeRootCode(
                result.RootFaultCode ?? result.Code,
                result.Reason);
            result.Code = result.RootFaultCode;
            var groupId = GetElectricalGroupId(result.Channel);
            var permitReason = "PowerCoordinatorMissing";
            result.PowerEnergizationPermitted = _powerSupply != null && groupId > 0 &&
                                                 _powerSupply.HasEnergizationPermit(
                                                     groupId,
                                                     out permitReason);
            result.InfrastructureTransitionObserved =
                IsInfrastructureTransitionForChannel(
                    result.Channel,
                    out var transitionReason);
            result.InfrastructureTransitionReason = result.InfrastructureTransitionObserved
                ? transitionReason
                : result.PowerEnergizationPermitted
                    ? string.Empty
                    : $"PowerPermitMissing Group={groupId} Reason={permitReason}";
        }

        private static string BuildStartupPositioningFailureReason(
            StartupPositioningResult result,
            bool openCircuit,
            FaultClassification classification)
        {
            var detail =
                $"StartupPositioningFailed Stage={result.Stage} Code={result.Code} " +
                $"Last={result.LastCurrentA:F3}A Peak={result.PeakCurrentA:F3}A " +
                $"Elapsed={result.ElapsedMs}ms CommandAccepted={result.ForwardCommandAccepted} " +
                $"DaqFresh={result.DaqEvidenceFresh} PowerPermit={result.PowerEnergizationPermitted} " +
                $"InfrastructureTransition={result.InfrastructureTransitionObserved} " +
                $"TransitionReason={result.InfrastructureTransitionReason} Detail={result.Reason}";
            if (!openCircuit || classification != FaultClassification.HardwareConfirmed)
                return detail;
            return
                $"EPB{result.Channel} 上电后连续约{result.NearZeroConfirmMs}ms电流不高于" +
                $"{result.NearZeroThresholdA:F2}A（末值{result.LastCurrentA:F3}A），" +
                "疑似卡钳、线束或驱动支路开路；本次运行已隔离该通道，" +
                "其余健康通道继续，下次启动将重新检测。 " + detail;
        }

        private static bool IsStartupPositioningOutputControlFailure(
            StartupPositioningResult result)
        {
            return result?.Code?.IndexOf(
                       "OutputOffCommandFailed",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   result?.Reason?.IndexOf(
                       "OutputOffCommandFailed",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsStartupPositioningOverCurrent(StartupPositioningResult result)
        {
            return result != null &&
                   (result.Code?.IndexOf("OverCurrent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    result.Reason?.IndexOf("OverCurrent", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool HasFreshStartupPowerEvidence(int channel)
        {
            if (_powerSupply == null || channel <= 0) return false;
            var groupId = GetElectricalGroupId(channel);
            return groupId > 0 && _powerSupply.HasFreshPowerFaultEvidence(groupId);
        }

        private void ExportStartupPositioningSnapshot(
            StartupPositioningResult result,
            ControlFault fault)
        {
            var baseDir = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName, "AlarmSnapshots");
            Directory.CreateDirectory(baseDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var snapshotDir = Path.Combine(
                baseDir,
                $"{stamp}-EPB{result.Channel:D2}-StartupPositioning");
            Directory.CreateDirectory(snapshotDir);

            var samples = result.Samples ?? Array.Empty<StartupPositioningSample>();
            using (var writer = new StreamWriter(
                       Path.Combine(snapshotDir, "startup-current.csv"),
                       false,
                       new UTF8Encoding(false)))
            {
                writer.WriteLine("utc,elapsed_ms,stage,current_a,slope_a_per_ms,decision");
                foreach (var sample in samples)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(sample.Utc.ToString("O", CultureInfo.InvariantCulture)),
                        sample.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                        Csv(sample.Stage.ToString()),
                        sample.CurrentA.ToString("R", CultureInfo.InvariantCulture),
                        sample.SlopeAperMs.ToString("R", CultureInfo.InvariantCulture),
                        Csv(sample.Decision)
                    }));
                }
            }

            try
            {
                _acq?.ExportFastCurrentEvidence(
                    snapshotDir,
                    result.Channel,
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{result.Channel}] 启动定位快速原始证据导出失败：{ex.Message}", "AI");
            }

            _runIdByChannel.TryGetValue(result.Channel, out var runId);
            var doEvents = _doControlTrace.Snapshot(DateTime.UtcNow, runId)
                .Where(x => x.Channel == result.Channel)
                .ToArray();
            WriteDoTimeline(Path.Combine(snapshotDir, "do-timeline.csv"), doEvents);

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"schemaVersion\": 1,");
            json.AppendLine($"  \"capturedUtc\": \"{DateTime.UtcNow:O}\",");
            json.AppendLine($"  \"correlationId\": \"{fault.CorrelationId:N}\",");
            json.AppendLine($"  \"runId\": \"{runId:N}\",");
            json.AppendLine($"  \"channel\": {result.Channel},");
            json.AppendLine($"  \"stage\": \"{Json(result.Stage.ToString())}\",");
            json.AppendLine($"  \"code\": \"{Json(result.Code)}\",");
            json.AppendLine($"  \"rootFaultCode\": \"{Json(result.RootFaultCode)}\",");
            json.AppendLine($"  \"reason\": \"{Json(result.Reason)}\",");
            json.AppendLine($"  \"peakCurrentA\": {StartupJsonNumber(result.PeakCurrentA)},");
            json.AppendLine($"  \"lastCurrentA\": {StartupJsonNumber(result.LastCurrentA)},");
            json.AppendLine($"  \"forwardCommandAccepted\": {result.ForwardCommandAccepted.ToString().ToLowerInvariant()},");
            json.AppendLine($"  \"daqEvidenceFresh\": {result.DaqEvidenceFresh.ToString().ToLowerInvariant()},");
            json.AppendLine($"  \"powerEnergizationPermitted\": {result.PowerEnergizationPermitted.ToString().ToLowerInvariant()},");
            json.AppendLine($"  \"infrastructureTransitionObserved\": {result.InfrastructureTransitionObserved.ToString().ToLowerInvariant()},");
            json.AppendLine($"  \"infrastructureTransitionReason\": \"{Json(result.InfrastructureTransitionReason)}\",");
            json.AppendLine($"  \"nearZeroThresholdA\": {StartupJsonNumber(result.NearZeroThresholdA)},");
            json.AppendLine($"  \"nearZeroConfirmMs\": {result.NearZeroConfirmMs},");
            json.AppendLine($"  \"lastSlopeAperMs\": {StartupJsonNumber(result.LastSlopeAperMs)},");
            json.AppendLine($"  \"elapsedMs\": {result.ElapsedMs},");
            json.AppendLine($"  \"forwardProgramProgressDeadlineMs\": {result.ForwardProgramProgressDeadlineMs},");
            json.AppendLine($"  \"forwardProjectLimitMs\": {result.ForwardProjectLimitMs},");
            json.AppendLine($"  \"forwardLearnedProgressDeadlineMs\": {result.ForwardLearnedProgressDeadlineMs},");
            json.AppendLine($"  \"forwardEffectiveProgressDeadlineMs\": {result.ForwardEffectiveProgressDeadlineMs},");
            json.AppendLine($"  \"forwardAbsoluteOnTimeMs\": {result.ForwardAbsoluteOnTimeMs},");
            json.AppendLine($"  \"reverseProgramProgressDeadlineMs\": {result.ReverseProgramProgressDeadlineMs},");
            json.AppendLine($"  \"reverseLearnedProgressDeadlineMs\": {result.ReverseLearnedProgressDeadlineMs},");
            json.AppendLine($"  \"reverseEffectiveProgressDeadlineMs\": {result.ReverseEffectiveProgressDeadlineMs},");
            json.AppendLine($"  \"reverseAbsoluteOnTimeMs\": {result.ReverseAbsoluteOnTimeMs},");
            json.AppendLine($"  \"reverseReleaseThresholdA\": {StartupJsonNumber(result.ReverseReleaseThresholdA)},");
            json.AppendLine($"  \"sampleCount\": {samples.Count},");
            json.AppendLine($"  \"doEventCount\": {doEvents.Length}");
            json.AppendLine("}");
            File.WriteAllText(
                Path.Combine(snapshotDir, "startup-metadata.json"),
                json.ToString(),
                new UTF8Encoding(false));

            _log?.Warn(
                $"启动定位报警快照已导出：EPB[{result.Channel}] -> {snapshotDir}",
                "落盘");
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string Json(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string StartupJsonNumber(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "null"
                : value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
