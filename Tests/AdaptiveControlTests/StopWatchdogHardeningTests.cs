using System;
using System.IO;
using System.Linq;
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
            Run("Watchdog v4心跳字段与旧载荷兼容", ProtocolV2IsAdditive, ref passed);
            Run("Stop阶段期限与材料进展宽限独立判定", StopStageDeadlineAndMaterialGrace, ref passed);
            Run("Stop液压释放10.441/15秒不提前接管", StopHydraulicReleaseDeadlineIsIndependent, ref passed);
            Run("Stop总45秒期限最终兜底", StopTotalHardDeadlineIsIndependent, ref passed);
            Run("Stop材料证据仅接受单调版本", StopMaterialEvidenceMustBeMonotonic, ref passed);
            Run("Stop阶段顺序包含液压释放后DAQ停止", StopStageOrderIsMonotonic, ref passed);
            Run("StopAll活动期间逻辑残留不得发起第二个安全事务",
                StopAllOwnsResponsiveControlRepair, ref passed);
            Run("窗口响应但控制修复未确认时五秒后自动接管",
                ResponsiveControlRepairHasBoundedDeadline, ref passed);
            Run("新会话只清退同产品且旧主进程已退出的孤儿看门狗",
                StaleSidecarCleanupIsIdentityBound, ref passed);
            Run("同一运行多通道形式槽失败只建立一个根停止事务",
                FormalSlotFailuresCoalesceByRun, ref passed);
            Run("恢复过渡窗显示详细倒计时且仅在稳定态隐藏", RecoveryTransitionPresentationIsDeterministic, ref passed);
            Run("ManualStopIntent保留Kill权限", ManualStopIntentKeepsAuthority, ref passed);
            Run("启动失败和恢复失败不得伪装会话终止", RetryableFailuresAreNotTerminal, ref passed);
            Run("未武装的新试验启动失败禁止Watchdog杀进程", UnarmedBatchStartFailureStaysSafeIdle, ref passed);
            Run("Stop阶段五秒无进展必须接管", StopStageStallTriggersTakeover, ref passed);
            Run("无RunId但逻辑对象残留必须接管", LogicalResidueTriggersTakeover, ref passed);
            Run("恢复对象与RecoveryActive矛盾必须接管", RecoveryEvidenceMismatchTriggersTakeover, ref passed);
            Run("15秒周期逐通道60秒无进展必须接管", FormalProgressStallTriggersTakeover, ref passed);
            Run("DO与峰值刷新不得掩盖机械圈60秒停滞", DiagnosticProgressCannotMaskMechanicalStall, ref passed);
            Run("Learning与Qualification只要求Runner且仍监督机械进展",
                PhaseAwareRuntimeContractPreventsLearningFalseTakeover, ref passed);
            Run("资源缺失先请求结构化刷新再按同一签名接管",
                StructuredRefreshPrecedesResourceTakeover, ref passed);
            Run("逻辑源过期先刷新且禁止伪机械停滞接管",
                StaleLogicalSourceCannotMasqueradeAsMechanicalStall, ref passed);
            Run("真实机械停滞不被逻辑源持续刷新掩盖",
                FreshLogicalSourceStillDetectsMechanicalStall, ref passed);
            Run("进程与附着身份切换重置逐通道监督期限",
                ProcessIdentityChangeResetsChannelDeadline, ref passed);
            Run("逻辑源提交版本独立于总聚合版本",
                LogicalSourceCommitIsVersionedIndependently, ref passed);
            Run("SafeIdle取消与终止授权一万次交错保持线性化",
                SafeIdleCancellationIsLinearizable, ref passed);
            Run("自动接管必须先取得可消费许可再越过kill边界",
                RelaunchPermitPrecedesTerminationBoundary, ref passed);
            Run("运行资源不变量按Run与契约签名独立去抖",
                RuntimeInvariantTimingUsesRunAndContractIdentity, ref passed);
            Run("恢复态使用owner与成对资源契约而非正式Timer契约",
                RecoveryAndTerminalContractsArePhaseAware, ref passed);
            Run("Watchdog主动取消不得计为新的恢复启动失败",
                WatchdogTakeoverCancellationIsSuperseded, ref passed);
            Run("五通道百轮学习资格正式停止不泄漏监督状态",
                HundredLifecycleTransitionsDoNotLeakSupervisionState, ref passed);
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
            Run("双端Watchdog发送使用统一有界截止", WatchdogTransportWritesAreBounded, ref passed);
            Run("Timer与Runner活动缓存引用按通道去重", RuntimeResourceCountsAreDistinct, ref passed);
            Run("逐通道心跳字段可往返", PerChannelProgressIsSerializable, ref passed);
            Run("墙钟前后跳变均可检测", WallClockStepsAreDetected, ref passed);
            Run("机械圈与正式证据圈独立计数", MechanicalCyclesAreIndependent, ref passed);
            Run("逻辑静止必须包含无上电通道", LogicalQuiescenceIncludesEnergization, ref passed);
            Run("停止超时结果永久禁止同进程重启", TimedOutStopCannotRestartInProcess, ref passed);
            Run("必须重启终态统一闭锁开始入口且无矛盾文案",
                ProcessRestartUiPolicyIsNonContradictory, ref passed);
            return passed;
        }

        private static void StopAllOwnsResponsiveControlRepair()
        {
            Assert(!ResponsiveControlRepairPolicy.ShouldRequest(
                    processAlive: true,
                    applicationTakeoverConfirmed: false,
                    stopActive: true,
                    logicalResidue: true,
                    inconsistentRecovery: true,
                    formalProgressStalled: true,
                    channelSupervisionFailed: true),
                "StopAll活动时Timer/Runner残留仍触发了第二个响应修复事务。");
            Assert(ResponsiveControlRepairPolicy.ShouldRequest(
                    processAlive: true,
                    applicationTakeoverConfirmed: false,
                    stopActive: false,
                    logicalResidue: true,
                    inconsistentRecovery: false,
                    formalProgressStalled: false,
                    channelSupervisionFailed: false),
                "非Stop阶段的真实逻辑残留没有触发响应修复事务。");
            Assert(!ResponsiveControlRepairPolicy.ShouldRequest(
                    processAlive: true,
                    applicationTakeoverConfirmed: true,
                    stopActive: false,
                    logicalResidue: true,
                    inconsistentRecovery: false,
                    formalProgressStalled: false,
                    channelSupervisionFailed: false),
                "已经确认接管后仍重复发起响应修复事务。");
        }

        private static void ResponsiveControlRepairHasBoundedDeadline()
        {
            var supervisor = new ResponsiveControlRepairSupervisor();
            const long frequency = 1000;
            var first = supervisor.Evaluate(true, false, 1000, frequency);
            var beforeDeadline = supervisor.Evaluate(
                true,
                false,
                1000 + ResponsiveControlRepairPolicy.AcknowledgementDeadlineMilliseconds - 1,
                frequency);
            var deadline = supervisor.Evaluate(
                true,
                false,
                1000 + ResponsiveControlRepairPolicy.AcknowledgementDeadlineMilliseconds,
                frequency);
            var repeated = supervisor.Evaluate(true, false, 7000, frequency);

            Assert(first.RequestStopAll && !first.TakeoverConfirmed,
                "首个逻辑故障没有只请求一次主程序自处理");
            Assert(!beforeDeadline.RequestStopAll && !beforeDeadline.TakeoverConfirmed,
                "控制修复在五秒宽限期内提前接管");
            Assert(deadline.TakeoverConfirmed && deadline.ReportTakeoverConfirmation,
                "窗口响应掩盖了超时未确认的控制失活");
            Assert(repeated.TakeoverConfirmed && !repeated.ReportTakeoverConfirmation,
                "同一控制失活重复发布接管确认");

            var stopOwned = supervisor.Evaluate(true, true, 7100, frequency);
            var recovered = supervisor.Evaluate(false, false, 7200, frequency);
            var next = supervisor.Evaluate(true, false, 8000, frequency);
            Assert(stopOwned.TransferredToStopTransaction &&
                   !recovered.TakeoverConfirmed && next.RequestStopAll,
                "StopAll接管或逻辑恢复后未正确复位响应修复事务");
        }

        private static void StaleSidecarCleanupIsIdentityBound()
        {
            Assert(StaleSidecarCleanupPolicy.CanRetire(
                    sameProductScope: true,
                    candidateStartedEarlier: true,
                    candidateIdentityExact: true,
                    supervisedProcessObservation:
                        DurableRelaunchProcessObservation.Dead),
                "同产品孤儿看门狗未能自动清退");
            Assert(!StaleSidecarCleanupPolicy.CanRetire(
                       true,
                       true,
                       true,
                       DurableRelaunchProcessObservation.Alive) &&
                   !StaleSidecarCleanupPolicy.CanRetire(
                       false,
                       true,
                       true,
                       DurableRelaunchProcessObservation.Dead) &&
                   !StaleSidecarCleanupPolicy.CanRetire(
                       true,
                       false,
                       true,
                       DurableRelaunchProcessObservation.Dead) &&
                   !StaleSidecarCleanupPolicy.CanRetire(
                       true,
                       true,
                       false,
                       DurableRelaunchProcessObservation.Dead),
                "活动主进程、其他产品、新进程或身份不明的Sidecar被误清退");
        }

        private static void FormalSlotFailuresCoalesceByRun()
        {
            var gate = new FormalSlotSafetyFailureGate();
            var run = Guid.NewGuid();
            Assert(gate.TryLatch(run, out var first) && first != Guid.Empty,
                "首个形式槽失败未建立根事务");
            Assert(!gate.TryLatch(run, out var duplicate) && duplicate == first,
                "同运行重复形式槽失败未复用根关联号");
            Assert(gate.TryLatch(Guid.NewGuid(), out var next) && next != first,
                "新运行未获得新的根停止事务");
        }

        private static void WatchdogTransportWritesAreBounded()
        {
            Assert(WatchdogTransportPolicy.SendGateWaitMs > 0 &&
                   WatchdogTransportPolicy.SendGateWaitMs <= 250,
                "发送串行锁必须快速失败，不能把控制线程拖入长等待。");
            Assert(WatchdogTransportPolicy.SendWriteTimeoutMs >=
                   WatchdogTransportPolicy.SendGateWaitMs &&
                   WatchdogTransportPolicy.SendWriteTimeoutMs <= 1000,
                "实际管道写必须有不超过1秒的统一硬截止。");
        }

        private static void RuntimeResourceCountsAreDistinct()
        {
            Assert(EpbManager.CountDistinctRuntimeChannels(
                       new[] { 4, 5, 11, 12 },
                       new[] { 4, 5, 11, 12 }) == 4 &&
                   EpbManager.CountDistinctRuntimeChannels(
                       new[] { 4, 5 },
                       new[] { 5, 11, 12, 99 }) == 4,
                "活动表与缓存表仍被直接相加，或非法通道进入逻辑快照");
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
                    StopHardDeadlineUtc = 456789,
                    StopStageHardDeadlineUtc = 345678,
                    StopStageNoProgressGraceMs = 5000,
                    StopLastMaterialProgressUtc = 234567,
                    StopTakeoverRequired = true,
                    StopTimedOut = true,
                    StopTerminalReason = "Stop safety hard deadline",
                    StopTransactionId = "stop-transaction-1",
                    StopGeneration = 11,
                    StageHardDeadlineUtc = 345678,
                    StageNoProgressGraceMs = 5000,
                    LastMaterialProgressUtc = 234567,
                    ManualPauseStage = "CurrentCycleDrain",
                    ManualPauseProgressVersion = 9,
                    ManualPauseHardDeadlineUtc = 123456,
                    ManualPauseEnergizedChannels = new[] { 4, 5 },
                    HardwareUnavailable = true,
                    HardwareFailureFingerprint = "PowerSafetyProbe|Group1|IDN",
                    ActiveCycleCount = 5,
                    TimerCount = 10,
                    RunnerCount = 10,
                    LogicalSourceVersion = 17,
                    LogicalCapturedUtcTicks = 7654321,
                    DiagnosticSinkAcceptedVersion = 23,
                    DiagnosticSinkFlushedVersion = 21,
                    DiagnosticSinkLastSuccessUtcTicks = 7000000,
                    DiagnosticSinkStalled = true,
                    DiagnosticSinkEmergencySpool = true,
                    DiagnosticSinkQueueDepth = 2,
                    DiagnosticSinkDroppedRecords = 3,
                    DiagnosticSinkFailure = "disk-offline"
                }
            };
            var roundTrip = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(message));
            Assert(roundTrip.ProtocolVersion == WatchdogProtocol.Version &&
                   roundTrip.ProtocolVersion == 4 && roundTrip.Heartbeat.StopAllActive &&
                   roundTrip.Heartbeat.StopProgressVersion == 7 &&
                   roundTrip.Heartbeat.StopHardDeadlineUtc == 456789 &&
                   roundTrip.Heartbeat.StopStageHardDeadlineUtc == 345678 &&
                   roundTrip.Heartbeat.StopStageNoProgressGraceMs == 5000 &&
                   roundTrip.Heartbeat.StopLastMaterialProgressUtc == 234567 &&
                   roundTrip.Heartbeat.StopTakeoverRequired &&
                   roundTrip.Heartbeat.StopTimedOut &&
                   roundTrip.Heartbeat.StopTerminalReason == "Stop safety hard deadline" &&
                   roundTrip.Heartbeat.StopTransactionId == "stop-transaction-1" &&
                   roundTrip.Heartbeat.StopGeneration == 11 &&
                   roundTrip.Heartbeat.ManualPauseProgressVersion == 9 &&
                   roundTrip.Heartbeat.ManualPauseEnergizedChannels.Length == 2 &&
                   roundTrip.Heartbeat.HardwareUnavailable &&
                    roundTrip.Heartbeat.HardwareFailureFingerprint.Contains("Group1") &&
                    roundTrip.Heartbeat.TimerCount == 10 &&
                    roundTrip.Heartbeat.ActiveCycleCount == 5 &&
                    roundTrip.Heartbeat.LogicalSourceVersion == 17 &&
                    roundTrip.Heartbeat.LogicalCapturedUtcTicks == 7654321 &&
                    roundTrip.Heartbeat.DiagnosticSinkAcceptedVersion == 23 &&
                    roundTrip.Heartbeat.DiagnosticSinkFlushedVersion == 21 &&
                    roundTrip.Heartbeat.DiagnosticSinkStalled &&
                    roundTrip.Heartbeat.DiagnosticSinkEmergencySpool &&
                    roundTrip.Heartbeat.DiagnosticSinkQueueDepth == 2 &&
                    roundTrip.Heartbeat.DiagnosticSinkDroppedRecords == 3 &&
                    roundTrip.Heartbeat.DiagnosticSinkFailure == "disk-offline",
                 "v2停止字段未能序列化往返");

            var v1 = WatchdogProtocol.Deserialize(
                "{\"ProtocolVersion\":1,\"Type\":\"Heartbeat\",\"SessionId\":\"v1\"," +
                "\"Heartbeat\":{\"Sequence\":3,\"RunActive\":false}}");
            Assert(v1.ProtocolVersion == 1 && v1.Heartbeat.Sequence == 3 &&
                   !v1.Heartbeat.StopAllActive &&
                   !v1.Heartbeat.DiagnosticSinkStalled &&
                   v1.Heartbeat.DiagnosticSinkAcceptedVersion == 0,
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

            var runId = Guid.NewGuid().ToString("N");
            var startFailure = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                new WatchdogMessage
                {
                    Type = WatchdogMessageType.BatchStartFailed,
                    BatchStartFailure = new WatchdogBatchStartFailureContext
                    {
                        RunId = runId,
                        CheckpointRunId = runId,
                        RunEpoch = 12,
                        CheckpointArmed = true,
                        RecoveryProcess = true
                    }
                }));
            Assert(startFailure.BatchStartFailure != null &&
                   startFailure.BatchStartFailure.CheckpointArmed &&
                   startFailure.BatchStartFailure.RunEpoch == 12 &&
                   startFailure.BatchStartFailure.RecoveryProcess,
                "启动失败接管资格上下文未能协议往返。");
        }

        private static void StopStageDeadlineAndMaterialGrace()
        {
            const long now = 10_000_000;
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1, false, false, false, 99, false,
                       stopAllActive: true,
                       nowUtcTicks: now,
                       stopNoProgressSeconds: 100,
                       stopStageNoProgressGraceMs: 5000),
                "缺少控制器阶段硬截止时不能猜测统一五秒接管");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1, false, false, false, 99, false,
                       stopAllActive: true,
                       nowUtcTicks: now,
                       stopStageHardDeadlineUtcTicks: now + 1,
                       stopNoProgressSeconds: 100,
                       stopStageNoProgressGraceMs: 5000),
                "阶段硬截止尚未到达时不能仅因历史无进展而接管");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1, false, false, false, 99, false,
                       stopAllActive: true,
                       nowUtcTicks: now + 2,
                       stopStageHardDeadlineUtcTicks: now + 1,
                       stopNoProgressSeconds: 4.999,
                       stopStageNoProgressGraceMs: 5000),
                "阶段硬截止后宽限期内不应接管");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1, false, false, false, 99, false,
                       stopAllActive: true,
                       nowUtcTicks: now + 2,
                       stopStageHardDeadlineUtcTicks: now + 1,
                       stopNoProgressSeconds: 5,
                       stopStageNoProgressGraceMs: 5000),
                "阶段硬截止且材料进展宽限耗尽后未接管");
        }

        private static void StopHydraulicReleaseDeadlineIsIndependent()
        {
            const long second = TimeSpan.TicksPerSecond;
            const long start = 100_000_000;
            const long releaseDeadline = start + 15 * second;
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, true, false, true, 1, false, false, false, 10.441, false,
                       stopAllActive: true,
                       nowUtcTicks: start + (long)(10.441 * second),
                       stopStageHardDeadlineUtcTicks: releaseDeadline,
                       stopNoProgressSeconds: 10.441,
                       stopStageNoProgressGraceMs: 5000),
                "液压释放约10.441秒仍在阶段截止前，不得接管");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, true, false, true, 1, false, false, false, 15, false,
                       stopAllActive: true,
                       nowUtcTicks: releaseDeadline,
                       stopStageHardDeadlineUtcTicks: releaseDeadline,
                       stopNoProgressSeconds: 0,
                       stopStageNoProgressGraceMs: 5000),
                "液压释放恰到15秒但刚进入截止，仍应等待真实材料宽限");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       false, true, false, true, 1, false, false, false, 20, false,
                       stopAllActive: true,
                       nowUtcTicks: releaseDeadline + 5 * second,
                       stopStageHardDeadlineUtcTicks: releaseDeadline,
                       stopNoProgressSeconds: 5,
                       stopStageNoProgressGraceMs: 5000),
                "液压释放阶段截止后持续无真实进展未接管");
        }

        private static void StopTotalHardDeadlineIsIndependent()
        {
            const long now = 200_000_000;
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, true, false, true, 1, false, false, false, 44.999, false,
                       stopAllActive: true,
                       nowUtcTicks: now,
                       stopStageHardDeadlineUtcTicks: now + 60 * TimeSpan.TicksPerSecond,
                       stopNoProgressSeconds: 100,
                       stopStageNoProgressGraceMs: 5000,
                       stopHardDeadlineUtcTicks: now + 1),
                "总45秒期限尚未到达时不能用过期的测试时钟提前接管");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       false, true, false, true, 1, false, false, false, 45, false,
                       stopAllActive: true,
                       nowUtcTicks: now + 2,
                       stopStageHardDeadlineUtcTicks: now + 60 * TimeSpan.TicksPerSecond,
                       stopNoProgressSeconds: 0,
                       stopStageNoProgressGraceMs: 5000,
                       stopHardDeadlineUtcTicks: now + 1),
                "总逃逸期限到达后没有最终安全接管");
        }

        private static void StopMaterialEvidenceMustBeMonotonic()
        {
            var gate = new StopSafetyMaterialEvidenceGate();
            Assert(gate.TryAccept("PersistenceBoundary:Dev1", 100) &&
                   !gate.TryAccept("PersistenceBoundary:Dev1", 100) &&
                   !gate.TryAccept("PersistenceBoundary:Dev1", 99) &&
                   gate.TryAccept("PersistenceBoundary:Dev1", 101),
                "重复或回退的持久化边界证据错误刷新Stop进展");
            Assert(!gate.TryAccept("PersistenceBoundary:Dev1", 0) &&
                   !gate.TryAccept("PersistenceBoundary:Dev2", 0) &&
                   !gate.TryAccept("", 1),
                "缺失来源或序列的诊断文本被错误当作材料进展");
            gate.Reset();
            Assert(gate.TryAccept("PersistenceBoundary:Dev1", 1),
                "新Stop事务未重置材料证据游标");
        }

        private static void StopStageOrderIsMonotonic()
        {
            Assert((int)StopSafetyStage.ClearRecoveryOwners <
                   (int)StopSafetyStage.ReleaseHydraulics &&
                   (int)StopSafetyStage.ReleaseHydraulics <
                   (int)StopSafetyStage.StopAcquisition &&
                   (int)StopSafetyStage.StopAcquisition <
                   (int)StopSafetyStage.ClosePersistenceBoundary,
                "Stop阶段枚举顺序未反映 ReleaseHydraulics -> StopAcquisition -> Persistence");
        }

        private static void UnarmedBatchStartFailureStaysSafeIdle()
        {
            var runId = Guid.NewGuid().ToString("N");
            Assert(!BatchStartTakeoverPolicy.ShouldTakeover(null),
                "缺失启动失败资格证据时仍允许杀进程。");
            Assert(!BatchStartTakeoverPolicy.ShouldTakeover(
                       new WatchdogBatchStartFailureContext
                       {
                           RunId = runId,
                           CheckpointRunId = runId,
                           CheckpointArmed = false,
                           FormalRunCommitted = false
                       }) &&
                   BatchStartTakeoverPolicy.DescribeRejection(
                       new WatchdogBatchStartFailureContext
                       {
                           RunId = runId,
                           CheckpointRunId = runId,
                           CheckpointArmed = false
                       }) == "CheckpointDisarmed",
                "未武装的新试验启动失败没有停留在安全空闲态。");
            Assert(!BatchStartTakeoverPolicy.ShouldTakeover(
                       new WatchdogBatchStartFailureContext
                       {
                           RunId = runId,
                           CheckpointRunId = Guid.NewGuid().ToString("N"),
                           CheckpointArmed = true,
                           FormalRunCommitted = true
                       }),
                "Run身份不匹配仍允许接管。");
            Assert(BatchStartTakeoverPolicy.ShouldTakeover(
                       new WatchdogBatchStartFailureContext
                       {
                           RunId = runId,
                           CheckpointRunId = runId,
                           CheckpointArmed = true,
                           FormalRunCommitted = true
                       }) &&
                   BatchStartTakeoverPolicy.ShouldTakeover(
                       new WatchdogBatchStartFailureContext
                       {
                           RunId = runId,
                           CheckpointRunId = runId,
                           CheckpointArmed = true,
                           RecoveryProcess = true
                       }),
                "合法正式Run或恢复进程被错误拒绝接管。");
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
                    stopAllActive: true,
                    nowUtcTicks: 10,
                    stopStageHardDeadlineUtcTicks: 9,
                    stopNoProgressSeconds: 5,
                    stopStageNoProgressGraceMs: 5000),
                "Stop阶段硬截止后的材料无进展未触发接管");
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

        private static void PhaseAwareRuntimeContractPreventsLearningFalseTakeover()
        {
            const long frequency = 1000;
            const long started = 200000;
            var tracker = new WatchdogChannelProgressTracker();
            var progress = ContractProgress("Learning", timer: false, runner: true);
            var heartbeat = new WatchdogHeartbeat
            {
                RunActive = true,
                RunId = "learning-run",
                RunEpoch = 1,
                ExpectedCyclePeriodMs = 15000,
                ChannelProgress = new[] { progress }
            };

            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null &&
                   tracker.Evaluate(heartbeat, new[] { 4 }, false, started + 5001, frequency) == null,
                "合法Learning(Timer=False,Runner=True)仍在5秒后触发ChannelExpectedRuntimeMissing");

            tracker.Reset();
            for (var second = 0; second <= 600; second += 15)
            {
                progress.MechanicalCompletedCount++;
                progress.LastMechanicalCompletedUtcTicks++;
                Assert(tracker.Evaluate(
                           heartbeat,
                           new[] { 4 },
                           false,
                           started + second * frequency,
                           frequency) == null,
                    $"合法Learning在第{second}秒被错误接管");
            }

            tracker.Reset();
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null &&
                   tracker.Evaluate(heartbeat, new[] { 4 }, false, started + 60000, frequency)
                       ?.Contains("ChannelProgressStalled:EPB=4") == true,
                "修复Learning资源误判时错误移除了学习阶段机械进展监督");
            tracker.Reset();

            progress.State = "Qualification";
            ApplyPublishedContract(progress);
            for (var second = 0; second <= 600; second += 15)
            {
                progress.MechanicalCompletedCount++;
                progress.LastMechanicalCompletedUtcTicks++;
                Assert(tracker.Evaluate(
                           heartbeat,
                           new[] { 4 },
                           false,
                           started + second * frequency,
                           frequency) == null,
                    $"合法Qualification在第{second}秒被错误接管");
            }

            progress.State = "Starting";
            progress.RunnerActive = false;
            ApplyPublishedContract(progress);
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started + 20000, frequency) == null &&
                   tracker.Evaluate(heartbeat, new[] { 4 }, false, started + 26000, frequency) == null,
                "Starting阶段被臆造Timer/Runner要求");

            tracker.Reset();
            progress = ContractProgress("Learning", timer: false, runner: false);
            heartbeat.ChannelProgress = new[] { progress };
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null,
                "Learning缺Runner首次观察未去抖");
            var missingRunner = tracker.Evaluate(
                heartbeat, new[] { 4 }, false, started + 5000, frequency);
            Assert(missingRunner?.Contains("LearningRunnerMissing:EPB=4") == true &&
                   missingRunner.Contains("ExpectedTimer=False") &&
                   missingRunner.Contains("ExpectedRunner=True"),
                "Learning真正缺少Runner时未按阶段契约接管");
        }

        private static void StructuredRefreshPrecedesResourceTakeover()
        {
            const long frequency = 1000;
            const long started = 250000;
            var tracker = new WatchdogChannelProgressTracker();
            var progress = ContractProgress("Running", timer: false, runner: true);
            var heartbeat = new WatchdogHeartbeat
            {
                RunActive = true,
                RunId = "refresh-run",
                RunEpoch = 5,
                ChannelProgress = new[] { progress }
            };

            var first = tracker.EvaluateDetailed(
                heartbeat, new[] { 4 }, false, started, frequency);
            Assert(first.RefreshRequested &&
                   string.IsNullOrEmpty(first.TakeoverReason) &&
                   first.RefreshReason.Contains("ChannelExpectedRuntimeMissing:EPB=4"),
                "首次正式资源缺失没有先请求结构化心跳刷新");

            progress.StateRevision = 9999;
            var beforeDeadline = tracker.EvaluateDetailed(
                heartbeat, new[] { 4 }, false, started + 4999, frequency);
            Assert(!beforeDeadline.RefreshRequested &&
                   string.IsNullOrEmpty(beforeDeadline.TakeoverReason),
                "同一不变量被StateRevision刷新重复请求，或在5秒前接管");

            var afterRefresh = tracker.EvaluateDetailed(
                heartbeat, new[] { 4 }, false, started + 5000, frequency);
            Assert(!afterRefresh.RefreshRequested &&
                   afterRefresh.TakeoverReason.Contains("ChannelExpectedRuntimeMissing:EPB=4"),
                "刷新后同一资源签名持续5秒仍未接管");

            progress.TimerActive = true;
            progress.RunnerActive = false;
            var missingRunner = tracker.EvaluateDetailed(
                heartbeat, new[] { 4 }, false, started + 6000, frequency);
            Assert(missingRunner.RefreshRequested &&
                   missingRunner.RefreshReason.Contains("ExpectedRunner=True"),
                "Running缺Runner的新资源签名未建立独立刷新/去抖");
            var runnerTakeover = tracker.EvaluateDetailed(
                heartbeat, new[] { 4 }, false, started + 11000, frequency);
            Assert(runnerTakeover.TakeoverReason.Contains("ChannelExpectedRuntimeMissing:EPB=4"),
                "Running缺Runner持续5秒未接管");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1, false, false, false, 5, true,
                       formalProgressStalled: true) &&
                   !WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, true, true, 1, false, false, false, 5, true,
                       formalProgressStalled: true),
                "同一正式资源缺失未受Takeover单飞门约束");

            progress.RunnerActive = true;
            var healthy = tracker.EvaluateDetailed(
                heartbeat, new[] { 4 }, false, started + 20000, frequency);
            Assert(!healthy.RefreshRequested && string.IsNullOrEmpty(healthy.TakeoverReason),
                "Running Timer/Runner均健康仍触发刷新或接管");
        }

        private static void StaleLogicalSourceCannotMasqueradeAsMechanicalStall()
        {
            const long frequency = 1000;
            const long started = 500000;
            var utc = new DateTime(2026, 8, 25, 1, 0, 0, DateTimeKind.Utc).Ticks;
            var tracker = new WatchdogChannelProgressTracker();
            var progress = ContractProgress("Running", timer: true, runner: true);
            progress.ProgressVersion = 1;
            progress.MechanicalCompletedCount = 3;
            progress.LastMechanicalCompletedUtcTicks = utc - TimeSpan.TicksPerSecond * 60;
            var heartbeat = new WatchdogHeartbeat
            {
                SessionId = "logical-stale",
                ProcessId = 12004,
                ProcessStartUtcTicks = utc - TimeSpan.TicksPerHour,
                AttachEpoch = 7,
                RunActive = true,
                RunId = "field-v21",
                RunEpoch = 21,
                ExpectedCyclePeriodMs = 15000,
                LogicalSourceVersion = 1,
                LogicalCapturedUtcTicks = utc - TimeSpan.TicksPerSecond * 4,
                ChannelProgress = new[] { progress }
            };

            var first = tracker.EvaluateDetailed(
                heartbeat, new[] { 4 }, false, started, frequency, utc);
            Assert(first.RefreshRequested &&
                   first.RefreshReason.Contains("WatchdogLogicalSourceStale") &&
                   string.IsNullOrEmpty(first.TakeoverReason),
                "过期逻辑源首次观察没有先请求Ping刷新");

            var persisted = tracker.EvaluateDetailed(
                heartbeat,
                new[] { 4 },
                false,
                started + 5000,
                frequency,
                utc + TimeSpan.TicksPerSecond * 5);
            Assert(persisted.TakeoverReason.Contains("WatchdogLogicalSourceStale") &&
                   !persisted.TakeoverReason.Contains("ChannelProgressStalled"),
                "旧逻辑源被错误解释成通道机械停滞");

            heartbeat.LogicalSourceVersion = 2;
            heartbeat.LogicalCapturedUtcTicks = utc + TimeSpan.TicksPerSecond * 6;
            progress.ProgressVersion = 2;
            progress.MechanicalCompletedCount = 4;
            progress.LastMechanicalCompletedUtcTicks = utc + TimeSpan.TicksPerSecond * 6;
            var refreshed = tracker.EvaluateDetailed(
                heartbeat,
                new[] { 4 },
                false,
                started + 6000,
                frequency,
                utc + TimeSpan.TicksPerSecond * 6);
            Assert(!refreshed.RefreshRequested && string.IsNullOrEmpty(refreshed.TakeoverReason),
                "Ping后的新逻辑源未能解除旧源冻结状态");

            var rollbackClock = tracker.EvaluateDetailed(
                heartbeat,
                new[] { 4 },
                false,
                started + 10001,
                frequency,
                utc - TimeSpan.TicksPerMinute);
            Assert(rollbackClock.RefreshRequested &&
                   rollbackClock.RefreshReason.Contains("WatchdogLogicalSourceStale"),
                "墙钟回拨时未用单调观察时长发现逻辑源停止发布");
        }

        private static void FreshLogicalSourceStillDetectsMechanicalStall()
        {
            const long frequency = 1000;
            const long started = 600000;
            var utc = new DateTime(2026, 8, 25, 2, 0, 0, DateTimeKind.Utc).Ticks;
            var tracker = new WatchdogChannelProgressTracker();
            var progress = ContractProgress("Running", timer: true, runner: true);
            progress.ProgressVersion = 10;
            progress.MechanicalCompletedCount = 10;
            progress.LastMechanicalCompletedUtcTicks = utc;
            var heartbeat = new WatchdogHeartbeat
            {
                SessionId = "real-stall",
                ProcessId = 13000,
                ProcessStartUtcTicks = utc - TimeSpan.TicksPerHour,
                AttachEpoch = 2,
                RunActive = true,
                RunId = "real-stall-run",
                RunEpoch = 1,
                ExpectedCyclePeriodMs = 15000,
                LogicalSourceVersion = 10,
                LogicalCapturedUtcTicks = utc,
                ChannelProgress = new[] { progress }
            };
            Assert(string.IsNullOrEmpty(tracker.Evaluate(
                       heartbeat, new[] { 4 }, false, started, frequency, utc)),
                "真实停滞首样本不应立即接管");

            // Logical source continues to publish, but the mechanically
            // meaningful per-channel signature does not change.
            heartbeat.LogicalSourceVersion = 130;
            heartbeat.LogicalCapturedUtcTicks = utc + TimeSpan.TicksPerSecond * 60;
            var stalled = tracker.Evaluate(
                heartbeat,
                new[] { 4 },
                false,
                started + 60000,
                frequency,
                utc + TimeSpan.TicksPerSecond * 60);
            Assert(stalled?.Contains("ChannelProgressStalled:EPB=4") == true,
                "持续刷新逻辑源错误掩盖了真实机械停滞");
        }

        private static void ProcessIdentityChangeResetsChannelDeadline()
        {
            const long frequency = 1000;
            const long started = 700000;
            var utc = new DateTime(2026, 8, 25, 3, 0, 0, DateTimeKind.Utc).Ticks;
            var tracker = new WatchdogChannelProgressTracker();
            var progress = ContractProgress("Running", timer: true, runner: true);
            progress.ProgressVersion = 1;
            var heartbeat = new WatchdogHeartbeat
            {
                SessionId = "identity-reset",
                ProcessId = 1,
                ProcessStartUtcTicks = utc - TimeSpan.TicksPerHour,
                AttachEpoch = 1,
                RunActive = true,
                RunId = "same-run-text",
                RunEpoch = 9,
                ExpectedCyclePeriodMs = 15000,
                LogicalSourceVersion = 1,
                LogicalCapturedUtcTicks = utc,
                ChannelProgress = new[] { progress }
            };
            tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency, utc);

            heartbeat.ProcessId = 2;
            heartbeat.ProcessStartUtcTicks = utc + TimeSpan.TicksPerSecond * 30;
            heartbeat.AttachEpoch = 2;
            heartbeat.LogicalSourceVersion = 2;
            heartbeat.LogicalCapturedUtcTicks = utc + TimeSpan.TicksPerSecond * 60;
            Assert(string.IsNullOrEmpty(tracker.Evaluate(
                       heartbeat,
                       new[] { 4 },
                       false,
                       started + 60000,
                       frequency,
                       utc + TimeSpan.TicksPerSecond * 60)),
                "新PID/start/attach继承了旧进程的机械停滞期限");
        }

        private static void LogicalSourceCommitIsVersionedIndependently()
        {
            var store = new RecoveryAggregateStore();
            var first = store.PublishLogicalSource(new LogicalQuiescenceSnapshot());
            var second = store.PublishLogicalSource(new LogicalQuiescenceSnapshot());
            var unrelated = store.PublishOperationalSources(
                new InfrastructureRecoverySource(),
                new PowerRecoverySource(),
                new TimerRecoverySource());
            Assert(first.Logical.SourceVersion == 1 &&
                   second.Logical.SourceVersion == 2 &&
                   second.Logical.CapturedUtcTicks > 0 &&
                   unrelated.Version > second.Version &&
                   unrelated.Logical.SourceVersion == second.Logical.SourceVersion &&
                   unrelated.Logical.CapturedUtcTicks == second.Logical.CapturedUtcTicks,
                "DAQ/Stop/ownership以外的聚合提交错误伪造了逻辑源刷新");
        }

        private static void SafeIdleCancellationIsLinearizable()
        {
            var coordinator = new TakeoverTransactionCoordinator();
            long previousGeneration = 0;
            for (var iteration = 0; iteration < 10000; iteration++)
            {
                Assert(coordinator.TryBegin(
                           "corr-" + iteration,
                           "authority-" + iteration,
                           out var lease),
                    "接管事务代次未能创建");
                Assert(lease.Generation > previousGeneration &&
                       coordinator.TryAdvance(lease, TakeoverTransactionStage.DumpCapture),
                    "接管代次未单调递增或未进入dump前检查点");
                previousGeneration = lease.Generation;

                var terminationExecuted = false;
                bool cancelled;
                bool terminationAuthorized;
                if ((iteration & 1) == 0)
                {
                    cancelled = coordinator.TryCancel("SafeIdle", out _);
                    terminationAuthorized = coordinator.TryExecute(
                        lease,
                        TakeoverTransactionStage.ProcessTermination,
                        () => terminationExecuted = true,
                        out _);
                }
                else
                {
                    terminationAuthorized = coordinator.TryExecute(
                        lease,
                        TakeoverTransactionStage.ProcessTermination,
                        () => terminationExecuted = true,
                        out _);
                    cancelled = coordinator.TryCancel("SafeIdle", out _);
                }

                Assert(cancelled != terminationAuthorized &&
                       terminationExecuted == terminationAuthorized,
                    "SafeIdle取消与kill授权同时获胜，违反线性化不变量");
                coordinator.Complete(lease);
            }
        }

        private static void RelaunchPermitPrecedesTerminationBoundary()
        {
            Assert(TakeoverTransactionStage.RelaunchPermit <
                   TakeoverTransactionStage.ProcessTermination,
                "接管阶段排序仍允许先kill后取许可。");
            var coordinator = new TakeoverTransactionCoordinator();
            Assert(coordinator.TryBegin("permit-first", "authority", out var lease),
                "无法创建许可前置接管事务。");
            Assert(coordinator.TryAdvance(lease, TakeoverTransactionStage.DumpCapture) &&
                   coordinator.TryAdvance(lease, TakeoverTransactionStage.RelaunchPermit),
                "无法在kill前提交许可阶段。");
            Assert(coordinator.TryCancel("permit-not-consumed", out var cancelled) &&
                   cancelled.CancelledFromStage == TakeoverTransactionStage.RelaunchPermit,
                "许可阶段尚未kill时不再允许安全取消。");
            var killed = false;
            Assert(!coordinator.TryExecute(
                       lease,
                       TakeoverTransactionStage.ProcessTermination,
                       () => killed = true,
                       out _) && !killed,
                "取消后仍执行了进程终止动作。");
            coordinator.Complete(lease);
        }

        private static void RuntimeInvariantTimingUsesRunAndContractIdentity()
        {
            const long frequency = 1000;
            const long started = 300000;
            var tracker = new WatchdogChannelProgressTracker();
            var progress = ContractProgress("Running", timer: false, runner: true);
            var heartbeat = new WatchdogHeartbeat
            {
                RunActive = true,
                RunId = "run-a",
                RunEpoch = 10,
                ChannelProgress = new[] { progress }
            };
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null,
                "正式运行资源缺失首次观察未去抖");
            progress.StateRevision = 999;
            Assert(tracker.Evaluate(
                       heartbeat, new[] { 4 }, false, started + 5000, frequency)
                       ?.Contains("ChannelExpectedRuntimeMissing:EPB=4") == true,
                "StateRevision刷新错误重置了同一运行资源不变量的5秒计时");

            tracker.Reset();
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null,
                "重置后首次观察异常");
            heartbeat.RunId = "run-b";
            heartbeat.RunEpoch = 11;
            Assert(tracker.Evaluate(
                       heartbeat, new[] { 4 }, false, started + 4999, frequency) == null,
                "新RunId/RunEpoch继承了上一轮不变量计时");
            Assert(tracker.Evaluate(
                       heartbeat, new[] { 4 }, false, started + 9999, frequency)
                       ?.Contains("RunId=run-b;RunEpoch=11") == true,
                "新运行独立去抖满5秒后没有报告带身份的资源缺失");

            tracker.Reset();
            heartbeat.RunId = "transition";
            heartbeat.RunEpoch = 12;
            progress.State = "Learning";
            progress.TimerActive = false;
            progress.RunnerActive = false;
            ApplyPublishedContract(progress);
            tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency);
            progress.State = "Running";
            progress.RunnerActive = true;
            ApplyPublishedContract(progress);
            Assert(tracker.Evaluate(
                       heartbeat, new[] { 4 }, false, started + 4999, frequency) == null,
                "Learning到Running契约转换沿用了旧阶段的不变量计时");
            Assert(tracker.Evaluate(
                       heartbeat, new[] { 4 }, false, started + 9999, frequency)
                       ?.Contains("State=Running") == true,
                "契约转换后新的Running缺Timer未独立计时并报告");
        }

        private static void RecoveryAndTerminalContractsArePhaseAware()
        {
            const long frequency = 1000;
            const long started = 400000;
            var tracker = new WatchdogChannelProgressTracker();
            var recovery = ContractProgress("Recovering", timer: false, runner: false);
            recovery.RecoveryOwned = true;
            recovery.RecoveryOwnerKind = "FormalTimer";
            recovery.RecoveryOwnerId = Guid.NewGuid().ToString("N");
            recovery.RecoveryOwnerGeneration = 20;
            recovery.RecoveryTargetPhase = "Formal";
            var heartbeat = new WatchdogHeartbeat
            {
                RunActive = true,
                RunId = "recovery",
                RunEpoch = 20,
                ChannelProgress = new[] { recovery }
            };
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started, frequency) == null &&
                   tracker.Evaluate(heartbeat, new[] { 4 }, false, started + 31000, frequency) == null,
                "合法恢复态被强制要求正式Timer/Runner或机械圈进展");

            recovery.RunnerActive = true;
            Assert(tracker.Evaluate(heartbeat, new[] { 4 }, false, started + 32000, frequency) == null,
                "恢复态单边资源首次观察未使用30秒去抖");
            Assert(tracker.Evaluate(
                       heartbeat, new[] { 4 }, false, started + 62000, frequency)
                       ?.Contains("ChannelRecoveryInvariantStalled:EPB=4") == true,
                "恢复态Timer/Runner单边残留30秒未被发现");

            tracker.Reset();
            var terminal = ContractProgress("Completed", timer: false, runner: true);
            heartbeat.RunId = "terminal";
            heartbeat.RunEpoch = 21;
            heartbeat.RunActive = false;
            heartbeat.ChannelProgress = new[] { terminal };
            // Completed 在生产心跳中会从 RecoveryEligibleChannels 排除，但终态
            // 资源残留仍必须被 sidecar 监督。
            tracker.Evaluate(heartbeat, Array.Empty<int>(), false, started, frequency);
            Assert(tracker.Evaluate(
                       heartbeat, Array.Empty<int>(), false, started + 5000, frequency)
                       ?.Contains("ChannelTerminalResourcesActive:EPB=4") == true,
                "Completed终态残留Runner五秒后未被接管");
        }

        private static void WatchdogTakeoverCancellationIsSuperseded()
        {
            const string correlation = "takeover-correlation";
            Assert(RecoveryFailurePolicy.IsSupersededByWatchdogTakeover(
                    true,
                    correlation,
                    "WatchdogTakeover",
                    correlation,
                    "RecoverySupersededByTakeover",
                    "WatchdogRecoveryStartupFailed:已取消该操作。",
                    "System.OperationCanceledException: The operation was canceled."),
                "同一Watchdog接管关联号引发的取消仍被当作新启动失败");
            Assert(!RecoveryFailurePolicy.IsSupersededByWatchdogTakeover(
                    true,
                    correlation,
                    "WatchdogTakeover",
                    "other-correlation",
                    "UnhandledSoftwareStartup",
                    "OperationCanceledException",
                    string.Empty) &&
                   !RecoveryFailurePolicy.IsSupersededByWatchdogTakeover(
                    false,
                    correlation,
                    "WatchdogTakeover",
                    correlation,
                    "UnhandledSoftwareStartup",
                    "OperationCanceledException",
                    string.Empty),
                "无关关联号或无活动接管的业务取消被错误吞掉");
        }

        private static void HundredLifecycleTransitionsDoNotLeakSupervisionState()
        {
            const long frequency = 1000;
            var tracker = new WatchdogChannelProgressTracker();
            var channels = new[] { 4, 5, 9, 10, 11 };
            var takeoverCount = 0;
            var refreshCount = 0;

            for (var run = 1; run <= 100; run++)
            {
                var progress = channels.Select(channel =>
                {
                    var item = ContractProgress("Learning", timer: false, runner: true);
                    item.Channel = channel;
                    return item;
                }).ToArray();
                var heartbeat = new WatchdogHeartbeat
                {
                    RunActive = true,
                    RunId = "lifecycle-" + run,
                    RunEpoch = run,
                    ExpectedCyclePeriodMs = 15000,
                    ChannelProgress = progress
                };
                var baseTimestamp = run * 1_000_000L;

                for (var cycle = 1; cycle <= 10; cycle++)
                {
                    foreach (var item in progress)
                    {
                        item.MechanicalCompletedCount = cycle;
                        item.LastMechanicalCompletedUtcTicks = cycle;
                    }
                    ObserveWithoutAction(
                        tracker,
                        heartbeat,
                        channels,
                        baseTimestamp + cycle * 15000L,
                        frequency,
                        ref refreshCount,
                        ref takeoverCount);
                }

                foreach (var item in progress)
                {
                    item.State = "Qualification";
                    item.MechanicalCompletedCount++;
                    item.LastMechanicalCompletedUtcTicks++;
                    ApplyPublishedContract(item);
                }
                ObserveWithoutAction(
                    tracker,
                    heartbeat,
                    channels,
                    baseTimestamp + 165000L,
                    frequency,
                    ref refreshCount,
                    ref takeoverCount);

                // 每十轮模拟一次检查点恢复：RunId 保持根身份，RunEpoch 前进，
                // 新进程再次穿越合法 Learning 后才能进入正式资源契约。
                if (run % 10 == 0)
                {
                    heartbeat.RunEpoch++;
                    foreach (var item in progress)
                    {
                        item.State = "Learning";
                        item.TimerActive = false;
                        item.RunnerActive = true;
                        ApplyPublishedContract(item);
                    }
                    for (var cycle = 1; cycle <= 10; cycle++)
                    {
                        foreach (var item in progress)
                        {
                            item.MechanicalCompletedCount++;
                            item.LastMechanicalCompletedUtcTicks++;
                        }
                        ObserveWithoutAction(
                            tracker,
                            heartbeat,
                            channels,
                            baseTimestamp + 180000L + cycle * 15000L,
                            frequency,
                            ref refreshCount,
                            ref takeoverCount);
                    }
                }

                foreach (var item in progress)
                {
                    item.State = "Running";
                    item.TimerActive = true;
                    item.RunnerActive = true;
                    item.MechanicalCompletedCount++;
                    item.LastMechanicalCompletedUtcTicks++;
                    ApplyPublishedContract(item);
                }
                ObserveWithoutAction(
                    tracker,
                    heartbeat,
                    channels,
                    baseTimestamp + 350000L,
                    frequency,
                    ref refreshCount,
                    ref takeoverCount);

                heartbeat.RunActive = false;
                foreach (var item in progress)
                {
                    item.State = "ManualStopped";
                    item.TimerActive = false;
                    item.RunnerActive = false;
                    item.Energized = false;
                    ApplyPublishedContract(item);
                }
                ObserveWithoutAction(
                    tracker,
                    heartbeat,
                    Array.Empty<int>(),
                    baseTimestamp + 351000L,
                    frequency,
                    ref refreshCount,
                    ref takeoverCount);
                Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                        false, true, false, true, 1, false, false, false, 0, false),
                    "学习/正式结束后的人工停止错误启动自动恢复");
            }

            Assert(refreshCount == 0 && takeoverCount == 0,
                $"百轮合法生命周期产生异常动作：Refresh={refreshCount};Takeover={takeoverCount}");
        }

        private static void ObserveWithoutAction(
            WatchdogChannelProgressTracker tracker,
            WatchdogHeartbeat heartbeat,
            int[] channels,
            long timestamp,
            long frequency,
            ref int refreshCount,
            ref int takeoverCount)
        {
            var result = tracker.EvaluateDetailed(
                heartbeat,
                channels,
                false,
                timestamp,
                frequency);
            if (result.RefreshRequested) refreshCount++;
            if (!string.IsNullOrWhiteSpace(result.TakeoverReason)) takeoverCount++;
        }

        private static WatchdogChannelProgress ContractProgress(
            string state,
            bool timer,
            bool runner)
        {
            var result = new WatchdogChannelProgress
            {
                Channel = 4,
                State = state,
                TimerActive = timer,
                RunnerActive = runner
            };
            ApplyPublishedContract(result);
            return result;
        }

        private static void ApplyPublishedContract(WatchdogChannelProgress progress)
        {
            var contract = WatchdogRuntimeContractPolicy.Resolve(progress.State);
            progress.LifecyclePhase = contract.LifecyclePhase;
            progress.RuntimeContractRevision = contract.Revision;
            progress.MechanicalProgressExpected = contract.MechanicalProgressExpected;
            progress.TimerRequired = contract.TimerRequired;
            progress.RunnerRequired = contract.RunnerRequired;
            progress.ResourcesMustBeInactive = contract.ResourcesMustBeInactive;
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
            Assert(RecoveryTransitionPolicy.DismissPromptButtonText.Contains("不停止恢复") &&
                   RecoveryTransitionPolicy.PreserveProcessButtonText.Contains("保留主程序"),
                "过渡窗没有公开保留主程序的安全关闭动作");
            Assert(RecoveryTransitionPolicy.OperatorStopButtonText.Contains("停止自动恢复") &&
                   RecoveryTransitionPolicy.OperatorStopButtonText.Contains("退出软件"),
                "过渡窗没有公开清晰的人工停止并退出语义");
            Assert(RecoveryTransitionPolicy.OperatorStopConfirmationText.Contains("选择“否”"),
                "破坏性退出动作没有声明默认保留主程序");
            Assert(RecoveryTransitionPolicy.ShouldPreferPreserveProcess(
                       WatchdogManualPauseStage.Completed,
                       true) &&
                   !RecoveryTransitionPolicy.ShouldPreferPreserveProcess(
                       WatchdogManualPauseStage.PersistenceDrain,
                       true),
                "Stage=4存活进程没有成为默认保留动作");
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
                        PeakCutoffSequence = 455,
                        ProgressVersion = 88,
                        LastProgressUtcTicks = 876543,
                        ProgressKind = "Learning",
                        LifecyclePhase = "Learning",
                        RuntimeContractRevision = WatchdogRuntimeContractPolicy.CurrentRevision,
                        MechanicalProgressExpected = true,
                        TimerRequired = false,
                        RunnerRequired = true,
                        RecoveryOwned = false,
                        RecoveryOwnerKind = "BatchLearning",
                        RecoveryOwnerId = "owner123",
                        RecoveryOwnerGeneration = 27,
                        RecoveryTargetPhase = "Learning",
                        SourceStateRevision = 11,
                        WarningActive = true,
                        WarningCode = "RapidLoadRise",
                        WarningRevision = 9,
                        PhaseHardDeadlineUtc = 987654
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
                    roundTrip.Heartbeat.ChannelProgress[0].ProgressVersion == 88 &&
                    roundTrip.Heartbeat.ChannelProgress[0].LastProgressUtcTicks == 876543 &&
                    roundTrip.Heartbeat.ChannelProgress[0].ProgressKind == "Learning" &&
                    roundTrip.Heartbeat.ChannelProgress[0].LifecyclePhase == "Learning" &&
                   roundTrip.Heartbeat.ChannelProgress[0].RuntimeContractRevision ==
                       WatchdogRuntimeContractPolicy.CurrentRevision &&
                   roundTrip.Heartbeat.ChannelProgress[0].RunnerRequired &&
                   roundTrip.Heartbeat.ChannelProgress[0].RecoveryOwnerKind == "BatchLearning" &&
                   roundTrip.Heartbeat.ChannelProgress[0].RecoveryOwnerGeneration == 27 &&
                   roundTrip.Heartbeat.ChannelProgress[0].WarningActive &&
                   roundTrip.Heartbeat.ChannelProgress[0].WarningCode == "RapidLoadRise" &&
                   roundTrip.Heartbeat.ChannelProgress[0].PhaseHardDeadlineUtc == 987654 &&
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
            Assert(!EpbManager.IsDaqClockRecoveryTrigger("DaqWallClockStep") &&
                   !EpbManager.RequiresDaqTaskRecreate("DaqWallClockStep"),
                "墙钟跳变错误升级为DAQ任务或机械恢复触发器");

            var defaultThreshold = new WallClockStepDetector();
            defaultThreshold.Observe(utc, 3000, 1000);
            Assert(defaultThreshold.Observe(utc.AddMilliseconds(250), 3100, 1000) != null,
                "默认墙钟跳变阈值仍高于100ms，无法及时保护圈边界");
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

        private static void ProcessRestartUiPolicyIsNonContradictory()
        {
            Assert(ProcessRestartUiPolicy.CanStartInProcess(false),
                "普通安全终态被错误闭锁");
            Assert(!ProcessRestartUiPolicy.CanStartInProcess(true),
                "必须重启终态仍允许同进程开始");
            Assert(ProcessRestartUiPolicy.LockedStartButtonText == "需重启软件",
                "闭锁按钮未明确提示必须重启");

            var watchdogMessage = ProcessRestartUiPolicy.GetOperatorMessage(true);
            var diagnosticMessage = ProcessRestartUiPolicy.GetOperatorMessage(false);
            Assert(watchdogMessage.Contains("Watchdog") &&
                   watchdogMessage.Contains("永久禁止再次开始") &&
                   !watchdogMessage.Contains("可以重新开始"),
                "Watchdog接管文案仍暗示可以同进程重新开始");
            Assert(diagnosticMessage.Contains("关闭软件后重新启动") &&
                   diagnosticMessage.Contains("永久禁止再次开始") &&
                   !diagnosticMessage.Contains("可以重新开始"),
                "诊断重启文案仍与启动闭锁互相矛盾");
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
