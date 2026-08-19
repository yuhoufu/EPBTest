using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using Controller.Adaptive;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class StopWatchdogHardeningTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("Watchdog v2与v1心跳向后兼容", ProtocolV2IsAdditive, ref passed);
            Run("恢复过渡窗显示详细倒计时且仅在稳定态隐藏", RecoveryTransitionPresentationIsDeterministic, ref passed);
            Run("ManualStopIntent保留Kill权限", ManualStopIntentKeepsAuthority, ref passed);
            Run("启动失败和恢复失败不得伪装会话终止", RetryableFailuresAreNotTerminal, ref passed);
            Run("Stop阶段五秒无进展必须接管", StopStageStallTriggersTakeover, ref passed);
            Run("无RunId但逻辑对象残留必须接管", LogicalResidueTriggersTakeover, ref passed);
            Run("恢复对象与RecoveryActive矛盾必须接管", RecoveryEvidenceMismatchTriggersTakeover, ref passed);
            Run("15秒周期逐通道60秒无进展必须接管", FormalProgressStallTriggersTakeover, ref passed);
            Run("DO与峰值刷新不得掩盖机械圈60秒停滞", DiagnosticProgressCannotMaskMechanicalStall, ref passed);
            Run("人工暂停不得被恢复计数误判接管", ManualPauseSuppressesRecoveryInference, ref passed);
            Run("人工暂停按动态硬截止与进展判定接管", ManualPauseUsesDynamicDeadlineAndProgress, ref passed);
            Run("恢复失败分类有界且RecoveryBlocked禁止再启动", RecoveryFailureCircuitBreakerIsBounded, ref passed);
            Run("健康活动圈假Recovery声明不得触发全局StopAll",
                HealthyCycleRecoveryClaimIsSuppressed, ref passed);
            Run("无人工owner的Paused通道五秒触发不变量接管",
                PausedWithoutManualOwnerTriggersTakeover, ref passed);
            Run("检查点损坏时按分类回退到最近有效副本", DurableCheckpointFallsBackWithClassification, ref passed);
            Run("检查点并发轮询与原子替换无共享冲突", DurableCheckpointConcurrentReadWriteIsShareSafe, ref passed);
            Run("过渡窗公开人工停止按钮语义", TransitionWindowExposesOperatorStop, ref passed);
            Run("逐通道心跳字段可往返", PerChannelProgressIsSerializable, ref passed);
            Run("墙钟前后跳变均可检测", WallClockStepsAreDetected, ref passed);
            Run("机械圈与正式证据圈独立计数", MechanicalCyclesAreIndependent, ref passed);
            Run("逻辑静止必须包含无上电通道", LogicalQuiescenceIncludesEnergization, ref passed);
            Run("停止超时结果永久禁止同进程重启", TimedOutStopCannotRestartInProcess, ref passed);
            return passed;
        }

        private static void ProtocolV2IsAdditive()
        {
            var message = new WatchdogMessage
            {
                Type = WatchdogMessageType.Heartbeat,
                SessionId = "v2",
                Heartbeat = new WatchdogHeartbeat
                {
                    StopAllActive = true,
                    StopStage = "ClearRecoveryOwners",
                    StopProgressVersion = 7,
                    ManualPauseStage = "CurrentCycleDrain",
                    ManualPauseProgressVersion = 9,
                    ManualPauseHardDeadlineUtc = 123456,
                    ManualPauseEnergizedChannels = new[] { 4, 5 },
                    HardwareUnavailable = true,
                    HardwareFailureFingerprint = "PowerSafetyProbe|Group1|IDN",
                    ActiveCycleCount = 5,
                    TimerCount = 10,
                    RunnerCount = 10
                }
            };
            var roundTrip = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(message));
            Assert(roundTrip.ProtocolVersion == 2 && roundTrip.Heartbeat.StopAllActive &&
                   roundTrip.Heartbeat.StopProgressVersion == 7 &&
                   roundTrip.Heartbeat.ManualPauseProgressVersion == 9 &&
                   roundTrip.Heartbeat.ManualPauseEnergizedChannels.Length == 2 &&
                   roundTrip.Heartbeat.HardwareUnavailable &&
                   roundTrip.Heartbeat.HardwareFailureFingerprint.Contains("Group1") &&
                   roundTrip.Heartbeat.TimerCount == 10 &&
                   roundTrip.Heartbeat.ActiveCycleCount == 5,
                "v2停止字段未能序列化往返");

            var v1 = WatchdogProtocol.Deserialize(
                "{\"ProtocolVersion\":1,\"Type\":\"Heartbeat\",\"SessionId\":\"v1\"," +
                "\"Heartbeat\":{\"Sequence\":3,\"RunActive\":false}}");
            Assert(v1.ProtocolVersion == 1 && v1.Heartbeat.Sequence == 3 &&
                   !v1.Heartbeat.StopAllActive,
                "v1心跳不能由v2协议模型兼容反序列化");

            var uiReady = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                new WatchdogMessage
                {
                    Type = WatchdogMessageType.MainUiReady,
                    SessionId = "recovery",
                    Reason = "MainWindowShown"
                }));
            Assert(uiReady.Type == WatchdogMessageType.MainUiReady &&
                   uiReady.Reason == "MainWindowShown",
                "恢复主界面就绪消息不能序列化往返");
        }

        private static void RecoveryTransitionPresentationIsDeterministic()
        {
            var countdown = RecoveryTransitionPolicy.FormatCountdown(60);
            Assert(countdown.Contains("01:00") && countdown.Contains("剩余 60 秒"),
                "60秒退避未格式化为可核对的详细倒计时");
            Assert(!RecoveryTransitionPolicy.ShouldHide(new WatchdogHeartbeat
                   {
                       RunActive = true,
                       Phase = "ManualPausePending"
                   }) &&
                   RecoveryTransitionPolicy.ShouldHide(new WatchdogHeartbeat
                   {
                       RunActive = true,
                       Phase = "ManualPaused"
                   }) &&
                   RecoveryTransitionPolicy.ShouldHide(new WatchdogHeartbeat
                   {
                       RunActive = true,
                       Phase = "Formal"
                   }),
                "过渡窗在未稳定附着时提前隐藏，或在稳定运行后没有隐藏");
        }

        private static void ManualStopIntentKeepsAuthority()
        {
            Assert(!SessionRevocationPolicy.IsRevoked(false, true, false),
                "ManualStopIntent被错误当作Session撤权");
            Assert(WatchdogProcessIdentityPolicy.CanKillOldProcess(false, true, true),
                "人工停止15秒超时后不能终止身份匹配的旧进程");
        }

        private static void RetryableFailuresAreNotTerminal()
        {
            Assert(WatchdogLifecyclePolicy.IsRetryableFailure(WatchdogMessageType.BatchStartFailed) &&
                   WatchdogLifecyclePolicy.IsRetryableFailure(WatchdogMessageType.RecoveryAttemptFailed),
                "可重试失败未进入显式重试协议");
            Assert(!WatchdogLifecyclePolicy.IsTerminalMessage(WatchdogMessageType.BatchStartFailed) &&
                   !WatchdogLifecyclePolicy.IsTerminalMessage(WatchdogMessageType.RecoveryAttemptFailed) &&
                   WatchdogLifecyclePolicy.IsTerminalMessage(WatchdogMessageType.ApplicationClosing),
                "可重试失败被错误归类为ApplicationClosing/RunStopped终态");
        }

        private static void StopStageStallTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, true, false, true, 1, false, false, false, 5, false,
                    stopAllActive: true),
                "Stop阶段五秒无进展未触发接管");
        }

        private static void LogicalResidueTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, false, false, false, 5, false,
                    logicalResidue: true),
                "RunActive=false且Timer/Runner/CTS残留五秒未触发接管");
        }

        private static void RecoveryEvidenceMismatchTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, false, false, false, 0, false,
                    inconsistentRecoveryEvidence: true),
                "恢复对象存在但RecoveryActive=false未触发接管");
        }

        private static void FormalProgressStallTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.SelectFormalProgressTimeoutSeconds(15000) == 60 &&
                   WatchdogTakeoverPolicy.SelectFormalProgressTimeoutSeconds(60000) == 180,
                "逐通道进展超时未按三个配置周期并保留60秒下限计算");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, false, false, false, 90, true,
                    formalProgressStalled: true),
                "正式圈计数停滞且仍有物理动作时未触发接管");
        }

        private static void DiagnosticProgressCannotMaskMechanicalStall()
        {
            const long frequency = 1000;
            const long started = 100000;
            var tracker = new WatchdogChannelProgressTracker();
            var progress = new WatchdogChannelProgress
            {
                Channel = 4,
                State = "Running",
                TimerActive = true,
                RunnerActive = true,
                MechanicalCompletedCount = 10,
                LastMechanicalCompletedUtcTicks = 1234,
                DoCommandSequence = 100,
                PeakCutoffGeneration = 7,
                PeakCutoffSequence = 500
            };
            var heartbeat = new WatchdogHeartbeat
            {
                RunActive = true,
                ExpectedCyclePeriodMs = 15000,
                ChannelProgress = new[] { progress }
            };
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null,
                "逐通道监督初次观察即错误接管");

            progress.DoCommandSequence = 999;
            progress.PeakCutoffSequence = 888;
            progress.StateRevision = 50;
            Assert(tracker.Evaluate(
                       heartbeat,
                       new[] { 4 },
                       false,
                       started + 59999,
                       frequency) == null,
                "机械无进展尚不足60秒时错误接管");
            var stalled = tracker.Evaluate(
                heartbeat,
                new[] { 4 },
                false,
                started + 60000,
                frequency);
            Assert(stalled != null && stalled.Contains("ChannelProgressStalled:EPB=4") &&
                   stalled.Contains("DO=999") && stalled.Contains("Peak=888"),
                "DO、峰值或状态持续刷新掩盖了机械完成圈停滞");

            tracker.Reset();
            progress.State = "Recovering";
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null,
                "系统恢复态首次观察即错误接管");
            progress.StateRevision = 999;
            progress.DoCommandSequence = 2000;
            Assert(tracker.Evaluate(
                       heartbeat,
                       new[] { 4 },
                       false,
                       started + 60000,
                       frequency)?.Contains("ChannelProgressStalled:EPB=4") == true,
                "系统自恢复标签与持续DO刷新仍掩盖机械圈停滞");

            progress.MechanicalCompletedCount++;
            progress.LastMechanicalCompletedUtcTicks++;
            Assert(tracker.Evaluate(
                       heartbeat,
                       new[] { 4 },
                       false,
                       started + 60001,
                       frequency) == null,
                "真实机械完成后未重置逐通道监督期限");
        }

        private static void ManualPauseSuppressesRecoveryInference()
        {
            Assert(WatchdogTakeoverPolicy.IsManualPauseCommanded(false, true) &&
                   WatchdogTakeoverPolicy.IsManualPauseCommanded(true, false) &&
                   !WatchdogTakeoverPolicy.IsManualPauseCommanded(false, false),
                "PausePending/Paused未统一进入人工暂停指令保护态");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, true, true, true, 999, true,
                    logicalResidue: true,
                    inconsistentRecoveryEvidence: true,
                    formalProgressStalled: true,
                    manualPauseActive: true),
                "健康人工暂停仍被恢复对象、旧时间戳或正式进展误判接管");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, false, false, false, 5, true,
                    manualPauseActive: true,
                    manualPauseUnsafe: true),
                "人工暂停超过安全期限仍上电时未触发安全空闲接管");
        }

        private static void ManualPauseUsesDynamicDeadlineAndProgress()
        {
            var deadlineMs = ManualPauseSafetyPolicy.SelectHardDeadlineMilliseconds(15000);
            Assert(deadlineMs == 60000 &&
                   deadlineMs == EpbManager.SelectManualPauseHardDeadlineMilliseconds(15000),
                "15秒周期没有统一计算为60秒人工暂停硬截止");
            const long deadline = 1000000;
            Assert(!ManualPauseSafetyPolicy.ShouldTakeover(
                    manualPausePending: true,
                    manualPauseActive: false,
                    controllerSafetyFault: false,
                    energizedChannelCount: 3,
                    hardDeadlineUtcTicks: deadline,
                    nowUtcTicks: deadline - 1,
                    noProgressSeconds: 100),
                "动态硬截止前仍在正常收尾时错误接管");
            Assert(!ManualPauseSafetyPolicy.ShouldTakeover(
                    true, false, false, 3, deadline, deadline + 1, 4.9),
                "硬截止后带电集合仍有进展时错误接管");
            Assert(ManualPauseSafetyPolicy.ShouldTakeover(
                    true, false, false, 3, deadline, deadline + 1, 5),
                "硬截止后带电集合持续无进展未接管");
            Assert(!ManualPauseSafetyPolicy.ShouldTakeover(
                    true, false, false, 0, deadline, deadline + 1, 100),
                "已全部断能但仍在持久化时错误接管");
            Assert(ManualPauseSafetyPolicy.ShouldTakeover(
                    true, false, true, 0, deadline, deadline - 1, 0),
                "控制器显式发布暂停安全故障后未立即接管");
            Assert(ManualPauseSafetyPolicy.ShouldTakeover(
                    false, true, false, 1, deadline, deadline - 1, 5),
                "已承诺Paused却仍带电时未执行安全接管");
        }

        private static void RecoveryFailureCircuitBreakerIsBounded()
        {
            var breaker = new RecoveryFailureCircuitBreaker();
            var first = breaker.Observe("PowerOffUnconfirmed:*IDN? response timeout");
            var second = breaker.Observe("PowerOffUnconfirmed:*IDN? response timeout");
            var third = breaker.Observe("PowerOffUnconfirmed:*IDN? response timeout");
            var fourth = breaker.Observe("PowerOffUnconfirmed:*IDN? response timeout");
            var fifth = breaker.Observe("PowerOffUnconfirmed:*IDN? response timeout");
            Assert(first.ProcessRelaunchAllowed && second.ProcessRelaunchAllowed &&
                   third.ProcessRelaunchAllowed && fourth.ProcessRelaunchAllowed &&
                   !fifth.ProcessRelaunchAllowed && fifth.ConsecutiveCount == 5,
                "同一恢复失败指纹没有在第五次熔断快速进程重拉");
            var changed = breaker.Observe("RecoveryAttachFailed");
            Assert(changed.ProcessRelaunchAllowed && changed.ConsecutiveCount == 1,
                "故障指纹变化后连续计数没有重置");
            Assert(RecoveryFailurePolicy.SelectInProcessProbeDelaySeconds(1) == 5 &&
                   RecoveryFailurePolicy.SelectInProcessProbeDelaySeconds(2) == 15 &&
                   RecoveryFailurePolicy.SelectInProcessProbeDelaySeconds(3) == 30 &&
                   RecoveryFailurePolicy.SelectInProcessProbeDelaySeconds(4) == 60 &&
                   RecoveryFailurePolicy.SelectInProcessProbeDelaySeconds(99) == 60,
                "原进程硬件探测退避不是5/15/30/60秒上限");
            var duplicate = RecoveryFailurePolicy.Classify(
                "ConfigDuplicateEpbId",
                true,
                "已添加了具有相同键的项");
            var transient = RecoveryFailurePolicy.Classify(
                null,
                false,
                "PowerSupply timeout");
            var unknown = RecoveryFailurePolicy.Classify(
                null,
                false,
                "NullReferenceException during startup");
            Assert(duplicate.Permanent && duplicate.MaximumProcessRelaunches == 0 &&
                   !transient.Permanent && transient.MaximumProcessRelaunches == 5 &&
                   !unknown.Permanent && unknown.MaximumProcessRelaunches == 2,
                "永久/瞬态/未知启动失败没有进入0/5/2次结构化预算");
            Assert(!RecoveryFailurePolicy.CanLaunchMainProcess(recoveryBlocked: true) &&
                   RecoveryFailurePolicy.CanLaunchMainProcess(recoveryBlocked: false) &&
                   !RecoveryFailurePolicy.AllowsMainProcessCircuitProbe,
                "RecoveryBlocked或CircuitProbe仍允许启动完整主程序");
        }

        private static void HealthyCycleRecoveryClaimIsSuppressed()
        {
            var heartbeat = new WatchdogHeartbeat
            {
                RunActive = true,
                Phase = "Formal",
                RecoveryActive = true,
                SoftwareRecoveryCount = 5,
                ActiveCycleCount = 5,
                RecoveryOwnerCount = 0,
                DaqRecoveryCount = 0,
                ChannelProgress = new[]
                {
                    new WatchdogChannelProgress
                    {
                        Channel = 4,
                        State = "Running",
                        TimerActive = true,
                        RunnerActive = true
                    }
                }
            };
            Assert(WatchdogRecoveryTelemetryPolicy.IsUnstructuredRecoveryClaim(heartbeat) &&
                   !WatchdogRecoveryTelemetryPolicy.ShouldTreatAsRecoveryActive(heartbeat),
                "健康运行仅因SoftwareRecoveryCount=ActiveCycleCount仍被当作恢复阶段");
            Assert(!WatchdogRecoveryTelemetryPolicy.ShouldPublishRecoveryActive(
                       false, false, 0, 5, 0, false, false) &&
                   WatchdogRecoveryTelemetryPolicy.ShouldPublishRecoveryActive(
                       false, true, 0, 1, 1, true, true),
                "心跳生产端仍允许无owner/事故/阶段的软件计数创建RecoveryActive");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1,
                       WatchdogRecoveryTelemetryPolicy.ShouldTreatAsRecoveryActive(heartbeat),
                       false, false, 600, true,
                       recoveryNoProgressSeconds: 600),
                "无结构恢复身份且机械资源健康时仍会触发ExternalRecoveryStageStalled/StopAll");
        }

        private static void PausedWithoutManualOwnerTriggersTakeover()
        {
            const long frequency = 1000;
            const long started = 10000;
            var tracker = new WatchdogChannelProgressTracker();
            var heartbeat = new WatchdogHeartbeat
            {
                RunActive = true,
                ChannelProgress = new[]
                {
                    new WatchdogChannelProgress
                    {
                        Channel = 4,
                        State = "Paused",
                        TimerActive = false,
                        RunnerActive = false
                    }
                }
            };
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null,
                "孤儿Paused首次观察即接管，未保留五秒去抖");
            var result = tracker.Evaluate(
                heartbeat,
                new[] { 4 },
                false,
                started + 5000,
                frequency);
            Assert(result?.Contains("ChannelPausedWithoutManualOwner:EPB=4") == true,
                "无ManualPause owner且Timer/Runner缺失的Paused通道五秒后仍未被发现");
            Assert(tracker.Evaluate(
                       heartbeat,
                       new[] { 4 },
                       true,
                       started + 10000,
                       frequency) == null,
                "真实人工暂停被孤儿暂停监督误接管");
        }

        private static void DurableCheckpointFallsBackWithClassification()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest-DurableCheckpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var primary = Path.Combine(directory, "checkpoint.json");
                var mirror = Path.Combine(directory, "mirror.json");
                DurableJsonFileStore.WriteAtomicWithBackup(
                    primary,
                    Encoding.UTF8.GetBytes("{\"Name\":\"v1\"}"));
                DurableJsonFileStore.WriteAtomicWithBackup(
                    primary,
                    Encoding.UTF8.GetBytes("{\"Name\":\"v2\"}"));
                File.WriteAllText(primary, "{broken", new UTF8Encoding(false));
                var backup = DurableJsonFileStore.ReadFirstValid<CheckpointProbe>(
                    primary,
                    primary + ".bak");
                Assert(backup.Value?.Name == "v1" &&
                       backup.SourcePath == primary + ".bak" &&
                       backup.Attempts[0].Status == DurableJsonReadStatus.JsonInvalid,
                    "主检查点损坏后没有回退到最近有效 .bak，或没有标记 JsonInvalid");

                DurableJsonFileStore.WriteAtomicWithBackup(
                    mirror,
                    Encoding.UTF8.GetBytes("{\"Name\":\"project\"}"));
                var project = DurableJsonFileStore.ReadFirstValid<CheckpointProbe>(
                    Path.Combine(directory, "missing-primary.json"),
                    Path.Combine(directory, "missing-primary.json.bak"),
                    mirror);
                Assert(project.Value?.Name == "project" && project.SourcePath == mirror &&
                       project.Attempts[0].Status == DurableJsonReadStatus.Missing,
                    "本地双副本缺失后没有回退到项目镜像，或没有标记 Missing");

                DurableJsonFileStore.WriteAtomicWithBackup(
                    mirror,
                    Encoding.UTF8.GetBytes(
                        "{\"Name\":\"project-disarmed\",\"Revision\":2,\"Armed\":false}"));
                var latestAuthorization = DurableJsonFileStore.ReadLatestValid<CheckpointProbe>(
                    checkpoint => checkpoint.Revision,
                    primary,
                    primary + ".bak",
                    mirror,
                    mirror + ".bak");
                Assert(latestAuthorization.Value?.Name == "project-disarmed" &&
                       latestAuthorization.Value.Armed == false &&
                       latestAuthorization.SourcePath == mirror,
                    "主副本损坏后错误采用旧 Armed 备份，覆盖了较新项目 Disarm 镜像");
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private sealed class CheckpointProbe
        {
            public string Name { get; set; }
            public long Revision { get; set; }
            public bool Armed { get; set; }
        }

        private static void DurableCheckpointConcurrentReadWriteIsShareSafe()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest-DurableCheckpoint-Concurrent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "checkpoint.json");
                DurableJsonFileStore.WriteAtomicWithBackup(
                    path,
                    Encoding.UTF8.GetBytes("{\"Name\":\"v0\",\"Revision\":0}"));
                var writer = Task.Run(() =>
                {
                    for (var revision = 1; revision <= 200; revision++)
                    {
                        DurableJsonFileStore.WriteAtomicWithBackup(
                            path,
                            Encoding.UTF8.GetBytes(
                                $"{{\"Name\":\"v{revision}\",\"Revision\":{revision}}}"));
                    }
                });

                var invalidReads = 0;
                var readCount = 0;
                while (!writer.IsCompleted || readCount < 200)
                {
                    var read = DurableJsonFileStore.ReadLatestValid<CheckpointProbe>(
                        value => value.Revision,
                        path,
                        path + ".bak");
                    if (read.Value == null || read.Status != DurableJsonReadStatus.Valid)
                        Interlocked.Increment(ref invalidReads);
                    readCount++;
                }
                writer.GetAwaiter().GetResult();
                var final = DurableJsonFileStore.ReadLatestValid<CheckpointProbe>(
                    value => value.Revision,
                    path,
                    path + ".bak");
                Assert(invalidReads == 0 && final.Value?.Revision == 200,
                    $"并发读写出现{invalidReads}次共享冲突/无有效副本，或最终版本错误");
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void TransitionWindowExposesOperatorStop()
        {
            Assert(RecoveryTransitionPolicy.OperatorStopButtonText.Contains("停止自动恢复") &&
                   RecoveryTransitionPolicy.OperatorStopButtonText.Contains("关闭"),
                "过渡窗没有公开清晰的人工停止并关闭语义");
            Assert(
                RecoveryTransitionPolicy.MustSuppressAutomaticRestart(
                    transitionOperatorStopStarted: true,
                    sessionRevoked: false),
                "弹窗人工停止一旦开始，任何接管任务都必须禁止再次自动启动进程");
            Assert(
                RecoveryTransitionPolicy.MustSuppressAutomaticRestart(
                    transitionOperatorStopStarted: false,
                    sessionRevoked: true),
                "会话撤销后必须禁止再次自动启动进程");
            Assert(
                !RecoveryTransitionPolicy.MustSuppressAutomaticRestart(
                    transitionOperatorStopStarted: false,
                    sessionRevoked: false),
                "未人工停止且会话有效时不应误伤正常安全恢复");
        }

        private static void PerChannelProgressIsSerializable()
        {
            var heartbeat = new WatchdogHeartbeat
            {
                ManualPausePending = true,
                ChannelProgress = new[]
                {
                    new WatchdogChannelProgress
                    {
                        Channel = 4,
                        MechanicalCompletedCount = 123,
                        ConsecutiveSoftwareAbortCount = 2,
                        DoCommandSequence = 456,
                        PeakCutoffGeneration = 12,
                        PeakCutoffSequence = 455
                    }
                }
            };
            var message = new WatchdogMessage
            {
                Type = WatchdogMessageType.Heartbeat,
                SessionId = "channel-progress",
                RecoveryFailureCode = "ConfigDuplicateEpbId",
                RecoveryFailurePermanent = true,
                RecoveryFailureContextSha256 = "abc123",
                Heartbeat = heartbeat
            };
            var roundTrip = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(message));
            Assert(roundTrip.Heartbeat.ManualPausePending &&
                   roundTrip.Heartbeat.ChannelProgress.Length == 1 &&
                   roundTrip.Heartbeat.ChannelProgress[0].MechanicalCompletedCount == 123 &&
                   roundTrip.Heartbeat.ChannelProgress[0].PeakCutoffGeneration == 12 &&
                   roundTrip.Heartbeat.ChannelProgress[0].PeakCutoffSequence == 455 &&
                   roundTrip.RecoveryFailureCode == "ConfigDuplicateEpbId" &&
                   roundTrip.RecoveryFailurePermanent &&
                   roundTrip.RecoveryFailureContextSha256 == "abc123",
                "逐通道监督字段未能序列化往返");
        }

        private static void WallClockStepsAreDetected()
        {
            var utc = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);
            var forward = new WallClockStepDetector(500);
            Assert(forward.Observe(utc, 1000, 1000) == null, "首样本不应报告跳变");
            var forwardStep = forward.Observe(utc.AddMilliseconds(1600), 1100, 1000);
            Assert(forwardStep != null && forwardStep.Direction == "Forward" &&
                   Math.Abs(forwardStep.StepMilliseconds - 1500) < 0.001,
                "+1.5秒墙钟跳变未被检测");

            var backward = new WallClockStepDetector(500);
            backward.Observe(utc, 2000, 1000);
            var backwardStep = backward.Observe(utc.AddMilliseconds(-1400), 2100, 1000);
            Assert(backwardStep != null && backwardStep.Direction == "Backward" &&
                   Math.Abs(backwardStep.StepMilliseconds + 1500) < 0.001,
                "-1.5秒墙钟跳变未被检测");
        }

        private static void MechanicalCyclesAreIndependent()
        {
            var record = Config.EpbTestRecord.CreateDefault(4, 100000);
            record.RunCount = 10;
            var evidenceAborted = EpbCycleOutcome.SoftwareRecovery(
                EpbCurrentStage.Released,
                "PeakCaptureInvalid");
            Assert(evidenceAborted.MechanicalCycleCompleted && !evidenceAborted.IsSuccess,
                "已完成机械释放的证据作废圈未进入机械计数口径");
            Assert(record.IncrementMechanicalCycle() == 11 && record.RunCount == 10,
                "机械圈计数错误修改了正式证据圈口径");
            record.IncrementCycleAndUpdateTime(DateTime.Now);
            Assert(record.RunCount == 11 && record.MechanicalCycleCount == 11,
                "正式圈提交后双口径未保持单调一致");

            var reordered = Config.EpbTestRecord.CreateDefault(4, 100000);
            reordered.RunCount = 10;
            reordered.MechanicalCycleCount = 10;
            // Simulate the formal callback reaching the UI queue before the
            // independently marshalled mechanical callback.
            reordered.IncrementCycleAndUpdateTime(DateTime.Now);
            reordered.ReconcileMechanicalCycleCount(11);
            reordered.ReconcileMechanicalCycleCount(11);
            Assert(reordered.RunCount == 11 && reordered.MechanicalCycleCount == 11,
                "机械/正式回调乱序或重复观察导致机械圈双计数");
        }

        private static void LogicalQuiescenceIncludesEnergization()
        {
            var snapshot = new LogicalQuiescenceSnapshot { EnergizedChannelCount = 1 };
            Assert(!snapshot.IsQuiescent, "仍有上电通道时逻辑静止被误判成立");
            snapshot.EnergizedChannelCount = 0;
            Assert(snapshot.IsQuiescent, "运行资源全部清空后逻辑静止未成立");
        }

        private static void TimedOutStopCannotRestartInProcess()
        {
            var result = new StopSafetyResult
            {
                Outcome = StopSafetyOutcome.SafeButRestartRequired,
                TimedOut = true,
                RequiresProcessRestart = true,
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = true,
                PersistenceBoundaryConfirmed = true,
                LogicalQuiescenceConfirmed = true
            };
            Assert(!result.CanRestartInProcess && result.CanReleaseAcquisition,
                "停止超时结果仍允许同进程开始，或丢失已确认物理安全证据");
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
