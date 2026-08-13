using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using Controller.Adaptive;
using DataOperation;
using IO.NI;
using Timing;

namespace AdaptiveControlTests
{
    internal static class DaqRealtimeControlTests
    {
        public static int RunFieldClockRegression()
        {
            var passed = 0;
            Run("锁定后低延迟包络连续超前才触发恢复", ClockTimelineRequiresSustainedResidualLead, ref passed);
            Run("锁定后样本时间持续滞后同样触发恢复", ClockTimelineRequiresSustainedResidualLag, ref passed);
            return passed;
        }

        internal static int RunDoCommandRingRegression()
        {
            var passed = 0;
            RunDoCommandRingRegression(ref passed);
            return passed;
        }

        private static void RunDoCommandRingRegression(ref int passed)
        {
            Run("DO固定优先级命令抢占低优先级积压", DoCommandRingPriorityPreempts, ref passed);
            Run("DO命令环64槽硬容量有界拒绝", DoCommandRingCapacityIsHardBounded, ref passed);
            Run("DO同优先级命令严格FIFO", DoCommandRingPreservesFifo, ref passed);
            Run("DO并发准入硬容量与完成回调精确一次", DoCommandRingConcurrentAdmissionCompletesExactlyOnce, ref passed);
            Run("DO预分配命令环十万次稳态零分配", DoCommandRingSteadyStateDoesNotAllocate, ref passed);
        }

        internal static int RunAll()
        {
            var passed = 0;
            Run("控制环顺序容量代次与设备隔离", RingOrderCapacityResetAndIsolation, ref passed);
            Run("控制环并发发布可见性", RingConcurrentVisibility, ref passed);
            Run("20到500ms控制延迟策略", LatencyPolicyFaultInjection, ref passed);
            Run("恢复轮询跨多批仍按连续序号累计", RecoveryVerifierAcceptsBurstProgress, ref passed);
            Run("恢复连续性故障后重新建立干净窗口", RecoveryVerifierResetsOnRealDiscontinuity, ref passed);
            Run("控制积压先追最新而回调故障才重建", DaqFastResyncRecreatePolicy, ref passed);
            Run("DAQ软件恢复持续局部退避且仅双重硬件证据报警", DaqSelfMaintenancePolicy, ref passed);
            Run("独立DAQ存活监督在带电100ms陈旧时触发且恢复期间去重", IndependentDaqLivenessSupervisorPolicy, ref passed);
            Run("DAQ存活日志转换按批次关联与参与设备有界去重", DaqLivenessLogTransitionDedup, ref passed);
            Run("未带电DAQ回调空窗只记录一次且不触发恢复", UnenergizedDaqGapObservationPolicy, ref passed);
            Run("后台冻结边界结果逐项报告Published与Raw未闭合谓词", BackgroundDrainResultExplainsPendingPredicate, ref passed);
            Run("恢复阶段只在终态导出完整重证据", IncidentSnapshotHeavyEvidencePolicy, ref passed);
            Run("百次事故症状共用容量2取证门且终态精确一次", IncidentEvidenceQueueIsBoundedAndCoalesced, ref passed);
            Run("同一批次双DAQ各自导出trigger和terminal且队列有界", DualDeviceIncidentEvidenceRootsRemainIndependent, ref passed);
            Run("DAQ事故根目录只由RunId和关联号决定", DaqIncidentEvidenceDirectoryIdentity, ref passed);
            Run("恢复边界矛盾只锁存首个原因并只允许一次", RecoveryBoundaryContradictionFirstWins, ref passed);
            Run("DAQ恢复先恢复安全电源再做机械定位", DaqRecoveryPrerequisiteOrder, ref passed);
            Run("DAQ截止在所有权等待前按暂停冻结抑制取消断电排序", DaqCutoffPreOwnershipSafetyOrder, ref passed);
            Run("Stop在恢复等待前先撤权暂停断电并启动电源关闭", StopPreRecoveryWaitSafetyOrder, ref passed);
            Run("Stop预取消调用令牌不能撤销物理电源关闭", StopPowerDisableIgnoresCallerCancellation, ref passed);
            Run("故障隔离先整组异步断电和电源关闭再启动拒绝项兜底", NonBlockingSafetyIsolationOrder, ref passed);
            Run("学习和资格通道即使没有定时器也恢复供电", DaqRecoveryIncludesRunnerOnlyChannels, ref passed);
            Run("UI发布限频不影响首批和周期后批次", UiDispatchGateUsesMonotonicRateLimit, ref passed);
            Run("UI最新值唤醒在阻塞期间只保留一个待处理信号", UiLatestValueSignalCoalescesBacklog, ref passed);
            Run("UI双设备邮箱十万批阻塞后每设备只保留最新一批", UiLatestPairMailboxStaysBounded, ref passed);
            Run("UI跳过显示批次保持连线而真实DAQ断点仍断笔", UiCurveBreakUsesAcquisitionTimeline, ref passed);
            Run("UI日志原位裁剪与滚动限频保持有界无分配", UiLogDisplayPolicyIsBoundedAndAllocationFree, ref passed);
            Run("DAQ陈旧根因区分回调与控制消费", DaqStaleRootClassification, ref passed);
            Run("DAQ批次和兼容队列包装不再持续分配", DaqBatchObjectsAreReusableValueBacked, ref passed);
            Run("后台工程与Raw预分配SPSC环容量并发零分配", PreallocatedSpscRingTests.RunAll, ref passed);
            Run("旧原始二进制写入池化后格式保持不变", LegacyRawWriterKeepsBinaryFormat, ref passed);
            Run("标定前原始数据复制后不被原地标定污染", OwnedRawBatchPreservesPreCalibrationValues, ref passed);
            Run("Stat流式中值保留跨批次尾部", StreamingStatMedianCarriesTailAcrossBatches, ref passed);
            Run("恢复后定时器只在未来完整周期锚点执行", TimerResumesAtFutureCompleteBoundary, ref passed);
            Run("暂停数据链必须越过捕获边界且无在途Raw", PauseDrainRequiresAllPipelineBoundaries, ref passed);
            Run("停止边界排除已编号但未被后台接收的末批", RejectedFinalBatchDoesNotPoisonStopBoundary, ref passed);
            Run("处理队列入口拒绝先锁存永久空洞再发布故障", ProcessingAdmissionRejectLatchesBeforeFaultPublication, ref passed);
            Run("处理空洞锁存阻止更大水位伪装完整前缀", ProcessingGapClampsPublishedWatermark, ref passed);
            Run("处理线程提交点后异常从下一批监督重入", ProcessingFaultAfterCommitDoesNotStrandTail, ref passed);
            Run("处理容量1024仍由500ms独立时效门限提前发现", ProcessingWatchdogUsesOldestAcceptedAge, ref passed);
            Run("Raw订阅失败不得伪装所有权已移交", RawTransferRequiresAuthoritativeAcceptance, ref passed);
            Run("Raw权威接收者即时Dispose后兼容快照仍安全", RawOwnerImmediateDisposeStillFeedsLegacySnapshot, ref passed);
            Run("Raw池化所有权拒绝多个权威订阅者", RawOwnerContractRejectsSecondSubscriber, ref passed);
            Run("Dev1与Dev2 Raw发布队列和专用线程完全隔离", RawPublicationPipelinesArePerDevice, ref passed);
            Run("Raw末端准入有界返回且超时仍保留所有权", RawTerminalAdmissionTimeoutIsBounded, ref passed);
            Run("Raw连续移交超时按真实经过时间晋升", RawTransferPromotionUsesElapsedTime, ref passed);
            Run("Raw与Stat最终落盘调用均受硬截止约束", RawAndStatFlushDeadlinesAreBounded, ref passed);
            Run("DAQ代次失效后已接收批次只退出实时链但继续归档", InvalidatedGenerationStillArchivesAcceptedBatch, ref passed);
            Run("恢复成功超时停止硬件确认并发只提交一个终态", RecoveryTerminalGateCommitsExactlyOnce, ref passed);
            Run("DAQ探测能力缺失不能误确认为硬件拔除", ProbeCapabilityMissingIsNotHardwareEvidence, ref passed);
            Run("DAQ事故关联去重优先级与新运行复位", IncidentCorrelationAndPriority, ref passed);
            Run("同设备百次事故症状只生成一个上下文触发和终态", HundredIncidentSymptomsMergeIntoOneContext, ref passed);
            Run("DAQ恢复终态结束旧事故且后续故障使用新关联号", IncidentCompletionStartsNewCorrelation, ref passed);
            Run("DAQ恢复期间旧运行和软预警不能覆盖恢复状态", DaqRecoveryStateCannotRegress, ref passed);
            RunDoCommandRingRegression(ref passed);
            Run("高优先级DO超时不回退到调用线程无限等待", HighPriorityDoTimeoutIsBounded, ref passed);
            Run("同通道重复OFF合并且不再分配未释放等待句柄", HighPriorityDoCoalescesDuplicateOff, ref passed);
            Run("异步OFF合并后每个提交者获得唯一完成", HighPriorityDoAsyncCoalescingCompletesEverySubmitter, ref passed);
            Run("异步OFF完成槽有界且关闭竞态不丢回调", HighPriorityDoCompletionCapacityAndDisposeRace, ref passed);
            Run("异步OFF准入与关闭线性化不丢已接纳回调", HighPriorityDoAdmissionAndStopAreLinearized, ref passed);
            Run("异步OFF阻塞不拖累DAQ订阅与另一设备", AdaptiveTerminalOffSubmissionIsNonBlockingAndIsolated, ref passed);
            Run("异步OFF硬截止一次联锁且迟到只补证据", AdaptiveTerminalOffDeadlineCommitsExactlyOnce, ref passed);
            Run("异步OFF物理失败只触发一次组级升级", SubmittedTerminalOffFailureEscalatesExactlyOnce, ref passed);
            Run("DAQ后台同根任务合并并在退出前观察异常", DaqBackgroundTasksAreCoalescedAndDrained, ref passed);
            Run("DO报警时间线保留兼容列并追加命令耗时", DoTimelineAppendsCommandElapsed, ref passed);
            Run("电源组重复联锁复用关联且允许重发安全动作", EmergencyPowerGroupLatchKeepsCorrelation, ref passed);
            Run("DAQ事故先断电后发布诊断", DaqSafetyActionsPrecedePublication, ref passed);
            Run("十万稳态样本控制计算无持续分配", AdaptiveHotLoopDoesNotAllocate, ref passed);
            Run("六通道学习节拍协作等待不再整毫秒自旋", CooperativeLearningCadenceDoesNotBurnCpu, ref passed);
            Run("因果中值滤波跨批正确且原地无持续分配", CausalMedianInPlaceIsCorrectAndAllocationFree, ref passed);
            Run("普通轨迹25Hz且动作轨迹不降采样", AdaptiveTraceRateAndActionRetention, ref passed);
            Run("控制诊断记录真实64批容量", ControlDiagnosticsUseRealCapacity, ref passed);
            Run("构建身份包含版本哈希位数与Git状态", BuildIdentityIsAuditable, ref passed);
            Run("正式包逐文件哈希阻止VS覆盖后继续试验", ReleasePackageVerificationRejectsMixedBuild, ref passed);
            Run("1Hz主机探针包含CPU内存句柄线程与磁盘余量", HostRuntimeProbeCapturesAuditableState, ref passed);
            Run("最后项目跨版本恢复且不可用时保留选择", LastProjectSelectionSurvivesUpgradeAndUnavailableStorage, ref passed);
            Run("DAQ生产区拒绝重叠回调", ProducerGateRejectsOverlap, ref passed);
            Run("采样时间按设备起点和累计样本推进", AcquisitionTimelineNeverSnapsCatchUpToFuture, ref passed);
            Run("Dev1与Dev2相反ppm连续72小时独立锁定", ClockTimelinesTrackOppositePpmFor72Hours, ref passed);
            Run("现场追赶回调不再触发时间轴故障", ClockTimelineReplaysFieldCatchUpWithoutFault, ref passed);
            Run("UTC前后跳变不改变单调采样时间轴", ClockTimelineIgnoresUtcJumps, ref passed);
            Run("时钟越界必须连续确认才失效", ClockTimelineRequiresConsecutiveInvalidEvidence, ref passed);
            Run("锁定后低延迟包络连续超前才触发恢复", ClockTimelineRequiresSustainedResidualLead, ref passed);
            Run("锁定后样本时间持续滞后同样触发恢复", ClockTimelineRequiresSustainedResidualLag, ref passed);
            Run("DAQ时钟恢复十分钟前三次允许第四次锁存", ClockRecoveryAttemptWindowIsBounded, ref passed);
            Run("自适应时钟十万批稳态无持续分配", ClockTimelineHotLoopDoesNotAllocate, ref passed);
            Run("旧TimelineFuture标志不再使有效电流失效", LegacyTimelineFlagIsDiagnosticOnly, ref passed);
            Run("控制批携带序号单调时钟和原始尾部", ControlBatchCarriesIdentityClockAndRawTail, ref passed);
            Run("快速控制复制完成后才移交后台原始批次", RawBatchOwnershipTransfersAfterControlCopy, ref passed);
            Run("Dev1和Dev2各十万批所有权移交不污染快速证据", RawBatchOwnershipStressForBothDevices, ref passed);
            Run("V2.10.2.1现场二次换算序列修复后不再过流", FieldIncidentReplayStaysBelowOverCurrent, ref passed);
            Run("控制消费拒绝重复倒序和非递增tick", ControlBatchIdentityRejectsInvalidOrder, ref passed);
            Run("每设备快速滤波隔离并按代次复位", FastFiltersAreIsolatedAndResettable, ref passed);
            Run("快速滤波十万次稳态更新无持续分配", FastFilterHotLoopDoesNotAllocate, ref passed);
            Run("启动定位相同批次不重复计数", StartupClassifierIgnoresDuplicateBatch, ref passed);
            Run("未来墙钟不改变控制经过时间", FutureWallClockDoesNotChangeControlElapsed, ref passed);
            Run("快速过流由全速率证据最终归因", FastTripClassificationUsesFullRateEvidence, ref passed);
            return passed;
        }

        private static void HostRuntimeProbeCapturesAuditableState()
        {
            var snapshot = HostRuntimeProbe.Capture();
            Assert(snapshot.ProcessId == Process.GetCurrentProcess().Id, "进程ID不一致");
            Assert(snapshot.ProcessBitness == 32 || snapshot.ProcessBitness == 64, "进程位数无效");
            Assert(snapshot.ProcessCpuPercent >= 0 && snapshot.ProcessCpuPercent <= 100, "进程CPU超界");
            Assert(snapshot.SystemCpuPercent >= 0 && snapshot.SystemCpuPercent <= 100, "系统CPU超界");
            Assert(snapshot.OtherCpuPercent >= 0 && snapshot.OtherCpuPercent <= 100, "外部进程合计CPU超界");
            Assert(snapshot.WorkingSetBytes > 0, "工作集未采集");
            Assert(snapshot.PrivateMemoryBytes > 0, "专用内存未采集");
            Assert(snapshot.VirtualMemoryBytes > 0, "虚拟内存未采集");
            Assert(snapshot.HandleCount > 0, "句柄数未采集");
            Assert(snapshot.ThreadCount > 0, "线程数未采集");
            Assert(snapshot.SystemAvailableMemoryBytes > 0, "系统可用内存未采集");
            Assert(snapshot.ProgramDriveFreeBytes > 0, "程序盘余量未采集");
            Assert(double.IsNaN(snapshot.DiskQueueLength) || snapshot.DiskQueueLength >= 0, "磁盘队列指标无效");
            Assert(double.IsNaN(snapshot.DiskReadBytesPerSecond) || snapshot.DiskReadBytesPerSecond >= 0,
                "磁盘读取速率无效");
            Assert(double.IsNaN(snapshot.DiskWriteBytesPerSecond) || snapshot.DiskWriteBytesPerSecond >= 0,
                "磁盘写入速率无效");
        }

        private static void CooperativeLearningCadenceDoesNotBurnCpu()
        {
            const int channelCount = 6;
            const int periodsPerChannel = 120;
            const int intervalMs = 3;
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, channelCount)
                .Select(async _ =>
                {
                    for (var period = 0; period < periodsPerChannel; period++)
                    {
                        var due = Stopwatch.GetTimestamp() +
                                  (long)(intervalMs / 1000.0 * Stopwatch.Frequency);
                        var reached = await EpbCycleRunner.DelayUntilMonotonicAsync(
                                due,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        Assert(reached >= due, "协作等待在单调截止前提前返回");
                    }
                })
                .ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            wall.Stop();
            process.Refresh();
            var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var wallMs = Math.Max(1, wall.Elapsed.TotalMilliseconds);
            Assert(cpuMs < wallMs * 1.25,
                $"六通道学习节拍仍接近持续自旋：CpuMs={cpuMs:F1} WallMs={wallMs:F1}");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                EpbCycleRunner.DelayUntilMonotonicAsync(
                        Stopwatch.GetTimestamp() + Stopwatch.Frequency,
                        cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException("已取消的协作等待没有退出");
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        private static void PauseDrainRequiresAllPipelineBoundaries()
        {
            Assert(TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 200, 200, 100, 200),
                "全部边界已发布且无Raw在途时未判定排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    99, 100, 200, 200, 100, 200),
                "Dev1尚未越过捕获边界时错误完成暂停排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 199, 200, 100, 200),
                "Dev2尚未越过捕获边界时错误完成暂停排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 200, 200, 99, 200),
                "Dev1 Raw尚未移交到写盘队列时错误完成暂停排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 200, 200, 100, 199),
                "Dev2 Raw尚未移交到写盘队列时错误完成暂停排空");
        }

        private static void RejectedFinalBatchDoesNotPoisonStopBoundary()
        {
            var sequence = new DaqPipelineSequenceState();
            var accepted = sequence.Allocate();
            sequence.Accept(accepted);
            var rejected = sequence.Allocate();

            Assert(sequence.LastAllocated == rejected && rejected == accepted + 1,
                "拒绝批次没有保留诊断序号");
            Assert(sequence.LastAccepted == accepted,
                "未入后台队列的末批错误扩大了停止耐久边界");
            Assert(sequence.LastObserved == rejected &&
                   TwoDeviceAiAcquirer.IsPermanentContinuityGapObserved(
                       rejected,
                       sequence.LastObserved),
                "最终拒绝序号大于LastAccepted时永久处理空洞被错误隐藏");
            Assert(TwoDeviceAiAcquirer.ClampPublishedBeforeGap(
                       sequence.LastAccepted,
                       rejected) == accepted,
                "最终拒绝批次错误收紧或扩大了可证明的连续接收前缀");
            Assert(TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    accepted, sequence.LastAccepted, 0, 0, accepted, 0),
                "已接收前缀全部发布后仍被不存在的拒绝末批永久阻塞");
        }

