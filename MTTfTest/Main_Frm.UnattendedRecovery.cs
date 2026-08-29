using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    public partial class Main_Frm
    {
        private static Guid ProtectedRoot(UnattendedRunCheckpoint checkpoint)
        {
            return Guid.TryParse(checkpoint?.RootRunId, out var root) ? root : Guid.Empty;
        }

        private RecoveryStartupIntent _recoveryStartupIntent;
        private WatchdogRecoveryIntent _watchdogRecoveryIntent;

        internal Main_Frm(RecoveryStartupIntent recoveryStartupIntent) : this()
        {
            _recoveryStartupIntent = recoveryStartupIntent;
        }

        internal Main_Frm(
            RecoveryStartupIntent recoveryStartupIntent,
            WatchdogRecoveryIntent watchdogRecoveryIntent) : this(recoveryStartupIntent)
        {
            _watchdogRecoveryIntent = watchdogRecoveryIntent;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_watchdogRecoveryIntent != null)
            {
                var watchdogIntent = _watchdogRecoveryIntent;
                _watchdogRecoveryIntent = null;
                BeginInvoke((Action)(async () =>
                {
                    if (watchdogIntent.StartIdle)
                        await EnterWatchdogSafeIdleAsync(watchdogIntent);
                    else
                        await ResumeWatchdogRunAsync(watchdogIntent);
                }));
                return;
            }
            if (_recoveryStartupIntent == null)
            {
                BeginInvoke((Action)(async () => await PrepareGracefulPauseResumeAsync()));
                return;
            }
            var intent = _recoveryStartupIntent;
            _recoveryStartupIntent = null;
            BeginInvoke((Action)(async () => await ResumeUnattendedRunAsync(intent)));
        }

        private async Task EnterWatchdogSafeIdleAsync(WatchdogRecoveryIntent intent)
        {
            UnattendedRecoveryCoordinator.Disarm("ManualStopWatchdogIdleRestart");
            UnattendedRunCheckpointStore.ClearGracefulPause("ManualStopWatchdogIdleRestart");
            var monitor = new FrmEpbMainMonitor { Name = "实时监视" };
            OpenChildForm(monitor);
            try
            {
                if (!await monitor.WaitUntilWatchdogControllerReadyAsync().ConfigureAwait(true))
                    throw new InvalidOperationException("Watchdog 空闲监视窗口未完成控制对象初始化。");

                // Idle restart is deliberately non-resuming, but it still
                // owns the same exact attached UI pipeline as normal start
                // and recovery.  This keeps the Main_Frm target/handler
                // lifecycle observable while the controller remains in a
                // safe idle state.
                var idleChannels = Enumerable.Range(1, 12)
                    .Where(channel => Cfg?.Test?.GetEpbRecord(channel)?.Enabled == true)
                    .ToArray();
                var idleStore = Cfg?.Test?.StoreDir;
                var idleName = Cfg?.Test?.TestName;
                if (!string.IsNullOrWhiteSpace(idleStore) && !string.IsNullOrWhiteSpace(idleName))
                    WatchdogRuntime.ConfigureJournalExportPath(
                        Path.Combine(idleStore, idleName, "WatchdogSessions"));
                // An idle restart is still a recovery of the existing
                // watchdog authority.  It must attach the frozen
                // session/pipe/PID/start/nonce identity and may never launch
                // a replacement Sidecar from this path.
                await WatchdogRuntime.AttachRecoverySessionAsync(
                        intent, idleChannels)
                    .ConfigureAwait(true);
                var idleAttachDecision = WinFormsWatchdogUiEntryPolicy
                    .EvaluateRecoveryIntent(intent, WatchdogRuntime.IsAttached);
                if (!idleAttachDecision.Allowed ||
                    !idleAttachDecision.AttachExistingAuthorityOnly)
                    throw new InvalidOperationException(
                        "Watchdog 空闲模式未完成 exact Attached；AttachOnly恢复被拒绝：" +
                        idleAttachDecision.Reason);
                var uiBinding = await monitor.BindWatchdogUiAfterAttachAsync()
                    .ConfigureAwait(true);
                if (uiBinding == null || !uiBinding.Accepted || !uiBinding.Ready)
                    throw new InvalidOperationException(
                        "Watchdog 空闲模式 UI 管线未完成绑定：" +
                        (uiBinding?.Reason ?? "Unknown"));
                var idleReadyDecision = WinFormsWatchdogUiEntryPolicy.Evaluate(
                    WinFormsWatchdogUiEntryKind.StartIdle,
                    WatchdogRuntime.IsAttached,
                    uiBinding.Accepted && uiBinding.Ready);
                if (!idleReadyDecision.Allowed ||
                    !idleReadyDecision.AttachExistingAuthorityOnly)
                    throw new InvalidOperationException(
                        "Watchdog 空闲模式UI入口未Ready：" + idleReadyDecision.Reason);
                await monitor.PrepareSafeIdleAfterWatchdogAsync(intent.SessionId, intent.PreviousPid)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                try
                {
                    // A failed attach/bind may leave a nonterminal retained
                    // context.  Ask the Main-owned retention path to close it
                    // safely; it keeps the message pump alive until terminal.
                    if (WatchdogRuntime.CaptureTransportSnapshot()?.Context != null)
                        RequestWatchdogOwnedExit(
                            "WatchdogIdleAttachRejected",
                            RuntimeShutdownIntent.WatchdogRecoveryExit);
                }
                catch { }
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "Watchdog 空闲重启安全预检失败；开始试验保持禁用：" + ex.Message,
                    "独立看门狗",
                    ex);
                ShowMainOperatorMessage(
                    "软件已因人工停止超时重新打开，但全断能预检未通过。\r\n" +
                    "开始试验已禁用，请检查电源/IO 后重启软件。\r\n" + ex.Message,
                    "安全空闲模式",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async Task ResumeWatchdogRunAsync(WatchdogRecoveryIntent intent)
        {
            FrmEpbMainMonitor monitor = null;
            var recoveryRunId = string.Empty;
            try
            {
                if (!UnattendedRunCheckpointStore.TryConsumeWatchdogRecovery(
                        intent.SessionId,
                        Cfg,
                        out var checkpoint,
                        out var error))
                {
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "独立看门狗恢复已拒绝：" + error + "；所有输出保持关闭。",
                        "独立看门狗");
                    try
                    {
                        await WatchdogRuntime.NotifyRecoveryCheckpointRejectedAsync(
                                intent,
                                error)
                            .ConfigureAwait(true);
                    }
                    catch (Exception notifyError)
                    {
                        ProjectLogHub.Write(
                            ProjectLogLevel.Error,
                            "恢复检查点拒绝回执发送失败：" + notifyError.GetBaseException().Message,
                            "独立看门狗",
                            notifyError);
                    }
                    RequestWatchdogOwnedExit(
                        "RecoveryCheckpointRejected",
                        RuntimeShutdownIntent.WatchdogRecoveryExit);
                    return;
                }
                recoveryRunId = checkpoint.RunId ?? string.Empty;

                // 恢复进程不会再次经过“点击开始”的 BatchGuard；必须在重新附着
                // 原 Watchdog Session 前恢复封存目录，保证多次接管后的 Journal
                // 仍落到当前项目数据目录，而不是只留在 LocalAppData。
                WatchdogRuntime.ConfigureJournalExportPath(
                    System.IO.Path.Combine(
                        checkpoint.StoreDir ?? string.Empty,
                        checkpoint.TestName ?? string.Empty,
                        "WatchdogSessions"));
                await WatchdogRuntime.AttachRecoverySessionAsync(
                    intent,
                    checkpoint.SelectedChannels).ConfigureAwait(true);
                var recoveryAttachDecision = WinFormsWatchdogUiEntryPolicy
                    .EvaluateRecoveryIntent(intent, WatchdogRuntime.IsAttached);
                if (!recoveryAttachDecision.Allowed ||
                    !recoveryAttachDecision.AttachExistingAuthorityOnly)
                    throw new InvalidOperationException(
                        "Watchdog恢复未完成 exact AttachOnly：" +
                        recoveryAttachDecision.Reason);
                WatchdogRuntime.NotifyRecoveryCheckpointValidated(
                    $"RunId={checkpoint.RunId};Revision={checkpoint.Revision};" +
                    $"Source={checkpoint.LastRecoveryLoadSource}",
                    new WatchdogCheckpointMirror
                    {
                        SchemaVersion = checkpoint.SchemaVersion,
                        Revision = checkpoint.Revision,
                        Armed = checkpoint.Armed,
                        RunId = checkpoint.RunId,
                        RunEpoch = checkpoint.RunEpoch,
                        SessionId = checkpoint.WatchdogSessionId,
                        StoreDir = checkpoint.StoreDir,
                        TestName = checkpoint.TestName,
                        SelectedChannels = checkpoint.SelectedChannels?.ToArray() ?? Array.Empty<int>(),
                        RemainingFormalCycles = checkpoint.RemainingFormalCycles == null
                            ? new System.Collections.Generic.Dictionary<string, int>()
                            : new System.Collections.Generic.Dictionary<string, int>(
                                checkpoint.RemainingFormalCycles),
                        SourcePath = checkpoint.LastRecoveryLoadSource,
                        Sha256 = checkpoint.LastRecoveryLoadSha256,
                        UpdatedUtc = checkpoint.UpdatedUtc
                    });
                monitor = new FrmEpbMainMonitor(ProtectedRoot(checkpoint)) { Name = "实时监视" };
                OpenChildForm(monitor);
                if (!await monitor.WaitUntilWatchdogControllerReadyAsync().ConfigureAwait(true))
                    throw new InvalidOperationException("恢复监视窗口未完成控制对象初始化。");
                var uiBinding = await monitor.BindWatchdogUiAfterAttachAsync()
                    .ConfigureAwait(true);
                if (uiBinding == null || !uiBinding.Accepted || !uiBinding.Ready)
                    throw new InvalidOperationException(
                        "恢复监视窗口未完成Watchdog UI管线绑定：" +
                        (uiBinding?.Reason ?? "Unknown"));
                var recoveryReadyDecision = WinFormsWatchdogUiEntryPolicy.Evaluate(
                    WinFormsWatchdogUiEntryKind.Recovery,
                    WatchdogRuntime.IsAttached,
                    uiBinding.Accepted && uiBinding.Ready);
                if (!recoveryReadyDecision.Allowed ||
                    !recoveryReadyDecision.AttachExistingAuthorityOnly)
                    throw new InvalidOperationException(
                        "Watchdog恢复UI入口未Ready：" + recoveryReadyDecision.Reason);
                var consecutiveHardwareFailures = 0;
                var previousFingerprint = string.Empty;
                while (!monitor.IsOperatorStopRequested)
                {
                    try
                    {
                        await monitor.ResumeFromWatchdogCheckpointAsync(checkpoint, intent)
                            .ConfigureAwait(true);
                        monitor.ClearHardwareUnavailable();
                        return;
                    }
                    catch (Exception rawHardwareError) when (
                        rawHardwareError is WatchdogHardwareUnavailableException ||
                        RecoveryFailurePolicy.IsHardwareOrDaqUnavailable(
                            rawHardwareError.GetBaseException().Message))
                    {
                        var hardwareError = rawHardwareError as WatchdogHardwareUnavailableException ??
                            new WatchdogHardwareUnavailableException(
                                new RecoveryFailureReport
                                {
                                    RootCode = rawHardwareError.GetBaseException().Message
                                        .IndexOf("Daq", StringComparison.OrdinalIgnoreCase) >= 0
                                        ? "DaqUnavailable"
                                        : "HardwareUnavailable",
                                    DeviceOrChannelGroup = "RecoveryPreflight",
                                    RunId = recoveryRunId,
                                    RecoveryStage = "ResumeCheckpoint",
                                    RecoveryProgressToken = "Preflight"
                                },
                                rawHardwareError.GetBaseException().Message);
                        consecutiveHardwareFailures = string.Equals(
                            previousFingerprint,
                            hardwareError.Fingerprint,
                            StringComparison.Ordinal)
                            ? consecutiveHardwareFailures + 1
                            : 1;
                        previousFingerprint = hardwareError.Fingerprint;
                        var delaySeconds = RecoveryFailurePolicy.SelectInProcessProbeDelaySeconds(
                            consecutiveHardwareFailures);
                        var nextProbeUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                        monitor.PublishHardwareUnavailable(
                            hardwareError.Fingerprint,
                            hardwareError.Detail,
                            consecutiveHardwareFailures,
                            nextProbeUtc);
                        ProjectLogHub.Write(
                            consecutiveHardwareFailures >= RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit
                                ? ProjectLogLevel.Error
                                : ProjectLogLevel.Warning,
                            $"Watchdog恢复进程原地等待硬件：Attempt={consecutiveHardwareFailures};" +
                            $"NextProbeUtc={nextProbeUtc:O};Fingerprint={hardwareError.Fingerprint};" +
                            "ProcessRelaunch=false;所有输出保持OFF。",
                            "独立看门狗",
                            hardwareError);
                        while (!monitor.IsOperatorStopRequested && DateTime.UtcNow < nextProbeUtc)
                            await Task.Delay(250).ConfigureAwait(true);
                    }
                }
            }
            catch (Exception ex)
            {
                var baseError = ex.GetBaseException();
                var supersededByTakeover = WatchdogRuntime.IsActiveTakeoverCancellation(baseError);
                var classification = RecoveryFailurePolicy.Classify(
                    supersededByTakeover ? "RecoverySupersededByTakeover" : null,
                    false,
                    baseError.ToString());
                var testConfigPath = ConfigLoader.GetProjectTestConfigPath(
                    Cfg?.Test?.StoreDir,
                    Cfg?.Test?.TestName);
                var testConfigSha256 = Controller.RuntimeBuildIdentity.ComputeFileSha256(
                    testConfigPath);
                ProjectLogHub.Write(
                    supersededByTakeover
                        ? ProjectLogLevel.Warning
                        : ProjectLogLevel.Error,
                    supersededByTakeover
                        ? "本次恢复已被同一 Watchdog 安全接管请求取代；该取消不登记为新的启动失败：" + ex.Message
                        : "独立看门狗恢复进程安全接管或启动失败；保持本轮授权，等待 Watchdog 退避重试：" + ex.Message,
                    "独立看门狗",
                    ex);
                monitor?.PrepareForWatchdogRetryExit();
                var handoff = await WatchdogRuntime.NotifyRecoveryAttemptFailedAndAwaitReceiptAsync(
                    classification.Code,
                    classification.Permanent,
                    "WatchdogRecoveryStartupFailed:" + baseError.Message,
                    ex.ToString(),
                    testConfigSha256).ConfigureAwait(true);
                if (handoff?.WatchdogOwnsExit == true)
                {
                    RequestWatchdogOwnedExit(
                        "RecoveryStartupFailed:" + handoff.Outcome,
                        RuntimeShutdownIntent.WatchdogRecoveryExit);
                }
                else
                {
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "恢复失败未取得 Watchdog 权威交接回执；主程序保持安全空闲且不退出。" +
                        $"Outcome={handoff?.Outcome} Detail={handoff?.Detail}",
                        "独立看门狗");
                    ShowMainOperatorMessage(
                        "恢复失败，但未取得看门狗的权威退出回执。\r\n" +
                        "程序将保持安全空闲，不会自行退出；请保留日志并人工检查。\r\n" +
                        (handoff?.Detail ?? "回执不可用"),
                        "恢复交接未完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private async Task PrepareGracefulPauseResumeAsync()
        {
            try
            {
                if (!UnattendedRunCheckpointStore.TryLoadGracefulPause(
                        Cfg,
                        out var checkpoint,
                        out _))
                    return;

                var monitor = new FrmEpbMainMonitor(ProtectedRoot(checkpoint)) { Name = "实时监视" };
                OpenChildForm(monitor);
                await monitor.PrepareGracefulPauseCheckpointAsync(checkpoint);
            }
            catch (Exception ex)
            {
                UnattendedRunCheckpointStore.ClearGracefulPause("GracefulPauseLoadFailed");
                ShowMainOperatorMessage(
                    "正常暂停检查点加载失败：" + ex.Message +
                    "\r\n检查点已撤销，下次开始将执行完整学习。",
                    "暂停恢复",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private async Task ResumeUnattendedRunAsync(RecoveryStartupIntent intent)
        {
            try
            {
                if (!UnattendedRunCheckpointStore.TryConsume(
                        intent,
                        Cfg,
                        out var checkpoint,
                        out var error))
                {
                    // 未授权、过期或身份不一致的恢复参数只能保持全断能，不能弹出一个
                    // 必须由现场人员确认的模态框。该次十万圈由现场门禁判失败。
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "无人值守自动续测已拒绝：" + error + "；所有输出保持关闭。",
                        "无人值守恢复");
                    return;
                }

                var monitor = new FrmEpbMainMonitor(ProtectedRoot(checkpoint)) { Name = "实时监视" };
                OpenChildForm(monitor);
                await monitor.ResumeFromUnattendedCheckpointAsync(checkpoint);
            }
            catch (Exception ex)
            {
                // nonce 已消费后的初始化/启动失败仍属于同一授权恢复链。保留检查点、
                // RootRunId 和重启历史，按既有三次预算再做受控进程交接；预算耗尽后
                // RestartAsync 会保持安全停机。禁止以消息框代替无人值守恢复。
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "无人值守恢复预检或批量启动失败，将进入有界进程重试：" + ex.Message,
                    "无人值守恢复",
                    ex);
                UnattendedRecoveryCoordinator.RequestRecoveryStartupRestart(
                    "RecoveryStartupFailed",
                    ex);
            }
        }
    }
}