        private static void ProcessingAdmissionRejectLatchesBeforeFaultPublication()
        {
            var type = typeof(TwoDeviceAiAcquirer);
            var enqueue = type.GetMethod(
                "EnqueueForProcessing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var latch = type.GetMethod(
                "LatchProcessingGapIfUnpublished",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var publish = type.GetMethod(
                "PublishQueueFullFault",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(enqueue != null && latch != null && publish != null,
                "无法读取处理队列入口的连续性锁存调用链");

            var il = enqueue.GetMethodBody()?.GetILAsByteArray();
            Assert(il != null && il.Length > 0, "EnqueueForProcessing没有可审计IL");
            var latchOffsets = FindMetadataTokenOffsets(il, latch.MetadataToken);
            var publishOffsets = FindMetadataTokenOffsets(il, publish.MetadataToken);
            Assert(latchOffsets.Count >= 2 && latchOffsets.Count == publishOffsets.Count,
                "处理入口的准入满或环不变量拒绝分支没有成对锁存空洞并发布故障");
            for (var i = 0; i < latchOffsets.Count; i++)
            {
                Assert(latchOffsets[i] < publishOffsets[i],
                    "入口拒绝先发布故障后锁存空洞，同步停止可能冻结错误边界");
            }
        }

        private static void InvalidatedGenerationStillArchivesAcceptedBatch()
        {
            Assert(TwoDeviceAiAcquirer.ClassifyAcceptedBatch(true) ==
                   AcceptedBatchDisposition.LiveAndArchive,
                "当前代次批次未同时进入实时链和归档链");
            Assert(TwoDeviceAiAcquirer.ClassifyAcceptedBatch(false) ==
                   AcceptedBatchDisposition.ArchiveOnly,
                "失效代次已接收批次被错误丢弃或重新进入实时控制");
        }

        private static void ProcessingGapClampsPublishedWatermark()
        {
            Assert(TwoDeviceAiAcquirer.ClampPublishedBeforeGap(250, 0) == 250,
                "无空洞时发布水位被错误收紧");
            Assert(TwoDeviceAiAcquirer.ClampPublishedBeforeGap(250, 201) == 200,
                "更大发布序号越过首个处理空洞并伪装成完整前缀");
            Assert(TwoDeviceAiAcquirer.ClampPublishedBeforeGap(150, 201) == 150,
                "尚未到达空洞时发布水位被错误扩大或收紧");

            // 队列在序号2拒绝后即使回调短暂继续并接收了序号3，序号3也只是
            // 明确作废的尾段；进程回收边界必须稳定停在连续前缀1。
            var sequence = new DaqPipelineSequenceState();
            var accepted1 = sequence.Allocate();
            sequence.Accept(accepted1);
            var rejected2 = sequence.Allocate();
            var accepted3 = sequence.Allocate();
            sequence.Accept(accepted3);
            Assert(accepted1 == 1 && rejected2 == 2 && accepted3 == 3 &&
                   sequence.LastAccepted == 3 && sequence.LastObserved == 3,
                "处理入口拒绝回放的序号/接收证据错误");
            Assert(TwoDeviceAiAcquirer.IsPermanentContinuityGapObserved(
                       rejected2,
                       sequence.LastObserved) &&
                   TwoDeviceAiAcquirer.ClampPublishedBeforeGap(
                       sequence.LastAccepted,
                       rejected2) == 1,
                "accept1-reject2-accept3错误形成大于1的进程回收边界");

            var stop = new StopSafetyResult
            {
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = true,
                PersistenceBoundaryConfirmed = true,
                LogicalQuiescenceConfirmed = true,
                DataContinuityCompromised = true
            };
            Assert(stop.FullyConfirmed && stop.CanCloseApplication,
                "已记录空洞的安全前缀闭合后仍阻止无人值守进程回收");
            Assert(!stop.CanRestartInProcess,
                "不可重放处理空洞错误允许同进程继续运行");
            Assert(EpbManager.MustBlockPauseOrResumeForContinuity(true, false) &&
                   EpbManager.MustBlockPauseOrResumeForContinuity(false, true) &&
                   !EpbManager.MustBlockPauseOrResumeForContinuity(false, false),
                "普通Pause/Resume没有将任一DAQ设备的永久数据gap作为同进程硬门禁");
            Assert(EpbManager.SelectBatchResumeFailureState(true, true) == BatchPauseState.Idle &&
                   EpbManager.SelectBatchResumeFailureState(true, false) == BatchPauseState.Stopping &&
                   EpbManager.SelectBatchResumeFailureState(false, false) == BatchPauseState.Paused,
                "永久DAQ数据空洞后批次仍会错误提交为可再次恢复的Paused");
        }

        private static void ProcessingFaultAfterCommitDoesNotStrandTail()
        {
            Assert(TwoDeviceAiAcquirer.IsAcceptedProcessingBatchPublished(240, 240),
                "当前批已完成SQLite所有权移交却未识别提交点");
            Assert(!TwoDeviceAiAcquirer.IsAcceptedProcessingBatchPublished(240, 239),
                "尚未完成SQLite所有权移交却被误判为已提交");
            Assert(TwoDeviceAiAcquirer.GetFirstUnprocessedSequenceAfterWorkerFault(
                       240, 240, 241, 260) == 0,
                "提交点后UI/诊断异常仍锁存当前批并阻止消费者监督重入");
            Assert(TwoDeviceAiAcquirer.GetFirstUnprocessedSequenceAfterWorkerFault(
                       240, 239, 241, 260) == 240,
                "提交点前异常没有锁存当前在途批次");
            Assert(TwoDeviceAiAcquirer.GetFirstUnprocessedSequenceAfterWorkerFault(
                       0, 240, 241, 260) == 241,
                "无在途标记时没有锁存真实队头首个未处理序号");
        }

        private static void ProcessingWatchdogUsesOldestAcceptedAge()
        {
            var now = Stopwatch.Frequency * 20L;
            var queued = now - (long)(Stopwatch.Frequency * 0.60);
            var inFlight = now - (long)(Stopwatch.Frequency * 0.30);
            var age = TwoDeviceAiAcquirer.GetOldestProcessingAgeMs(queued, inFlight, now);
            Assert(age >= 599 && age <= 601,
                $"看门狗未按队列/在途中的最老已接纳批次计龄：{age:F1}ms");
            Assert(TwoDeviceAiAcquirer.GetOldestProcessingAgeMs(0, inFlight, now) >= 299,
                "队列为空时忽略了仍在处理的批次");
            Assert(TwoDeviceAiAcquirer.GetOldestProcessingAgeMs(0, 0, now) == 0,
                "空流水线被误判为陈旧");
        }

        private static void RawTransferRequiresAuthoritativeAcceptance()
        {
            Assert(!TwoDeviceAiAcquirer.IsRawPublicationComplete(true, false, false, false),
                "正式Raw接收者抛错后仍被伪装为已移交");
            Assert(TwoDeviceAiAcquirer.IsRawPublicationComplete(true, true, true, false),
                "正式Raw所有权已接收却被兼容观察者异常撤销");
            Assert(TwoDeviceAiAcquirer.IsRawPublicationComplete(false, false, true, true),
                "仅兼容Raw链成功时未完成发布");
            Assert(!TwoDeviceAiAcquirer.IsRawPublicationComplete(false, false, true, false),
                "兼容Raw链失败仍推进Transferred水位");
        }

        private static void RawOwnerImmediateDisposeStillFeedsLegacySnapshot()
        {
            var now = DateTime.UtcNow;
            var batch = OwnedDaqRawBatch.CopyFrom(
                "Dev1",
                new[,] { { 1.25, 2.5 }, { 3.75, 5.0 } },
                now,
                now.AddMilliseconds(-1),
                77);
            var ownerCalled = false;
            var legacyCalled = false;
            var reRentedOriginalBuffer = false;
            var rentedAfterDispose = new List<double[]>();
            var accepted = TwoDeviceAiAcquirer.DispatchRawSubscribersExactOnce(
                batch,
                owned =>
                {
                    ownerCalled = true;
                    // 模拟权威接收者在返回前已由末端消费者完成并归还池化数组。
                    var returnedBuffer = owned.Values;
                    owned.Dispose();
                    // 立即从共享池并发重租并覆写；即使正好拿回同一数组，legacy 也必须
                    // 只读取移交前的独立快照，而不能观察复用后的内容。
                    for (var attempt = 0; attempt < 64; attempt++)
                    {
                        var rented = ArrayPool<double>.Shared.Rent(returnedBuffer.Length);
                        rentedAfterDispose.Add(rented);
                        if (!ReferenceEquals(rented, returnedBuffer)) continue;
                        reRentedOriginalBuffer = true;
                        for (var index = 0; index < rented.Length; index++) rented[index] = -999.0;
                        break;
                    }
                },
                (device, matrix, current, last) =>
                {
                    legacyCalled = true;
                    Assert(device == "Dev1" && current == now && last == now.AddMilliseconds(-1),
                        "兼容Raw快照元数据错误");
                    Assert(Math.Abs(matrix[0, 0] - 1.25) < 1e-12 &&
                           Math.Abs(matrix[1, 1] - 5.0) < 1e-12,
                        "权威接收者即时Dispose后兼容快照读取了已归还对象池的数组");
                },
                out var legacyError);
            foreach (var rented in rentedAfterDispose)
                ArrayPool<double>.Shared.Return(rented, clearArray: false);
            Assert(accepted && ownerCalled && legacyCalled && legacyError == null,
                "Raw权威所有权和兼容快照未按精确一次顺序完成");
            Assert(reRentedOriginalBuffer,
                "测试未实际覆盖权威接收者Dispose后同一池化数组被立即重租的ABA场景");

            var legacyFaultBatch = OwnedDaqRawBatch.CopyFrom(
                "Dev1", new[,] { { 6.0 } }, now, now, 78);
            var ownerAcceptedDespiteLegacyFault = TwoDeviceAiAcquirer.DispatchRawSubscribersExactOnce(
                legacyFaultBatch,
                owned => owned.Dispose(),
                (_, _, _, _) => throw new InvalidOperationException("legacy fault"),
                out var isolatedLegacyError);
            Assert(ownerAcceptedDespiteLegacyFault && isolatedLegacyError is InvalidOperationException,
                "兼容观察者异常撤销了已完成的Raw权威所有权移交");
        }

        private static void RawOwnerContractRejectsSecondSubscriber()
        {
            Action<OwnedDaqRawBatch> first = _ => { };
            Action<OwnedDaqRawBatch> second = _ => { };
            var current = TwoDeviceAiAcquirer.AddSingleOwnedRawSubscriber(null, first);
            var rejected = false;
            try { _ = TwoDeviceAiAcquirer.AddSingleOwnedRawSubscriber(current, second); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(current == first && rejected,
                "多个Raw权威订阅者仍可形成前一订阅者接纳、后一订阅者抛错后的重试/双重Dispose");
        }

        private static void RawPublicationPipelinesArePerDevice()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(TwoDeviceAiAcquirer);
            foreach (var suffix in new[] { "Queue", "Signal", "Slots", "Worker" })
            {
                Assert(type.GetField("_rawPublication" + suffix + "Dev1", flags) != null,
                    "Dev1 Raw" + suffix + "未独立定义");
                Assert(type.GetField("_rawPublication" + suffix + "Dev2", flags) != null,
                    "Dev2 Raw" + suffix + "未独立定义");
            }
        }

        private static void RawTerminalAdmissionTimeoutIsBounded()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "epb-raw-admission-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var context = new DaqAIContext("Dev1", 1, 60, 1, 1, 1, directory);
            OwnedDaqRawBatch rejected = null;
            try
            {
                var now = DateTime.UtcNow;
                for (var sequence = 1; sequence <= context.RawQueueCapacity; sequence++)
                {
                    var accepted = OwnedDaqRawBatch.CopyFrom(
                        "Dev1", new[,] { { (double)sequence } }, now, now.AddMilliseconds(-1), sequence);
                    context.EnqueueRawData(accepted);
                }

                // 只在队列已经填满后启用派生统计，证明失败准入不会在每次原序重试时
                // 重复累计同一 Owned 批次。
                context.eMBToDaqCurrentChannel = new SortedDictionary<string, int>
                {
                    ["EPB1_current"] = 0
                };
                context.paraNameToScale["EPB1_current"] = 1;
                context.paraNameToOffset["EPB1_current"] = 0;
                context.paraNameToZeroValue["EPB1_current"] = 0;

                rejected = OwnedDaqRawBatch.CopyFrom(
                    "Dev1", new[,] { { 999.0 } }, now, now.AddMilliseconds(-1), 999);
                var elapsed = Stopwatch.StartNew();
                var timedOut = false;
                try { context.EnqueueRawData(rejected); }
                catch (TimeoutException) { timedOut = true; }
                elapsed.Stop();
                Assert(timedOut, "Raw末端队列满后仍无限等待或错误接纳超容量批次");
                Assert(elapsed.ElapsedMilliseconds < 2000,
                    $"Raw末端准入没有有界返回：{elapsed.ElapsedMilliseconds}ms");
                Assert(Math.Abs(rejected[0, 0] - 999.0) < 1e-12,
                    "准入超时后上游所有权已被错误Dispose/归还对象池");
                var statCountsField = typeof(DaqAIContext).GetField(
                    "statMedianCounts",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(statCountsField != null, "未找到Raw派生统计计数器");
                var countsAfterReject = (int[])statCountsField.GetValue(context);
                Assert(countsAfterReject.Length == 0 || countsAfterReject[0] == 0,
                    "Raw准入失败时已提前累计派生统计，原序重试会重复计数");

                context.FlushRawToDiskAsync().GetAwaiter().GetResult();
                context.EnqueueRawData(rejected);
                rejected = null; // 所有权已转移给 context。
                var countsAfterRetry = (int[])statCountsField.GetValue(context);
                Assert(countsAfterRetry.Length == 1 && countsAfterRetry[0] == 1,
                    "Raw解除背压后的唯一成功准入没有精确累计一次派生统计");
            }
            finally
            {
                rejected?.Dispose();
                try { context.FlushRawToDiskAsync().GetAwaiter().GetResult(); } catch { }
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void RawTransferPromotionUsesElapsedTime()
        {
            var start = Stopwatch.Frequency * 10L;
            var before = start + (long)(Stopwatch.Frequency * 29.999);
            var deadline = start + Stopwatch.Frequency * 30L;
            Assert(!TwoDeviceAiAcquirer.HasTransferTimedOut(start, before, 30000),
                "Raw瞬时/短时背压被提前晋升为永久数据空洞");
            Assert(TwoDeviceAiAcquirer.HasTransferTimedOut(start, deadline, 30000),
                "Raw持续阻塞30秒仍未晋升永久数据空洞");
        }

        private static void RawAndStatFlushDeadlinesAreBounded()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "epb-flush-deadline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var context = new DaqAIContext("Dev1", 1, 60, 1, 1, 1, directory);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var rawGate = (SemaphoreSlim)typeof(DaqAIContext)
                .GetField("rawQueueGate", flags)?.GetValue(context);
            var statGate = (SemaphoreSlim)typeof(DaqAIContext)
                .GetField("statFileLock", flags)?.GetValue(context);
            Assert(rawGate != null && statGate != null, "Raw/Stat落盘门禁字段不可用");
            var rawHeldByTest = false;
            var statHeldByTest = false;
            try
            {
                rawGate.Wait();
                rawHeldByTest = true;
                var rawElapsed = Stopwatch.StartNew();
                var rawTimedOut = false;
                try { context.FlushRawToDiskAsync(100, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (TimeoutException) { rawTimedOut = true; }
                rawElapsed.Stop();
                Assert(rawTimedOut && rawElapsed.ElapsedMilliseconds < 2000,
                    $"Raw落盘永久等待未被deadline切断：{rawElapsed.ElapsedMilliseconds}ms");
                rawGate.Release();
                rawHeldByTest = false;

                statGate.Wait();
                statHeldByTest = true;
                var statElapsed = Stopwatch.StartNew();
                var statTimedOut = false;
                try { context.FlushStatToDiskAsync(100, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (TimeoutException) { statTimedOut = true; }
                statElapsed.Stop();
                Assert(statTimedOut && statElapsed.ElapsedMilliseconds < 2000,
                    $"Stat落盘永久等待未被deadline切断：{statElapsed.ElapsedMilliseconds}ms");
                statGate.Release();
                statHeldByTest = false;
            }
            finally
            {
                if (rawHeldByTest) rawGate.Release();
                if (statHeldByTest) statGate.Release();
                Thread.Sleep(50);
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void RingOrderCapacityResetAndIsolation()
        {
            var dev1 = new ControlBatchRing(64, 2);
            var dev2 = new ControlBatchRing(64, 2);
            var sample = new FastControlSampleValue[1];
            for (var sequence = 0; sequence < 64; sequence++)
            {
                sample[0] = new FastControlSampleValue(4, sequence);
                Assert(dev1.TryEnqueue(7, DateTime.UtcNow, sequence + 1, sample),
                    "Dev1在容量前拒绝入队");
            }
            Assert(dev1.Depth == 64 && dev1.Capacity == 64, "控制环真实容量或深度错误");
            Assert(!dev1.TryEnqueue(7, DateTime.UtcNow, 100, sample), "满环发生静默覆盖");
            Assert(dev2.Depth == 0, "Dev1积压污染Dev2");

            var destination = new FastControlSampleValue[2];
            for (var sequence = 0; sequence < 64; sequence++)
            {
                Assert(dev1.TryDequeue(destination, out var count, out var generation,
                    out _, out _), "控制环提前变空");
                Assert(count == 1 && generation == 7 && destination[0].Amps == sequence,
                    "控制环FIFO顺序或代次错误");
            }

            sample[0] = new FastControlSampleValue(4, 99);
            Assert(dev1.TryEnqueue(7, DateTime.UtcNow, 1, sample), "旧代次测试入队失败");
            dev1.Reset();
            Assert(dev1.Depth == 0 && !dev1.TryDequeue(destination, out _, out _, out _, out _),
                "新运行复位未清除旧代次批");
            sample[0] = new FastControlSampleValue(4, 100);
            Assert(dev1.TryEnqueue(8, DateTime.UtcNow, 2, sample), "新代次入队失败");
            Assert(dev1.TryDequeue(destination, out _, out var newGeneration, out _, out _) &&
                   newGeneration == 8, "旧代次进入新运行");
        }

        private static void RingConcurrentVisibility()
        {
            const int total = 20000;
            var ring = new ControlBatchRing(64, 1);
            var failure = string.Empty;
            var consumed = 0;
            var producer = new Thread(() =>
            {
                var item = new FastControlSampleValue[1];
                for (var sequence = 0; sequence < total && failure.Length == 0; sequence++)
                {
                    item[0] = new FastControlSampleValue(4, sequence);
                    while (!ring.TryEnqueue(3, DateTime.UtcNow, sequence + 1, item))
                        Thread.Yield();
                }
            });
            var consumer = new Thread(() =>
            {
                var destination = new FastControlSampleValue[1];
                while (consumed < total && failure.Length == 0)
                {
                    if (!ring.TryDequeue(destination, out _, out var generation, out _, out _))
                    {
                        Thread.Yield();
                        continue;
                    }
                    if (generation != 3 || destination[0].Amps != consumed)
                    {
                        failure = $"并发读取不一致 expected={consumed} actual={destination[0].Amps}";
                        return;
                    }
                    consumed++;
                }
            });
            producer.Start();
            consumer.Start();
            Assert(producer.Join(5000) && consumer.Join(5000), "控制环并发测试超时");
            Assert(failure.Length == 0 && consumed == total, failure.Length == 0 ? "批数不完整" : failure);
        }

        private static void LatencyPolicyFaultInjection()
        {
            Assert(Policy(20, true) == ControlLatencyAction.Healthy, "20ms不应预警");
            Assert(Policy(50, true) == ControlLatencyAction.Warning, "50ms应内部预警");
            Assert(Policy(80, true) == ControlLatencyAction.Warning, "80ms应可追赶且不硬停");
            Assert(Policy(120, true) == ControlLatencyAction.HardFault, "120ms带电未硬停");
            Assert(Policy(150, true) == ControlLatencyAction.HardFault, "150ms回调空窗带电未硬停");
            Assert(Policy(500, true) == ControlLatencyAction.HardFault, "500ms带电未硬停");
            Assert(Policy(1000, true) == ControlLatencyAction.HardFault, "1s回调空窗带电未硬停");
            Assert(Policy(4000, true) == ControlLatencyAction.HardFault, "4s回调空窗带电未硬停");
            Assert(Policy(120, false) == ControlLatencyAction.ResynchronizeInactive,
                "120ms断电积压未重同步");
            Assert(Policy(500, false) == ControlLatencyAction.ResynchronizeInactive,
                "500ms断电积压不应报警");
        }

        private static ControlLatencyAction Policy(double ageMs, bool active)
        {
            return ControlLatencyPolicy.Evaluate(ageMs, active, 50, 100);
        }

        private static void RecoveryVerifierAcceptsBurstProgress()
        {
            var verifier = new DaqRecoveryFreshnessVerifier(6, 10);
            verifier.Seed(Freshness(6, 100, 1000, 0));
            Assert(!verifier.Observe(Freshness(6, 103, 1010, 0)),
                "首个+3突发被过早判定完成");
            Assert(verifier.FreshCallbacks == 3 && verifier.FirstVerifiedSequence == 101,
                $"+3突发没有按真实连续批数累计，Count={verifier.FreshCallbacks}");
            Assert(verifier.Observe(Freshness(6, 110, 1020, 0)),
                "轮询跨过连续+7批后未满足10批恢复条件");
            Assert(verifier.FreshCallbacks == 10 && verifier.LastVerifiedSequence == 110,
                "恢复序号范围记录错误");
        }

        private static void RecoveryVerifierResetsOnRealDiscontinuity()
        {
            var verifier = new DaqRecoveryFreshnessVerifier(6, 3);
            verifier.Seed(Freshness(6, 200, 2000, 0));
            Assert(!verifier.Observe(Freshness(6, 202, 2010, 1)),
                "真实连续性故障被误判为恢复完成");
            Assert(verifier.FreshCallbacks == 0 && verifier.FirstVerifiedSequence == 0,
                "连续性故障后恢复窗口未清零");
            Assert(verifier.Observe(Freshness(6, 205, 2020, 1)),
                "故障后新的3个连续批次未重新建立恢复窗口");

            var stale = Freshness(6, 206, 2030, 1);
            stale.IsFresh = false;
            Assert(!verifier.Observe(stale) && verifier.FreshCallbacks == 0,
                "陈旧样本没有清除恢复证据");
        }

        private static DaqFreshnessSnapshot Freshness(
            long generation,
            long sequence,
            long arrivalTicks,
            long discontinuities)
        {
            return new DaqFreshnessSnapshot
            {
                Device = "Dev2",
                Generation = generation,
                LastProcessedSequence = sequence,
                LastArrivalMonotonicTicks = arrivalTicks,
                ControlDiscontinuityCount = discontinuities,
                LastControlDiscontinuitySequence = discontinuities > 0 ? sequence : 0,
                IsFresh = true
            };
        }

        private static void DaqFastResyncRecreatePolicy()
        {
            Assert(!EpbManager.RequiresDaqTaskRecreate("ControlLatencyExceeded"),
                "控制积压仍被强制Stop/Start，未先丢旧追新");
            Assert(!EpbManager.RequiresDaqTaskRecreate("ControlQueueFull"),
                "控制队列积压未走快速重同步");
            Assert(!EpbManager.RequiresDaqTaskRecreate("BackgroundProcessingStale") &&
                   !EpbManager.RequiresDaqTaskRecreate("BackgroundWorkerFault") &&
                   !EpbManager.RequiresDaqTaskRecreate("BackgroundBatchTransferFault") &&
                   !EpbManager.RequiresDaqTaskRecreate("RawPersistenceTransferFault") &&
                   !EpbManager.RequiresDaqTaskRecreate("RawPersistencePermanentFault") &&
                   !EpbManager.RequiresDaqTaskRecreate("DaqPersistenceWriteStall"),
                "后台消费者/所有权移交故障仍错误重建健康NI任务");
            Assert(!EpbManager.RequiresDaqTaskRecreate("DaqCallbackStale"),
                "短暂回调停顿仍被强制Stop/Start，未先复核已自行恢复的新鲜回调");
            Assert(EpbManager.RequiresDaqTaskRecreate("DaqClockModelInvalid"),
                "时钟模型失效未要求DAQ任务重建");
        }

        private static void DaqSelfMaintenancePolicy()
        {
            Assert(DaqRecoveryFailurePolicy.Evaluate(0, false) ==
                   DaqRecoveryFailureDisposition.ContinueSelfMaintenance,
                "普通软件恢复失败被升级成硬件报警停机");
            Assert(DaqRecoveryFailurePolicy.Evaluate(1, true) ==
                   DaqRecoveryFailureDisposition.ContinueSelfMaintenance,
                "单份硬件证据被错误锁存报警");
            Assert(DaqRecoveryFailurePolicy.Evaluate(2, true) ==
                   DaqRecoveryFailureDisposition.ConfirmedHardwareAlarm,
                "两份独立硬件失效证据未触发硬件报警");
            Assert(EpbManager.GetDaqSelfMaintenanceDelayMs(1) == 1000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(2) == 2000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(3) == 5000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(4) == 10000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(20) == 30000,
                "自维护退避不是1/2/5/10/30秒有界序列");
            Assert(!EpbManager.ShouldEscalateSoftwareRecovery(2) &&
                   EpbManager.ShouldEscalateSoftwareRecovery(3) &&
                   EpbManager.ShouldKeepSoftwareRecoveryLocal("DaqSelfMaintenance"),
                "DAQ自维护未保持局部退避或丢失通用三次阈值");
        }

        private static void IndependentDaqLivenessSupervisorPolicy()
        {
            var stale = new DaqFreshnessSnapshot
            {
                Device = "Dev1",
                Generation = 7,
                LastCallbackMonotonicTicks = Stopwatch.GetTimestamp(),
                CallbackAgeMs = 120,
                LastProducedSequence = 100,
                LastProcessedSequence = 100
            };
            var trip = EpbManager.EvaluateDaqLiveness(true, true, false, stale, 0, 100);
            Assert(trip.Trip && trip.Code == "DaqCallbackStale",
                "带电DAQ回调陈旧未由独立监督器触发");
            Assert(!EpbManager.EvaluateDaqLiveness(true, false, false, stale, 0, 100).Trip,
                "未带电设备被独立监督器错误断言为故障");
            Assert(!EpbManager.EvaluateDaqLiveness(true, true, true, stale, 0, 100).Trip,
                "既有恢复上下文期间重复发布DAQ存活故障");
            stale.CallbackAgeMs = 99;
            Assert(!EpbManager.EvaluateDaqLiveness(true, true, false, stale, 0, 100).Trip,
                "100ms门槛以内的新鲜回调被错误停机");
            stale.CallbackGapEventCount = 4;
            stale.LastCallbackGapIntervalMs = 120;
            var recoveredBeforeWatchdog = EpbManager.EvaluateDaqLiveness(
                true,
                true,
                false,
                stale,
                observedGapEventCount: 3,
                staleThresholdMs: 100);
            Assert(recoveredBeforeWatchdog.Trip &&
                   recoveredBeforeWatchdog.Code == "DaqCallbackGap",
                "DAQ回调先恢复、监督器后执行时丢失了带电期间120ms历史空窗");
            Assert(!EpbManager.EvaluateDaqLiveness(
                       true,
                       true,
                       false,
                       stale,
                       observedGapEventCount: 4,
                       staleThresholdMs: 100).Trip,
                "已经消费的DAQ历史空窗事件被重复发布");
        }

        private static void DaqLivenessLogTransitionDedup()
        {
            var gate = new DaqLivenessLogTransitionGate();
            var runId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            gate.BeginSession(runId, 11);

            Assert(gate.TryAccept(
                       runId,
                       11,
                       correlationId,
                       new[] { "dev2", "DEV1" },
                       "BatchMerged"),
                "首次BatchMerged转换没有被接受");
            Assert(!gate.TryAccept(
                       runId,
                       11,
                       correlationId,
                       new[] { "Dev1", "Dev2" },
                       "BatchMerged"),
                "同一Run/RunEpoch/关联号/参与设备集重复记录了BatchMerged");
            Assert(gate.TryAccept(
                       runId,
                       11,
                       correlationId,
                       new[] { "Dev1" },
                       "BatchMerged"),
                "参与设备集变化后未允许新的BatchMerged转换");

            Assert(gate.TryAccept(
                       runId,
                       11,
                       correlationId,
                       new[] { "Dev1" },
                       "Trip",
                       "DEV1:7"),
                "新的Trip转换没有被接受");
            Assert(!gate.TryAccept(
                       runId,
                       11,
                       correlationId,
                       new[] { "dev1" },
                       "Trip",
                       "DEV1:7"),
                "同一Trip转换重复记录");
            Assert(gate.TryAccept(
                       runId,
                       11,
                       correlationId,
                       new[] { "Dev1" },
                       "Trip",
                       "DEV1:8"),
                "新代次Trip转换被错误去重");

            for (var index = 0; index < 100; index++)
                gate.TryAccept(
                    runId,
                    11,
                    Guid.NewGuid(),
                    new[] { "Dev1" },
                    "BatchMerged");
            Assert(gate.Count <= 32, $"存活日志去重状态无界：{gate.Count}");

            Assert(gate.TryAccept(
                       Guid.NewGuid(),
                       12,
                       correlationId,
                       new[] { "Dev1", "Dev2" },
                       "BatchMerged"),
                "新批次未清空旧转换去重状态");
        }

        private static void UnenergizedDaqGapObservationPolicy()
        {
            var freshness = new DaqFreshnessSnapshot
            {
                Device = "Dev2",
                Generation = 9,
                LastCallbackMonotonicTicks = Stopwatch.GetTimestamp(),
                CallbackAgeMs = 1,
                CallbackGapEventCount = 12,
                LastCallbackGapIntervalMs = 150,
                LastProducedSequence = 200,
                LastProcessedSequence = 200
            };

            var baseline = EpbManager.EvaluateUnenergizedDaqGap(
                batchActive: true,
                deviceEnergized: false,
                recoveryActive: false,
                freshness,
                hasObservedBaseline: false,
                observedGapEventCount: 0,
                staleThresholdMs: 100);
            Assert(!baseline.Emit,
                "未带电设备首次观察到历史空窗时不应追溯记录INFO");

            var noNewGap = EpbManager.EvaluateUnenergizedDaqGap(
                true,
                false,
                false,
                freshness,
                hasObservedBaseline: true,
                observedGapEventCount: 12,
                staleThresholdMs: 100);
            Assert(!noNewGap.Emit,
                "已消费的未带电回调空窗被重复记录");

            freshness.CallbackGapEventCount = 13;
            var observed = EpbManager.EvaluateUnenergizedDaqGap(
                true,
                false,
                false,
                freshness,
                hasObservedBaseline: true,
                observedGapEventCount: 12,
                staleThresholdMs: 100);
            Assert(observed.Emit && observed.Code == "ObservedUnenergizedGap",
                "新的未带电回调空窗没有生成低严重度INFO决策");
            Assert(!EpbManager.EvaluateUnenergizedDaqGap(
                       true,
                       false,
                       false,
                       freshness,
                       hasObservedBaseline: true,
                       observedGapEventCount: 13,
                       staleThresholdMs: 100).Emit,
                "同一未带电回调空窗第二次仍生成INFO决策");
            Assert(!EpbManager.EvaluateUnenergizedDaqGap(
                       true,
                       false,
                       true,
                       freshness,
                       hasObservedBaseline: true,
                       observedGapEventCount: 12,
                       staleThresholdMs: 100).Emit,
                "恢复期间未带电回调空窗错误生成INFO决策");

            freshness.CallbackGapEventCount = 1;
            freshness.LastCallbackGapIntervalMs = 120;
            freshness.CallbackAgeMs = 1;
            var energizedTrip = EpbManager.EvaluateDaqLiveness(
                batchActive: true,
                deviceEnergized: true,
                recoveryActive: false,
                freshness,
                observedGapEventCount: 0,
                staleThresholdMs: 100);
            Assert(energizedTrip.Trip && energizedTrip.Code == "DaqCallbackGap",
                "带电且尚无空窗基线时真实回调空窗未触发Trip");
        }

        private static void RecoveryBoundaryContradictionFirstWins()
        {
            var gate = new object();
            var committed = 0;
            var reason = string.Empty;
            var accepted = 0;
            var contenders = Enumerable.Range(0, 32)
                .Select(index => Task.Run(() =>
                {
                    if (EpbManager.TryCaptureFirstBoundaryContradiction(
                            gate,
                            ref committed,
                            ref reason,
                            "reason-" + index))
                        Interlocked.Increment(ref accepted);
                }))
                .ToArray();
            Task.WaitAll(contenders);
            Assert(accepted == 1 &&
                   Volatile.Read(ref committed) == 1 &&
                   !string.IsNullOrWhiteSpace(reason) &&
                   reason.StartsWith("reason-", StringComparison.Ordinal),
                $"并发边界矛盾未保持首个原因：Accepted={accepted} Reason={reason}");
            Assert(!EpbManager.TryCaptureFirstBoundaryContradiction(
                       gate,
                       ref committed,
                       ref reason,
                       "second") &&
                   reason.StartsWith("reason-", StringComparison.Ordinal),
                "后续边界矛盾覆盖了首个原因或重复获准记录");
        }

        private static void BackgroundDrainResultExplainsPendingPredicate()
        {
            var pending = new DaqBackgroundDrainResult
            {
                Dev1Boundary = 100,
                Dev2Boundary = 200,
                Dev1Published = 100,
                Dev1RawTransferred = 99,
                Dev2Published = 199,
                Dev2RawTransferred = 200,
                Completed = false
            };
            Assert(pending.PendingPredicate == "Dev1.RawTransferred,Dev2.Published",
                "后台排空结果没有指出真实未闭合谓词");
            pending.Dev1RawTransferred = 100;
            pending.Dev2Published = 200;
            pending.Completed = true;
            Assert(pending.PendingPredicate == "none",
                "边界全部闭合后仍报告伪未决谓词");
        }

        private static void IncidentSnapshotHeavyEvidencePolicy()
        {
            Assert(!EpbManager.ShouldIncludeFullDaqIncidentEvidence("00-trigger") &&
                   !EpbManager.ShouldIncludeFullDaqIncidentEvidence("40-self-maintenance") &&
                   EpbManager.ShouldIncludeFullDaqIncidentEvidence("90-recovered") &&
                   EpbManager.ShouldIncludeFullDaqIncidentEvidence("90-hardware-confirmed"),
                "恢复关键窗口仍会重复导出完整诊断和最近圈证据");
            Assert(EpbManager.ShouldIncludeDaqTimingEvidence("00-trigger") &&
                   !EpbManager.ShouldIncludeDaqTimingEvidence("40-self-maintenance") &&
                   EpbManager.ShouldIncludeDaqTimingEvidence("90-recovered"),
                "触发瞬间未保留轻量时序证据，或维护阶段仍在重复导出");
        }

        private static void IncidentEvidenceQueueIsBoundedAndCoalesced()
        {
            var exported = new ConcurrentQueue<DaqIncidentEvidenceBatch>();
            using var triggerEntered = new ManualResetEventSlim(false);
            using var releaseTrigger = new ManualResetEventSlim(false);
            var observedWorkers = 0;
            var queue = new DaqIncidentEvidenceQueue(
                batch =>
                {
                    exported.Enqueue(batch);
                    if (batch.Trigger == null) return;
                    triggerEntered.Set();
                    if (!releaseTrigger.Wait(5000))
                        throw new TimeoutException("测试未及时释放trigger证据写入");
                },
                _ => Interlocked.Increment(ref observedWorkers));
            var runId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var contextKey = $"{runId:N}:{correlationId:N}";
            Assert(queue.Submit(new DaqIncidentEvidenceSubmission
                {
                    ContextKey = contextKey,
                    RunId = runId,
                    CorrelationId = correlationId,
                    Device = "Dev1",
                    StartedUtc = DateTime.UtcNow,
                    PhaseKey = "00-trigger",
                    PhaseDirectoryName = "00-trigger-001-000000_000",
                    IncidentJson = "{}",
                    IsTrigger = true
                }),
                "根事故trigger未进入取证门");
            Assert(triggerEntered.Wait(2000), "trigger取证worker未启动");

            var submitWatch = Stopwatch.StartNew();
            try
            {
                for (var index = 0; index < 100; index++)
                {
                    Assert(queue.Submit(new DaqIncidentEvidenceSubmission
                        {
                            ContextKey = contextKey,
                            RunId = runId,
                            CorrelationId = correlationId,
                            Device = "Dev1",
                            StartedUtc = DateTime.UtcNow,
                            PhaseKey = $"symptom-{index:D3}",
                            IncidentJson = "{}"
                        }),
                        $"第{index + 1}个派生症状未合并到根事故");
                }
                Assert(queue.Submit(new DaqIncidentEvidenceSubmission
                    {
                        ContextKey = contextKey,
                        RunId = runId,
                        CorrelationId = correlationId,
                        Device = "Dev1",
                        StartedUtc = DateTime.UtcNow,
                        PhaseKey = "90-recovered",
                        PhaseDirectoryName = "90-recovered-102-000001_000",
                        IncidentJson = "{}",
                        IsTerminal = true
                    }),
                    "根事故terminal未获保留");
                submitWatch.Stop();
                Assert(submitWatch.ElapsedMilliseconds < 500,
                    $"恢复调用被证据写入拖慢：{submitWatch.ElapsedMilliseconds}ms");
                Assert(queue.ContextCount == 1,
                    $"百次症状产生了多个根上下文：{queue.ContextCount}");
                Assert(queue.RunningCount <= 1 && queue.PendingCount <= 1 &&
                       queue.ActiveJobCount <= 2,
                    $"取证门越界：Running={queue.RunningCount} Pending={queue.PendingCount} " +
                    $"Active={queue.ActiveJobCount}");
                Assert(queue.WorkerStartCount == 1 && observedWorkers == 1,
                    $"派生症状创建了额外Task：Starts={queue.WorkerStartCount} " +
                    $"Observed={observedWorkers}");
                var boundedDrain = Stopwatch.StartNew();
                Assert(!queue.DrainAsync(50).GetAwaiter().GetResult(),
                    "trigger仍阻塞时drain错误报告完成");
                boundedDrain.Stop();
                Assert(boundedDrain.ElapsedMilliseconds < 1000,
                    $"阻塞证据的drain未保持有界：{boundedDrain.ElapsedMilliseconds}ms");
            }
            finally
            {
                releaseTrigger.Set();
            }

            Assert(queue.DrainAsync(5000).GetAwaiter().GetResult(),
                "释放证据写入后队列未在5秒内排空");
            var submissions = exported
                .SelectMany(batch => batch.OrderedSubmissions())
                .ToArray();
            Assert(submissions.Count(item => item.IsTrigger) == 1,
                "根事故trigger导出次数不是1");
            Assert(submissions.Count(item => item.IsTerminal) == 1,
                "根事故terminal导出次数不是1");
            var symptoms = submissions.Where(item => !item.IsTrigger && !item.IsTerminal).ToArray();
            Assert(symptoms.Length == 100 &&
                   symptoms.Select(item => item.PhaseKey).Distinct().Count() == 100,
                $"派生症状manifest不完整或重复：Count={symptoms.Length}");
            Assert(submissions.Count(item =>
                       !string.IsNullOrWhiteSpace(item.PhaseDirectoryName)) == 2 &&
                   symptoms.All(item => string.IsNullOrWhiteSpace(item.PhaseDirectoryName)),
                "派生症状仍创建了独立phase目录，未只追加根manifest");
            Assert(queue.ContextCount == 0 && queue.RunningCount == 0 &&
                   queue.PendingCount == 0 && queue.ActiveJobCount == 0,
                "terminal后根上下文或门状态未收口");

            var unfinished = new DaqIncidentEvidenceQueue(_ => { });
            var unfinishedRun = Guid.NewGuid();
            var unfinishedCorrelation = Guid.NewGuid();
            Assert(unfinished.Submit(new DaqIncidentEvidenceSubmission
                {
                    ContextKey = $"{unfinishedRun:N}:{unfinishedCorrelation:N}",
                    RunId = unfinishedRun,
                    CorrelationId = unfinishedCorrelation,
                    Device = "Dev2",
                    StartedUtc = DateTime.UtcNow,
                    PhaseKey = "00-trigger",
                    IncidentJson = "{}",
                    IsTrigger = true
                }),
                "未终态反例的trigger未获准");
            Assert(SpinWait.SpinUntil(
                    () => unfinished.WorkerStartCount > 0 &&
                          unfinished.RunningCount == 0 && unfinished.PendingCount == 0,
                    2000),
                "未终态反例的trigger未完成导出");
            var unfinishedDrain = Stopwatch.StartNew();
            Assert(!unfinished.DrainAsync(50).GetAwaiter().GetResult(),
                "只含trigger、尚无terminal的根事故被drain误报完成");
            unfinishedDrain.Stop();
            Assert(unfinishedDrain.ElapsedMilliseconds < 1000,
                $"未终态根事故drain未保持有界：{unfinishedDrain.ElapsedMilliseconds}ms");
            Assert(unfinished.Submit(new DaqIncidentEvidenceSubmission
                {
                    ContextKey = $"{unfinishedRun:N}:{unfinishedCorrelation:N}",
                    RunId = unfinishedRun,
                    CorrelationId = unfinishedCorrelation,
                    Device = "Dev2",
                    StartedUtc = DateTime.UtcNow,
                    PhaseKey = "90-recovered",
                    IncidentJson = "{}",
                    IsTerminal = true
                }),
                "未终态反例补交terminal失败");
            Assert(unfinished.DrainAsync(2000).GetAwaiter().GetResult(),
                "未终态反例补交terminal后仍未收口");
        }

        private static void DualDeviceIncidentEvidenceRootsRemainIndependent()
        {
            var exported = new ConcurrentQueue<DaqIncidentEvidenceBatch>();
            var queue = new DaqIncidentEvidenceQueue(batch => exported.Enqueue(batch));
            var runId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var started = DateTime.UtcNow;

            foreach (var device in new[] { "Dev1", "Dev2" })
            {
                var contextKey = EpbManager.DaqIncidentEvidenceContextKey(
                    runId,
                    correlationId,
                    device);
                var trigger = new DaqIncidentEvidenceSubmission
                {
                    ContextKey = contextKey,
                    RunId = runId,
                    CorrelationId = correlationId,
                    Device = device,
                    StartedUtc = started,
                    PhaseKey = "00-trigger",
                    PhaseDirectoryName = $"00-trigger-{device}",
                    IncidentJson = "{}",
                    IsTrigger = true
                };
                Assert(SpinWait.SpinUntil(
                           () => queue.Submit(trigger),
                           2000),
                    $"{device} trigger未进入取证门");
                Assert(queue.Submit(new DaqIncidentEvidenceSubmission
                    {
                        ContextKey = contextKey,
                        RunId = runId,
                        CorrelationId = correlationId,
                        Device = device,
                        StartedUtc = started,
                        PhaseKey = "90-recovered",
                        PhaseDirectoryName = $"90-recovered-{device}",
                        IncidentJson = "{}",
                        IsTerminal = true
                    }),
                    $"{device} terminal未进入取证门");
                Assert(queue.RunningCount <= 1 &&
                       queue.PendingCount <= 1 &&
                       queue.ActiveJobCount <= 2,
                    $"双DAQ取证门超出容量：Running={queue.RunningCount} " +
                    $"Pending={queue.PendingCount} Active={queue.ActiveJobCount}");
            }

            Assert(queue.DrainAsync(5000).GetAwaiter().GetResult(),
                "双DAQ取证队列未排空");
            var submissions = exported
                .SelectMany(batch => batch.OrderedSubmissions())
                .ToArray();
            foreach (var device in new[] { "Dev1", "Dev2" })
            {
                Assert(submissions.Count(item =>
                           item.Device == device && item.IsTrigger) == 1,
                    $"{device} trigger导出次数不是1");
                Assert(submissions.Count(item =>
                           item.Device == device && item.IsTerminal) == 1,
                    $"{device} terminal导出次数不是1");
            }
            Assert(queue.ContextCount == 0 &&
                   queue.RunningCount == 0 &&
                   queue.PendingCount == 0 &&
                   queue.ActiveJobCount == 0,
                "双DAQ终态后取证根上下文或容量未收口");
        }

        private static void DaqIncidentEvidenceDirectoryIdentity()
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBTest", "IncidentSnapshots");
            var runId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var dev1 = EpbManager.DaqIncidentEvidenceDirectoryPath(
                root,
                runId,
                correlationId);
            var dev2 = EpbManager.DaqIncidentEvidenceDirectoryPath(
                root,
                runId,
                correlationId);
            Assert(dev1 == dev2 &&
                   dev1.EndsWith(
                       EpbManager.DaqIncidentEvidenceDirectoryName(runId, correlationId),
                       StringComparison.Ordinal),
                "同一Run/关联号未生成同一确定性事故根目录");

            var differentRun = EpbManager.DaqIncidentEvidenceDirectoryPath(
                root,
                Guid.NewGuid(),
                correlationId);
            var differentCorrelation = EpbManager.DaqIncidentEvidenceDirectoryPath(
                root,
                runId,
                Guid.NewGuid());
            Assert(differentRun != dev1 && differentCorrelation != dev1,
                "不同Run或关联号错误复用了事故根目录");
        }

        private static void DaqRecoveryPrerequisiteOrder()
        {
            var order = new List<string>();
            EpbManager.ExecuteDaqRecoveryRejoinPrerequisitesAsync(
                    new[] { 10, 8, 8 },
                    (channels, _) =>
                    {
                        order.Add("Power:" + string.Join(",", channels));
                        return Task.CompletedTask;
                    },
                    (channels, _) =>
                    {
                        order.Add("Mechanical:" + string.Join(",", channels));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(order.SequenceEqual(new[] { "Power:8,10", "Mechanical:8,10" }),
                "DAQ恢复仍在电源安全预检前执行机械定位");
            Assert(EpbManager.ShouldDeferIndependentRejoinForDaq(true) &&
                   !EpbManager.ShouldDeferIndependentRejoinForDaq(false),
                "通道级恢复没有正确服从DAQ组恢复所有权");
        }

        private static void DaqCutoffPreOwnershipSafetyOrder()
        {
            var order = new List<string>();
            EpbManager.ExecuteDaqCutoffBeforeOwnershipWait(
                () => order.Add("Watchdog"),
                () => order.Add("PauseAll"),
                () => order.Add("FreezeSuppress"),
                () => order.Add("CancelSubmitOffAll"),
                () => order.Add("StartPowerDisable"),
                () => order.Add("StartRejectedOffFallbacks"),
                () => order.Add("RevokeHydraulicStartRelease"),
                () => order.Add("PublishObservers"));
            Assert(order.SequenceEqual(new[]
                {
                    "Watchdog",
                    "PauseAll",
                    "FreezeSuppress",
                    "CancelSubmitOffAll",
                    "StartPowerDisable",
                    "StartRejectedOffFallbacks",
                    "RevokeHydraulicStartRelease",
                    "PublishObservers"
                }),
                "DAQ截止仍可能在暂停/冻结前取消圈，或在整组断能前调用日志/UI观察者");

            using var observerEntered = new ManualResetEventSlim(false);
            using var releaseObserver = new ManualResetEventSlim(false);
            var paused = false;
            var offSubmitted = false;
            var powerStarted = false;
            var hydraulicRevoked = false;
            var blocked = Task.Run(() => EpbManager.ExecuteDaqCutoffBeforeOwnershipWait(
                () => { },
                () => paused = true,
                () => { },
                () => offSubmitted = true,
                () => powerStarted = true,
                () => { },
                () => hydraulicRevoked = true,
                () =>
                {
                    observerEntered.Set();
                    releaseObserver.Wait(2000);
                }));
            Assert(observerEntered.Wait(1000), "DAQ截止观察者阻塞测试未进入观察者");
            Assert(paused && offSubmitted && powerStarted && hydraulicRevoked,
                "DAQ截止观察者阻塞时仍有兄弟通道未暂停/OFF、未启动电源Disable或未退出液压代次");
            releaseObserver.Set();
            Assert(blocked.Wait(1000), "DAQ截止观察者释放后顺序助手未退出");

            using var ownershipEntered = new ManualResetEventSlim(false);
            using var releaseOwnership = new ManualResetEventSlim(false);
            var participantRemoved = false;
            var leaseReleaseSubmitted = false;
            var ownershipBlocked = Task.Run(() =>
            {
                EpbManager.ExecuteDaqCutoffBeforeOwnershipWait(
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    () =>
                    {
                        participantRemoved = true;
                        leaseReleaseSubmitted = true;
                    },
                    () => { });
                ownershipEntered.Set();
                releaseOwnership.Wait(2000);
            });
            Assert(ownershipEntered.Wait(1000), "DAQ恢复未进入模拟ownership等待");
            Assert(participantRemoved && leaseReleaseSubmitted,
                "ownership阻塞前受影响通道仍占用液压参与代次或lease release尚未投递");
            releaseOwnership.Set();
            Assert(ownershipBlocked.Wait(1000), "释放模拟ownership等待后测试任务未退出");
        }

        private static void StopPreRecoveryWaitSafetyOrder()
        {
            var order = new List<string>();
            var powerTask = EpbManager.ExecuteStopSafetyBeforeRecoveryWait(
                () => order.Add("RevokeBatch"),
                () => order.Add("PauseFreezeAll"),
                () => order.Add("CancelSubmitOffAll"),
                () =>
                {
                    order.Add("StartPowerDisable");
                    return "PowerDisableStarted";
                },
                () => order.Add("StartRejectedOffFallbacks"),
                () => order.Add("SealAndPublish"));
            Assert(powerTask == "PowerDisableStarted" && order.SequenceEqual(new[]
                {
                    "RevokeBatch",
                    "PauseFreezeAll",
                    "CancelSubmitOffAll",
                    "StartPowerDisable",
                    "StartRejectedOffFallbacks",
                    "SealAndPublish"
                }),
                "Stop仍可能先等待恢复所有权，或在总电源Disable前封圈/发布观察者");

            using var sealEntered = new ManualResetEventSlim(false);
            using var releaseSeal = new ManualResetEventSlim(false);
            var allOffSubmitted = false;
            var disableStarted = false;
            var blockedSeal = Task.Run(() => EpbManager.ExecuteStopSafetyBeforeRecoveryWait(
                () => { },
                () => { },
                () => allOffSubmitted = true,
                () =>
                {
                    disableStarted = true;
                    return true;
                },
                () => { },
                () =>
                {
                    sealEntered.Set();
                    releaseSeal.Wait(2000);
                }));
            Assert(sealEntered.Wait(1000), "Stop阻塞封圈测试未进入证据阶段");
            Assert(allOffSubmitted && disableStarted,
                "Stop封圈阻塞时整组OFF或电源Disable尚未启动");
            releaseSeal.Set();
            Assert(blockedSeal.Wait(1000) && blockedSeal.Result,
                "Stop封圈释放后顺序助手未正常返回");
        }

        private static void StopPowerDisableIgnoresCallerCancellation()
        {
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            var invoked = false;
            var physicalToken = new CancellationToken(canceled: true);
            var result = EpbManager.ExecuteStopPowerDisableSafetyAsync(
                    token =>
                    {
                        invoked = true;
                        physicalToken = token;
                        return Task.CompletedTask;
                    },
                    caller.Token)
                .GetAwaiter()
                .GetResult();
            Assert(invoked && result.ok && !physicalToken.CanBeCanceled,
                "预取消调用方令牌仍传播到物理Disable，或导致物理关闭未启动");
        }

        private static void NonBlockingSafetyIsolationOrder()
        {
            var order = new List<string>();
            EpbManager.ExecuteNonBlockingSafetyIsolationOrder(
                () => order.Add("FreezeCancelAll"),
                () => order.Add("SubmitOffAll"),
                () => order.Add("StartPowerDisable"),
                () => order.Add("PublishSchedule"),
                () => order.Add("StartRejectedFallbacks"));
            Assert(order.SequenceEqual(new[]
                {
                    "FreezeCancelAll",
                    "SubmitOffAll",
                    "StartPowerDisable",
                    "PublishSchedule",
                    "StartRejectedFallbacks"
                }),
                "单路同步OFF兜底仍可能阻塞兄弟通道、电源关闭或故障发布");

            order.Clear();
            EpbManager.ExecuteNonBlockingSafetyIsolationOrder(
                () => order.Add("FreezeCancelAll"),
                () => order.Add("SubmitOffAll"),
                null,
                () => order.Add("PublishSystemFault"),
                () => order.Add("StartRejectedFallbacks"));
            Assert(order.SequenceEqual(new[]
                {
                    "FreezeCancelAll",
                    "SubmitOffAll",
                    "PublishSystemFault",
                    "StartRejectedFallbacks"
                }),
                "软件恢复熔断仍可能在SystemFault/回收发布前同步等待OFF兜底");
        }

        private static void DaqRecoveryIncludesRunnerOnlyChannels()
        {
            var selected = EpbManager.SelectDaqRecoveryPowerChannels(
                new[] { 5, 4, 5, 6 },
                channel => channel == 6,
                _ => false,
                channel => channel != 4);
            Assert(selected.SequenceEqual(new[] { 5 }),
                "学习/资格 Runner 通道未进入恢复供电集合，或禁用/报警通道未被隔离");
        }

        private static void IncidentCorrelationAndPriority()
        {
            var latch = new DaqIncidentLatch();
            var run1 = Guid.NewGuid();
            var firstSeen = DateTime.UtcNow;
            latch.BeginRun(run1, new[] { "Dev1", "Dev2" });
            var first = latch.Observe(run1, "Dev1", 5, "ControlLatencyExceeded", "late",
                firstSeen, new[] { 5, 4 }, primaryChannel: 5);
            Assert(first.IsFirst && first.Context.PrimaryChannel == 5, "首事故或触发通道不正确");
            var derived = latch.Observe(run1, "Dev1", 5, "DaqSampleStale", "stale",
                DateTime.UtcNow, new[] { 6 });
            Assert(!derived.IsFirst && derived.Context.CorrelationId == first.Context.CorrelationId,
                "同设备派生故障未复用关联号");
            var upgraded = latch.Observe(run1, "Dev1", 5, "ControlQueueFull", "full",
                DateTime.UtcNow, new[] { 4, 5, 6 });
            Assert(!upgraded.IsFirst && upgraded.PrimaryChanged &&
                   upgraded.Context.PrimaryCode == "ControlQueueFull", "根故障优先级未升级");
            Assert(latch.TryStartSnapshot(run1, "Dev1", out _) &&
                   !latch.TryStartSnapshot(run1, "Dev1", out _), "同事故启动了多个主快照");

            var otherDevice = latch.Observe(run1, "Dev2", 2, "DaqSampleStale", "stale",
                firstSeen.AddSeconds(1), new[] { 8 });
            Assert(otherDevice.Context.CorrelationId != first.Context.CorrelationId, "跨设备事故被错误合并");
            latch.Complete("Dev1", first.Context.CorrelationId);
            latch.Complete("Dev2", otherDevice.Context.CorrelationId);
            var simultaneousCorrelation = Guid.NewGuid();
            var simultaneousDev1 = latch.Observe(
                run1, "Dev1", 6, "DaqCallbackStale", "shared pause",
                DateTime.UtcNow, new[] { 4, 5 }, correlationId: simultaneousCorrelation);
            var simultaneousDev2 = latch.Observe(
                run1, "Dev2", 3, "DaqCallbackStale", "shared pause",
                DateTime.UtcNow, new[] { 8, 9 }, correlationId: simultaneousCorrelation);
            Assert(simultaneousDev1.Context.CorrelationId == simultaneousCorrelation &&
                   simultaneousDev2.Context.CorrelationId == simultaneousCorrelation,
                "同一扫描双DAQ异常没有复用批次级关联身份");
            latch.Complete("Dev1", simultaneousCorrelation);
            latch.Complete("Dev2", simultaneousCorrelation);
            var automaticallyMergedDev1 = latch.Observe(
                run1, "Dev1", 7, "DaqSampleStale", "cycle watchdog",
                firstSeen.AddSeconds(3), new[] { 4, 5 });
            var automaticallyMergedDev2 = latch.Observe(
                run1, "Dev2", 4, "DaqCallbackGap", "liveness watchdog",
                firstSeen.AddSeconds(3).AddMilliseconds(20), new[] { 8, 9 });
            Assert(automaticallyMergedDev1.Context.CorrelationId ==
                   automaticallyMergedDev2.Context.CorrelationId,
                "两个入口在100ms内发现的双DAQ公共空窗没有从第一刻合并批次身份");
            Assert(EpbManager.BuildDaqRecoveryTerminalKey("Dev1", simultaneousCorrelation) !=
                   EpbManager.BuildDaqRecoveryTerminalKey("Dev2", simultaneousCorrelation),
                "共享批次关联号错误地吞并了两台DAQ各自必须完成的终态");
            var run2 = Guid.NewGuid();
            latch.BeginRun(run2, new[] { "Dev1" });
            var newRun = latch.Observe(run2, "Dev1", 6, "DaqSampleStale", "stale",
                DateTime.UtcNow, new[] { 4 });
            Assert(newRun.IsFirst && newRun.Context.CorrelationId != first.Context.CorrelationId,
                "显式新运行未复位事故锁存");
        }

        private static void DaqSafetyActionsPrecedePublication()
        {
            var order = new List<string>();
            EpbManager.ExecuteDaqFaultSafetyFirst(
                () => order.Add("freeze-cancel-all"),
                () => order.Add("submit-off-all"),
                () => order.Add("start-power-disable"),
                () => order.Add("publish-fault"),
                () => order.Add("start-rejected-fallbacks"));
            Assert(order.SequenceEqual(new[]
                {
                    "freeze-cancel-all",
                    "submit-off-all",
                    "start-power-disable",
                    "publish-fault",
                    "start-rejected-fallbacks"
                }),
                "DAQ硬件确认仍在整组异步OFF/电源Disable前发布故障，或在发布前同步执行拒绝项兜底");

            using var firstFallbackEntered = new ManualResetEventSlim(false);
            using var releaseFirstFallback = new ManualResetEventSlim(false);
            using var secondFallbackEntered = new ManualResetEventSlim(false);
            IReadOnlyDictionary<int, Task<bool>> fallbackTasks = null;
            var powerDisableStarted = false;
            var faultPublicationStarted = false;
            EpbManager.ExecuteDaqFaultSafetyFirst(
                () => { },
                () => { },
                () => powerDisableStarted = true,
                () => faultPublicationStarted = true,
                () => fallbackTasks = EpbManager.StartIndependentSafetyFallbackTasks(
                    new Dictionary<int, string>
                    {
                        [4] = "Rejected",
                        [5] = "Rejected"
                    },
                    (channel, _) =>
                    {
                        if (channel == 4)
                        {
                            firstFallbackEntered.Set();
                            releaseFirstFallback.Wait(2000);
                        }
                        else
                        {
                            secondFallbackEntered.Set();
                        }
                        return true;
                    }));
            Assert(powerDisableStarted && faultPublicationStarted,
                "DAQ硬件确认的电源Disable或故障发布未在拒绝项同步兜底前启动");
            Assert(firstFallbackEntered.Wait(1000), "首通道阻塞OFF兜底未启动");
            Assert(secondFallbackEntered.Wait(1000),
                "首通道同步OFF兜底阻塞了兄弟通道独立兜底");
            releaseFirstFallback.Set();
            Assert(fallbackTasks != null &&
                   Task.WaitAll(fallbackTasks.Values.ToArray(), 2000),
                "释放首通道OFF兜底后独立任务未全部退出");
        }

        private static void AdaptiveHotLoopDoesNotAllocate()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            var decision = new EpbAdaptiveDecision();
            var tick = Stopwatch.Frequency;
            machine.ArmForward(tick, 1000, 10000, 15, 1, 3);
            for (var i = 0; i < 5000; i++)
                machine.OnSampleReusable(tick, 1.0, double.NaN, decision);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100000; i++)
                machine.OnSampleReusable(tick, 1.0, double.NaN, decision);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 4096, $"十万样本持续分配 {allocated} bytes");
            Assert(machine.RetainedWindowSampleCount <= 1024, "固定窗口超过1024容量");
        }

        private static void CausalMedianInPlaceIsCorrectAndAllocationFree()
        {
            var filter = new ClsDataFilter.MedianStreamCausal(
                2, 2, MedianSelectPointsMode.OnlyPrevious, 5);
            var first = new double[,]
            {
                { 5, 1, 9 },
                { 3, 7, 4 }
            };
            var second = new double[,]
            {
                { 2, 8 },
                { 6, 0 }
            };
            filter.ProcessInPlace(first);
            filter.ProcessInPlace(second);
            AssertRow(first, 0, new[] { 5d, 1d, 5d });
            AssertRow(first, 1, new[] { 3d, 3d, 4d });
            AssertRow(second, 0, new[] { 2d, 8d });
            AssertRow(second, 1, new[] { 6d, 4d });

            var hotFilter = new ClsDataFilter.MedianStreamCausal(
                2, 3, MedianSelectPointsMode.OnlyPrevious, 5);
            var batch = new double[2, 5];
            for (var warmup = 0; warmup < 1000; warmup++)
                hotFilter.ProcessInPlace(batch);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20000; i++)
                hotFilter.ProcessInPlace(batch);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 4096, $"原地中值滤波持续分配 {allocated} bytes");
        }

        private static void AssertRow(double[,] actual, int row, double[] expected)
        {
            for (var column = 0; column < expected.Length; column++)
                Assert(Math.Abs(actual[row, column] - expected[column]) < 1e-12,
                    $"中值滤波结果错误 row={row} column={column} expected={expected[column]} actual={actual[row, column]}");
        }

        private static void AdaptiveTraceRateAndActionRetention()
        {
            var interval = Math.Max(1, Stopwatch.Frequency / 25);
            long lastNormal = 0;
            var normalCount = 0;
            for (var millisecond = 0; millisecond < 1000; millisecond++)
            {
                var tick = 1 + (long)(millisecond * Stopwatch.Frequency / 1000.0);
                if (EpbCycleRunner.ShouldRecordAdaptiveTrace(tick, false, interval, ref lastNormal))
                    normalCount++;
            }
            Assert(normalCount <= 25, $"普通轨迹超过25Hz，实际={normalCount}");
            for (var i = 0; i < 100; i++)
                Assert(EpbCycleRunner.ShouldRecordAdaptiveTrace(i, true, interval, ref lastNormal),
                    "动作轨迹被降采样");

            var buffer = new AdaptiveDecisionTraceBuffer();
            var run = Guid.NewGuid();
            var now = DateTime.UtcNow;
            for (var i = 0; i < 100; i++)
                buffer.Append(new AdaptiveDecisionTraceSample
                {
                    SampleUtc = now.AddMilliseconds(i),
                    RunId = run,
                    Channel = 4,
                    Action = "HardFault",
                    Reason = "Injected"
                });
            var snapshot = buffer.Snapshot(now.AddSeconds(1), run, 4);
            Assert(snapshot.Count(x => x.Action == "HardFault") == 100, "动作轨迹发生丢失");
        }

        private static void ControlDiagnosticsUseRealCapacity()
        {
            var diagnostics = new DaqDiagnosticRing(8);
            diagnostics.Append(new DaqTimingValue
            {
                TimestampUtc = DateTime.UtcNow,
                Device = "Dev1",
                Kind = "Control",
                QueueDepth = 2,
                ControlQueueCapacity = 64
            });
            var snapshot = diagnostics.Snapshot();
            Assert(snapshot.Length == 1 && snapshot[0].ControlQueueCapacity == 64,
                "控制诊断误写后台队列容量");
        }

        private static void BuildIdentityIsAuditable()
        {
            var identity = RuntimeBuildIdentity.Capture();
            var json = identity.ToJson();
            Assert(identity.ProcessBitness == IntPtr.Size * 8, "进程位数记录错误");
            Assert(identity.ProcessId > 0, "进程PID未写入构建身份");
            Assert(!string.IsNullOrWhiteSpace(identity.ProductVersion) &&
                   !string.IsNullOrWhiteSpace(identity.ExecutableSha256) &&
                   !string.IsNullOrWhiteSpace(identity.ConfigSha256), "版本或哈希字段缺失");
            Assert(identity.ProductVersion.Split('.').Length == 4,
                "产品版本未保留补丁Revision，无法区分2.12.0.x候选");
            Assert(RuntimeBuildIdentity.FormatProductVersion(new Version(2, 12, 0, 22)) ==
                   "V2.12.0.22",
                "四段现场补丁版本被截断，事故身份无法区分候选");
            Assert(RuntimeBuildIdentity.FormatProductVersion(new Version(2, 13, 0, 3)) ==
                   "V2.13.0.3",
                "V2.13.0.3产品入口版本未保留到Learning manifest身份");
            Assert(json.Contains("\"gitCommit\"") && json.Contains("\"gitDirty\"") &&
                   json.Contains("\"processId\"") && json.Contains("\"buildUtc\"") &&
                   json.Contains("\"releaseConfigSha256\"") &&
                   json.Contains("\"releasePackageVerified\"") &&
                   json.Contains("\"releasePackageCode\""),
                "Git/PID/构建时间/发布配置/包校验身份字段缺失");
            var startupLine = identity.ToStartupLogLine();
            Assert(startupLine.Contains("AssemblyVersion=") &&
                   startupLine.Contains("PID=") &&
                   startupLine.Contains("ExecutablePath=\"") &&
                   startupLine.Contains("ExeSha256=") &&
                   startupLine.Contains("GitCommit=") &&
                   startupLine.Contains("PackageVerified="),
                "启动日志没有完整输出程序集/PID/路径/EXE哈希/Git/包校验身份");
            var display = identity.ToDisplayText();
            Assert(display.Contains("程序集版本：") &&
                   display.Contains("PID ") &&
                   display.Contains("EXE 路径：") &&
                   display.Contains("EXE SHA-256：") &&
                   display.Contains("Git SHA：") &&
                   display.Contains("发布包校验："),
                "运行身份界面文本缺少程序集/PID/路径/EXE哈希/Git/包校验身份");
        }

        private static void ReleasePackageVerificationRejectsMixedBuild()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "EPBTest-ReleasePackage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                const string version = "V2.12.0.32";
                const string commit = "0123456789abcdef0123456789abcdef01234567";
                const string buildUtc = "2026-08-09T13:00:00.0000000Z";
                const string configSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                File.WriteAllText(Path.Combine(root, "MTTFTest.exe"), "formal-exe");
                File.WriteAllText(Path.Combine(root, "Config.dll"), "formal-config-dll");
                Directory.CreateDirectory(Path.Combine(root, "Config"));
                File.WriteAllText(Path.Combine(root, "Config", "TestConfig.xml"), "<test value=\"1\" />");
                File.WriteAllText(
                    Path.Combine(root, "build-identity.json"),
                    "{\n" +
                    $"  \"productVersion\": \"{version}\",\n" +
                    "  \"releaseStatus\": \"FORMAL_RELEASE_CANDIDATE\",\n" +
                    "  \"deploymentApproved\": true,\n" +
                    $"  \"gitCommit\": \"{commit}\",\n" +
                    "  \"gitDirty\": false,\n" +
                    $"  \"buildUtc\": \"{buildUtc}\",\n" +
                    $"  \"configSha256\": \"{configSha}\"\n" +
                    "}\n");
                WriteReleaseChecksums(root);

                var valid = ReleasePackageVerifier.VerifyDirectory(
                    root, version, commit, "false", buildUtc, configSha);
                Assert(valid.Verified && valid.VerifiedFileCount == 4,
                    "完整正式包未通过校验：" + valid);

                File.WriteAllText(Path.Combine(root, "Config", "TestConfig.xml"), "<test value=\"2\" />");
                var editedConfig = ReleasePackageVerifier.VerifyDirectory(
                    root, version, commit, "false", buildUtc, configSha);
                Assert(editedConfig.Verified,
                    "界面允许编辑的项目配置导致候选包自失效：" + editedConfig);

                Directory.CreateDirectory(Path.Combine(root, "DataStore", "session-1"));
                File.WriteAllText(Path.Combine(root, "DataStore", "session-1", "runtime.db"), "runtime");
                var withRuntimeData = ReleasePackageVerifier.VerifyDirectory(
                    root, version, commit, "false", buildUtc, configSha);
                Assert(withRuntimeData.Verified,
                    "运行期数据目录导致候选包自失效：" + withRuntimeData);

                File.WriteAllText(Path.Combine(root, "Config.dll"), "vs-overwrite");
                var mixed = ReleasePackageVerifier.VerifyDirectory(
                    root, version, commit, "false", buildUtc, configSha);
                Assert(!mixed.Verified && mixed.Code == "PackageFileHashMismatch",
                    "VS覆盖DLL后仍被误认为正式包：" + mixed);

                File.WriteAllText(Path.Combine(root, "Config.dll"), "formal-config-dll");
                File.WriteAllText(Path.Combine(root, "unexpected.tmp"), "extra");
                var extra = ReleasePackageVerifier.VerifyDirectory(
                    root, version, commit, "false", buildUtc, configSha);
                Assert(!extra.Verified && extra.Code == "PackageFileSetMismatch",
                    "发布目录出现清单外文件后仍被放行：" + extra);

                File.Delete(Path.Combine(root, "unexpected.tmp"));
                File.WriteAllText(Path.Combine(root, "Config", "Backup.xml"), "<backup />");
                var unexpectedConfig = ReleasePackageVerifier.VerifyDirectory(
                    root, version, commit, "false", buildUtc, configSha);
                Assert(!unexpectedConfig.Verified &&
                       unexpectedConfig.Code == "PackageMutableConfigSetMismatch",
                    "发布目录出现清单外配置后仍被放行：" + unexpectedConfig);

                File.Delete(Path.Combine(root, "Config", "Backup.xml"));
                File.WriteAllText(
                    Path.Combine(root, "build-identity.json"),
                    "{\n" +
                    $"  \"productVersion\": \"{version}\",\n" +
                    "  \"releaseStatus\": \"VS2022_RELEASE_CANDIDATE\",\n" +
                    "  \"deploymentApproved\": false,\n" +
                    "  \"gitCommit\": \"unknown\",\n" +
                    "  \"gitDirty\": true,\n" +
                    "  \"buildUtc\": \"unknown\",\n" +
                    "  \"configSha256\": \"unknown\"\n" +
                    "}\n");
                WriteReleaseChecksums(root);
                var vsCandidate = ReleasePackageVerifier.VerifyDirectory(
                    root, version, "unknown", "unknown", "unknown", "unknown");
                Assert(vsCandidate.Verified && vsCandidate.Code == "VerifiedVs2022",
                    "VS2022 自洽独立候选未被允许试运行：" + vsCandidate);

                File.WriteAllText(Path.Combine(root, "Config.dll"), "second-vs-overwrite");
                var changedVsCandidate = ReleasePackageVerifier.VerifyDirectory(
                    root, version, "unknown", "unknown", "unknown", "unknown");
                Assert(!changedVsCandidate.Verified &&
                       changedVsCandidate.Code == "PackageFileHashMismatch",
                    "VS2022 独立候选的不可变DLL被修改后仍被放行：" + changedVsCandidate);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void WriteReleaseChecksums(string root)
        {
            var lines = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    "SHA256SUMS.txt",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                {
                    using (var stream = File.OpenRead(path))
                    using (var sha = SHA256.Create())
                    {
                        var hash = string.Concat(
                            sha.ComputeHash(stream).Select(value => value.ToString("x2")));
                        var relative = path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                        return $"{hash}  {relative}";
                    }
                })
                .ToArray();
            File.WriteAllLines(Path.Combine(root, "SHA256SUMS.txt"), lines);
        }

        private static void LastProjectSelectionSurvivesUpgradeAndUnavailableStorage()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "EPBTest-LastProjectSelection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            try
            {
                var storeDir = Path.Combine(testRoot, "Data");
                const string projectName = "10358-029";
                var projectConfig = ConfigLoader.GetProjectTestConfigPath(storeDir, projectName);
                Directory.CreateDirectory(Path.GetDirectoryName(projectConfig));
                var template = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
                File.Copy(template, projectConfig);

                var statePath = Path.Combine(testRoot, "UserState", "user-state.json");
                Assert(LastProjectSelectionStore.TrySave(
                    storeDir, projectName, out var saveError, statePath), saveError);
                var json = File.ReadAllText(statePath);
                Assert(json.Contains("10358-029") && !json.Contains("DaqControlHardFaultAgeMs"),
                    "用户状态混入程序级安全配置");

                var global = new GlobalConfig
                {
                    Test = new TestConfig { StoreDir = testRoot, TestName = "DefaultProject" }
                };
                var restored = LastProjectSelectionStore.Restore(
                    global, null, Config.NullLogger.Instance, statePath);
                Assert(restored.Restored && global.Test.TestName == projectName &&
                       Path.GetFullPath(global.Test.StoreDir) == Path.GetFullPath(storeDir),
                    "升级后未恢复最后项目");

                File.Move(projectConfig, projectConfig + ".offline");
                var unavailable = LastProjectSelectionStore.Restore(
                    global, null, Config.NullLogger.Instance, statePath);
                Assert(unavailable.SelectionFound && !unavailable.Restored && File.Exists(statePath),
                    "项目暂不可用时错误清除了最后选择");
                File.Move(projectConfig + ".offline", projectConfig);

                var bootstrapState = Path.Combine(testRoot, "BootstrapState", "user-state.json");
                var bootstrap = LastProjectSelectionStore.Restore(
                    new GlobalConfig { Test = new TestConfig() },
                    Path.Combine(storeDir, projectName),
                    Config.NullLogger.Instance,
                    bootstrapState);
                Assert(bootstrap.Restored && bootstrap.UsedBootstrap && File.Exists(bootstrapState),
                    "首次引导项目未写入独立用户状态");
            }
            finally
            {
                if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            }
        }

        private static void IncidentCompletionStartsNewCorrelation()
        {
            var latch = new DaqIncidentLatch();
            var run = Guid.NewGuid();
            latch.BeginRun(run, new[] { "Dev1" });
            var first = latch.Observe(run, "Dev1", 4, "DaqCallbackStale", "first",
                DateTime.UtcNow, new[] { 4, 5 });
            Assert(latch.Complete("Dev1", first.Context.CorrelationId),
                "恢复终态没有结束活动事故");
            var second = latch.Observe(run, "Dev1", 5, "BackgroundQueueFull", "second",
                DateTime.UtcNow, new[] { 4, 5 });
            Assert(second.IsFirst && second.Context.CorrelationId != first.Context.CorrelationId,
                "同一运行中的后续独立事故仍复用了已终结关联号");
            Assert(!latch.Complete("Dev1", first.Context.CorrelationId),
                "旧关联号错误清除了新的活动事故");
        }

        private static void HundredIncidentSymptomsMergeIntoOneContext()
        {
            var latch = new DaqIncidentLatch();
            var run = Guid.NewGuid();
            var started = DateTime.UtcNow;
            latch.BeginRun(run, new[] { "Dev1" });
            Guid correlation = Guid.Empty;
            var firstCount = 0;
            for (var index = 0; index < 100; index++)
            {
                var observation = latch.Observe(
                    run,
                    "Dev1",
                    10 + index,
                    index % 2 == 0 ? "DaqCallbackStale" : "DaqSampleStale",
                    $"fault-{index}",
                    started.AddMilliseconds(index * 100),
                    new[] { 4, 5 });
                if (observation.IsFirst) firstCount++;
                if (correlation == Guid.Empty) correlation = observation.Context.CorrelationId;
                Assert(observation.Context.CorrelationId == correlation,
                    $"第{index + 1}次派生症状创建了新事故关联号");
            }
            Assert(firstCount == 1, $"百次症状首触发次数错误：{firstCount}");
            Assert(latch.TryStartSnapshot(run, "Dev1", out var context),
                "根事故未允许首次trigger快照");
            Assert(!latch.TryStartSnapshot(run, "Dev1", out _),
                "根事故重复允许trigger快照");
            Assert(latch.Complete("Dev1", context.CorrelationId),
                "根事故terminal未能完成一次");
            Assert(!latch.Complete("Dev1", context.CorrelationId),
                "根事故重复提交terminal成功");
            Assert(!latch.TryGet(run, "Dev1", out _),
                "根事故终态后仍残留活动上下文");
        }

        private static void DoCommandRingPriorityPreempts()
        {
            using var worker = new DoController.HighPriorityDoWorker(
                "PriorityPreemption",
                combineDistinctChannels: false);
            using var pressureEntered = new ManualResetEventSlim(false);
            using var releasePressure = new ManualResetEventSlim(false);
            using var completed = new CountdownEvent(5);
            var execution = new ConcurrentQueue<string>();

            Func<IReadOnlyList<int>, DoWriteTiming, bool> BlockingPressure = (channels, timing) =>
            {
                execution.Enqueue("pressure-running");
                pressureEntered.Set();
                return releasePressure.Wait(5000);
            };
            Func<IReadOnlyList<int>, DoWriteTiming, bool> Record(string name)
                => (channels, timing) =>
                {
                    execution.Enqueue(name);
                    return true;
                };
            void OnCompleted(HighPriorityDoTelemetry _) => completed.Signal();

            try
            {
                Assert(worker.TryPostCommand(
                        1,
                        DoController.DoCommandPriority.Pressure,
                        BlockingPressure,
                        OnCompleted,
                        out _),
                    "首个Pressure命令未获接纳");
                Assert(pressureEntered.Wait(1000), "首个Pressure命令未进入设备worker");
                Assert(worker.TryPostCommand(
                        2,
                        DoController.DoCommandPriority.Pressure,
                        Record("pressure-queued"),
                        OnCompleted,
                        out _),
                    "排队Pressure命令未获接纳");
                Assert(worker.TryPostCommand(
                        3,
                        DoController.DoCommandPriority.DirectionChange,
                        Record("direction"),
                        OnCompleted,
                        out _),
                    "DirectionChange命令未获接纳");
                Assert(worker.TryPostCommand(
                        4,
                        DoController.DoCommandPriority.ChannelOff,
                        Record("channel-off"),
                        OnCompleted,
                        out _),
                    "ChannelOff命令未获接纳");
                Assert(worker.TryPostCommand(
                        5,
                        DoController.DoCommandPriority.EmergencyGroupOff,
                        Record("emergency-group-off"),
                        OnCompleted,
                        out _),
                    "EmergencyGroupOff命令未获接纳");
                Assert(worker.PendingWorkItems == 5,
                    $"优先级测试命令数错误：{worker.PendingWorkItems}");

                releasePressure.Set();
                Assert(completed.Wait(3000), "优先级命令未全部完成");
                Assert(execution.SequenceEqual(new[]
                    {
                        "pressure-running",
                        "emergency-group-off",
                        "channel-off",
                        "direction",
                        "pressure-queued"
                    }),
                    "DO固定优先级未在当前物理写结束后抢占低优先级积压：" +
                    string.Join(",", execution));
            }
            finally
            {
                releasePressure.Set();
            }
        }

        private static void DoCommandRingCapacityIsHardBounded()
        {
            using var worker = new DoController.HighPriorityDoWorker(
                "HardCapacity",
                combineDistinctChannels: false);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var completed = new CountdownEvent(64);
            Func<IReadOnlyList<int>, DoWriteTiming, bool> work = (channels, timing) =>
            {
                entered.Set();
                return release.Wait(5000);
            };
            void OnCompleted(HighPriorityDoTelemetry _) => completed.Signal();

            try
            {
                Assert(worker.TryPostCommand(
                        1,
                        DoController.DoCommandPriority.Pressure,
                        work,
                        OnCompleted,
                        out _),
                    "硬容量测试首命令未获接纳");
                Assert(entered.Wait(1000), "硬容量测试首命令未阻塞worker");
                for (var channel = 2; channel <= 64; channel++)
                {
                    Assert(worker.TryPostCommand(
                            channel,
                            DoController.DoCommandPriority.Pressure,
                            work,
                            OnCompleted,
                            out _),
                        $"硬容量第{channel}槽被提前拒绝");
                }

                Assert(worker.PendingWorkItems == 64,
                    $"命令环未把in-flight计入64槽硬容量：{worker.PendingWorkItems}");
                var rejectionClock = Stopwatch.StartNew();
                Assert(!worker.TryPostCommand(
                        65,
                        DoController.DoCommandPriority.Pressure,
                        work,
                        OnCompleted,
                        out _),
                    "第65个物理命令越过硬容量");
                rejectionClock.Stop();
                Assert(rejectionClock.Elapsed.TotalMilliseconds < 20,
                    $"硬容量拒绝超过20ms：{rejectionClock.Elapsed.TotalMilliseconds:F3}ms");

                release.Set();
                Assert(completed.Wait(5000), "64个已接纳命令未全部完成");
                Assert(SpinWait.SpinUntil(() => worker.PendingWorkItems == 0, 1000),
                    "命令环排空后仍残留租用槽位");
            }
            finally
            {
                release.Set();
            }
        }

        private static void DoCommandRingPreservesFifo()
        {
            using var worker = new DoController.HighPriorityDoWorker(
                "PriorityFifo",
                combineDistinctChannels: false);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var completed = new CountdownEvent(5);
            var execution = new ConcurrentQueue<int>();

            try
            {
                Assert(worker.TryPostCommand(
                        1,
                        DoController.DoCommandPriority.Pressure,
                        (channels, timing) =>
                        {
                            execution.Enqueue(-1);
                            entered.Set();
                            return release.Wait(5000);
                        },
                        _ => completed.Signal(),
                        out _),
                    "FIFO屏障命令未获接纳");
                Assert(entered.Wait(1000), "FIFO屏障命令未进入worker");
                for (var index = 0; index < 4; index++)
                {
                    var captured = index;
                    Assert(worker.TryPostCommand(
                            10 + index,
                            DoController.DoCommandPriority.DirectionChange,
                            (channels, timing) =>
                            {
                                execution.Enqueue(captured);
                                return true;
                            },
                            _ => completed.Signal(),
                            out _),
                        $"FIFO第{index + 1}个命令未获接纳");
                }

                release.Set();
                Assert(completed.Wait(3000), "FIFO命令未全部完成");
                Assert(execution.SequenceEqual(new[] { -1, 0, 1, 2, 3 }),
                    "同优先级命令未保持提交FIFO：" + string.Join(",", execution));
            }
            finally
            {
                release.Set();
            }
        }

        private static void DoCommandRingConcurrentAdmissionCompletesExactlyOnce()
        {
            using var worker = new DoController.HighPriorityDoWorker(
                "ConcurrentAdmission",
                combineDistinctChannels: false);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var completionCounts = new ConcurrentDictionary<Guid, int>();
            var accepted = 0;
            var rejected = 0;
            var callbackCount = 0;
            Func<IReadOnlyList<int>, DoWriteTiming, bool> work = (channels, timing) =>
            {
                entered.Set();
                return release.Wait(5000);
            };
            void OnCompleted(HighPriorityDoTelemetry telemetry)
            {
                completionCounts.AddOrUpdate(telemetry.CommandId, 1, (_, count) => count + 1);
                Interlocked.Increment(ref callbackCount);
            }

            try
            {
                Assert(worker.TryPostCommand(
                        1,
                        DoController.DoCommandPriority.Pressure,
                        work,
                        OnCompleted,
                        out _),
                    "并发准入屏障命令未获接纳");
                accepted = 1;
                Assert(entered.Wait(1000), "并发准入屏障命令未进入worker");

                var producers = Enumerable.Range(0, 8)
                    .Select(producer => Task.Run(() =>
                    {
                        for (var index = 0; index < 32; index++)
                        {
                            if (worker.TryPostCommand(
                                    1000 + (producer * 32) + index,
                                    DoController.DoCommandPriority.Pressure,
                                    work,
                                    OnCompleted,
                                    out _))
                                Interlocked.Increment(ref accepted);
                            else
                                Interlocked.Increment(ref rejected);
                        }
                    }))
                    .ToArray();
                Assert(Task.WaitAll(producers, 5000), "并发准入生产者未在有界时间退出");
                Assert(accepted > 1 && accepted <= 64 && rejected > 0,
                    $"并发准入未受64槽硬容量约束：Accepted={accepted} Rejected={rejected}");
                Assert(worker.PendingWorkItems == accepted,
                    $"并发准入计数与租用槽位不一致：Accepted={accepted} " +
                    $"Pending={worker.PendingWorkItems}");

                release.Set();
                Assert(SpinWait.SpinUntil(
                        () => Volatile.Read(ref callbackCount) == Volatile.Read(ref accepted),
                        5000),
                    $"已接纳并发命令未精确完成：Accepted={accepted} Callbacks={callbackCount}");
                Thread.Sleep(50);
                Assert(callbackCount == accepted && completionCounts.Count == accepted &&
                       completionCounts.All(pair => pair.Value == 1),
                    $"并发完成存在丢失/重复：Accepted={accepted} Callbacks={callbackCount} " +
                    $"Unique={completionCounts.Count}");
                Assert(SpinWait.SpinUntil(() => worker.PendingWorkItems == 0, 1000),
                    "并发命令完成后仍残留命令环槽位");
            }
            finally
            {
                release.Set();
            }
        }

        private static void DoCommandRingSteadyStateDoesNotAllocate()
        {
            const int capacity = 64;
            const int iterations = 100000;
            var ring = new DoController.PreallocatedDoCommandRing(capacity);

            for (var index = 0; index < 1000; index++)
            {
                Assert(ring.TryRent(out var slot), "命令环预热租槽失败");
                var priority = (DoController.DoCommandPriority)(index & 3);
                Assert(ring.TryEnqueue(slot, priority), "命令环预热入队失败");
                Assert(ring.TryDequeueHighest(out var dequeued, out var actualPriority) &&
                       dequeued == slot && actualPriority == priority,
                    "命令环预热出队错误");
                Assert(ring.Return(dequeued), "命令环预热归还失败");
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var valid = true;
            for (var index = 0; index < iterations; index++)
            {
                var priority = (DoController.DoCommandPriority)(index & 3);
                valid &= ring.TryRent(out var slot);
                valid &= ring.TryEnqueue(slot, priority);
                valid &= ring.TryDequeueHighest(out var dequeued, out var actualPriority);
                valid &= dequeued == slot && actualPriority == priority;
                valid &= ring.Return(dequeued);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert(valid, "命令环十万次稳态操作发生状态损坏");
            Assert(allocated == 0,
                $"预分配命令环十万次稳态仍分配 {allocated} bytes");
            Assert(ring.Capacity == capacity && ring.QueuedCount == 0 && ring.LeasedCount == 0,
                $"命令环稳态后未完全归还：Queued={ring.QueuedCount} Leased={ring.LeasedCount}");
        }

        private static void HighPriorityDoTimeoutIsBounded()
        {
            using var controller = new DoController(new DoConfig());
            Assert(DoController.HighPriorityOffTimeoutMs == 100,
                "高优先级DO等待时限未按现场要求设置为100ms");
            var deviceType = typeof(DoController).GetNestedType(
                "DoDevice",
                BindingFlags.NonPublic);
            Assert(
                deviceType?.GetField("WriteGate", BindingFlags.Instance | BindingFlags.Public) != null &&
                deviceType.GetField("HighPriorityWorker", BindingFlags.Instance | BindingFlags.Public) != null,
                "DO设备未配置独立写锁和高优先级Worker");
            var field = typeof(DoController).GetField("_doTaskLock",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "未找到DO任务锁");
            var gate = field.GetValue(controller);
            using var entered = new ManualResetEventSlim(false);
            using var telemetryReady = new ManualResetEventSlim(false);
            HighPriorityDoTelemetry telemetry = null;
            controller.HighPriorityOffCompleted += item =>
            {
                telemetry = item;
                telemetryReady.Set();
            };
            var holder = new Thread(() =>
            {
                lock (gate)
                {
                    entered.Set();
                    Thread.Sleep(500);
                }
            });
            holder.IsBackground = true;
            holder.Start();
            Assert(entered.Wait(1000), "未能注入DO锁竞争");

            var clock = Stopwatch.StartNew();
            var result = controller.SetEpbOffHighPriority(4);
            clock.Stop();
            Assert(!result, "锁竞争超时时错误报告断电命令成功");
            Assert(clock.ElapsedMilliseconds >= 80 && clock.ElapsedMilliseconds < 300,
                $"高优先级DO超时后仍在调用线程阻塞 {clock.ElapsedMilliseconds}ms");
            Assert(holder.Join(1000), "DO锁竞争注入线程未退出");
            Assert(telemetryReady.Wait(1000), "超时后实际DO任务完成时未发布晚完成诊断");
            Assert(telemetry != null &&
                   telemetry.TimeoutMs == 100 &&
                   telemetry.CallerTimedOut &&
                   telemetry.LateHardwareSuccess == telemetry.Result &&
                   telemetry.HardwareCompletedUtc != default &&
                   telemetry.QueueDepthAtEnqueue >= 1 &&
                   telemetry.TotalMs >= 300,
                "高优先级DO晚完成诊断缺少队列或总耗时证据");
        }

        private static void HighPriorityDoCoalescesDuplicateOff()
        {
            using var worker = new DoController.HighPriorityDoWorker("TestDevice");
            using var batchEntered = new ManualResetEventSlim(false);
            using var releaseBatch = new ManualResetEventSlim(false);
            using var duplicateStart = new ManualResetEventSlim(false);
            using var duplicateReady = new CountdownEvent(16);
            var batchCalls = 0;
            var batchChannelCount = 0;
            Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork = (channels, timing) =>
            {
                Interlocked.Increment(ref batchCalls);
                batchChannelCount = channels.Count;
                batchEntered.Set();
                return releaseBatch.Wait(2000);
            };

            var first = Task.Run(() => worker.InvokeHi(4, batchWork, 2000, null));
            Assert(batchEntered.Wait(1000), "首个OFF未进入专用设备Worker");
            var duplicates = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() =>
                {
                    duplicateReady.Signal();
                    duplicateStart.Wait();
                    return worker.InvokeHi(4, batchWork, 2000, null);
                }))
                .ToArray();
            Assert(duplicateReady.Wait(1000), "重复OFF并发调用未准备完成");
            duplicateStart.Set();
            Assert(SpinWait.SpinUntil(() => worker.CoalescedRequests >= duplicates.Length, 1000),
                $"重复OFF未全部合并：Coalesced={worker.CoalescedRequests}");
            Assert(worker.PendingWorkItems == 1,
                $"同通道重复OFF错误扩大队列：Pending={worker.PendingWorkItems}");

            releaseBatch.Set();
            Assert(first.Wait(1000) && first.Result, "首个合并OFF未完成");
            Assert(Task.WaitAll(duplicates, 1000) && duplicates.All(task => task.Result),
                "合并等待者未收到唯一硬件命令结果");
            Assert(batchCalls == 1 && batchChannelCount == 1,
                $"同通道OFF被重复执行：Calls={batchCalls} Channels={batchChannelCount}");
            Assert(SpinWait.SpinUntil(() => worker.PendingWorkItems == 0, 1000),
                "合并OFF完成后仍残留待处理命令");

            var workItemType = typeof(DoController.HighPriorityDoWorker).GetNestedType(
                "WorkItem",
                BindingFlags.NonPublic);
            Assert(workItemType != null &&
                   workItemType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                       .All(field => field.FieldType != typeof(ManualResetEventSlim)),
                "高频OFF仍持有必须显式释放的ManualResetEventSlim等待句柄");
        }

        private static void HighPriorityDoAsyncCoalescingCompletesEverySubmitter()
        {
            using var worker = new DoController.HighPriorityDoWorker("AsyncCoalescing");
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var allCompleted = new CountdownEvent(17);
            var batchCalls = 0;
            var callbackCalls = 0;
            var commandIds = new ConcurrentDictionary<Guid, byte>();
            Func<IReadOnlyList<int>, DoWriteTiming, bool> work = (channels, timing) =>
            {
                Interlocked.Increment(ref batchCalls);
                entered.Set();
                return release.Wait(2000);
            };
            void Completed(HighPriorityDoTelemetry telemetry)
            {
                if (telemetry != null && commandIds.TryAdd(telemetry.CommandId, 0))
                    Interlocked.Increment(ref callbackCalls);
                allCompleted.Signal();
            }

            var firstClock = Stopwatch.StartNew();
            Assert(worker.TryPostHi(4, work, Completed, out var firstId),
                "首个异步OFF未被接纳");
            firstClock.Stop();
            Assert(firstClock.Elapsed.TotalMilliseconds < 20,
                $"首个异步OFF提交阻塞 {firstClock.Elapsed.TotalMilliseconds:F3}ms");
            Assert(entered.Wait(1000), "异步OFF未进入专用worker");

            for (var index = 0; index < 16; index++)
            {
                var clock = Stopwatch.StartNew();
                Assert(worker.TryPostHi(4, work, Completed, out var commandId),
                    $"第{index + 1}个合并异步OFF未被接纳");
                clock.Stop();
                Assert(clock.Elapsed.TotalMilliseconds < 20,
                    $"合并异步OFF提交阻塞 {clock.Elapsed.TotalMilliseconds:F3}ms");
                Assert(commandId != Guid.Empty && commandId != firstId,
                    "合并异步OFF未分配独立CommandId");
            }

            Assert(worker.PendingWorkItems == 1 && worker.CoalescedRequests >= 16,
                $"异步同通道请求未合并：Pending={worker.PendingWorkItems} " +
                $"Coalesced={worker.CoalescedRequests}");
            Assert(allCompleted.CurrentCount == 17,
                "物理写仍阻塞时异步提交者被提前报告完成");
            release.Set();
            Assert(allCompleted.Wait(2000), "合并异步OFF存在提交者未收到完成回调");
            Assert(batchCalls == 1 && callbackCalls == 17 && commandIds.Count == 17,
                $"异步合并完成语义错误：Batch={batchCalls} Callback={callbackCalls} " +
                $"Ids={commandIds.Count}");
        }

        private static void AdaptiveTerminalOffSubmissionIsNonBlockingAndIsolated()
        {
            using (var dev1 = new DoController.HighPriorityDoWorker("AsyncDev1"))
            using (var dev2 = new DoController.HighPriorityDoWorker("AsyncDev2"))
            using (var dev1Entered = new ManualResetEventSlim(false))
            using (var dev1Completed = new ManualResetEventSlim(false))
            using (var dev2Completed = new ManualResetEventSlim(false))
            {
                Assert(dev1.TryPostHi(
                        4,
                        (channels, timing) =>
                        {
                            dev1Entered.Set();
                            Thread.Sleep(80);
                            return true;
                        },
                        _ => dev1Completed.Set(),
                        out _),
                    "Dev1阻塞OFF未获接纳");
                Assert(dev1Entered.Wait(1000), "Dev1阻塞OFF未进入worker");

                var otherDeviceClock = Stopwatch.StartNew();
                Assert(dev2.TryPostHi(
                        10,
                        (channels, timing) => true,
                        _ => dev2Completed.Set(),
                        out _),
                    "Dev2独立OFF未获接纳");
                otherDeviceClock.Stop();
                Assert(otherDeviceClock.Elapsed.TotalMilliseconds < 20 &&
                       dev2Completed.Wait(50) && !dev1Completed.IsSet,
                    $"Dev1阻塞错误拖累Dev2：Submit={otherDeviceClock.Elapsed.TotalMilliseconds:F3}ms");
                Assert(dev1Completed.Wait(1000), "Dev1阻塞OFF未最终完成");
            }

            // 使用真实 FeedCurrentSample -> ProcessAdaptiveSample -> terminal OFF 提交链。
            // 通过持有 DoController 初始化锁让专用 worker 内部阻塞，DAQ 调用线程不得等待该锁。
            using var controller = new DoController(new DoConfig());
            using var ao = new AoController(new AoConfig());
            var hydraulic = new HydraulicController(
                controller,
                new TestConfig(),
                _ => 0.0,
                ao);
            var runner = new EpbCycleRunner(
                4,
                1,
                _ => 0.0,
                controller,
                null,
                hydraulic,
                15.0,
                1000,
                epbControlMode: EpbControlMode.AdaptiveCurrent,
                adaptiveShadowMode: false,
                programSafetySettings: new EpbProgramSafetySettings());
            var begin = typeof(EpbCycleRunner).GetMethod(
                "BeginAdaptiveForwardMonitoring",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(begin != null, "未找到自适应正向监控入口");
            // 先走一遍同一真实路径，排除首次 JIT 对 20ms 运行时门限的污染。
            begin.Invoke(runner, new object[] { 1000 });
            runner.FeedCurrentSample(new FastEpbCurrentSample(
                4,
                0,
                0,
                DateTime.UtcNow,
                Stopwatch.GetTimestamp(),
                1,
                1,
                FastSignalQualityFlags.ProducerReentry));
            Assert(runner.GetTerminalOffCurrentVerificationTask().Wait(1000),
                "自适应OFF预热路径未收口");
            begin.Invoke(runner, new object[] { 1000 });
            var forwardCompletionField = typeof(EpbCycleRunner).GetField(
                "_adaptiveForwardCompletion",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(forwardCompletionField != null, "未找到自适应正向完成门");
            var forwardCompletion =
                (TaskCompletionSource<EpbAdaptiveDecision>)forwardCompletionField.GetValue(runner);

            var taskGateField = typeof(DoController).GetField(
                "_doTaskLock",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(taskGateField != null, "未找到DO初始化锁");
            var taskGate = taskGateField.GetValue(controller);
            using var lockEntered = new ManualResetEventSlim(false);
            using var releaseDoLock = new ManualResetEventSlim(false);
            var holder = new Thread(() =>
            {
                lock (taskGate)
                {
                    lockEntered.Set();
                    releaseDoLock.Wait(2000);
                }
            }) { IsBackground = true };
            holder.Start();
            Assert(lockEntered.Wait(1000), "未能注入DO worker内部永久阻塞模拟");

            var subscriberClock = Stopwatch.StartNew();
            runner.FeedCurrentSample(new FastEpbCurrentSample(
                4,
                0,
                0,
                DateTime.UtcNow,
                Stopwatch.GetTimestamp(),
                2,
                2,
                FastSignalQualityFlags.ProducerReentry));
            subscriberClock.Stop();
            Assert(subscriberClock.Elapsed.TotalMilliseconds < 20,
                $"真实adaptive subscriber仍同步等待DO：{subscriberClock.Elapsed.TotalMilliseconds:F3}ms");
            Assert(!forwardCompletion.Task.IsCompleted,
                "DO worker仍阻塞时正向阶段已提前完成，存在反向重上电风险");
            Assert(forwardCompletion.Task.Wait(500) &&
                   forwardCompletion.Task.Result.HardFault &&
                   forwardCompletion.Task.Result.Reason.Contains("TerminalOffHardwareTimeout") &&
                   holder.IsAlive,
                "NI写永久阻塞时自适应阶段未在独立100ms硬截止内以硬故障收口");
            var committedReason = forwardCompletion.Task.Result.Reason;
            releaseDoLock.Set();
            Assert(holder.Join(1000), "DO worker阻塞注入线程未退出");
            Thread.Sleep(100);
            Assert(forwardCompletion.Task.Result.Reason == committedReason,
                "迟到物理回调二次迁移了已经超时提交的自适应状态");
        }

        private static void AdaptiveTerminalOffDeadlineCommitsExactlyOnce()
        {
            using var worker = new DoController.HighPriorityDoWorker("DeadlineGate");
            using var hardwareEntered = new ManualResetEventSlim(false);
            using var releaseHardware = new ManualResetEventSlim(false);
            using var lateCallback = new ManualResetEventSlim(false);
            var resolution = new AdaptiveTerminalOffResolutionGate();
            var emergencyCount = 0;
            var stateTransitionCount = 0;
            var lateEvidenceCount = 0;

            var submitClock = Stopwatch.StartNew();
            Assert(worker.TryPostHi(
                    4,
                    (channels, timing) =>
                    {
                        hardwareEntered.Set();
                        releaseHardware.Wait(2000);
                        return true;
                    },
                    telemetry =>
                    {
                        if (resolution.TryCommitHardwareCompletion())
                            Interlocked.Increment(ref stateTransitionCount);
                        else
                        {
                            Interlocked.Increment(ref lateEvidenceCount);
                            lateCallback.Set();
                        }
                    },
                    out _),
                "硬截止测试OFF未被接纳");
            submitClock.Stop();
            Assert(submitClock.Elapsed.TotalMilliseconds < 20,
                $"NI永久阻塞模拟的caller提交耗时{submitClock.Elapsed.TotalMilliseconds:F3}ms");
            Assert(hardwareEntered.Wait(1000), "硬截止测试未进入物理写");

            var deadline = EpbCycleRunner.MonitorAcceptedTerminalOffDeadlineAsync(
                resolution,
                50,
                _ =>
                {
                    Interlocked.Increment(ref emergencyCount);
                    Interlocked.Increment(ref stateTransitionCount);
                });
            Assert(deadline.Wait(500) && deadline.Result,
                "已接纳OFF在NI永久阻塞时未触发独立单调时钟截止");
            Assert(emergencyCount == 1 && stateTransitionCount == 1 &&
                   resolution.Resolution == AdaptiveTerminalOffResolution.HardwareTimedOut,
                $"硬截止未唯一提交组联锁：Emergency={emergencyCount} " +
                $"Transitions={stateTransitionCount} State={resolution.Resolution}");

            releaseHardware.Set();
            Assert(lateCallback.Wait(1000), "解除NI阻塞后未收到迟到物理证据");
            Assert(emergencyCount == 1 && stateTransitionCount == 1 && lateEvidenceCount == 1,
                $"迟到物理回调发生二次迁移：Emergency={emergencyCount} " +
                $"Transitions={stateTransitionCount} Late={lateEvidenceCount}");
        }

        private static void HighPriorityDoCompletionCapacityAndDisposeRace()
        {
            var worker = new DoController.HighPriorityDoWorker("DisposeRace");
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var completed = new CountdownEvent(64);
            var ids = new ConcurrentDictionary<Guid, byte>();
            Func<IReadOnlyList<int>, DoWriteTiming, bool> work = (channels, timing) =>
            {
                entered.Set();
                return release.Wait(2000);
            };
            void OnCompleted(HighPriorityDoTelemetry telemetry)
            {
                if (telemetry != null) ids.TryAdd(telemetry.CommandId, 0);
                completed.Signal();
            }

            try
            {
                Assert(worker.TryPostHi(4, work, OnCompleted, out _),
                    "关闭竞态首个异步OFF未接纳");
                Assert(entered.Wait(1000), "关闭竞态物理写未进入worker");
                for (var index = 1; index < 64; index++)
                    Assert(worker.TryPostHi(4, work, OnCompleted, out _),
                        $"完成槽第{index + 1}个请求被提前拒绝");

                var rejectionClock = Stopwatch.StartNew();
                Assert(!worker.TryPostHi(4, work, OnCompleted, out _),
                    "同一物理任务超过64个完成登记后仍无界接纳");
                rejectionClock.Stop();
                Assert(rejectionClock.Elapsed.TotalMilliseconds < 20,
                    $"完成登记容量拒绝不够快：{rejectionClock.Elapsed.TotalMilliseconds:F3}ms");

                var disposing = Task.Run(() => worker.Dispose());
                Thread.Sleep(20);
                release.Set();
                Assert(disposing.Wait(2000), "worker关闭未在物理写解除后收口");
                Assert(completed.Wait(2000) && ids.Count == 64,
                    $"关闭竞态丢失已接纳完成：Callbacks={64 - completed.CurrentCount} " +
                    $"UniqueIds={ids.Count}");
            }
            finally
            {
                release.Set();
                worker.Dispose();
            }
        }

        private static void HighPriorityDoAdmissionAndStopAreLinearized()
        {
            var worker = new DoController.HighPriorityDoWorker("AdmissionStopRace");
            using var indexed = new ManualResetEventSlim(false);
            using var releaseAdmission = new ManualResetEventSlim(false);
            using var disposeStarted = new ManualResetEventSlim(false);
            using var callbackArrived = new ManualResetEventSlim(false);
            var callbackCount = 0;
            var accepted = false;
            Guid acceptedCommandId = Guid.Empty;

            worker.AdmissionIndexedTestHook = () =>
            {
                indexed.Set();
                releaseAdmission.Wait(2000);
            };

            try
            {
                var submitting = Task.Run(() =>
                {
                    accepted = worker.TryPostHi(
                        4,
                        (channels, timing) => true,
                        telemetry =>
                        {
                            if (telemetry != null && telemetry.CommandId == acceptedCommandId)
                                Interlocked.Increment(ref callbackCount);
                            callbackArrived.Set();
                        },
                        out acceptedCommandId);
                });
                Assert(indexed.Wait(1000),
                    "未进入pending索引登记与物理队列入队之间的确定性竞态窗口");

                var contendedAdmissionClock = Stopwatch.StartNew();
                Assert(!worker.TryPostHi(
                        5,
                        (channels, timing) => true,
                        _ => { },
                        out _),
                    "准入门被占用时另一通道不应越过有界提交预算");
                contendedAdmissionClock.Stop();
                Assert(contendedAdmissionClock.Elapsed.TotalMilliseconds < 20,
                    $"准入门竞争导致异步提交阻塞" +
                    $"{contendedAdmissionClock.Elapsed.TotalMilliseconds:F3}ms");

                var disposing = Task.Run(() =>
                {
                    disposeStarted.Set();
                    worker.Dispose();
                });
                Assert(disposeStarted.Wait(1000), "关闭线程未启动");
                Assert(!disposing.Wait(50),
                    "Dispose越过尚未完成的准入窗口，可能排空后再接纳工作");

                releaseAdmission.Set();
                Assert(submitting.Wait(1000) && accepted && acceptedCommandId != Guid.Empty,
                    "线性化窗口解除后提交未作为已接纳命令返回");
                Assert(disposing.Wait(2000), "线性化关闭未在已接纳命令收口后完成");
                Assert(callbackArrived.Wait(1000) && callbackCount == 1,
                    $"关闭竞态丢失或重复完成回调：Callbacks={callbackCount}");
            }
            finally
            {
                releaseAdmission.Set();
                worker.AdmissionIndexedTestHook = null;
                worker.Dispose();
            }
        }

        private static void SubmittedTerminalOffFailureEscalatesExactlyOnce()
        {
            var escalations = 0;
            var failed = new HighPriorityDoTelemetry
            {
                CommandId = Guid.NewGuid(),
                Channel = 4,
                Result = false,
                HardwareCompletedUtc = DateTime.UtcNow
            };
            Assert(!EpbCycleRunner.CompleteSubmittedTerminalOffWithEscalation(
                       failed,
                       () => Interlocked.Increment(ref escalations)) &&
                   escalations == 1,
                "异步OFF物理失败未从完成回调触发唯一组级升级");

            var succeeded = new HighPriorityDoTelemetry
            {
                CommandId = Guid.NewGuid(),
                Channel = 10,
                Result = true,
                HardwareCompletedUtc = DateTime.UtcNow
            };
            Assert(EpbCycleRunner.CompleteSubmittedTerminalOffWithEscalation(
                       succeeded,
                       () => Interlocked.Increment(ref escalations)) &&
                   escalations == 1,
                "异步OFF物理成功仍错误触发组级升级");
        }

        private static void DaqBackgroundTasksAreCoalescedAndDrained()
        {
            using var supervisor = new CoalescingTaskSupervisor(Config.NullLogger.Instance);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var executions = 0;
            Assert(supervisor.TryRun("DeviceFault:Dev1:QueueFull:1", () =>
            {
                Interlocked.Increment(ref executions);
                entered.Set();
                release.Wait(2000);
            }), "首个DAQ后台任务未被接受");
            Assert(entered.Wait(1000), "DAQ后台任务未开始执行");

            var acceptedDuplicates = 0;
            for (var i = 0; i < 100; i++)
                if (supervisor.TryRun("DeviceFault:Dev1:QueueFull:1", () => { }))
                    acceptedDuplicates++;
            Assert(acceptedDuplicates == 0 && supervisor.ActiveCount == 1 &&
                   supervisor.ActiveKeyCount == 1 && supervisor.CoalescedCount == 100,
                $"同根后台任务未严格合并：Accepted={acceptedDuplicates} Active={supervisor.ActiveCount} " +
                $"Keys={supervisor.ActiveKeyCount} Coalesced={supervisor.CoalescedCount}");

            release.Set();
            Assert(supervisor.StopAcceptingAndDrain(1000), "DAQ后台任务未在退出门禁内收口");
            Assert(executions == 1 && supervisor.ActiveCount == 0 && supervisor.ActiveKeyCount == 0,
                "DAQ后台任务完成后仍残留活动任务或业务键");
            Assert(!supervisor.TryRun("late", () => { }), "停止接收后仍启动了迟到后台任务");
        }

        private static void EmergencyPowerGroupLatchKeepsCorrelation()
        {
            var latch = new EmergencyPowerGroupLatch();
            var firstCorrelation = Guid.NewGuid();
            var first = latch.Register(3, firstCorrelation, DateTime.UtcNow);
            var duplicate = latch.Register(
                3,
                Guid.NewGuid(),
                DateTime.UtcNow.AddSeconds(1),
                nonDaqFault: true);
            var repeatedHardFault = latch.Register(
                3,
                Guid.NewGuid(),
                DateTime.UtcNow.AddSeconds(1),
                nonDaqFault: true);

            Assert(first.IsFirst && first.RequestCount == 1 &&
                   first.CorrelationId == firstCorrelation,
                "首次电源组联锁未建立活动关联");
            Assert(!duplicate.IsFirst && duplicate.RequestCount == 2 &&
                   duplicate.CorrelationId == first.CorrelationId &&
                   duplicate.ShouldPublishNonDaqFault,
                "重复电源组联锁未复用原关联或被错误当成首次请求");
            Assert(!repeatedHardFault.ShouldPublishNonDaqFault &&
                   repeatedHardFault.RequestCount == 3,
                "同一活动联锁重复发布了非DAQ组故障");
            Assert(latch.ContainsKey(3) && latch.TryRemove(3) && !latch.ContainsKey(3),
                "电源组联锁恢复提交后未能清除活动关联");

            var next = latch.Register(3, Guid.NewGuid(), DateTime.UtcNow.AddSeconds(2));
            Assert(next.IsFirst && next.RequestCount == 1 &&
                   next.CorrelationId != first.CorrelationId,
                "旧联锁清除后的新事故仍复用了历史关联");
        }

        private static void DoTimelineAppendsCommandElapsed()
        {
            var path = Path.Combine(Path.GetTempPath(), "epb-do-timeline-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                EpbManager.WriteDoTimeline(
                    path,
                    new[]
                    {
                        new DoControlTraceEvent
                        {
                            Utc = DateTime.UtcNow,
                            MonotonicTicks = 1,
                            RunId = Guid.NewGuid(),
                            Channel = 8,
                            Stage = "EnsureAdaptiveTerminalPowerOff",
                            Command = EpbDoCommand.OffHighPriority,
                            DoCommandResult = false,
                            BranchCurrentA = 0.389,
                            CommandElapsedMs = 100.5
                        }
                    });
                var csv = File.ReadAllText(path);
                Assert(csv.Contains("BranchCurrentA,PhysicalPowerState,CommandElapsedMs") &&
                       csv.Contains(",false,0.389000,NotMeasured,100.500"),
                    "DO时间线未在兼容列尾追加命令耗时");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void DaqRecoveryStateCannotRegress()
        {
            Assert(EpbManager.ShouldPreserveDaqRecoveringState(
                    ChannelRuntimeState.Running, true) &&
                   EpbManager.ShouldPreserveDaqRecoveringState(
                    ChannelRuntimeState.WarningRunning, true),
                "DAQ恢复期间旧Runner状态仍可覆盖系统自恢复");
            Assert(!EpbManager.ShouldPreserveDaqRecoveringState(
                    ChannelRuntimeState.Running, false) &&
                   !EpbManager.ShouldPreserveDaqRecoveringState(
                    ChannelRuntimeState.AlarmStopped, true),
                "正常运行或硬件停机状态被错误拦截");
        }

        private static void OwnedRawBatchPreservesPreCalibrationValues()
        {
            var source = new[,]
            {
                { 1.25, 2.5, 3.75 },
                { -4.0, 5.125, 6.25 }
            };
            using (var owned = OwnedDaqRawBatch.CopyFrom(
                       "Dev1", source, DateTime.UtcNow, DateTime.UtcNow.AddMilliseconds(-10)))
            {
                for (var channel = 0; channel < source.GetLength(0); channel++)
                for (var sample = 0; sample < source.GetLength(1); sample++)
                {
                    var expected = source[channel, sample];
                    source[channel, sample] = expected * 1000 + 7;
                    Assert(Math.Abs(owned[channel, sample] - expected) < 1e-12,
                        "池化原始缓冲被后续原地标定修改。");
                }

                Assert(owned.ChannelCount == 2 && owned.SampleCount == 3 && owned.Device == "Dev1",
                    "池化原始批次身份或维度错误。");
            }
        }

        private static void StreamingStatMedianCarriesTailAcrossBatches()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "epb-stat-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var context = new DaqAIContext("Dev1", 256, 60, 1, 1, 2, directory)
                {
                    medianLens = 3,
                    eMBToDaqCurrentChannel = new SortedDictionary<string, int>
                    {
                        ["EPB1_current"] = 0
                    }
                };
                context.paraNameToScale["EPB1_current"] = 1;
                context.paraNameToOffset["EPB1_current"] = 0;
                context.paraNameToZeroValue["EPB1_current"] = 0;

                context.EnqueueStatData(new[,] { { 100.0, 1.0 } }, DateTime.UtcNow);
                context.EnqueueStatData(new[,] { { 2.0 } }, DateTime.UtcNow.AddMilliseconds(2));
                context.FlushStatToDiskAsync().GetAwaiter().GetResult();

                var file = Directory.GetFiles(directory, "DAQ_Dev1_Stat.bin").Single();
                using (var reader = new BinaryReader(File.OpenRead(file)))
                {
                    Assert(reader.ReadInt32() == 1, "Stat记录计数格式发生变化。");
                    reader.ReadInt64();
                    var maximum = reader.ReadDouble();
                    var minimum = reader.ReadDouble();
                    Assert(Math.Abs(maximum - 2.0) < 1e-12 && Math.Abs(minimum - 2.0) < 1e-12,
                        $"跨批次中值尾部丢失：Min={minimum}, Max={maximum}。");
                }
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void TimerResumesAtFutureCompleteBoundary()
        {
            var timer = new HighPrecisionTimer(500, OverrunPolicy.AlignToWallClock);
            using (var firstStarted = new ManualResetEventSlim(false))
            using (var releaseFirst = new ManualResetEventSlim(false))
            using (var secondStarted = new ManualResetEventSlim(false))
            {
                var clock = Stopwatch.StartNew();
                long secondAt = -1;
                var running = timer.StartAsync(2, 0, (cycle, token) =>
                {
                    if (cycle == 1)
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(token);
                    }
                    else
                    {
                        secondAt = clock.ElapsedMilliseconds;
                        secondStarted.Set();
                    }
                    return Task.FromResult(true);
                });

                Assert(firstStarted.Wait(TimeSpan.FromSeconds(2)), "首个周期未进入测试阻塞点。");
                timer.Pause();
                releaseFirst.Set();
                Thread.Sleep(50);
                var resumedAt = clock.ElapsedMilliseconds;
                timer.ResumeAtNextBoundary(220);

                Assert(secondStarted.Wait(TimeSpan.FromSeconds(2)), "恢复后的未来完整周期未执行。");
                running.GetAwaiter().GetResult();
                var delay = secondAt - resumedAt;
                Assert(delay >= 180 && delay < 1500,
                    $"定时器恢复后沿用旧半圈或锚点异常：Delay={delay}ms。");
            }
        }

        private static void RecoveryTerminalGateCommitsExactlyOnce()
        {
            var gate = new DaqRecoveryTerminalGate();
            var winners = 0;
            using (var start = new ManualResetEventSlim(false))
            {
                var tasks = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
                {
                    start.Wait();
                    var proposed = (DaqRecoveryTerminal)(index % 4 + 1);
                    if (gate.TryCommit(proposed)) Interlocked.Increment(ref winners);
                })).ToArray();
                start.Set();
                Task.WaitAll(tasks);
            }
            Assert(winners == 1, $"并发恢复终态提交次数错误：{winners}。");
            Assert(gate.Current != DaqRecoveryTerminal.None,
                "并发恢复没有留下唯一可审计终态。");
            Assert(!gate.TryCommit(DaqRecoveryTerminal.SystemFault),
                "恢复终态提交后仍可被迟到超时覆盖。");
        }

        private static void ProbeCapabilityMissingIsNotHardwareEvidence()
        {
            var unavailable = new DaqHardwareProbeResult
            {
                Device = "Dev1",
                EnumerationSucceeded = true,
                DevicePresent = true,
                SelfTestAttempted = false,
                SelfTestSucceeded = false
            };
            var absent = new DaqHardwareProbeResult
            {
                Device = "Dev1",
                EnumerationSucceeded = true,
                DevicePresent = false
            };
            Assert(!unavailable.IndependentFailureConfirmed &&
                   unavailable.ToEvidence().Code == "DeviceSelfTestUnavailable",
                "DAQmx版本能力缺失被误当成设备硬件故障。");
            Assert(absent.IndependentFailureConfirmed,
                "DAQmx明确枚举缺失未形成独立硬件证据。");
        }

        private static void UiDispatchGateUsesMonotonicRateLimit()
        {
            var gate = new PeriodicDispatchGate(25);
            var interval = Stopwatch.Frequency / 25;
            var start = Stopwatch.Frequency;
            Assert(gate.TryAcquire(start), "首个UI批次未发布");
            Assert(!gate.TryAcquire(start + interval / 2), "限频窗口内重复发布");
            Assert(gate.TryAcquire(start + interval + 1), "限频窗口后未发布");
            gate.Reset();
            Assert(gate.TryAcquire(start + interval + 2), "复位后首批未立即发布");
        }

        private static void UiLatestValueSignalCoalescesBacklog()
        {
            using var signal = new CoalescingAsyncSignal();
            for (var i = 0; i < 10000; i++) signal.Set();
            Assert(signal.PendingCount == 1, "最新值发布在消费者阻塞时积累了历史空唤醒");

            signal.WaitAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(signal.PendingCount == 0, "消费一次后仍残留历史空唤醒");

            signal.Set();
            Assert(signal.PendingCount == 1, "消费后新的最新值不能重新唤醒消费者");
        }

        private static void UiLatestPairMailboxStaysBounded()
        {
            var mailbox = new LatestPairMailbox<MailboxValue>();
            Parallel.Invoke(
                () =>
                {
                    for (var i = 0; i < 100000; i++)
                        mailbox.Publish(0, new MailboxValue { Sequence = i });
                },
                () =>
                {
                    for (var i = 0; i < 100000; i++)
                        mailbox.Publish(1, new MailboxValue { Sequence = 100000 + i });
                });

            Assert(mailbox.PendingSlotCount == 2,
                "消费者阻塞后UI邮箱积累了超过两块设备的历史批次");
            Assert(mailbox.TryTake(out var first, out var second), "UI邮箱未返回最新批次");
            Assert(first.Sequence == 99999 && second.Sequence == 199999,
                "UI邮箱没有保留每块设备各自的最新批次");
            Assert(!mailbox.HasPending && mailbox.PendingSlotCount == 0,
                "消费最新批次后仍残留历史UI工作");
        }

        private sealed class MailboxValue
        {
            public int Sequence;
        }

        private static void UiCurveBreakUsesAcquisitionTimeline()
        {
            var previous = new DateTime(2026, 8, 9, 6, 0, 0, DateTimeKind.Utc);
            const int sampleCount = 20;
            const double sampleIntervalSeconds = 0.0005;

            // 即使 UI 数百毫秒没有绘制，只要被发布批次携带的上一 DAQ 批次仍相邻，
            // 就不能把显示邮箱的覆盖误画成采集断点。
            Assert(!UiCurveContinuityPolicy.ShouldBreakLine(
                    previous.AddMilliseconds(10), previous, sampleCount, sampleIntervalSeconds),
                "相邻DAQ批次被错误断笔");
            Assert(UiCurveContinuityPolicy.ShouldBreakLine(
                    previous.AddMilliseconds(500), previous, sampleCount, sampleIntervalSeconds),
                "真实DAQ时间轴空窗未断笔");
            Assert(!UiCurveContinuityPolicy.ShouldBreakLine(
                    previous.AddMilliseconds(500), DateTime.MinValue, sampleCount, sampleIntervalSeconds),
                "首批无上一时间戳时被错误断笔");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var breaks = 0;
            for (var i = 0; i < 100000; i++)
                if (UiCurveContinuityPolicy.ShouldBreakLine(
                        previous.AddMilliseconds(10), previous, sampleCount, sampleIntervalSeconds))
                    breaks++;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(breaks == 0, "连续批次压力测试出现误断笔");
            Assert(allocated <= 256, $"曲线连续性判定十万次持续分配 {allocated} bytes");
        }

        private static void UiLogDisplayPolicyIsBoundedAndAllocationFree()
        {
            Assert(UiLogDisplayPolicy.CalculateLinesToRemove(2000, 2000, 1800) == 0,
                "达到显示上限时不应提前裁剪");
            Assert(UiLogDisplayPolicy.CalculateLinesToRemove(2001, 2000, 1800) == 201,
                "越过水位线后未一次裁剪到保留线数");

            var frequency = Stopwatch.Frequency;
            var started = frequency;
            Assert(UiLogDisplayPolicy.ShouldAutoScroll(started, 0, frequency, 500),
                "首次日志批次未允许自动滚动");
            Assert(!UiLogDisplayPolicy.ShouldAutoScroll(
                    started + frequency * 499 / 1000, started, frequency, 500),
                "500ms限频窗口内重复滚动");
            Assert(UiLogDisplayPolicy.ShouldAutoScroll(
                    started + frequency / 2, started, frequency, 500),
                "达到500ms后未允许滚动");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var removals = 0;
            for (var i = 0; i < 100000; i++)
            {
                removals += UiLogDisplayPolicy.CalculateLinesToRemove(2001, 2000, 1800);
                UiLogDisplayPolicy.ShouldAutoScroll(started + i, started, frequency, 500);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(removals == 20100000, "日志裁剪压力测试结果不稳定");
            Assert(allocated <= 256, $"日志显示策略十万次持续分配 {allocated} bytes");
        }

        private static void DaqStaleRootClassification()
        {
            Assert(EpbManager.ClassifyDaqStaleRoot(new DaqFreshnessSnapshot
            {
                CallbackAgeMs = 1167,
                ControlEnqueueAgeMs = 1167,
                ControlProcessedAgeMs = 1167
            }) == "DaqCallbackStale", "回调空窗未识别为根故障");
            Assert(EpbManager.ClassifyDaqStaleRoot(new DaqFreshnessSnapshot
            {
                CallbackAgeMs = 10,
                ControlEnqueueAgeMs = 120,
                ControlProcessedAgeMs = 130
            }) == "ControlEnqueueStale", "回调到控制入队停顿未识别");
            Assert(EpbManager.ClassifyDaqStaleRoot(new DaqFreshnessSnapshot
            {
                CallbackAgeMs = 10,
                ControlEnqueueAgeMs = 10,
                ControlProcessedAgeMs = 120
            }) == "ControlProcessingStale", "控制消费停顿未识别");
        }

        private static void DaqBatchObjectsAreReusableValueBacked()
        {
            Assert(typeof(DaqAIData).IsValueType, "旧原始/统计队列仍为每批创建引用对象");

            DaqDiskBatch Rent(long sequence)
            {
                var timestamps = ArrayPool<DateTime>.Shared.Rent(1);
                var currents = ArrayPool<double>.Shared.Rent(1);
                var channels = ArrayPool<DaqDiskChannelBatch>.Shared.Rent(1);
                timestamps[0] = DateTime.UtcNow;
                currents[0] = sequence;
                channels[0] = new DaqDiskChannelBatch(4, currents);
                return DaqDiskBatch.Rent(
                    "Dev1", 1, sequence, 1, timestamps, channels, 1,
                    null, null, Stopwatch.GetTimestamp(),
                    2000.0302, ClockState.Locked, 15.1, -25.4, 60);
            }

            var first = Rent(1);
            first.Dispose();
            var second = Rent(2);
            Assert(ReferenceEquals(first, second), "持久化批次对象池未复用对象");
            Assert(Math.Abs(second.EffectiveSampleRateHz - 2000.0302) < 1e-9 &&
                   second.ClockState == ClockState.Locked &&
                   Math.Abs(second.EstimatedSkewPpm - 15.1) < 1e-9,
                "持久化批次没有携带归档时钟元数据");
            second.Dispose();
        }

        private static void LegacyRawWriterKeepsBinaryFormat()
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBTest-RawWriter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var context = new DaqAIContext("Dev1", 10, 60, 0.5, 2, 2, root);
                var now = DateTime.Now;
                context.EnqueueRawData(new double[,] { { 0, 0 }, { 0, 0 } }, now, now.AddMilliseconds(-1));
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();
                context.EnqueueRawData(new double[,] { { 1.25, 2.5 }, { -3.75, 4.5 } },
                    now.AddMilliseconds(1), now);
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();

                var path = Path.Combine(root, "DAQ_Dev1_Raw_1.bin");
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                Assert(stream.Length == 112, $"原始二进制长度变化或首次Flush仍被丢弃：{stream.Length}");
                Assert(reader.ReadInt32() == 1, "原始二进制计数器布局变化");
                _ = reader.ReadInt64();
                Assert(Math.Abs(reader.ReadDouble()) < 1e-12 &&
                       Math.Abs(reader.ReadDouble()) < 1e-12,
                    "原始二进制首次Flush首样本布局变化");
                Assert(reader.ReadInt32() == 1, "原始二进制第二样本计数器变化");
                _ = reader.ReadInt64();
                Assert(Math.Abs(reader.ReadDouble()) < 1e-12 &&
                       Math.Abs(reader.ReadDouble()) < 1e-12,
                    "原始二进制首次Flush第二样本布局变化");
                Assert(reader.ReadInt32() == 2, "第二次Flush原始二进制计数器变化");
                _ = reader.ReadInt64();
                Assert(Math.Abs(reader.ReadDouble() - 1.25) < 1e-12 &&
                       Math.Abs(reader.ReadDouble() + 3.75) < 1e-12,
                    "原始二进制第三样本布局变化");
                Assert(reader.ReadInt32() == 2, "第二次Flush原始二进制第二样本计数器变化");
                _ = reader.ReadInt64();
                Assert(Math.Abs(reader.ReadDouble() - 2.5) < 1e-12 &&
                       Math.Abs(reader.ReadDouble() - 4.5) < 1e-12,
                    "原始二进制第四样本布局变化");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void ProducerGateRejectsOverlap()
        {
            var gate = new DaqCallbackProducerGate();
            Assert(gate.TryEnter(), "首个生产者未取得门");
            var rejected = 0;
            var workers = Enumerable.Range(0, 8)
                .Select(_ => new Thread(() =>
                {
                    for (var attempt = 0; attempt < 1000; attempt++)
                    {
                        if (!gate.TryEnter()) Interlocked.Increment(ref rejected);
                        else gate.Exit();
                    }
                }))
                .ToArray();
            foreach (var worker in workers) worker.Start();
            foreach (var worker in workers) Assert(worker.Join(1000), "重叠生产者测试超时");
            var expectedRejected = workers.Length * 1000;
            Assert(rejected == expectedRejected && gate.ReentryCount == expectedRejected,
                "重叠生产者未全部拒绝或重入计数错误");
            gate.Exit();
            Assert(gate.TryEnter(), "生产区退出后无法重新进入");
            gate.Exit();
        }

        private static void AcquisitionTimelineNeverSnapsCatchUpToFuture()
        {
            var origin = new DateTime(2026, 8, 5, 1, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency;
            var timeline = new ClockDisciplinedSampleTimeline();
            timeline.Reset(origin, originTick, 2000);
            var first = timeline.Advance(
                20, origin.AddMilliseconds(500),
                originTick + Stopwatch.Frequency / 2);
            var second = timeline.Advance(
                20, origin.AddMilliseconds(501),
                originTick + (long)(0.501 * Stopwatch.Frequency));
            Assert(first.BatchEndUtc == origin.AddMilliseconds(10) &&
                   second.BatchEndUtc == origin.AddMilliseconds(20),
                "追赶回调错误贴到主机时间后继续推进");
            Assert(!first.RequiresRecovery && !second.RequiresRecovery && first.ArrivalDelayMs > 400,
                "历史积压批被误判为未来样本");
        }

        private static void ClockTimelinesTrackOppositePpmFor72Hours()
        {
            const double nominalRate = 2000;
            const double dev1Ppm = 15.1;
            const double dev2Ppm = -29.5;
            const int hours = 72;
            var origin = new DateTime(2026, 8, 5, 2, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 10L;
            var dev1 = new ClockDisciplinedSampleTimeline();
            var dev2 = new ClockDisciplinedSampleTimeline();
            dev1.Reset(origin, originTick, nominalRate);
            dev2.Reset(origin, originTick, nominalRate);
            long dev1Samples = 0;
            long dev2Samples = 0;
            var dev1Previous = origin;
            var dev2Previous = origin;
            ClockDisciplinedTimelineResult dev1Result = default;
            ClockDisciplinedTimelineResult dev2Result = default;
            var dev1Rate = nominalRate * (1 + dev1Ppm / 1_000_000.0);
            var dev2Rate = nominalRate * (1 + dev2Ppm / 1_000_000.0);

            for (var second = 0; second < hours * 3600; second++)
            {
                dev1Samples += 2000;
                dev2Samples += 2000;
                dev1Result = AdvanceClock(
                    dev1, origin, originTick, dev1Samples, 2000, dev1Rate, 26, second % 3 == 0 ? 0.3 : 0);
                dev2Result = AdvanceClock(
                    dev2, origin, originTick, dev2Samples, 2000, dev2Rate, 29, second % 5 == 0 ? 0.4 : 0);
                Assert(dev1Result.BatchEndUtc > dev1Previous && dev2Result.BatchEndUtc > dev2Previous,
                    "72小时模拟中批次时间没有严格递增");
                Assert(!dev1Result.RequiresRecovery && !dev2Result.RequiresRecovery,
                    "合理ppm漂移被误判为时钟模型失效");
                dev1Previous = dev1Result.BatchEndUtc;
                dev2Previous = dev2Result.BatchEndUtc;
            }

            Assert(dev1Result.ClockState == ClockState.Locked &&
                   dev2Result.ClockState == ClockState.Locked,
                "72小时后设备时钟没有锁定");
            Assert(Math.Abs(dev1Result.EstimatedSkewPpm - dev1Ppm) < 1.0,
                $"Dev1 ppm估计不准确：{dev1Result.EstimatedSkewPpm:F3}");
            Assert(Math.Abs(dev2Result.EstimatedSkewPpm - dev2Ppm) < 1.0,
                $"Dev2 ppm估计不准确：{dev2Result.EstimatedSkewPpm:F3}");
            Assert(dev1Result.TotalSamples == (long)hours * 3600 * 2000 &&
                   dev2Result.TotalSamples == (long)hours * 3600 * 2000,
                "72小时模拟存在样本丢失");
        }

        private static void ClockTimelineReplaysFieldCatchUpWithoutFault()
        {
            const double nominalRate = 2000;
            var actualRate = nominalRate * (1 + 15.1 / 1_000_000.0);
            var origin = new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 20L;
            var timeline = new ClockDisciplinedSampleTimeline();
            timeline.Reset(origin, originTick, nominalRate);
            long totalSamples = 0;
            ClockDisciplinedTimelineResult result = default;
            for (var second = 0; second < 29 * 60; second++)
            {
                totalSamples += 2000;
                result = AdvanceClock(timeline, origin, originTick, totalSamples, 2000, actualRate, 26, 0);
            }

            var callbackTick = originTick + (long)Math.Round(
                (totalSamples / actualRate + 0.026) * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero);
            var intervalsMs = new[] { 15.047, 1.334, 0.142 };
            var sequenceCount = 0;
            foreach (var intervalMs in intervalsMs)
            {
                totalSamples += 20;
                callbackTick += (long)Math.Round(
                    intervalMs / 1000.0 * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero);
                result = timeline.Advance(20, origin, callbackTick);
                Assert(!result.RequiresRecovery, "现场追赶回调被误判为时钟模型硬故障");
                sequenceCount++;
            }
            Assert(sequenceCount == 3 && result.TotalSamples == totalSamples,
                "现场175218~175220追赶批次没有全部保留");
        }

        private static void ClockTimelineIgnoresUtcJumps()
        {
            var origin = new DateTime(2026, 8, 5, 7, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 30L;
            var baseline = new ClockDisciplinedSampleTimeline();
            var jumped = new ClockDisciplinedSampleTimeline();
            baseline.Reset(origin, originTick, 2000);
            jumped.Reset(origin, originTick, 2000);
            long samples = 0;
            for (var second = 1; second <= 120; second++)
            {
                samples += 2000;
                var callbackTick = originTick + (long)Math.Round(
                    (second + 0.025) * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero);
                var normalUtc = origin.AddSeconds(second).AddMilliseconds(25);
                var jumpedUtc = second < 40
                    ? normalUtc
                    : second < 80
                        ? normalUtc.AddHours(6)
                        : normalUtc.AddHours(-6);
                var normal = baseline.Advance(2000, normalUtc, callbackTick);
                var shifted = jumped.Advance(2000, jumpedUtc, callbackTick);
                Assert(normal.BatchEndUtc == shifted.BatchEndUtc &&
                       normal.BatchEndMonotonicTicks == shifted.BatchEndMonotonicTicks &&
                       normal.ClockState == shifted.ClockState,
                    "UTC跳变污染了采样时间轴或时钟状态");
            }
        }

        private static void ClockTimelineRequiresConsecutiveInvalidEvidence()
        {
            var options = new ClockDisciplineOptions(
                estimatorWindowSeconds: 30,
                warmupSeconds: 10,
                maxAbsSkewPpm: 250,
                maxCorrectionPpmPerUpdate: 5,
                residualHardLimitMs: 100,
                invalidConfirmations: 10);
            var origin = new DateTime(2026, 8, 5, 8, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 40L;
            var timeline = new ClockDisciplinedSampleTimeline(options);
            timeline.Reset(origin, originTick, 2000);
            var outOfSpecRate = 2000 * (1 + 1000.0 / 1_000_000.0);
            long samples = 0;
            ClockDisciplinedTimelineResult result = default;
            var firstInvalidSecond = -1;
            for (var second = 0; second < 40; second++)
            {
                samples += 2000;
                result = AdvanceClock(timeline, origin, originTick, samples, 2000, outOfSpecRate, 25, 0);
                if (result.RequiresRecovery)
                {
                    firstInvalidSecond = second;
                    break;
                }
            }
            Assert(firstInvalidSecond >= 19,
                $"时钟异常未经过10次连续确认即失效：second={firstInvalidSecond}");

            var transient = new ClockDisciplinedSampleTimeline(options);
            transient.Reset(origin, originTick, 2000);
            samples = 0;
            for (var second = 0; second < 45; second++)
            {
                samples += 2000;
                var rate = second == 20 ? outOfSpecRate : 2000;
                result = AdvanceClock(transient, origin, originTick, samples, 2000, rate, 25, 0);
                Assert(!result.RequiresRecovery, "单次采样时钟离群错误触发恢复");
            }
        }

        private static void ClockTimelineRequiresSustainedResidualLead()
        {
            var options = new ClockDisciplineOptions(
                estimatorWindowSeconds: 60,
                warmupSeconds: 30,
                maxAbsSkewPpm: 250,
                maxCorrectionPpmPerUpdate: 5,
                residualHardLimitMs: 100,
                invalidConfirmations: 10);
            var origin = new DateTime(2026, 8, 5, 8, 30, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 45L;
            var timeline = new ClockDisciplinedSampleTimeline(options);
            timeline.Reset(origin, originTick, 2000);
            long samples = 0;
            var lockedSeen = false;
            var lockedFitsBeforeInvalid = 0;
            ClockDisciplinedTimelineResult result = default;

            // 保持名义采样率不变，只让低延迟包络持续领先100ms以上。
            // 暖机阶段不得失效；锁定后仍须连续10次确认才进入恢复。
            for (var second = 1; second <= 90; second++)
            {
                samples += 2000;
                var callbackSeconds = samples / 2000.0 - 0.150;
                var callbackTick = originTick + (long)Math.Round(
                    callbackSeconds * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero);
                result = timeline.Advance(2000, origin.AddSeconds(callbackSeconds), callbackTick);
                if (result.ClockState == ClockState.Locked)
                {
                    lockedSeen = true;
                    lockedFitsBeforeInvalid++;
                }
                if (result.RequiresRecovery) break;
            }

            Assert(lockedSeen, "持续residual超前在模型锁定前错误进入恢复");
            Assert(result.RequiresRecovery && result.ResidualMs > 100,
                "锁定后的持续residual超前没有触发DAQ时钟恢复");
            Assert(lockedFitsBeforeInvalid >= 9,
                $"residual超前未经过连续确认即失效：lockedFits={lockedFitsBeforeInvalid}");
        }

        private static void ClockTimelineRequiresSustainedResidualLag()
        {
            var options = new ClockDisciplineOptions(
                estimatorWindowSeconds: 60,
                warmupSeconds: 30,
                maxAbsSkewPpm: 250,
                maxCorrectionPpmPerUpdate: 5,
                residualHardLimitMs: 100,
                invalidConfirmations: 10);
            var origin = new DateTime(2026, 8, 8, 23, 50, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 45L;
            var timeline = new ClockDisciplinedSampleTimeline(options);
            timeline.Reset(origin, originTick, 2000);
            long samples = 0;
            var lockedSeen = false;
            var lockedFitsBeforeInvalid = 0;
            ClockDisciplinedTimelineResult result = default;

            // 复现 V2.12.0.2 现场方向：样本时间轴比回调到达时刻落后 1160ms。
            // 旧代码只判断 residual>100ms，负 residual 永远不会进入恢复。
            for (var second = 1; second <= 90; second++)
            {
                samples += 2000;
                var callbackSeconds = samples / 2000.0 + 1.160;
                var callbackTick = originTick + (long)Math.Round(
                    callbackSeconds * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero);
                result = timeline.Advance(2000, origin.AddSeconds(callbackSeconds), callbackTick);
                if (result.ClockState == ClockState.Locked)
                {
                    lockedSeen = true;
                    lockedFitsBeforeInvalid++;
                }
                if (result.RequiresRecovery) break;
            }

            Assert(lockedSeen, "持续residual滞后在模型锁定前错误进入恢复");
            Assert(result.RequiresRecovery && result.ResidualMs < -100,
                $"锁定后的持续residual滞后没有触发DAQ时钟恢复：{result.ResidualMs:F1}ms");
            Assert(lockedFitsBeforeInvalid >= 9,
                $"residual滞后未经过连续确认即失效：lockedFits={lockedFitsBeforeInvalid}");
        }

        private static void ClockRecoveryAttemptWindowIsBounded()
        {
            var window = new DaqRecoveryAttemptWindow(3, TimeSpan.FromMinutes(10));
            var start = Stopwatch.Frequency * 100L;
            Assert(window.TryRegister("Dev1", start, out var first) && first == 1,
                "第一次时钟恢复被拒绝");
            Assert(window.TryRegister("Dev1", start + Stopwatch.Frequency, out var second) && second == 2,
                "第二次时钟恢复被拒绝");
            Assert(window.TryRegister("Dev1", start + 2 * Stopwatch.Frequency, out var third) && third == 3,
                "第三次时钟恢复被拒绝");
            Assert(!window.TryRegister("Dev1", start + 3 * Stopwatch.Frequency, out var fourth) && fourth == 3,
                "10分钟内第四次时钟恢复没有锁存");
            Assert(window.TryRegister("Dev2", start + 3 * Stopwatch.Frequency, out var isolated) && isolated == 1,
                "Dev1恢复计数污染Dev2");
            Assert(window.TryRegister(
                       "Dev1",
                       start + (long)(TimeSpan.FromMinutes(12).TotalSeconds * Stopwatch.Frequency) + 1,
                       out var expired) && expired == 1,
                "10分钟窗口过期后恢复次数没有释放");
        }

        private static void ClockTimelineHotLoopDoesNotAllocate()
        {
            var origin = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 50L;
            var timeline = new ClockDisciplinedSampleTimeline();
            timeline.Reset(origin, originTick, 2000);
            long samples = 0;
            for (var i = 0; i < 10000; i++)
            {
                samples += 20;
                AdvanceClock(timeline, origin, originTick, samples, 20, 2000.03, 25, 0);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            ClockDisciplinedTimelineResult result = default;
            for (var i = 0; i < 100000; i++)
            {
                samples += 20;
                result = AdvanceClock(timeline, origin, originTick, samples, 20, 2000.03, 25, 0);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 256,
                $"自适应时钟热路径发生持续分配：{allocated} bytes");
            Assert(result.ClockState == ClockState.Locked && !result.RequiresRecovery,
                "稳态无分配测试中的时钟状态异常");
        }

        private static ClockDisciplinedTimelineResult AdvanceClock(
            ClockDisciplinedSampleTimeline timeline,
            DateTime origin,
            long originTick,
            long totalSamples,
            int sampleCount,
            double actualSampleRateHz,
            double baseLatencyMs,
            double jitterMs)
        {
            var callbackSeconds = totalSamples / actualSampleRateHz +
                                  (baseLatencyMs + jitterMs) / 1000.0;
            var callbackTick = originTick + (long)Math.Round(
                callbackSeconds * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero);
            var callbackUtc = origin.AddTicks((long)Math.Round(
                callbackSeconds * TimeSpan.TicksPerSecond,
                MidpointRounding.AwayFromZero));
            return timeline.Advance(sampleCount, callbackUtc, callbackTick);
        }

        private static void LegacyTimelineFlagIsDiagnosticOnly()
        {
            var sample = new FastEpbCurrentSample(
                4,
                12.5,
                12.5,
                DateTime.UtcNow,
                Stopwatch.GetTimestamp(),
                3,
                100,
                FastSignalQualityFlags.TimelineFuture);
            Assert(sample.IsControlUsable,
                "兼容保留的TimelineFuture数值仍错误阻断控制数据");
            Assert(!FastPathTripClassifier.HasInvalidControlQuality(
                    FastSignalQualityFlags.TimelineFuture),
                "过流归因仍把归档时间偏差当作电流信号无效");
        }

        private static void ControlBatchCarriesIdentityClockAndRawTail()
        {
            var ring = new ControlBatchRing(4, 1);
            var raw = new double[1, 20];
            for (var i = 0; i < 20; i++) raw[0, i] = i / 10.0;
            var values = new[] { new FastControlSampleValue(0, 4, 0, 12.5) };
            var metadata = new FastControlBatchMetadata(
                7, 123, DateTime.UtcNow, DateTime.UtcNow,
                1000, 1100, 0, 42, FastSignalQualityFlags.None);
            Assert(ring.TryEnqueue(metadata, values, raw), "结构化控制批入环失败");
            // 控制环必须持有自己的尾点副本。后台取得 raw 所有权并原地换算后，
            // 不得反向污染已发布的快速证据。
            raw[0, 19] = 19.0;
            var destination = new FastControlSampleValue[1];
            var rawTail = new double[ControlBatchRing.RawTailCapacity];
            Assert(ring.TryDequeue(destination, rawTail, out var count, out var rawCount, out var actual),
                "结构化控制批出环失败");
            Assert(count == 1 && rawCount == 20 && actual.Generation == 7 &&
                   actual.SourceSequence == 123 && actual.CaptureMonotonicTicks == 1000 &&
                   Math.Abs(rawTail[19] - 1.9) < 1e-12,
                "控制批身份、单调时钟或原始尾部损坏");
        }

        private static void RawBatchOwnershipTransfersAfterControlCopy()
        {
            var type = typeof(TwoDeviceAiAcquirer);
            var callback = type.GetMethod("OnAiBatch", BindingFlags.Instance | BindingFlags.NonPublic);
            var enqueueControl = type.GetMethod("EnqueueForControl", BindingFlags.Instance | BindingFlags.NonPublic);
            var enqueueProcessing = type.GetMethod(
                "EnqueueForProcessing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(callback != null && enqueueControl != null && enqueueProcessing != null,
                "无法读取DAQ原始批次所有权方法");

            var il = callback.GetMethodBody()?.GetILAsByteArray();
            Assert(il != null && il.Length > 0, "OnAiBatch没有可审计的IL");
            var controlOffset = FindMetadataTokenOffset(il, enqueueControl.MetadataToken);
            var processingOffset = FindMetadataTokenOffset(il, enqueueProcessing.MetadataToken);
            Assert(controlOffset >= 0 && processingOffset >= 0,
                "OnAiBatch未同时调用控制环复制和后台发布");
            Assert(controlOffset < processingOffset,
                "后台在控制环复制完成前取得raw引用，可能触发电压到安培二次换算");

            // 回放 EPB5 的典型批次：-0.7515V 只允许按 10A/V 换算一次。
            const double voltage = -0.7515;
            const double scale = 10.0;
            const double intercept = -0.02411;
            var once = voltage * scale + intercept;
            var twice = once * scale + intercept;
            Assert(Math.Abs(once + 7.53911) < 1e-9 && Math.Abs(twice + 75.41521) < 1e-9,
                "现场二次换算回放基准错误");
        }

        private static int FindMetadataTokenOffset(byte[] il, int metadataToken)
        {
            var token = BitConverter.GetBytes(metadataToken);
            for (var offset = 1; offset <= il.Length - token.Length; offset++)
            {
                var matches = true;
                for (var index = 0; index < token.Length; index++)
                {
                    if (il[offset + index] == token[index]) continue;
                    matches = false;
                    break;
                }
                if (!matches) continue;
                // call(0x28) 和 callvirt(0x6f) 的四字节操作数均为方法元数据令牌。
                if (il[offset - 1] == 0x28 || il[offset - 1] == 0x6f) return offset;
            }
            return -1;
        }

        private static List<int> FindMetadataTokenOffsets(byte[] il, int metadataToken)
        {
            var offsets = new List<int>();
            var token = BitConverter.GetBytes(metadataToken);
            for (var offset = 1; offset <= il.Length - token.Length; offset++)
            {
                var matches = true;
                for (var index = 0; index < token.Length; index++)
                {
                    if (il[offset + index] == token[index]) continue;
                    matches = false;
                    break;
                }
                if (!matches) continue;
                if (il[offset - 1] == 0x28 || il[offset - 1] == 0x6f)
                    offsets.Add(offset);
            }
            return offsets;
        }

        private static void RawBatchOwnershipStressForBothDevices()
        {
            ExerciseRawOwnershipTransfer("Dev1", 5, -0.7515, -0.02411);
            ExerciseRawOwnershipTransfer("Dev2", 11, -0.485, -0.02123);
        }

        private static void ExerciseRawOwnershipTransfer(
            string device,
            int channel,
            double baseVoltage,
            double intercept)
        {
            const int batchCount = 100000;
            const double scale = 10.0;
            var raw = new double[1, ControlBatchRing.RawTailCapacity];
            var ring = new ControlBatchRing(2, 1);
            var samples = new FastControlSampleValue[1];
            var destination = new FastControlSampleValue[1];
            var rawTail = new double[ControlBatchRing.RawTailCapacity];
            var barrier = new Barrier(2);
            var copyFailure = string.Empty;

            // 后台只能在第一个栅栏之后取得 raw 所有权，并在第二个栅栏前完成原地换算。
            // 主线程在转移前完成代表值计算和 RawTail 深复制，形成确定性竞态回归。
            var processingThread = new Thread(() =>
            {
                for (var batch = 0; batch < batchCount; batch++)
                {
                    barrier.SignalAndWait();
                    for (var column = 0; column < raw.GetLength(1); column++)
                        raw[0, column] = raw[0, column] * scale + intercept;
                    barrier.SignalAndWait();
                }
            }) { IsBackground = true, Name = device + "-RawOwnershipTest" };
            processingThread.Start();

            for (var batch = 0; batch < batchCount; batch++)
            {
                var voltage = baseVoltage + ((batch % 7) - 3) * 0.0001;
                for (var column = 0; column < raw.GetLength(1); column++)
                    raw[0, column] = voltage;
                var representative = voltage * scale + intercept;
                samples[0] = new FastControlSampleValue(0, channel, 0, representative);
                var tick = batch + 1L;
                var metadata = new FastControlBatchMetadata(
                    2,
                    batch + 1L,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    tick,
                    tick,
                    0,
                    Thread.CurrentThread.ManagedThreadId,
                    FastSignalQualityFlags.None);

                if (!ring.TryEnqueue(metadata, samples, raw) && copyFailure.Length == 0)
                    copyFailure = device + " 控制批在所有权转移前入环失败";

                barrier.SignalAndWait();
                barrier.SignalAndWait();

                if (!ring.TryDequeue(
                        destination,
                        rawTail,
                        out var count,
                        out var rawCount,
                        out var actual))
                {
                    if (copyFailure.Length == 0) copyFailure = device + " 控制批出环失败";
                    continue;
                }

                if (copyFailure.Length == 0 &&
                    (count != 1 || rawCount != ControlBatchRing.RawTailCapacity ||
                     actual.SourceSequence != batch + 1L ||
                     Math.Abs(destination[0].RepresentativeA - representative) > 1e-12 ||
                     Math.Abs(rawTail[ControlBatchRing.RawTailCapacity - 1] - voltage) > 1e-12))
                    copyFailure = device + " 后台原地换算污染了快速代表值或原始尾点";
            }

            processingThread.Join();
            barrier.Dispose();
            Assert(copyFailure.Length == 0, copyFailure);
        }

        private static void FieldIncidentReplayStaysBelowOverCurrent()
        {
            ReplayCorrectedIncidentSequence(
                "EPB5_current",
                -0.02411,
                new[]
                {
                    -7.515635319637731, -7.596068084299695, -75.1804631963773,
                    -7.4094640702839385, -72.12401813922267, -72.22053745681703,
                    -68.52063028236668, -69.45365035244546, -6.84643471765019,
                    -6.688786498912741, -6.547224833107684, -6.653396082461477
                });
            ReplayCorrectedIncidentSequence(
                "EPB11_current",
                -0.02123,
                new[]
                {
                    -48.5411549511214, -4.977261573819928, -4.810236135542877,
                    -4.678543001516741, -4.858416550430488, -0.04679911698775366,
                    0.8598304469755665, 0.08489401703838259
                });
        }

        private static void ReplayCorrectedIncidentSequence(
            string channelKey,
            double intercept,
            double[] observedRepresentatives)
        {
            const double scale = 10.0;
            const double overCurrentLimitA = 18.0;
            var filter = new ClsDataFilter.FastFilter(9, 0.4, 0);
            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            var start = Stopwatch.Frequency;
            machine.ArmForward(start, 1000, 10000, 15, 0, 3);
            var maxRepresentative = 0.0;
            var maxFiltered = 0.0;
            var overCurrentFault = false;

            for (var index = 0; index < observedRepresentatives.Length; index++)
            {
                var observed = observedRepresentatives[index];
                // 现场绝对值超过 18A 的代表值具有二次换算指纹；逆推一次得到真实工程值，
                // 再还原成回调应独占的原始电压，走修复后的单次换算路径。
                var expectedOnce = Math.Abs(observed) >= overCurrentLimitA
                    ? (observed - intercept) / scale
                    : observed;
                var rawVoltage = (expectedOnce - intercept) / scale;
                var representative = rawVoltage * scale + intercept;
                var tick = start + (index + 1L) * Stopwatch.Frequency / 100;
                var filtered = filter.Update(channelKey, representative, tick);
                var decision = machine.OnSample(tick, filtered, Math.Abs(representative));
                maxRepresentative = Math.Max(maxRepresentative, Math.Abs(representative));
                maxFiltered = Math.Max(maxFiltered, Math.Abs(filtered));
                if (decision.Reason?.IndexOf(
                        "OverCurrent3Samples",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    overCurrentFault = true;

                Assert(Math.Abs(representative - expectedOnce) < 1e-10,
                    channelKey + " 现场批次未严格执行一次换算");
            }

            Assert(maxRepresentative < overCurrentLimitA && maxFiltered < overCurrentLimitA,
                channelKey + " 修复后仍产生39A、55A或75A二次换算峰值");
            Assert(!overCurrentFault, channelKey + " 修复后的现场序列仍触发OverCurrent3Samples");
        }

        private static void FastFiltersAreIsolatedAndResettable()
        {
            var dev1 = new ClsDataFilter.FastFilter(3, 0.5, 0);
            var dev2 = new ClsDataFilter.FastFilter(3, 0.5, 0);
            var tick = Stopwatch.Frequency;
            dev1.Update("EPB4_current", 50, tick);
            var dev2Value = dev2.Update("EPB8_current", 2, tick);
            Assert(Math.Abs(dev2Value - 2) < 1e-12, "Dev1快速值污染Dev2滤波状态");
            dev1.Update("EPB4_current", 60, tick + 1);
            dev1.Reset();
            var resetValue = dev1.Update("EPB4_current", 3, tick + 2);
            Assert(Math.Abs(resetValue - 3) < 1e-12, "代次复位后仍继承旧快速滤波状态");
        }

        private static void ControlBatchIdentityRejectsInvalidOrder()
        {
            var validator = new ControlBatchIdentityValidator();
            FastControlBatchMetadata Metadata(long generation, long sequence, long tick) =>
                new FastControlBatchMetadata(
                    generation, sequence, DateTime.UtcNow, DateTime.UtcNow,
                    tick, tick + 1, 0, 7, FastSignalQualityFlags.None);

            Assert(validator.Validate(Metadata(1, 10, 100)) == ControlBatchIdentityResult.GenerationChanged,
                "首批未建立代次身份");
            Assert(validator.Validate(Metadata(1, 11, 110)) == ControlBatchIdentityResult.Accepted,
                "连续批次被拒绝");
            Assert(validator.Validate(Metadata(1, 11, 120)) == ControlBatchIdentityResult.Duplicate,
                "重复批次未拒绝");
            Assert(validator.Validate(Metadata(1, 9, 130)) == ControlBatchIdentityResult.OutOfOrder,
                "倒序批次未拒绝");
            Assert(validator.Validate(Metadata(1, 12, 110)) == ControlBatchIdentityResult.MonotonicTickInvalid,
                "非递增回调tick未拒绝");
            Assert(validator.Validate(Metadata(1, 13, 130)) == ControlBatchIdentityResult.Gap,
                "序号缺口未识别");
            validator.Reset();
            Assert(validator.Validate(Metadata(2, 1, 10)) == ControlBatchIdentityResult.GenerationChanged,
                "新代次未复位批次身份");
        }

        private static void FastFilterHotLoopDoesNotAllocate()
        {
            var filter = new ClsDataFilter.FastFilter(9, 0.4, 0);
            var tick = Stopwatch.Frequency;
            for (var i = 0; i < 100; i++)
                filter.Update("EPB4_current", i % 20, tick + i);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100000; i++)
                filter.Update("EPB4_current", i % 20, tick + 100 + i);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 1024,
                $"快速滤波稳态热路径产生持续托管分配：{allocated} bytes");
        }

        private static void StartupClassifierIgnoresDuplicateBatch()
        {
            var classifier = new StartupPositioningCurrentClassifier(100, 14.2, 18, 3);
            Assert(classifier.Evaluate(10, 120, 20) == StartupCurrentClassification.None,
                "首个过流样本过早触发");
            Assert(classifier.Evaluate(10, 130, 20) == StartupCurrentClassification.None &&
                   classifier.Evaluate(10, 140, 20) == StartupCurrentClassification.None,
                "相同批次被重复累计");
            Assert(classifier.Evaluate(11, 150, 20) == StartupCurrentClassification.None,
                "第二个独立批次过早触发");
            Assert(classifier.Evaluate(12, 160, 20) == StartupCurrentClassification.OverCurrent,
                "三个独立批次未触发过流");
        }

        private static void FutureWallClockDoesNotChangeControlElapsed()
        {
            var start = Stopwatch.Frequency;
            var sample = new FastEpbCurrentSample(
                4, 5, 5, DateTime.UtcNow.AddMilliseconds(500),
                start + Stopwatch.Frequency / 20, 1, 1, FastSignalQualityFlags.None);
            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            machine.ArmForward(start, 100, 1000, 15, 0, 3);
            var decision = machine.OnSample(sample.CaptureMonotonicTicks, sample.CurrentA);
            Assert(decision.ElapsedMs >= 49 && decision.ElapsedMs <= 51 &&
                   decision.Stage == EpbCurrentStage.Inrush,
                "未来墙钟跨过了100ms浪涌窗口");
        }

        private static void FastTripClassificationUsesFullRateEvidence()
        {
            var invalid62 = FastPathTripClassifier.ClassifyOverCurrent(
                62.333, 18, 14.780, 10, 1, 100, FastSignalQualityFlags.None);
            var invalid96 = FastPathTripClassifier.ClassifyOverCurrent(
                95.873, 18, 15.53, 10, 1, 100, FastSignalQualityFlags.AdcNearRail);
            var confirmed = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, 18.5, 10, 1, 100, FastSignalQualityFlags.None);
            var unavailable = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, double.NaN, double.PositiveInfinity, 1, 100, FastSignalQualityFlags.None);
            var stale = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, 20, 101, 1, 100, FastSignalQualityFlags.None);
            var invalidTick = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, 20, 10, 1, 100, FastSignalQualityFlags.MonotonicTickInvalid);
            Assert(invalid62.Classification == FastPathTripClassification.FastPathSignalInvalid &&
                   invalid96.Classification == FastPathTripClassification.FastPathSignalInvalid,
                "09:08事故特征仍被归类为真实过流");
            Assert(confirmed.Classification == FastPathTripClassification.ConfirmedOverCurrent,
                "真实全速率过流未确认");
            Assert(unavailable.Classification == FastPathTripClassification.FastPathEvidenceUnavailable &&
                   stale.Classification == FastPathTripClassification.FastPathEvidenceUnavailable,
                "证据缺失或过期未按失效安全归类");
            Assert(invalidTick.Classification == FastPathTripClassification.FastPathSignalInvalid,
                "单调时钟异常未归类为快速信号无效");

            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            machine.ArmForward(Stopwatch.Frequency, 100, 1000, 15, 0, 3);
            var qualityFault = machine.OnInvalidFastSignalReusable(
                Stopwatch.Frequency + 1,
                double.NaN,
                FastSignalQualityFlags.NonFinite,
                new EpbAdaptiveDecision());
            Assert(qualityFault.HardFault &&
                   qualityFault.Reason.Contains("FastPathSignalInvalid"),
                "带电阶段无效快速样本没有直接触发安全故障");
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
