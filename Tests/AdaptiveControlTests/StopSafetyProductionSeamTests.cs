using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using DataOperation;
using IO.NI;
using MTEmbTest;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;
using PowerSupply.Core;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Exercises the production StopSafetyTransactionRunner.  Only the
    /// execution port and clock are fakes; lease, ordering, progress and
    /// terminal publication are the controller implementation under test.
    /// </summary>
    internal static class StopSafetyProductionSeamTests
    {
        // The production acceptance command is intentionally narrow: every case
        // below must enter through the real EpbManager.StopAllAsync path.  The
        // execution-port/runner/supervisor checks live in RunUnitTests so that a
        // production acceptance count cannot be inflated by fakes or direct
        // component calls.
        internal static int RunProductionAcceptance()
        {
            var passed = 0;
            Run("EpbManager.StopAllAsync真实十阶段与虚拟12秒液压",
                EpbManagerStopAllRunsProductionStages, ref passed);
            Run("StopAll入口同步安装禁止再上电栅栏",
                StopAllAdmissionInstallsEnergizationFenceSynchronously, ref passed);
            Run("EpbManager生产DO物理失败与准入拒绝进入统一终态",
                EpbManagerPhysicalOffFailureIsSticky, ref passed);
            Run("EpbManager生产DO准入拒绝先启动PSU再安全收口",
                EpbManagerAdmissionRejectStartsPsuBeforeFallback, ref passed);
            Run("P0-5场景7：真实StopAll材料证据幂等与持久化截止",
                EpbManagerPowerAndPersistenceFailuresUseOuterRunner, ref passed);
            Run("P0-5场景8：生产aggregate到heartbeat身份与一次派发",
                ProductionAggregateHeartbeatMonitorDispatchesOnce, ref passed);
            Run("EpbManager硬件释放并发重入只执行一次并同步完成",
                EpbManagerHardwareReleaseIsSynchronousAndOnce, ref passed);
            Run("监控窗体硬件释放所有者统一manager与fallback路径",
                MonitorHardwareReleaseOwnerIsOnce, ref passed);
            Run("人工停止关闭凭证绑定Run并在新启动时撤权",
                ManualStopExitReceiptIsRunBound, ref passed);
            Run("AO释放后拒绝写入且不伪报冷启动归零失败",
                AoDisposedOperationsDoNotReportColdStartFailure, ref passed);
            return passed;
        }

        internal static int RunUnitTests()
        {
            var passed = 0;
            Run("EpbManager生产port发布物理边沿",
                EpbManagerProductionPortPublishesEdges, ref passed);
            Run("Stop runner stages are strictly ordered and stage deadlines are independent",
                StrictStageOrderAndDeadlines, ref passed);
            Run("Stop runner DO/power failure enters one safe-idle terminal",
                StageFailureIsSafeIdle, ref passed);
            Run("Stop runner detail and duplicate material do not renew stage",
                DetailDoesNotRenewProgress, ref passed);
            Run("Stop runner caller cancellation does not cancel safety core",
                CallerCancellationDoesNotCancelSafety, ref passed);
            Run("Stop runner hard timeout starts a fresh request transaction while isolating the orphan",
                HardTimeoutStartsFreshTransaction, ref passed);
            Run("Stop stage deadline starts a fresh request transaction while isolating the orphan",
                StageDeadlineStartsFreshTransaction, ref passed);
            Run("Stop runner aggregate projection dispatches inactive timeout once",
                AggregateProjectionDispatchesOnce, ref passed);
            Run("硬件安全等待仅抑制非活动粘滞Stop终态且保留接管边界",
                HardwareSafeIdleSuppressesOnlyInactiveStickyStop, ref passed);
            Run("Stop supervisor orders heartbeats by validated process attachment",
                ValidatedAttachmentOrdersHeartbeat, ref passed);
            return passed;
        }

        // Keep the old aggregate entry point for callers outside Program while
        // preserving the explicit production/unit split used by the commands.
        internal static int RunAll()
        {
            return RunUnitTests() + RunProductionAcceptance();
        }

        private static void EpbManagerHardwareReleaseIsSynchronousAndOnce()
        {
            using (var fixture = new ProductionManagerFixture())
            {
                var callers = Enumerable.Range(0, 16)
                    .Select(_ => Task.Run((Action)fixture.Manager.ReleaseHardwareForRestart))
                    .ToArray();
                Assert(Task.WaitAll(callers, 15000),
                    "并发硬件释放调用没有全部等待唯一owner完成");
                Assert(fixture.Manager.HardwareReleaseExecutionCount == 1,
                    "EpbManager并发硬件释放执行次数不是1：" +
                    fixture.Manager.HardwareReleaseExecutionCount);
                Assert(fixture.Ao.IsDisposed && fixture.Ao.ResetAllExecutionCount == 1,
                    "EpbManager没有在Dispose前唯一执行一次AO归零");

                fixture.Manager.ReleaseHardwareForRestart();
                Assert(fixture.Manager.HardwareReleaseExecutionCount == 1 &&
                       fixture.Ao.ResetAllExecutionCount == 1,
                    "已完成后的硬件释放重入重复执行AO/资源释放");
            }
        }

        private static void MonitorHardwareReleaseOwnerIsOnce()
        {
            var owner = new EpbMonitorHardwareReleaseOwner();
            var managerCalls = 0;
            var fallbackCalls = 0;
            var callers = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => owner.Release(
                    () =>
                    {
                        Interlocked.Increment(ref managerCalls);
                        Thread.Sleep(25);
                    },
                    () => Interlocked.Increment(ref fallbackCalls),
                    null)))
                .ToArray();
            Assert(Task.WaitAll(callers, 5000) && callers.All(task => task.Result),
                "FormClosing/FormClosed并发释放没有共享同一完成结果");
            Assert(managerCalls == 1 && fallbackCalls == 0 && owner.ReleaseCount == 1,
                "正常manager释放路径没有CAS-once：manager=" + managerCalls +
                ";fallback=" + fallbackCalls + ";owner=" + owner.ReleaseCount);

            var failedOwner = new EpbMonitorHardwareReleaseOwner();
            var failures = 0;
            fallbackCalls = 0;
            Assert(failedOwner.Release(
                       () => throw new InvalidOperationException("manager-release-test"),
                       () => Interlocked.Increment(ref fallbackCalls),
                       _ =>
                       {
                           Interlocked.Increment(ref failures);
                           throw new InvalidOperationException("diagnostic-sink-test");
                       }) &&
                   failedOwner.Release(
                       () => Interlocked.Increment(ref managerCalls),
                       () => Interlocked.Increment(ref fallbackCalls),
                       null),
                "manager失败后的fallback没有形成可复用终态");
            Assert(failures == 1 && fallbackCalls == 1 && failedOwner.ReleaseCount == 1,
                "manager失败路径没有只执行一次fallback/失败通知");

            var partialInitializationOwner = new EpbMonitorHardwareReleaseOwner();
            fallbackCalls = 0;
            Assert(partialInitializationOwner.Release(
                       null,
                       () => Interlocked.Increment(ref fallbackCalls),
                       null) &&
                   fallbackCalls == 1 && partialInitializationOwner.ReleaseCount == 1,
                "EpbManager未创建时没有执行一次直接硬件兜底");
        }

        private static void ManualStopExitReceiptIsRunBound()
        {
            var owner = new ManualStopExitReceiptOwner();
            var runId = Guid.NewGuid();
            var result = new StopSafetyResult
            {
                Outcome = StopSafetyOutcome.CompletedSafe,
                LastStage = StopSafetyStage.Completed,
                Source = StopSource.ManualUi,
                CorrelationId = Guid.NewGuid().ToString("N"),
                SafetyTransactionId = Guid.NewGuid(),
                RunId = runId,
                RunEpoch = 7,
                SafetyBoundaryGeneration = 11,
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = false,
                PersistenceBoundaryConfirmed = true,
                LogicalQuiescenceConfirmed = true,
                CompletedUtc = DateTime.UtcNow
            };

            Assert(owner.Publish(result), "满足现有关闭准入的人工停止结果未被保留");
            Assert(owner.TryCapture(batchSessionActive: true) == null,
                "仍有活动批次时错误复用人工停止结果");
            var captured = owner.TryCapture(batchSessionActive: false);
            Assert(captured != null && captured.ReusedPreviousResult &&
                   captured.RunId == runId && captured.CorrelationId == result.CorrelationId &&
                   !captured.PressureSafeConfirmed,
                "人工停止结果没有按Run/Correlation复用或改变了既有压力退出策略");

            owner.RevokeForNewStart();
            Assert(owner.TryCapture(batchSessionActive: false) == null,
                "新启动后仍能复用上一轮人工停止授权");
            result.Source = StopSource.SystemFault;
            Assert(owner.Publish(result, "operator-adopt"),
                "人工退出未能采用同Run/epoch/代次的SystemFault停止终态");
            var intent = owner.CaptureIntent();
            Assert(intent != null && intent.Matches(result) &&
                   intent.OriginalSource == StopSource.SystemFault,
                "SystemFault原始来源或精确停止身份未保留");
            result.SafetyBoundaryGeneration = 0;
            Assert(!owner.Publish(result), "缺少安全代次的旧停止结果错误获得退出授权");
            result.SafetyBoundaryGeneration = 11;
            result.RunId = Guid.Empty;
            Assert(!owner.Publish(result), "缺少RunId的停止结果错误获得退出授权");
        }

        private static void AoDisposedOperationsDoNotReportColdStartFailure()
        {
            var logger = new RecordingAoLogger();
            var ao = new AoController(new AoConfig(), logger);
            Assert(!ao.TryResetAll() && logger.Errors.Any(message =>
                       message.IndexOf("冷启动安全基线写零失败", StringComparison.Ordinal) >= 0),
                "Dispose前真实AO基线不完整没有保留ERROR");

            logger.Clear();
            ao.Dispose();
            ao.Dispose();
            Assert(!ao.TryResetAll(), "Dispose后的AO归零错误返回成功");
            Assert(!ao.WritePressureDetailed("missing", 0).Success,
                "Dispose后的AO写入错误返回成功");
            Assert(ao.IsDisposed && ao.ResetAllExecutionCount == 1,
                "Dispose后仍执行了第二次真实AO归零");
            Assert(logger.Errors.All(message =>
                       message.IndexOf("冷启动安全基线写零失败", StringComparison.Ordinal) < 0),
                "Dispose后的拒绝仍伪报冷启动AO归零失败");
            Assert(logger.Warnings.Count(message =>
                       message.IndexOf("AO 控制器已释放", StringComparison.Ordinal) >= 0) == 1,
                "Dispose后生命周期拒绝没有形成唯一准确WARN");
        }

        private static void AssertStrictProductionStageSequence(
            IReadOnlyList<StopSafetyProgressSnapshot> observed,
            object observedGate)
        {
            List<StopSafetyProgressSnapshot> snapshots;
            lock (observedGate)
                snapshots = observed.Select(item => item.Clone()).ToList();
            var meaningful = snapshots
                .Where(item => item.Stage != StopSafetyStage.None &&
                               item.Stage != StopSafetyStage.TimedOut)
                .ToList();
            Assert(meaningful.Count > 0,
                "真实StopAll未发布任何有效阶段快照。");
            var transactionId = meaningful[0].TransactionId;
            var generation = meaningful[0].Generation;
            Assert(transactionId != Guid.Empty && generation > 0,
                "Stop阶段快照缺少事务身份。");
            Assert(meaningful.All(item => item.TransactionId == transactionId &&
                                          item.Generation == generation),
                "Stop阶段快照出现TransactionId/Generation串代。");

            var transitions = new List<StopSafetyStage>();
            foreach (var item in meaningful)
            {
                if (transitions.Count == 0 ||
                    transitions[transitions.Count - 1] != item.Stage)
                    transitions.Add(item.Stage);
            }
            var expected = new[]
            {
                StopSafetyStage.AdmitAndSubmitSafety,
                StopSafetyStage.FreezeActiveWork,
                StopSafetyStage.RevokeExecutionAuthorization,
                StopSafetyStage.SubmitPhysicalOff,
                StopSafetyStage.StartPowerDisable,
                StopSafetyStage.ClearTimerAndRunner,
                StopSafetyStage.ClearRecoveryOwners,
                StopSafetyStage.ReleaseHydraulics,
                StopSafetyStage.StopAcquisition,
                StopSafetyStage.ClosePersistenceBoundary,
                StopSafetyStage.VerifyLogicalQuiescence,
                StopSafetyStage.Completed
            };
            Assert(transitions.SequenceEqual(expected),
                "Stop阶段首次顺序/每阶段一次不符合生产十阶段：" +
                string.Join("->", transitions));
        }

        private static void EpbManagerStopAllRunsProductionStages()
        {
            using (var fixture = new ProductionManagerFixture(
                       new FailingPowerSupply(fail: false)))
            {
                var clock = new ManualClock();
                var writer = new ProductionDoBatchWriter();
                var hydraulic = new ProductionHydraulicAdapter(clock);
                fixture.ConfigurePhysicalOff(writer);
                var observed = new List<StopSafetyProgressSnapshot>();
                var observedGate = new object();
                fixture.Manager.StopSafetyProgressPublished += snapshot =>
                {
                    lock (observedGate) observed.Add(snapshot.Clone());
                };
                fixture.Manager.ConfigureStopSafetyProductionSeams(
                    clock,
                    hydraulic);
                var stopTask = fixture.Manager.StopAllAsync(NewContext());
                Assert(hydraulic.Started.Wait(TimeSpan.FromSeconds(2)),
                    "真实StopAll未进入液压物理适配器。");
                clock.Advance(TimeSpan.FromSeconds(12));
                var hydraulicAggregate =
                    fixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                var hydraulicProjection =
                    StopSafetyWatchdogHeartbeatMapper.Map(hydraulicAggregate);
                var hydraulicMonitor = new WatchdogHostStopMonitor("stop-12s");
                hydraulicMonitor.Observe(hydraulicProjection);
                var hydraulicSinkCount = 0;
                var stageStarted = hydraulicProjection.StageStartedUtc;
                var stageDeadline = hydraulicProjection.StageHardDeadlineUtc;
                // Evaluate the real production monitor at 12/13/14 seconds
                // using the same ManualClock that drives the manager.  A
                // A one-second boundary tick proves the 15s stage deadline is
                // a positive takeover decision without advancing the
                // hydraulic elapsed-time assertion.
                for (var tick = 0; tick < 3; tick++)
                {
                    hydraulicMonitor.EvaluateTick(
                        clock.UtcNow,
                        processAlive: true,
                        heartbeatAgeSeconds: 0,
                        sessionRevoked: false,
                        manualStopRequested: false,
                        alreadyTakingOver: false,
                        sink: _ => Interlocked.Increment(ref hydraulicSinkCount));
                    if (tick < 2)
                        clock.Advance(TimeSpan.FromSeconds(1));
                }
                clock.AdvanceWithoutReleasingDueDelays(TimeSpan.FromSeconds(1));
                var positiveDeadlineDecision = hydraulicMonitor.EvaluateTick(
                    clock.UtcNow,
                    processAlive: true,
                    heartbeatAgeSeconds: 0,
                    sessionRevoked: false,
                    manualStopRequested: false,
                    alreadyTakingOver: false,
                    sink: _ => Interlocked.Increment(ref hydraulicSinkCount));
                var beforeHydraulicComplete =
                    fixture.Manager.CaptureStopSafetyProgress();
                Assert(hydraulicProjection.Active &&
                       !hydraulicProjection.TakeoverRequired &&
                       !hydraulicProjection.TimedOut &&
                       positiveDeadlineDecision && hydraulicSinkCount == 1 &&
                       beforeHydraulicComplete.StageStartedUtc.Ticks == stageStarted &&
                       beforeHydraulicComplete.StageHardDeadlineUtc.Ticks == stageDeadline,
                    "12/13/14秒液压阶段或15秒截止边界的生产EvaluateTick判定错误：" +
                    $"Sink={hydraulicSinkCount};Positive={positiveDeadlineDecision}");
                hydraulic.Complete(true, string.Empty);
                var result = stopTask.GetAwaiter().GetResult();
                // The same clock was advanced to the 15s boundary without
                // releasing its due runner deadline.  Release that delay only
                // after the hydraulic completion has won the core race.
                clock.ReleaseDueDelays();
                Assert(result.Outcome == StopSafetyOutcome.CompletedSafe &&
                       writer.BatchWriteCount >= 2 &&
                       fixture.Manager.GetPhysicalEnergizationEdgeCount(4) >= 1 &&
                       fixture.Manager.GetPhysicalEnergizationEdgeCount(10) >= 1 &&
                       fixture.Manager.StopSafetySafeIdleIssueCount == 0 &&
                       !fixture.Manager.StopSafetyHasOrphanCore &&
                       fixture.PowerDisableCallCount == 1 &&
                       fixture.PersistenceFlushCallCount == 1 &&
                       hydraulic.ConfirmCallCount == 1 &&
                       hydraulic.VirtualElapsedSeconds == 15,
                    "真实StopAll十阶段/12秒液压链未完成：" +
                    $"Outcome={result.Outcome};DO={writer.BatchWriteCount};" +
                    $"Edges={fixture.Manager.GetPhysicalEnergizationEdgeCount(4)}/" +
                    $"{fixture.Manager.GetPhysicalEnergizationEdgeCount(10)};" +
                    $"Hyd={hydraulic.VirtualElapsedSeconds};" +
                    $"LastStage={result.LastStage};Error={result.StageError}");
                AssertStrictProductionStageSequence(observed, observedGate);
                var receipts = fixture.Receipts;
                Assert(receipts.Count >= 2 &&
                       receipts.All(item => item != null &&
                                            item.CommandId != Guid.Empty) &&
                       receipts.Select(item => item.CommandId).Distinct().Count() ==
                       receipts.Count &&
                       receipts.Any(item => item.Channel == 4),
                    "真实HP worker receipt未形成非空CommandId且与EPB4 scope匹配。");
                var progress = fixture.Manager.CaptureStopSafetyProgress();
                Assert(progress.Stage == StopSafetyStage.Completed &&
                       !progress.Active && progress.PhysicalSafe,
                    "真实StopAll未发布Completed物理安全终态。");
            }
        }

        private static void StopAllAdmissionInstallsEnergizationFenceSynchronously()
        {
            using (var fixture = new ProductionManagerFixture(
                       new FailingPowerSupply(fail: false)))
            {
                fixture.ConfigurePhysicalOff(new ProductionDoBatchWriter());
                var stopTask = fixture.Manager.StopAllAsync(NewContext());
                Assert(fixture.Manager.IsEnergizationRevoked,
                    "StopAll返回共享Task前尚未安装进程级禁止再上电栅栏。");
                var result = stopTask.GetAwaiter().GetResult();
                Assert(result != null && fixture.Manager.IsEnergizationRevoked,
                    "StopAll终态错误清除了禁止再上电栅栏。");
            }
        }

        private static void EpbManagerPhysicalOffFailureIsSticky()
        {
            using (var fixture = new ProductionManagerFixture())
            {
                var writer = new ProductionDoBatchWriter(physicalFailure: true);
                fixture.ConfigurePhysicalOff(writer);
                fixture.Manager.ConfigureStopSafetyProductionSeams(
                    new SystemStopSafetyClock(),
                    new ProductionHydraulicAdapter(immediateSuccess: true));
                var result = fixture.Manager.StopAllAsync(NewContext())
                    .GetAwaiter().GetResult();
                Assert(result.RequiresProcessRestart &&
                       result.LastStage == StopSafetyStage.ReleaseHydraulics &&
                       result.StageError.IndexOf("StageFailure:ReleaseHydraulics",
                           StringComparison.Ordinal) >= 0 &&
                       writer.BatchWriteCount >= 2 &&
                       fixture.Manager.GetPhysicalEnergizationEdgeCount(4) >= 1,
                    "DO物理失败未沿真实receipt链进入ReleaseHydraulics sticky终态。");

                // The cached result is not the hardware barrier.  Wait until
                // the initial OFF receipts and the authoritative SafeIdle
                // receipt pair have both crossed the real HighPriorityDoWorker
                // completion callback before taking the IO baseline used by
                // re-entry assertions.
                Assert(fixture.WaitForReceiptCountAtLeast(
                           4, TimeSpan.FromSeconds(2)),
                    "SafeIdle权威硬件receipt/barrier尚未完成，不能建立重入IO基线。");
                Assert(fixture.Manager.StopSafetySafeIdleIssueCount == 1 &&
                       !fixture.Manager.StopSafetyHasOrphanCore,
                    "DO physical receipt=false未形成单次SafeIdle且无孤儿core。");
                var terminalAggregate =
                    fixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                Assert(terminalAggregate?.StopProgress != null &&
                       !terminalAggregate.StopProgress.Active &&
                       terminalAggregate.StopProgress.TakeoverRequired,
                    "物理失败未先发布sticky terminal safety snapshot。");
                var failureMonitor = new WatchdogHostStopMonitor("physical-failure");
                failureMonitor.Observe(
                    StopSafetyWatchdogHeartbeatMapper.Map(terminalAggregate));
                var failureSinkCount = 0;
                for (var i = 0; i < 8; i++)
                    failureMonitor.EvaluateTick(
                        DateTime.UtcNow,
                        processAlive: true,
                        heartbeatAgeSeconds: 0,
                        sessionRevoked: false,
                        manualStopRequested: false,
                        alreadyTakingOver: false,
                        sink: _ => Interlocked.Increment(ref failureSinkCount));
                Assert(failureSinkCount == 1,
                    "物理失败terminal经共享mapper/monitor未只派发一次。");

                var writesAtTerminal = writer.BatchWriteCount;
                var reentry = fixture.Manager.StopAllAsync(NewContext())
                    .GetAwaiter().GetResult();
                Assert(!reentry.ReusedPreviousResult &&
                       reentry.SafetyTransactionId != result.SafetyTransactionId &&
                       writer.BatchWriteCount > writesAtTerminal,
                    "DO物理失败后的新请求没有建立新代次重做幂等OFF。");
                var writesAfterReentry = writer.BatchWriteCount;
                var finalExit = fixture.Manager.StopAllAsync(new StopContext
                {
                    Source = StopSource.ProgramExit,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Reason = "physical-failure-final-exit"
                }).GetAwaiter().GetResult();
                Assert(!finalExit.ReusedPreviousResult &&
                       finalExit.SafetyTransactionId != reentry.SafetyTransactionId &&
                       writer.BatchWriteCount > writesAfterReentry,
                    "物理失败后的FinalExit没有取得本次安全事务结果。");
            }
        }

        private static void EpbManagerAdmissionRejectStartsPsuBeforeFallback()
        {
            var order = new ProductionActionOrder();
            var powerSupply = new FailingPowerSupply(order, false);
            using (var fixture = new ProductionManagerFixture(powerSupply))
            {
                var writer = new ProductionDoBatchWriter(order, blockFirstWrite: true);
                fixture.ConfigurePhysicalOff(writer);
                // Saturate the real HighPriorityDoWorker, not a manager seam.
                // EPB10 is on Dev2; saturate that device's real worker so the
                // stop submission for the configured scope receives an actual
                // admission rejection while EPB4 remains a separate scope.
                var prefill = fixture.TrySubmitPhysicalOff(10, null, out _);
                Assert(prefill && writer.BlockStarted.Wait(TimeSpan.FromSeconds(2)),
                    "无法进入真实HighPriorityDoWorker物理写阻塞边界。");
                for (var i = 0; i < 80; i++)
                    fixture.TrySubmitPhysicalOff(10, null, out _);
                order.Reset();
                var fallbackOrder = 0;
                var fallbackIssued = new ManualResetEventSlim(false);
                var fallbackChannels = new List<int>();
                var fallbackChannelsGate = new object();
                fixture.Manager.StopSafetyFallbackIssued += channel =>
                {
                    lock (fallbackChannelsGate) fallbackChannels.Add(channel);
                    fallbackOrder = order.Next("Fallback");
                    fallbackIssued.Set();
                };
                fixture.Manager.ConfigureStopSafetyProductionSeams(
                    new SystemStopSafetyClock(),
                    new ProductionHydraulicAdapter(immediateSuccess: true));
                var stopTask = fixture.Manager.StopAllAsync(NewContext());
                // The runner has entered the PSU stage only after the real DO
                // admission attempts have been made.  Wait for the actual
                // production power-disable and fallback events before
                // releasing the saturated worker; a wall-clock sleep would
                // make this acceptance race-dependent.
                Assert(powerSupply.DisableStarted.Wait(TimeSpan.FromSeconds(2)),
                    "真实PSU DisableAllForSafetyAsync未启动，不能释放饱和worker。");
                Assert(fallbackIssued.Wait(TimeSpan.FromSeconds(2)),
                    "真实HP admission reject未触发fallback事件，不能释放饱和worker。");
                writer.Release.Set();
                var result = stopTask.GetAwaiter().GetResult();
                writer.Release.Set();
                bool fallbackForEpb10;
                lock (fallbackChannelsGate)
                    fallbackForEpb10 = fallbackChannels.Contains(10);
                Assert(order.FirstPowerDisableOrder > 0 &&
                       fallbackOrder > order.FirstPowerDisableOrder &&
                       fixture.SawAdmissionReject(10) &&
                       fallbackForEpb10,
                    "DO准入拒绝未沿真实HighPriorityDoWorker链在PSU之后执行fallback：" +
                    $"Stage={result.LastStage};Error={result.StageError};" +
                    $"Power={order.FirstPowerDisableOrder};Fallback={fallbackOrder};" +
                    $"EPB10Rejected={fixture.SawAdmissionReject(10)};" +
                    $"FallbackEPB10={fallbackForEpb10}");
                Assert((result.Outcome == StopSafetyOutcome.CompletedSafe &&
                        result.LastStage == StopSafetyStage.Completed &&
                        fixture.Manager.StopSafetySafeIdleIssueCount == 0) ||
                       (result.RequiresProcessRestart &&
                        result.LastStage == StopSafetyStage.ReleaseHydraulics &&
                        fixture.Manager.StopSafetySafeIdleIssueCount == 1),
                    "真实HP admission reject未沿单一runner收口为安全完成或安全接管终态：" +
                    $"Outcome={result.Outcome};Stage={result.LastStage};" +
                    $"SafeIdle={fixture.Manager.StopSafetySafeIdleIssueCount};" +
                    $"Orphan={fixture.Manager.StopSafetyHasOrphanCore}");
                Assert(!fixture.Manager.StopSafetyHasOrphanCore,
                    "真实HP admission reject留下孤儿Stop core。");
                Assert(fixture.Receipts.Any(item => item != null &&
                                                   item.Channel == 4 &&
                                                   item.CommandId != Guid.Empty),
                    "Admission拒绝场景未观察到EPB4真实accepted receipt。");
            }
        }

        private static void EpbManagerPowerAndPersistenceFailuresUseOuterRunner()
        {
            var powerOrder = new ProductionActionOrder();
            var failingPower = new FailingPowerSupply(
                powerOrder,
                fail: true,
                delayFirstSafetyFailure: true);
            using (var powerFixture = new ProductionManagerFixture(failingPower))
            {
                var writer = new ProductionDoBatchWriter();
                powerFixture.ConfigurePhysicalOff(writer);
                var powerProgress = new List<StopSafetyProgressSnapshot>();
                var powerProgressGate = new object();
                powerFixture.Manager.StopSafetyProgressPublished += snapshot =>
                {
                    lock (powerProgressGate)
                        powerProgress.Add(snapshot.Clone());
                };
                powerFixture.Manager.ConfigureStopSafetyProductionSeams(
                    new SystemStopSafetyClock(),
                    new ProductionHydraulicAdapter(immediateSuccess: true));
                var powerTask = powerFixture.Manager.StopAllAsync(NewContext());
                Assert(failingPower.DisableStarted.Wait(TimeSpan.FromSeconds(2)),
                    "PSU失败fixture未进入真实DisableAllForSafetyAsync调用。");
                Assert(powerFixture.WaitForSuccessfulReceiptChannels(
                           new[] { 4, 10 }, TimeSpan.FromSeconds(2)),
                    "释放PSU失败TCS前未收到EPB4/EPB10首轮真实CommandId receipt。");
                var firstRoundReceipts = powerFixture.Receipts
                    .Where(item => item != null &&
                                   (item.Channel == 4 || item.Channel == 10) &&
                                   item.Result && item.CommandId != Guid.Empty)
                    .ToArray();
                Assert(firstRoundReceipts.Length == 2,
                    "PSU失败释放前首轮DO receipt数量不是EPB4/EPB10各一条：" +
                    firstRoundReceipts.Length);
                var firstRoundCommandIds = new HashSet<Guid>(
                    firstRoundReceipts.Select(item => item.CommandId));
                var firstRoundReceiptOrders = powerOrder.Snapshot()
                    .Where(item => string.Equals(item.Action, "DO-receipt",
                        StringComparison.Ordinal) &&
                                   firstRoundCommandIds.Contains(item.CommandId))
                    .Select(item => item.Sequence)
                    .ToArray();
                Assert(firstRoundReceiptOrders.Length == 2,
                    "首轮EPB4/EPB10 receipt未在统一order recorder中按CommandId冻结：" +
                    firstRoundReceiptOrders.Length);
                failingPower.ReleaseFirstSafetyFailure();
                var powerFailure = powerTask.GetAwaiter().GetResult();
                var failureReleaseOrder = failingPower.FailureReleaseOrder;
                Assert(powerFailure.RequiresProcessRestart &&
                       powerFailure.LastStage == StopSafetyStage.AdmitAndSubmitSafety &&
                       powerFailure.StageError.IndexOf("StageFailure:AdmitAndSubmitSafety",
                           StringComparison.Ordinal) >= 0 &&
                       firstRoundReceiptOrders.All(item => item < failureReleaseOrder) &&
                       !failingPower.LastConfirmedOff &&
                       powerFixture.Receipts.Count(item => item != null &&
                                                          item.Result &&
                                                          item.CommandId != Guid.Empty) >= 2,
                    "PSU硬件失败未在立即安全提交阶段由outer runner收口，或DO receipt未先成功：" +
                    $"Stage={powerFailure.LastStage};PSU={failingPower.DisableCallCount};" +
                    $"ConfirmedOff={failingPower.LastConfirmedOff};" +
                    $"Error={powerFailure.StageError};" +
                    $"Receipts={powerFixture.Receipts.Count};" +
                    $"ReleaseOrder={failureReleaseOrder};" +
                    $"ReceiptOrders={string.Join(",", firstRoundReceiptOrders)}");
                StopSafetyStage[] powerStages;
                lock (powerProgressGate)
                    powerStages = powerProgress.Select(item => item.Stage).ToArray();
                var powerStageIndex = Array.IndexOf(
                    powerStages,
                    StopSafetyStage.AdmitAndSubmitSafety);
                var forbiddenAfterPower = new HashSet<StopSafetyStage>
                {
                    StopSafetyStage.FreezeActiveWork,
                    StopSafetyStage.RevokeExecutionAuthorization,
                    StopSafetyStage.SubmitPhysicalOff,
                    StopSafetyStage.StartPowerDisable,
                    StopSafetyStage.ClearTimerAndRunner,
                    StopSafetyStage.ClearRecoveryOwners,
                    StopSafetyStage.ReleaseHydraulics,
                    StopSafetyStage.StopAcquisition,
                    StopSafetyStage.ClosePersistenceBoundary,
                    StopSafetyStage.VerifyLogicalQuiescence,
                    StopSafetyStage.Completed
                };
                Assert(powerStageIndex >= 0 &&
                       !powerStages.Skip(powerStageIndex + 1).Any(
                           stage => forbiddenAfterPower.Contains(stage)),
                    "PSU失败后不应再进入Power之后的生产阶段（允许Power自身重发）：" +
                    string.Join("->", powerStages));

                var powerAggregate =
                    powerFixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                var powerProjection = StopSafetyWatchdogHeartbeatMapper.Map(powerAggregate);
                Assert(powerProjection.TransactionId.Length > 0 &&
                       powerProjection.Generation > 0 &&
                       !powerProjection.Active &&
                       powerProjection.TakeoverRequired &&
                       powerProjection.TerminalReason.IndexOf(
                           "StageFailure:AdmitAndSubmitSafety",
                           StringComparison.Ordinal) >= 0,
                    "PSU失败未先发布带身份的terminal aggregate/projection。");
                var powerMonitor = new WatchdogHostStopMonitor("power-failure");
                var powerPid = 4101;
                var powerStart = 4101001L;
                var powerAttach = 1L;
                powerProjection.ProcessId = powerPid;
                powerProjection.ProcessStartUtcTicks = powerStart;
                powerProjection.AttachEpoch = powerAttach;
                powerMonitor.NotifyValidatedAttached(
                    "power-failure",
                    powerProjection.TransactionId,
                    powerProjection.Generation,
                    "power-attach",
                    powerPid,
                    powerStart,
                    powerAttach);
                powerMonitor.Observe(powerProjection);
                var powerSinkCount = 0;
                Assert(powerMonitor.EvaluateTick(
                           DateTime.UtcNow,
                           processAlive: true,
                           heartbeatAgeSeconds: 0,
                           sessionRevoked: false,
                           manualStopRequested: false,
                           alreadyTakingOver: false,
                           sink: _ => Interlocked.Increment(ref powerSinkCount)) &&
                       powerSinkCount == 1,
                    "PSU失败terminal未经共享mapper/生产EvaluateTick派发一次。");
                Assert(powerFixture.Manager.StopSafetySafeIdleIssueCount == 1,
                    "PSU ConfirmedOff=false未触发唯一SafeIdle。");

                Assert(powerFixture.WaitForReceiptCountAtLeast(
                           4, TimeSpan.FromSeconds(2)) &&
                       failingPower.WaitForDisableCallCountAtLeast(
                           2, TimeSpan.FromSeconds(2)),
                    "PSU失败SafeIdle额外DO receipt或第二次PSU safety call尚未完成，不能建立IO基线。");
                var powerWrites = writer.BatchWriteCount;
                var powerCalls = failingPower.DisableCallCount;
                var powerReentry = powerFixture.Manager.StopAllAsync(NewContext())
                    .GetAwaiter().GetResult();
                var powerFinalExit = powerFixture.Manager.StopAllAsync(new StopContext
                {
                    Source = StopSource.ProgramExit,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Reason = "psu-failure-final-exit"
                }).GetAwaiter().GetResult();
                Assert(!powerReentry.ReusedPreviousResult &&
                       !powerFinalExit.ReusedPreviousResult &&
                       powerReentry.SafetyTransactionId != powerFailure.SafetyTransactionId &&
                       powerFinalExit.SafetyTransactionId != powerReentry.SafetyTransactionId &&
                       writer.BatchWriteCount > powerWrites &&
                       failingPower.DisableCallCount > powerCalls,
                    "PSU失败后的Stop/FinalExit没有分别执行当前代幂等安全动作。");
            }

            var hangingPower = new FailingPowerSupply(fail: false);
            using (var hangingFixture = new ProductionManagerFixture(hangingPower))
            {
                var clock = new ManualClock();
                var flushStarted = new ManualResetEventSlim(false);
                var hangingFlushCallCount = 0;
                var releaseFlush = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                hangingFixture.Manager.RegisterPausePersistenceFlush(
                    (boundaries, token) =>
                    {
                        Interlocked.Increment(ref hangingFlushCallCount);
                        flushStarted.Set();
                        return releaseFlush.Task;
                    });
                var writer = new ProductionDoBatchWriter();
                hangingFixture.ConfigurePhysicalOff(writer);
                hangingFixture.Manager.ConfigureStopSafetyProductionSeams(
                    clock,
                    new ProductionHydraulicAdapter(immediateSuccess: true));
                var stopTask = hangingFixture.Manager.StopAllAsync(NewContext());
                Assert(flushStarted.Wait(TimeSpan.FromSeconds(2)),
                    "持久化挂起场景未进入真实ClosePersistenceBoundary回调。");
                Assert(Volatile.Read(ref hangingFlushCallCount) == 1,
                    "持久化挂起首次flush调用次数不是1：" +
                    Volatile.Read(ref hangingFlushCallCount));
                var beforeEvidence = hangingFixture.Manager.CaptureStopSafetyProgress();
                var stableBeforeEvidence =
                    hangingFixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                Assert(stableBeforeEvidence != null &&
                       stableBeforeEvidence.StopProgress != null &&
                       stableBeforeEvidence.StopProgress.TransactionId ==
                           beforeEvidence.TransactionId &&
                       stableBeforeEvidence.StopProgress.Generation ==
                           beforeEvidence.Generation &&
                       stableBeforeEvidence.StopProgress.ProgressVersion ==
                           beforeEvidence.ProgressVersion,
                    "accepted前runner与stable aggregate身份/版本不一致。");
                var stableBeforeAggregateVersion = stableBeforeEvidence.Version;
                var firstEvidence = hangingFixture.Manager.RecordStopSafetyMaterialFromRunner(
                    beforeEvidence.Generation,
                    "DAQ:Dev1",
                    42,
                    "production-persistence-evidence");
                var afterFirstEvidence = hangingFixture.Manager.CaptureStopSafetyProgress();
                var stableAfterFirstEvidence =
                    hangingFixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                Assert(stableAfterFirstEvidence != null &&
                       stableAfterFirstEvidence.StopProgress != null &&
                       stableAfterFirstEvidence.StopProgress.Generation ==
                           afterFirstEvidence.Generation &&
                       stableAfterFirstEvidence.Version >
                           stableBeforeAggregateVersion &&
                       stableAfterFirstEvidence.StopProgress.TransactionId ==
                           afterFirstEvidence.TransactionId &&
                       stableAfterFirstEvidence.StopProgress.RunId ==
                           afterFirstEvidence.RunId &&
                       stableAfterFirstEvidence.StopProgress.RunEpoch ==
                           afterFirstEvidence.RunEpoch &&
                       stableAfterFirstEvidence.StopProgress.ProgressVersion ==
                           afterFirstEvidence.ProgressVersion &&
                       stableAfterFirstEvidence.StopProgress.MaterialEvidenceVersion ==
                           afterFirstEvidence.MaterialEvidenceVersion &&
                       stableAfterFirstEvidence.StopProgress.LastMaterialProgressUtc ==
                           afterFirstEvidence.LastMaterialProgressUtc &&
                       stableAfterFirstEvidence.StopProgress.StageStartedUtc ==
                           afterFirstEvidence.StageStartedUtc &&
                       stableAfterFirstEvidence.StopProgress.StageHardDeadlineUtc ==
                           afterFirstEvidence.StageHardDeadlineUtc &&
                       stableAfterFirstEvidence.StopProgress.Detail ==
                           afterFirstEvidence.Detail &&
                       stableAfterFirstEvidence.StopProgress.Detail ==
                           "production-persistence-evidence",
                    "首次真实材料证据后未取得同代稳定aggregate。");
                var stableAggregateVersion = stableAfterFirstEvidence.Version;
                var secondEvidence = hangingFixture.Manager.RecordStopSafetyMaterialFromRunner(
                    beforeEvidence.Generation,
                    "DAQ:Dev1",
                    42,
                    "duplicate-detail-must-not-renew");
                var afterSecondEvidence = hangingFixture.Manager.CaptureStopSafetyProgress();
                var stableAfterDuplicate =
                    hangingFixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                Assert(firstEvidence && !secondEvidence &&
                       afterFirstEvidence.ProgressVersion > beforeEvidence.ProgressVersion &&
                       afterSecondEvidence.ProgressVersion == afterFirstEvidence.ProgressVersion &&
                       afterFirstEvidence.StageStartedUtc == beforeEvidence.StageStartedUtc &&
                       afterFirstEvidence.StageHardDeadlineUtc ==
                           beforeEvidence.StageHardDeadlineUtc &&
                       afterSecondEvidence.LastMaterialProgressUtc ==
                           afterFirstEvidence.LastMaterialProgressUtc &&
                       afterSecondEvidence.StageStartedUtc == afterFirstEvidence.StageStartedUtc &&
                       afterSecondEvidence.StageHardDeadlineUtc ==
                           afterFirstEvidence.StageHardDeadlineUtc &&
                       afterSecondEvidence.MaterialEvidenceVersion ==
                           afterFirstEvidence.MaterialEvidenceVersion &&
                       afterSecondEvidence.Detail == afterFirstEvidence.Detail &&
                       afterSecondEvidence.Detail ==
                           "production-persistence-evidence" &&
                       stableAfterDuplicate != null &&
                       stableAfterDuplicate.Version == stableAggregateVersion &&
                       stableAfterDuplicate.StopProgress != null &&
                       stableAfterDuplicate.StopProgress.TransactionId ==
                           afterFirstEvidence.TransactionId &&
                       stableAfterDuplicate.StopProgress.RunId ==
                           afterFirstEvidence.RunId &&
                       stableAfterDuplicate.StopProgress.RunEpoch ==
                           afterFirstEvidence.RunEpoch &&
                       stableAfterDuplicate.StopProgress.ProgressVersion ==
                           afterFirstEvidence.ProgressVersion &&
                       stableAfterDuplicate.StopProgress.MaterialEvidenceVersion ==
                           afterFirstEvidence.MaterialEvidenceVersion &&
                       stableAfterDuplicate.StopProgress.LastMaterialProgressUtc ==
                           afterFirstEvidence.LastMaterialProgressUtc &&
                       stableAfterDuplicate.StopProgress.StageStartedUtc ==
                           afterFirstEvidence.StageStartedUtc &&
                       stableAfterDuplicate.StopProgress.StageHardDeadlineUtc ==
                           afterFirstEvidence.StageHardDeadlineUtc &&
                       stableAfterDuplicate.StopProgress.Detail ==
                           afterFirstEvidence.Detail,
                    "真实RecordStopSafetyMaterialFromRunner未保持同source/version幂等与原阶段deadline。");
                var originalStageDeadline = beforeEvidence.StageHardDeadlineUtc;
                var remainingToOriginalDeadline = originalStageDeadline - clock.UtcNow;
                Assert(remainingToOriginalDeadline > TimeSpan.Zero,
                    "持久化挂起场景原始ClosePersistenceBoundary截止已过，无法验证精确边界。");
                clock.Advance(remainingToOriginalDeadline);
                var result = stopTask.GetAwaiter().GetResult();
                const string persistenceTimeoutReason =
                    "StageDeadlineExceeded:ClosePersistenceBoundary";
                var timedOutProgress = hangingFixture.Manager.CaptureStopSafetyProgress();
                Assert(result.TimedOut && result.LastStage == StopSafetyStage.TimedOut &&
                       string.Equals(result.StageError, persistenceTimeoutReason,
                           StringComparison.Ordinal) &&
                       string.Equals(timedOutProgress.TerminalReason,
                           persistenceTimeoutReason, StringComparison.Ordinal),
                    "持久化挂起未由outer runner阶段截止收口：" +
                    $"TimedOut={result.TimedOut};Stage={result.LastStage};" +
                    $"Error={result.StageError};Terminal={timedOutProgress.TerminalReason}");
                Assert(hangingFixture.Manager.SafeIdleIssueCount == 1 &&
                       hangingFixture.Manager.HasOrphanCore,
                    "Persistence hang未登记唯一SafeIdle/orphan：" +
                    $"SafeIdle={hangingFixture.Manager.SafeIdleIssueCount};" +
                    $"Orphan={hangingFixture.Manager.HasOrphanCore}");

                var persistenceAggregate =
                    hangingFixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                var persistenceProjection =
                    StopSafetyWatchdogHeartbeatMapper.Map(persistenceAggregate);
                Assert(persistenceProjection.TransactionId.Length > 0 &&
                       persistenceProjection.Generation > 0 &&
                       !persistenceProjection.Active &&
                       persistenceProjection.TimedOut &&
                       persistenceProjection.TakeoverRequired &&
                       string.Equals(persistenceProjection.TerminalReason,
                           persistenceTimeoutReason, StringComparison.Ordinal),
                    "Persistence hang未形成inactive/timedout/takeover terminal projection。");
                var persistenceMonitor = new WatchdogHostStopMonitor("persistence-hang");
                var persistencePid = 4201;
                var persistenceStart = 4201001L;
                var persistenceAttach = 1L;
                persistenceProjection.ProcessId = persistencePid;
                persistenceProjection.ProcessStartUtcTicks = persistenceStart;
                persistenceProjection.AttachEpoch = persistenceAttach;
                persistenceMonitor.NotifyValidatedAttached(
                    "persistence-hang",
                    persistenceProjection.TransactionId,
                    persistenceProjection.Generation,
                    "persistence-attach",
                    persistencePid,
                    persistenceStart,
                    persistenceAttach);
                persistenceMonitor.Observe(persistenceProjection);
                var persistenceSinkCount = 0;
                Assert(persistenceMonitor.EvaluateTick(
                           DateTime.UtcNow,
                           processAlive: true,
                           heartbeatAgeSeconds: 0,
                           sessionRevoked: false,
                           manualStopRequested: false,
                           alreadyTakingOver: false,
                           sink: _ => Interlocked.Increment(ref persistenceSinkCount)) &&
                       persistenceSinkCount == 1,
                    "Persistence terminal未经共享mapper/生产EvaluateTick派发一次。");

                // Wait for the actual SafeIdle DO completion receipts before
                // taking the re-entry IO baseline.  This fixture has two
                // configured channels, so initial stop + SafeIdle is four
                // real HighPriorityDoWorker receipts.
                Assert(hangingFixture.WaitForReceiptCountAtLeast(
                           4, TimeSpan.FromSeconds(2)),
                    "Persistence SafeIdle硬件receipt尚未完成，不能建立重入基线。");
                Assert(SpinWait.SpinUntil(
                           () => hangingPower.DisableCallCount >= 2,
                           2000) &&
                       Volatile.Read(ref hangingFlushCallCount) == 1,
                    "Persistence SafeIdle第二次PSU或flush调用计数未收口：" +
                    $"PSU={hangingPower.DisableCallCount};" +
                    $"Flush={Volatile.Read(ref hangingFlushCallCount)}");
                var persistenceWrites = writer.BatchWriteCount;
                var persistenceFlushes = Volatile.Read(ref hangingFlushCallCount);
                var persistencePowerCalls = hangingPower.DisableCallCount;
                var persistenceTerminal =
                    hangingFixture.Manager.CaptureStopSafetyProgress();
                var persistenceReentryTask = hangingFixture.Manager.StopAllAsync(NewContext());
                Assert(SpinWait.SpinUntil(
                           () => hangingPower.DisableCallCount > persistencePowerCalls &&
                                 Volatile.Read(ref hangingFlushCallCount) > persistenceFlushes &&
                                 hangingFixture.Manager.CaptureStopSafetyProgress().TransactionId !=
                                     persistenceTerminal.TransactionId,
                           2000),
                    "旧orphan存在时新请求没有重新提交幂等安全动作。" +
                    $" DO={writer.BatchWriteCount}/{persistenceWrites};" +
                    $"PSU={hangingPower.DisableCallCount}/{persistencePowerCalls};" +
                    $"Flush={Volatile.Read(ref hangingFlushCallCount)}/{persistenceFlushes};" +
                    $"Progress={hangingFixture.Manager.CaptureStopSafetyProgress().TransactionId}/" +
                    $"{persistenceTerminal.TransactionId}:" +
                    hangingFixture.Manager.CaptureStopSafetyProgress().Stage);
                var persistenceFinalExitTask = hangingFixture.Manager.StopAllAsync(new StopContext
                {
                    Source = StopSource.ProgramExit,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Reason = "persistence-hang-final-exit"
                });
                releaseFlush.TrySetResult(true);
                var persistenceReentry = persistenceReentryTask.GetAwaiter().GetResult();
                var persistenceFinalExit = persistenceFinalExitTask.GetAwaiter().GetResult();
                Assert(!persistenceReentry.ReusedPreviousResult &&
                       !persistenceFinalExit.ReusedPreviousResult &&
                       persistenceReentry.SafetyTransactionId != persistenceTerminal.TransactionId &&
                       persistenceFinalExit.SafetyTransactionId == persistenceReentry.SafetyTransactionId,
                    "旧orphan后的新请求未建新代次，或活动请求没有加入同一物理事务。");
                var orphanSettled = SpinWait.SpinUntil(() =>
                {
                    var late = hangingFixture.Manager.CaptureStopSafetyProgress();
                    return !hangingFixture.Manager.HasOrphanCore &&
                           late.Stage == StopSafetyStage.Completed &&
                           !late.Active && !late.TakeoverRequired &&
                           late.TransactionId == persistenceReentry.SafetyTransactionId;
                }, 2000);
                var lateTerminal = hangingFixture.Manager.CaptureStopSafetyProgress();
                Assert(orphanSettled && !hangingFixture.Manager.HasOrphanCore &&
                       lateTerminal.Stage == StopSafetyStage.Completed &&
                       !lateTerminal.Active && !lateTerminal.TakeoverRequired &&
                       lateTerminal.TransactionId == persistenceReentry.SafetyTransactionId,
                    "旧Persistence orphan迟到回写覆盖了新代安全终态：" +
                    $"OrphanSettled={orphanSettled};Stage={lateTerminal.Stage};" +
                    $"Tx={lateTerminal.TransactionId};Generation={lateTerminal.Generation};" +
                    $"Progress={lateTerminal.ProgressVersion};" +
                    $"Reason={lateTerminal.TerminalReason};" +
                    $"Flush={Volatile.Read(ref hangingFlushCallCount)};" +
                    $"PSU={hangingPower.DisableCallCount}");
            }
        }

        private static void ProductionAggregateHeartbeatMonitorDispatchesOnce()
        {
            using (var fixture = new ProductionManagerFixture())
            {
                var writer = new ProductionDoBatchWriter(physicalFailure: true);
                fixture.ConfigurePhysicalOff(writer);
                fixture.Manager.ConfigureStopSafetyProductionSeams(
                    new SystemStopSafetyClock(),
                    new ProductionHydraulicAdapter(immediateSuccess: true));
                var result = fixture.Manager.StopAllAsync(NewContext())
                    .GetAwaiter().GetResult();
                var aggregate = fixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                var duplicateAggregate = fixture.Manager.CaptureWatchdogRecoveryAggregateSnapshot();
                var runnerProgress = fixture.Manager.CaptureStopSafetyProgress();
                Assert(aggregate != null && aggregate.StopProgress != null,
                    "生产StopAll未产生不可变aggregate stop source。");
                Assert(runnerProgress.TransactionId == aggregate.StopProgress.TransactionId &&
                       runnerProgress.Generation == aggregate.StopProgress.Generation &&
                       runnerProgress.ProgressVersion ==
                           aggregate.StopProgress.ProgressVersion &&
                       runnerProgress.Stage == aggregate.StopProgress.Stage &&
                       runnerProgress.Active == aggregate.StopProgress.Active,
                    "runner与stable aggregate的Stop身份/阶段/进度版本不一致。");
                Assert(duplicateAggregate != null &&
                       duplicateAggregate.Version == aggregate.Version,
                    "同一生产Stop终态重复Capture不应拼接或改变aggregate版本。");
                var projection = StopSafetyWatchdogHeartbeatMapper.Map(aggregate);
                var duplicateProjection = StopSafetyWatchdogHeartbeatMapper.Map(
                    duplicateAggregate);
                Assert(projection.TransactionId.Length > 0 &&
                       projection.Generation > 0 &&
                       duplicateProjection.TransactionId == projection.TransactionId &&
                       duplicateProjection.Generation == projection.Generation &&
                       projection.ProgressVersion ==
                           aggregate.StopProgress.ProgressVersion &&
                       projection.Stage == aggregate.StopProgress.Stage.ToString() &&
                       !projection.Active,
                    "manager稳定aggregate经共享mapper后丢失Stop事务身份。");
                var noStopMonitor = new WatchdogHostStopMonitor(
                    "production-no-stop-guard");
                var noStopSinkCount = 0;
                Assert(!noStopMonitor.EvaluateTick(
                           DateTime.UtcNow,
                           processAlive: false,
                           heartbeatAgeSeconds: 10,
                           sessionRevoked: false,
                           manualStopRequested: false,
                           alreadyTakingOver: false,
                           sink: _ => Interlocked.Increment(ref noStopSinkCount)) &&
                       noStopSinkCount == 0 &&
                       noStopMonitor.DispatchCount == 0,
                    "无Stop事务的生产EvaluateTick不应消费once gate或派发接管。");
                var monitor = new WatchdogHostStopMonitor("production-seam");
                var processId = 4301;
                var processStart = 4301001L;
                var attachEpoch = 9L;
                projection.ProcessId = processId;
                projection.ProcessStartUtcTicks = processStart;
                projection.AttachEpoch = attachEpoch;
                monitor.NotifyValidatedAttached(
                    "production-seam",
                    projection.TransactionId,
                    projection.Generation,
                    "production-attach",
                    processId,
                    processStart,
                    attachEpoch);
                monitor.Observe(projection);
                var sinkCount = 0;
                var sink = new Action<string>(
                    _ => Interlocked.Increment(ref sinkCount));
                for (var i = 0; i < 100; i++)
                    monitor.EvaluateTick(
                        DateTime.UtcNow,
                        processAlive: true,
                        heartbeatAgeSeconds: 0,
                        sessionRevoked: false,
                        manualStopRequested: false,
                        alreadyTakingOver: false,
                        sink: sink);
                var concurrent = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => monitor.EvaluateTick(
                        DateTime.UtcNow,
                        processAlive: true,
                        heartbeatAgeSeconds: 0,
                        sessionRevoked: false,
                        manualStopRequested: false,
                        alreadyTakingOver: false,
                        sink: sink)))
                    .ToArray();
                Task.WaitAll(concurrent);
                var observedTerminal = monitor.Snapshot;
                // Physical failure is a real sticky takeover projection, so
                // the production EvaluateTick must dispatch exactly one sink
                // event; duplicate receipt/source observations cannot create a
                // second.
                Assert(!projection.Active && projection.TakeoverRequired &&
                       observedTerminal.TransactionId == projection.TransactionId &&
                       observedTerminal.Generation == projection.Generation &&
                       observedTerminal.ProgressVersion == projection.ProgressVersion &&
                       observedTerminal.AggregateVersion == projection.AggregateVersion &&
                       sinkCount == 1 &&
                       projection.AggregateVersion == aggregate.Version &&
                       result != null,
                    "生产aggregate→mapper→EvaluateTick→monitor/sink发生重复派发或版本混用。");

                var beforeStale = monitor.Snapshot;
                var oldProcess = projection.Clone();
                oldProcess.ProcessId = processId - 1;
                oldProcess.ProcessStartUtcTicks = processStart - 1;
                oldProcess.AttachEpoch = attachEpoch - 1;
                oldProcess.Generation = projection.Generation + 99;
                oldProcess.TakeoverRequired = false;
                oldProcess.TimedOut = false;
                oldProcess.Active = false;
                monitor.Observe(oldProcess);
                Assert(monitor.Snapshot.ProcessId == beforeStale.ProcessId &&
                       monitor.Snapshot.ProcessStartUtcTicks ==
                           beforeStale.ProcessStartUtcTicks &&
                       monitor.Snapshot.AttachEpoch == beforeStale.AttachEpoch &&
                       monitor.Snapshot.Generation == beforeStale.Generation &&
                       monitor.Snapshot.TransactionId == beforeStale.TransactionId &&
                       monitor.Snapshot.ProgressVersion == beforeStale.ProgressVersion &&
                       monitor.Snapshot.TakeoverRequired,
                    "旧PID/start/attachEpoch迟到heartbeat复活或覆盖了terminal。");

                var lowerGeneration = projection.Clone();
                lowerGeneration.Generation = projection.Generation > 0
                    ? projection.Generation - 1
                    : 0;
                lowerGeneration.ProgressVersion = projection.ProgressVersion + 10;
                monitor.Observe(lowerGeneration);
                Assert(monitor.Snapshot.Generation == beforeStale.Generation &&
                       monitor.Snapshot.ProgressVersion == beforeStale.ProgressVersion &&
                       monitor.Snapshot.TransactionId == beforeStale.TransactionId,
                    "同attachment内更低Generation heartbeat不应覆盖当前事实。");

                var rollback = projection.Clone();
                rollback.ProgressVersion = Math.Max(0, projection.ProgressVersion - 1);
                rollback.AggregateVersion = Math.Max(0, projection.AggregateVersion - 1);
                monitor.Observe(rollback);
                Assert(monitor.Snapshot.ProgressVersion == beforeStale.ProgressVersion &&
                       monitor.Snapshot.AggregateVersion == beforeStale.AggregateVersion,
                    "同validated attachment内版本倒退heartbeat未被拒绝。");

                // The production stop transaction starts at generation 1.  Exercise the
                // cross-attachment ordering rule with a real validated attachment at a
                // larger generation first, then accept a new transaction at a smaller
                // (but still positive) generation only after its own validation.  This
                // is deliberately a monitor-domain transition; no stop fields are
                // manufactured on the production aggregate itself.
                var bridgeGeneration = projection.Generation + 1;
                var bridgeTransaction = "bridge-production-transaction";
                monitor.NotifyValidatedAttached(
                    "production-seam",
                    bridgeTransaction,
                    bridgeGeneration,
                    "production-attach-bridge",
                    processId + 1,
                    processStart + 1,
                    attachEpoch + 1);

                var nextGeneration = projection.Generation;
                Assert(nextGeneration > 0 && nextGeneration < bridgeGeneration,
                    "跨附着回放需要正数且小于前一附着的generation。");
                var nextTransaction = "next-production-transaction";
                var nextBeforeAttach = projection.Clone();
                nextBeforeAttach.TransactionId = nextTransaction;
                nextBeforeAttach.Generation = nextGeneration;
                nextBeforeAttach.ProcessId = processId + 2;
                nextBeforeAttach.ProcessStartUtcTicks = processStart + 2;
                nextBeforeAttach.AttachEpoch = attachEpoch + 2;
                monitor.Observe(nextBeforeAttach);
                Assert(monitor.Snapshot.TransactionId == bridgeTransaction &&
                       monitor.Snapshot.Generation == bridgeGeneration &&
                       monitor.Snapshot.ProcessId == processId + 1 &&
                       monitor.Snapshot.AttachEpoch == attachEpoch + 1,
                    "未经过新validated Attached的下一事务heartbeat不应被接受。");
                monitor.NotifyValidatedAttached(
                    "production-seam",
                    nextTransaction,
                    nextGeneration,
                    "production-attach-2",
                    processId + 2,
                    processStart + 2,
                    attachEpoch + 2);
                var nextProjection = new StopSafetyHeartbeatProjection
                {
                    Active = false,
                    TakeoverRequired = true,
                    TerminalReason = "next-terminal",
                    TransactionId = nextTransaction,
                    Generation = nextGeneration,
                    ProcessId = processId + 2,
                    ProcessStartUtcTicks = processStart + 2,
                    AttachEpoch = attachEpoch + 2
                };
                monitor.Observe(nextProjection);
                monitor.EvaluateTick(
                    DateTime.UtcNow,
                    processAlive: true,
                    heartbeatAgeSeconds: 0,
                    sessionRevoked: false,
                    manualStopRequested: false,
                    alreadyTakingOver: false,
                    sink: sink);
                Assert(sinkCount == 2 &&
                       monitor.Snapshot.TransactionId == nextTransaction &&
                       monitor.Snapshot.Generation == nextGeneration &&
                       monitor.Snapshot.ProcessId == processId + 2 &&
                       monitor.Snapshot.AttachEpoch == attachEpoch + 2,
                    "新validated Attached未开启下一事务的唯一接管域。");

                for (var i = 0; i < 100; i++)
                {
                    monitor.EvaluateTick(
                        DateTime.UtcNow,
                        processAlive: true,
                        heartbeatAgeSeconds: 0,
                        sessionRevoked: false,
                        manualStopRequested: false,
                        alreadyTakingOver: false,
                        sink: sink);
                }

                var repeatedNext = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => monitor.EvaluateTick(
                        DateTime.UtcNow,
                        processAlive: true,
                        heartbeatAgeSeconds: 0,
                        sessionRevoked: false,
                        manualStopRequested: false,
                        alreadyTakingOver: false,
                        sink: sink)))
                    .ToArray();
                Task.WaitAll(repeatedNext);
                Assert(sinkCount == 2 &&
                       monitor.Snapshot.TransactionId == nextTransaction &&
                       monitor.Snapshot.Generation == nextGeneration,
                    "新附着较小正generation的重复终态评估不应重复派发。");
            }
        }

        private static void EpbManagerProductionPortPublishesEdges()
        {
            using (var fixture = new ProductionManagerFixture())
            {
                var writer = new ProductionDoBatchWriter();
                fixture.ConfigurePhysicalOff(writer);
                var transaction = new StopSafetyTransactionContext(
                    new StopContext
                    {
                        Source = StopSource.ManualUi,
                        CorrelationId = Guid.NewGuid().ToString("N"),
                        Reason = "production-port-seam"
                    },
                    Guid.NewGuid(),
                    1,
                    Guid.NewGuid(),
                    1,
                    DateTime.UtcNow,
                    DateTime.UtcNow.AddSeconds(45),
                    CancellationToken.None);
                var port = fixture.Manager.CreateStopSafetyExecutionPortForProduction();
                var freeze = port.ExecuteStageAsync(
                    StopSafetyStage.FreezeActiveWork,
                    transaction,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(freeze.Succeeded, "EpbManager真实Freeze阶段失败：" + freeze.Error);
                var off = port.ExecuteStageAsync(
                    StopSafetyStage.SubmitPhysicalOff,
                    transaction,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(off.Succeeded && off.PhysicalOffSubmitted == true,
                    "真实EpbManager DO阶段未发布positive edge。");
                var power = port.ExecuteStageAsync(
                    StopSafetyStage.StartPowerDisable,
                    transaction,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(power.Succeeded && power.PowerDisableStarted == true,
                    "真实EpbManager PSU阶段未发布positive edge：" + power.Error);
            }
        }

        private static void StrictStageOrderAndDeadlines()
        {
            var clock = new ManualClock();
            var port = new FakePort();
            var snapshots = new List<StopSafetyProgressSnapshot>();
            var runId = Guid.NewGuid();
            var runner = new StopSafetyTransactionRunner(
                port,
                clock,
                new StopSafetyTransactionOptions(),
                snapshot => snapshots.Add(snapshot),
                () => runId,
                () => 9,
                () => 17);

            var result = runner.StopAsync(NewContext()).GetAwaiter().GetResult();
            Assert(result.Outcome == StopSafetyOutcome.CompletedSafe,
                "正常阶段未完成安全终态。");
            Assert(result.SafetyTransactionId != Guid.Empty &&
                   result.SafetyTransactionId == snapshots.Last().TransactionId &&
                   result.RunId == runId && result.RunEpoch == 9 &&
                   result.SafetyBoundaryGeneration == 17,
                "StopSafetyResult未完整克隆事务/RunEpoch/安全代次身份。");
            var entered = port.EnteredStages.ToArray();
            var expected = new[]
            {
                StopSafetyStage.AdmitAndSubmitSafety,
                StopSafetyStage.FreezeActiveWork,
                StopSafetyStage.RevokeExecutionAuthorization,
                StopSafetyStage.SubmitPhysicalOff,
                StopSafetyStage.StartPowerDisable,
                StopSafetyStage.ClearTimerAndRunner,
                StopSafetyStage.ClearRecoveryOwners,
                StopSafetyStage.ReleaseHydraulics,
                StopSafetyStage.StopAcquisition,
                StopSafetyStage.ClosePersistenceBoundary,
                StopSafetyStage.VerifyLogicalQuiescence
            };
            Assert(expected.SequenceEqual(entered),
                "生产runner阶段顺序不严格：" + string.Join(",", entered));
            var release = snapshots.Last(item => item.Stage == StopSafetyStage.ReleaseHydraulics);
            var persistence = snapshots.Last(item => item.Stage == StopSafetyStage.ClosePersistenceBoundary);
            Assert((release.StageHardDeadlineUtc - release.StageStartedUtc).TotalSeconds == 15 &&
                   (persistence.StageHardDeadlineUtc - persistence.StageStartedUtc).TotalSeconds == 15,
                "液压释放/持久化没有分别保留15秒阶段期限。");
            Assert(snapshots.Any(item => item.Stage == StopSafetyStage.Completed && !item.Active),
                "Completed终态没有粘性发布。");
        }

        private static void StageFailureIsSafeIdle()
        {
            var port = new FakePort { FailureStage = StopSafetyStage.StartPowerDisable };
            var runner = new StopSafetyTransactionRunner(port, new ManualClock());
            var result = runner.StopAsync(NewContext()).GetAwaiter().GetResult();
            Assert(SpinWait.SpinUntil(() => port.SafeIdleCount == 1, 2000),
                "阶段Failure已发布终态但异步SafeIdle未在有界窗口内执行。");
            var executeCount = port.ExecuteCount;
            var reentry = runner.StopAsync(NewContext()).GetAwaiter().GetResult();
            Assert(SpinWait.SpinUntil(() => port.SafeIdleCount == 2, 2000),
                "第二次失败事务没有形成自己的SafeIdle收口。");
            Assert(result.RequiresProcessRestart &&
                   result.LastStage == StopSafetyStage.StartPowerDisable &&
                   port.SafeIdleCount == 2 &&
                   !reentry.ReusedPreviousResult &&
                   reentry.SafetyTransactionId != result.SafetyTransactionId &&
                   port.ExecuteCount > executeCount,
                "DO/Power阶段失败后的新请求没有建立独立安全事务。");
            var snapshot = runner.CaptureProgress();
            Assert(!snapshot.Active && snapshot.TakeoverRequired &&
                   !snapshot.TimedOut &&
                   snapshot.Stage == StopSafetyStage.StartPowerDisable &&
                   snapshot.TerminalReason.Contains("StageFailure:StartPowerDisable"),
                "阶段失败终态快照缺少接管原因。");
        }

        private static void DetailDoesNotRenewProgress()
        {
            var clock = new ManualClock();
            var port = new FakePort { DuplicateDetail = true };
            var snapshots = new List<StopSafetyProgressSnapshot>();
            var runner = new StopSafetyTransactionRunner(
                port,
                clock,
                new StopSafetyTransactionOptions(),
                snapshot => snapshots.Add(snapshot));
            runner.StopAsync(NewContext()).GetAwaiter().GetResult();
            var sameStage = snapshots
                .Where(item => item.Stage == StopSafetyStage.FreezeActiveWork)
                .ToArray();
            Assert(sameStage.Length >= 2 &&
                   sameStage.Select(item => item.StageStartedUtc).Distinct().Count() == 1,
                "同阶段Detail回调刷新了阶段开始时间。");
            Assert(port.MaterialEvidenceCalls == 1,
                "相同材料证据被重复接受，错误续期。");
        }

        private static void CallerCancellationDoesNotCancelSafety()
        {
            var clock = new ManualClock();
            var port = new FakePort();
            var runner = new StopSafetyTransactionRunner(
                port,
                clock,
                new StopSafetyTransactionOptions
                {
                    Stages = new[] { StopSafetyStage.FreezeActiveWork }
                });
            using (var caller = new CancellationTokenSource())
            {
                caller.Cancel();
                var result = runner.StopAsync(NewContext(), caller.Token)
                    .GetAwaiter().GetResult();
                Assert(!result.TimedOut &&
                       !port.LastSafetyTokenWasCanceled,
                    "Caller取消错误地取消了断能事务。");
            }
        }

        private static void HardTimeoutStartsFreshTransaction()
        {
            var clock = new ManualClock();
            var port = new FakePort { Hang = true };
            var runner = new StopSafetyTransactionRunner(port, clock);
            using (var caller = new CancellationTokenSource())
            {
                var first = runner.StopAsync(NewContext(), caller.Token);
                Assert(port.StageEntered.Wait(TimeSpan.FromSeconds(2)), "hung stage未启动。");
                caller.Cancel();
                clock.Advance(TimeSpan.FromSeconds(45));
                var timeout = first.GetAwaiter().GetResult();
                Assert(SpinWait.SpinUntil(() => port.SafeIdleCount == 1, 2000),
                    "硬截止已发布但异步SafeIdle未在有界窗口内执行。");
                Assert(timeout.TimedOut && timeout.RequiresProcessRestart &&
                       port.SafeIdleCount == 1 && runner.HasOrphanCore,
                    "45秒硬截止没有进入单孤儿接管终态。 TimedOut=" + timeout.TimedOut +
                    ";Restart=" + timeout.RequiresProcessRestart +
                    ";SafeIdle=" + port.SafeIdleCount +
                    ";Orphan=" + runner.HasOrphanCore +
                    ";Stage=" + timeout.LastStage);
                var calls = port.ExecuteCount;
                port.Hang = false;
                var receipt = runner.StopWithReceiptAsync(NewContext()).GetAwaiter().GetResult();
                var second = receipt.Result;
                Assert(!second.TimedOut && !second.ReusedPreviousResult &&
                       second.SafetyTransactionId != timeout.SafetyTransactionId &&
                       receipt.PhysicalTransactionId == second.SafetyTransactionId &&
                       receipt.PreviousTransactionId == timeout.SafetyTransactionId &&
                       !receipt.JoinedActiveTransaction &&
                       port.ExecuteCount > calls,
                    "TimedOut后新请求没有执行新的幂等安全事务/返回请求级回执。");
                port.ReleaseHungStage();
                SpinWait.SpinUntil(() => !runner.HasOrphanCore, 2000);
                Assert(port.SafeIdleCount == 1, "迟到core重复执行SafeIdle。");
            }
        }

        private static void StageDeadlineStartsFreshTransaction()
        {
            var clock = new ManualClock();
            var port = new FakePort { HangStage = StopSafetyStage.ReleaseHydraulics };
            var runner = new StopSafetyTransactionRunner(port, clock);
            var first = runner.StopAsync(NewContext());
            Assert(SpinWait.SpinUntil(
                    () => port.EnteredStages.Contains(StopSafetyStage.ReleaseHydraulics),
                    2000), "ReleaseHydraulics阶段未启动。");
            clock.Advance(TimeSpan.FromSeconds(15));
            var timeout = first.GetAwaiter().GetResult();
            Assert(SpinWait.SpinUntil(() => port.SafeIdleCount == 1, 2000),
                "阶段硬截止已发布但异步SafeIdle未在有界窗口内执行。");
            Assert(timeout.TimedOut && timeout.RequiresProcessRestart &&
                   timeout.StageError == "StageDeadlineExceeded:ReleaseHydraulics" &&
                   runner.CaptureProgress().TerminalReason ==
                   "StageDeadlineExceeded:ReleaseHydraulics" &&
                   port.SafeIdleCount == 1 && runner.HasOrphanCore,
                "阶段硬截止未形成粘性TimedOut/TakeoverRequired与唯一orphan。");
            var calls = port.ExecuteCount;
            port.HangStage = null;
            var receipt = runner.StopWithReceiptAsync(NewContext()).GetAwaiter().GetResult();
            var second = receipt.Result;
            Assert(!second.TimedOut && !second.ReusedPreviousResult &&
                   second.SafetyTransactionId != timeout.SafetyTransactionId &&
                   receipt.PreviousTransactionId == timeout.SafetyTransactionId &&
                   port.ExecuteCount > calls,
                "阶段超时后的请求没有建立新代次并重做安全动作。");
            port.ReleaseHungStage();
            Assert(SpinWait.SpinUntil(() => !runner.HasOrphanCore, 2000),
                "迟到阶段任务未被观察并收回orphan槽。");
            Assert(port.SafeIdleCount == 1, "迟到阶段完成重复触发SafeIdle。");
        }

        private static void AggregateProjectionDispatchesOnce()
        {
            var clock = new ManualClock();
            var port = new FakePort { FailureStage = StopSafetyStage.StartPowerDisable };
            var store = new RecoveryAggregateStore();
            var runner = new StopSafetyTransactionRunner(
                port,
                clock,
                publishProgress: snapshot => store.PublishStopSource(snapshot),
                runIdProvider: () => Guid.Parse("11111111-1111-1111-1111-111111111111"),
                runEpochProvider: () => 7,
                generationProvider: () => 3);
            var result = runner.StopAsync(NewContext()).GetAwaiter().GetResult();
            Assert(result.RequiresProcessRestart, "故障事务未进入接管边界。");

            var aggregate = store.Capture();
            var heartbeat = new WatchdogHeartbeat
            {
                StopAllActive = aggregate.StopProgress.Active,
                StopStage = aggregate.StopProgress.Stage.ToString(),
                StopProgressVersion = aggregate.StopProgress.ProgressVersion,
                StopStageStartedUtc = aggregate.StopProgress.StageStartedUtc.Ticks,
                StopStageHardDeadlineUtc = aggregate.StopProgress.StageHardDeadlineUtc.Ticks,
                StopHardDeadlineUtc = aggregate.StopProgress.HardDeadlineUtc.Ticks,
                StopStageNoProgressGraceMs = aggregate.StopProgress.StageNoProgressGraceMs,
                StopLastMaterialProgressUtc = aggregate.StopProgress.LastMaterialProgressUtc.Ticks,
                StopTakeoverRequired = aggregate.StopProgress.TakeoverRequired,
                StopTimedOut = aggregate.StopProgress.TimedOut,
                StopTerminalReason = aggregate.StopProgress.TerminalReason,
                StopTransactionId = aggregate.StopProgress.TransactionId.ToString("N"),
                StopGeneration = aggregate.StopProgress.Generation
            };
            var projection = StopSafetyHeartbeatProjection.FromHeartbeat(heartbeat);
            Assert(!projection.Active && projection.TakeoverRequired &&
                   projection.StopHardDeadlineUtc == heartbeat.StopHardDeadlineUtc,
                "终态投影没有保留 inactive/takeover 事实。");

            var noStopMonitor = new WatchdogHostStopMonitor("no-stop");
            var noStopSinkCount = 0;
            Assert(!noStopMonitor.EvaluateTick(
                           DateTime.UtcNow,
                           processAlive: false,
                           heartbeatAgeSeconds: 10,
                           sessionRevoked: false,
                           manualStopRequested: false,
                           alreadyTakingOver: false,
                           sink: _ => Interlocked.Increment(ref noStopSinkCount)) &&
                   noStopSinkCount == 0 && noStopMonitor.DispatchCount == 0,
                "无Stop事务的EvaluateTick不应消费once gate或派发接管。");

            // Exercise the same pure policy boundary used by MonitorAsync:
            // a hard stop deadline is sufficient evidence once the runner has
            // supplied the material-progress clock.
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 0, false, false, false, 0,
                    false, true, nowUtcTicks: heartbeat.StopStageHardDeadlineUtc + 1,
                    stopStageHardDeadlineUtcTicks: heartbeat.StopStageHardDeadlineUtc,
                    stopNoProgressSeconds: 5.0,
                    stopStageNoProgressGraceMs: 5000),
                "停止安全投影没有通过统一接管策略边界。");

            var supervisor = new HostStopSafetySupervisor("session-1");
            supervisor.Observe(projection);
            var sinkCount = 0;
            var sink = new Action<string>(_ => Interlocked.Increment(ref sinkCount));
            for (var i = 0; i < 100; i++)
                supervisor.EvaluateAndDispatch(sink);
            var concurrent = Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => supervisor.EvaluateAndDispatch(sink)))
                .ToArray();
            Task.WaitAll(concurrent);
            Assert(sinkCount == 1 && supervisor.DispatchCount == 1,
                "100次串行+64次并发评估产生了重复接管。");

            var stale = aggregate.StopProgress.Clone();
            stale.Active = true;
            stale.TakeoverRequired = false;
            stale.TimedOut = false;
            store.PublishStopSource(stale);
            Assert(store.Capture().StopProgress.TakeoverRequired,
                "聚合store允许迟到正常发布清除粘性接管事实。");

            supervisor.NotifyValidatedAttached("session-1", "new-transaction", 4);
            supervisor.Observe(new StopSafetyHeartbeatProjection
            {
                Active = false,
                TakeoverRequired = true,
                TransactionId = "new-transaction",
                Generation = 4,
                TerminalReason = "new-timeout"
            });
            supervisor.EvaluateAndDispatch(sink);
            Assert(sinkCount == 2,
                "只有新的 validated Attached 才能开启下一事务接管门，但未重置。");
        }

        private static void HardwareSafeIdleSuppressesOnlyInactiveStickyStop()
        {
            var heartbeat = new WatchdogHeartbeat
            {
                ProcessId = 4242,
                ProcessStartUtcTicks = 987654321,
                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                HardwareUnavailable = true,
                Phase = "SafeIdleHardwareUnavailable",
                EnergizedChannelCount = 0,
                HardwareFailureFingerprint = "daq-missing-dev2",
                HardwareProbeAttempt = 3,
                HardwareNextProbeUtc = DateTime.UtcNow.AddSeconds(30).Ticks
            };
            Assert(WatchdogHost.IsVerifiedHardwareSafeIdle(
                    heartbeat,
                    heartbeat.ProcessId,
                    heartbeat.ProcessStartUtcTicks,
                    processAlive: true,
                    heartbeatAgeSeconds: 1,
                    sessionRevoked: false,
                    manualStopRequested: false,
                    alreadyTakingOver: false),
                "完整的恢复进程硬件安全等待证据未被识别。");

            var terminal = new StopSafetyHeartbeatProjection
            {
                Active = false,
                TakeoverRequired = true,
                TransactionId = "hardware-safe-idle",
                Generation = 1,
                TerminalReason = "PreviousStopTerminal"
            };
            var idleMonitor = new WatchdogHostStopMonitor("hardware-safe-idle");
            idleMonitor.Observe(terminal);
            var idleDispatches = 0;
            Assert(!idleMonitor.EvaluateTick(
                    DateTime.UtcNow,
                    processAlive: true,
                    heartbeatAgeSeconds: 1,
                    sessionRevoked: false,
                    manualStopRequested: false,
                    alreadyTakingOver: false,
                    hardwareSafeIdle: true,
                    sink: _ => Interlocked.Increment(ref idleDispatches)) &&
                   idleDispatches == 0,
                "硬件安全等待仍因非活动Stop粘滞终态派发了重启。");

            var activeMonitor = new WatchdogHostStopMonitor("active-stop");
            activeMonitor.Observe(new StopSafetyHeartbeatProjection
            {
                Active = true,
                TimedOut = true,
                TransactionId = "active-stop",
                Generation = 1,
                TerminalReason = "ActiveStopTimedOut"
            });
            var activeDispatches = 0;
            var activeSink = new Action<string>(
                _ => Interlocked.Increment(ref activeDispatches));
            Assert(activeMonitor.EvaluateTick(
                    DateTime.UtcNow,
                    processAlive: true,
                    heartbeatAgeSeconds: 1,
                    sessionRevoked: false,
                    manualStopRequested: false,
                    alreadyTakingOver: false,
                    hardwareSafeIdle: true,
                    sink: activeSink),
                "活动StopAll被硬件安全等待错误抑制。");
            activeMonitor.EvaluateTick(
                DateTime.UtcNow,
                processAlive: true,
                heartbeatAgeSeconds: 1,
                sessionRevoked: false,
                manualStopRequested: false,
                alreadyTakingOver: false,
                hardwareSafeIdle: true,
                sink: activeSink);
            Assert(activeDispatches == 1,
                "活动StopAll接管没有保持单次派发。");

            var deadMonitor = new WatchdogHostStopMonitor("dead-recovery");
            deadMonitor.Observe(terminal);
            var deadDispatches = 0;
            Assert(deadMonitor.EvaluateTick(
                    DateTime.UtcNow,
                    processAlive: false,
                    heartbeatAgeSeconds: 1,
                    sessionRevoked: false,
                    manualStopRequested: false,
                    alreadyTakingOver: false,
                    hardwareSafeIdle: true,
                    sink: _ => Interlocked.Increment(ref deadDispatches)) &&
                   deadDispatches == 1,
                "恢复进程死亡被硬件安全等待错误抑制。");

            Assert(!WatchdogHost.IsVerifiedHardwareSafeIdle(
                       heartbeat,
                       heartbeat.ProcessId,
                       heartbeat.ProcessStartUtcTicks,
                       true,
                       5,
                       false,
                       false,
                       false) &&
                   !WatchdogHost.IsVerifiedHardwareSafeIdle(
                       heartbeat,
                       heartbeat.ProcessId,
                       heartbeat.ProcessStartUtcTicks,
                       true,
                       1,
                       false,
                       true,
                       false),
                "心跳超时或人工停止仍被误判为硬件安全等待。");
            heartbeat.EnergizedChannelCount = 1;
            Assert(!WatchdogHost.IsVerifiedHardwareSafeIdle(
                    heartbeat,
                    heartbeat.ProcessId,
                    heartbeat.ProcessStartUtcTicks,
                    true,
                    1,
                    false,
                    false,
                    false),
                "重新带电仍被误判为硬件安全等待。");
            heartbeat.EnergizedChannelCount = 0;
            heartbeat.HardwareFailureFingerprint = string.Empty;
            Assert(!WatchdogHost.IsVerifiedHardwareSafeIdle(
                    heartbeat,
                    heartbeat.ProcessId,
                    heartbeat.ProcessStartUtcTicks,
                    true,
                    1,
                    false,
                    false,
                    false),
                "缺少硬件指纹仍被误判为硬件安全等待。");
        }

        private static void ValidatedAttachmentOrdersHeartbeat()
        {
            var supervisor = new HostStopSafetySupervisor("session-order");
            supervisor.NotifyValidatedAttached(
                "session-order", "old", 10, "11:100", 11, 100, 5);
            supervisor.Observe(new StopSafetyHeartbeatProjection
            {
                ProcessId = 11,
                ProcessStartUtcTicks = 100,
                AttachEpoch = 5,
                Generation = 10,
                ProgressVersion = 3,
                TransactionId = "old",
                TakeoverRequired = true,
                TerminalReason = "old-timeout"
            });
            Assert(supervisor.Snapshot.TransactionId == "old",
                "当前validated attachment心跳未被接受。");

            // A new validated process may legitimately restart its generation
            // at a lower value; it opens a new ordering domain.
            supervisor.NotifyValidatedAttached(
                "session-order", "new", 2, "12:200", 12, 200, 6);
            supervisor.Observe(new StopSafetyHeartbeatProjection
            {
                ProcessId = 12,
                ProcessStartUtcTicks = 200,
                AttachEpoch = 6,
                Generation = 2,
                ProgressVersion = 4,
                TransactionId = "new",
                TakeoverRequired = true,
                TerminalReason = "new-timeout"
            });
            Assert(supervisor.Snapshot.TransactionId == "new" &&
                   supervisor.Snapshot.Generation == 2,
                "新validated进程的小generation未开启新排序域。");

            // Old process/epoch traffic must not revive or overwrite the new
            // terminal decision, even with a numerically larger generation.
            supervisor.Observe(new StopSafetyHeartbeatProjection
            {
                ProcessId = 11,
                ProcessStartUtcTicks = 100,
                AttachEpoch = 5,
                Generation = 99,
                ProgressVersion = 99,
                TransactionId = "old-late",
                TakeoverRequired = false
            });
            Assert(supervisor.Snapshot.TransactionId == "new" &&
                   supervisor.Snapshot.Generation == 2 &&
                   supervisor.Snapshot.TakeoverRequired,
                "旧PID/start/attach epoch迟到心跳复活了新排序域。");

            // Same attachment cannot regress its material progress version.
            supervisor.Observe(new StopSafetyHeartbeatProjection
            {
                ProcessId = 12,
                ProcessStartUtcTicks = 200,
                AttachEpoch = 6,
                Generation = 2,
                ProgressVersion = 1,
                TransactionId = "new",
                TakeoverRequired = false
            });
            Assert(supervisor.Snapshot.ProgressVersion == 4 &&
                   supervisor.Snapshot.TakeoverRequired,
                "同attachment的低版本心跳覆盖了高版本安全事实。");
        }

        private static StopContext NewContext()
        {
            return new StopContext
            {
                Source = StopSource.ManualUi,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Reason = "production-seam"
            };
        }

        private static void Run(string name, Action action, ref int passed)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("FAIL " + name + ": " + ex.Message, ex);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakePort : IStopSafetyExecutionPort
        {
            private readonly object _gate = new object();
            private int _safeIdleCount;
            private readonly List<StopSafetyStage> _entered = new List<StopSafetyStage>();
            private readonly TaskCompletionSource<StopSafetyPortResult> _hung =
                new TaskCompletionSource<StopSafetyPortResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal StopSafetyStage? FailureStage { get; set; }
            internal StopSafetyStage? HangStage { get; set; }
            internal bool Hang { get; set; }
            internal bool DuplicateDetail { get; set; }
            internal int SafeIdleCount => Volatile.Read(ref _safeIdleCount);
            internal int ExecuteCount { get; private set; }
            internal int MaterialEvidenceCalls { get; private set; }
            internal bool LastSafetyTokenWasCanceled { get; private set; }
            internal ManualResetEventSlim StageEntered { get; } =
                new ManualResetEventSlim(false);

            internal IReadOnlyList<StopSafetyStage> EnteredStages
            {
                get { lock (_gate) return _entered.ToArray(); }
            }

            public Task<StopSafetyPortResult> ExecuteStageAsync(
                StopSafetyStage stage,
                StopSafetyTransactionContext transaction,
                CancellationToken safetyCancellationToken)
            {
                lock (_gate)
                {
                    _entered.Add(stage);
                    ExecuteCount++;
                }
                StageEntered.Set();
                LastSafetyTokenWasCanceled = safetyCancellationToken.IsCancellationRequested;
                if (FailureStage == stage)
                    return Task.FromResult(StopSafetyPortResult.Failure("Injected " + stage + " failure"));
                if (Hang || HangStage == stage) return _hung.Task;
                if (DuplicateDetail && stage == StopSafetyStage.FreezeActiveWork)
                {
                    MaterialEvidenceCalls++;
                    return Task.FromResult(StopSafetyPortResult.Success(
                        "same-detail", true, "fake-daq", 1));
                }
                if (stage == StopSafetyStage.VerifyLogicalQuiescence)
                {
                    return Task.FromResult(new StopSafetyPortResult
                    {
                        Succeeded = true,
                        Detail = stage.ToString(),
                        PhysicalOffSubmitted = true,
                        PowerDisableStarted = true,
                        PhysicalSafe = true,
                        Result = new StopSafetyResult
                        {
                            MotorOffCommandSucceeded = true,
                            PowerOffConfirmed = true,
                            PressureSafeConfirmed = true,
                            PersistenceBoundaryConfirmed = true,
                            LogicalQuiescenceConfirmed = true
                        }
                    });
                }
                return Task.FromResult(StopSafetyPortResult.Success(
                    stage == StopSafetyStage.SubmitPhysicalOff ? "DO submitted" : stage.ToString(),
                    stage == StopSafetyStage.SubmitPhysicalOff,
                    stage == StopSafetyStage.SubmitPhysicalOff ? "fake-do" : null,
                    stage == StopSafetyStage.SubmitPhysicalOff ? 1 : 0));
            }

            public void EnterSafeIdleOnce(
                StopSafetyTransactionContext transaction,
                string reason)
            {
                Interlocked.Increment(ref _safeIdleCount);
            }

            internal void ReleaseHungStage()
            {
                _hung.TrySetResult(StopSafetyPortResult.Success("late completion"));
            }
        }

        /// <summary>
        /// Acceptance seam at DoController.SetEpbOffBatchCore.  The real
        /// HighPriorityDoWorker still owns admission, registration, completion
        /// and telemetry receipts; this helper only controls the final
        /// physical-write outcome.
        /// </summary>
        private sealed class ProductionDoBatchWriter : IHighPriorityOffPhysicalWriter
        {
            private readonly bool _physicalFailure;
            private readonly bool _blockFirstWrite;
            private int _writeCount;
            private int _firstWriteBlocked;
            private readonly ProductionActionOrder _order;

            internal ProductionDoBatchWriter(
                ProductionActionOrder order = null,
                bool physicalFailure = false,
                bool blockFirstWrite = false)
            {
                _order = order;
                _physicalFailure = physicalFailure;
                _blockFirstWrite = blockFirstWrite;
            }

            internal ManualResetEventSlim BlockStarted { get; } =
                new ManualResetEventSlim(false);
            internal ManualResetEventSlim Release { get; } =
                new ManualResetEventSlim(false);
            internal bool AcceptancePhase { get; set; }
            internal int BatchWriteCount => Volatile.Read(ref _writeCount);
            internal int FirstAcceptanceWriteOrder { get; private set; }

            public bool TryWrite(
                string deviceName,
                IReadOnlyList<int> channels,
                bool[] nextStates)
            {
                var count = Interlocked.Increment(ref _writeCount);
                if (AcceptancePhase && FirstAcceptanceWriteOrder == 0)
                    FirstAcceptanceWriteOrder = _order?.Next("DO-fallback") ?? count;
                else
                    _order?.Next("DO");
                if (_blockFirstWrite &&
                    Interlocked.CompareExchange(ref _firstWriteBlocked, 1, 0) == 0)
                {
                    BlockStarted.Set();
                    Release.Wait(TimeSpan.FromSeconds(10));
                }
                return !_physicalFailure;
            }

            internal bool Write(IReadOnlyList<int> channels) =>
                TryWrite("test", channels, null);
        }

        private sealed class ProductionActionOrder
        {
            private readonly object _gate = new object();
            private int _sequence;
            private readonly List<ProductionActionEvent> _events =
                new List<ProductionActionEvent>();
            internal int PowerDisableOrder { get; private set; }
            internal int FirstPowerDisableOrder { get; private set; }

            internal int Next(string action)
            {
                return Next(action, Guid.Empty);
            }

            internal int Next(string action, Guid commandId)
            {
                lock (_gate)
                {
                    var value = ++_sequence;
                    _events.Add(new ProductionActionEvent(value, action, commandId));
                    if (string.Equals(action, "PSU", StringComparison.Ordinal))
                    {
                        PowerDisableOrder = value;
                        if (FirstPowerDisableOrder == 0)
                            FirstPowerDisableOrder = value;
                    }
                    return value;
                }
            }

            internal void Reset()
            {
                lock (_gate)
                {
                    _sequence = 0;
                    _events.Clear();
                    PowerDisableOrder = 0;
                    FirstPowerDisableOrder = 0;
                }
            }

            internal IReadOnlyList<ProductionActionEvent> Snapshot()
            {
                lock (_gate) return _events.ToArray();
            }

            internal sealed class ProductionActionEvent
            {
                internal ProductionActionEvent(
                    int sequence,
                    string action,
                    Guid commandId)
                {
                    Sequence = sequence;
                    Action = action ?? string.Empty;
                    CommandId = commandId;
                }

                internal int Sequence { get; }
                internal string Action { get; }
                internal Guid CommandId { get; }
            }
        }

        private sealed class ProductionHydraulicAdapter : IStopSafetyHydraulicAdapter
        {
            private readonly object _gate = new object();
            private readonly ManualResetEventSlim _started =
                new ManualResetEventSlim(false);
            private readonly TaskCompletionSource<(bool ok, string error)> _completion =
                new TaskCompletionSource<(bool ok, string error)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly IStopSafetyClock _clock;
            private readonly bool _immediateSuccess;
            private DateTime _startedUtc;

            internal ProductionHydraulicAdapter(
                IStopSafetyClock clock = null,
                bool immediateSuccess = false)
            {
                _clock = clock;
                _immediateSuccess = immediateSuccess;
            }

            internal ManualResetEventSlim Started => _started;
            internal int VirtualElapsedSeconds { get; private set; }
            internal int ConfirmCallCount { get; private set; }

            public Task<(bool ok, string error)> ConfirmPressureSafeAsync(
                StopContext context,
                Task<(bool ok, string error)> powerOffTask,
                long stopGeneration)
            {
                lock (_gate)
                    ConfirmCallCount++;
                lock (_gate)
                    _startedUtc = _clock?.UtcNow ?? DateTime.UtcNow;
                _started.Set();
                if (_immediateSuccess)
                    return Task.FromResult((true, string.Empty));
                return _completion.Task;
            }

            internal void Complete(bool ok, string error)
            {
                lock (_gate)
                {
                    var now = _clock?.UtcNow ?? DateTime.UtcNow;
                    VirtualElapsedSeconds = Math.Max(
                        0,
                        (int)Math.Round(
                            (now - _startedUtc).TotalSeconds,
                            MidpointRounding.AwayFromZero));
                }
                _completion.TrySetResult((ok, error ?? string.Empty));
            }
        }

        private sealed class FailingPowerSupply : IPowerSupplyCoordinator
        {
            private readonly ProductionActionOrder _order;
            private readonly bool _fail;
            private readonly bool _delayFirstSafetyFailure;
            private readonly TaskCompletionSource<PowerSafetyDisableResult[]> _firstFailure =
                new TaskCompletionSource<PowerSafetyDisableResult[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim _firstFailureReleaseGate =
                new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _disableStarted =
                new ManualResetEventSlim(false);
            private int _disableCallCount;
            private int _lastConfirmedOff = -1;
            private int _failureReleased;
            private int _failureReleaseOrder;

            internal FailingPowerSupply(
                ProductionActionOrder order = null,
                bool fail = true,
                bool delayFirstSafetyFailure = false)
            {
                _order = order;
                _fail = fail;
                _delayFirstSafetyFailure = delayFirstSafetyFailure;
            }

            public event Action<PowerSupplyTelemetry> TelemetryUpdated;
            public event Action<PowerSupplyFault> FaultRaised;

            public Task PrepareAndEnableAsync(
                IEnumerable<int> selectedChannels,
                CancellationToken token) => Task.CompletedTask;

            public Task RevalidateEnabledAsync(
                IEnumerable<int> selectedChannels,
                CancellationToken token) => Task.CompletedTask;

            public Task DisableGroupAsync(
                int electricalGroupId,
                string reason,
                CancellationToken token) => Task.CompletedTask;

            public Task<PowerSafetyDisableResult> DisableGroupForSafetyAsync(
                int electricalGroupId,
                string reason,
                CancellationToken token) => Task.FromResult(new PowerSafetyDisableResult
                {
                    ElectricalGroupId = electricalGroupId,
                    OperationGeneration = 1,
                    Outcome = PowerSafetyDisableOutcome.ConfirmedOff,
                    ConfirmedOff = true,
                    StartedUtc = DateTime.UtcNow,
                    CompletedUtc = DateTime.UtcNow
                });

            public Task DisableAllAsync(
                string reason,
                CancellationToken token) => Task.CompletedTask;

            public Task<PowerSafetyDisableResult[]> DisableAllForSafetyAsync(
                string reason,
                CancellationToken token)
            {
                _order?.Next("PSU");
                var call = Interlocked.Increment(ref _disableCallCount);
                _disableStarted.Set();
                if (_fail && _delayFirstSafetyFailure && call == 1)
                {
                    // Keep the production power stage inside its own call
                    // until the test has observed both real first-round DO
                    // receipts.  The result itself is still delivered through
                    // the TCS, so the test controls exactly when the false
                    // physical confirmation becomes visible.
                    _firstFailureReleaseGate.Wait();
                    return _firstFailure.Task;
                }
                return Task.FromResult(BuildResult());
            }

            private PowerSafetyDisableResult[] BuildResult()
            {
                Interlocked.Exchange(ref _lastConfirmedOff, _fail ? 0 : 1);
                return _fail
                    ? new[]
                    {
                        new PowerSafetyDisableResult
                        {
                            ElectricalGroupId = 1,
                            Outcome = PowerSafetyDisableOutcome.CommandFailed,
                            ConfirmedOff = false,
                            Error = "InjectedPsuFailure"
                        }
                    }
                    : new[]
                    {
                        new PowerSafetyDisableResult
                        {
                            ElectricalGroupId = 1,
                            Outcome = PowerSafetyDisableOutcome.ConfirmedOff,
                            ConfirmedOff = true,
                            Error = string.Empty
                        }
                    };
            }

            internal void ReleaseFirstSafetyFailure()
            {
                if (Interlocked.Exchange(ref _failureReleased, 1) != 0)
                    return;
                _failureReleaseOrder = _order?.Next("PSU-failure") ?? 0;
                _firstFailure.TrySetResult(BuildResult());
                _firstFailureReleaseGate.Set();
            }

            internal int FailureReleaseOrder => Volatile.Read(ref _failureReleaseOrder);

            internal ProductionActionOrder ActionOrder => _order;

            internal bool WaitForDisableCallCountAtLeast(int count, TimeSpan timeout)
            {
                return SpinWait.SpinUntil(
                    () => Volatile.Read(ref _disableCallCount) >= count,
                    timeout);
            }

            public Task ResetFaultAsync(int electricalGroupId, CancellationToken token) =>
                Task.CompletedTask;

            public bool HasFreshPowerFaultEvidence(int electricalGroupId) => false;

            public bool HasEnergizationPermit(int electricalGroupId, out string reason)
            {
                reason = "InjectedPsuFailure";
                return false;
            }

            public void RecordSuccessfulActionCycle(int epbChannel, long groupCycleSlot) { }
            public PswSnapshot GetLatestSnapshot(int electricalGroupId) => null;
            public PowerSupplyRuntimeState GetRuntimeState(int electricalGroupId) =>
                new PowerSupplyRuntimeState { ElectricalGroupId = electricalGroupId };
            public IReadOnlyList<PowerSupplyTelemetry> GetRecentTelemetry(
                int electricalGroupId,
                TimeSpan window) => Array.Empty<PowerSupplyTelemetry>();
            public IReadOnlyCollection<int> ActiveGroups => Array.Empty<int>();
            public int ColdStartRelaySettleMs => 0;
            internal int DisableCallCount => Volatile.Read(ref _disableCallCount);
            internal ManualResetEventSlim DisableStarted => _disableStarted;
            internal bool LastConfirmedOff => Volatile.Read(ref _lastConfirmedOff) == 1;
            public void Dispose()
            {
                if (_delayFirstSafetyFailure &&
                    Volatile.Read(ref _failureReleased) == 0)
                    ReleaseFirstSafetyFailure();
                _firstFailureReleaseGate.Dispose();
            }
        }

        private sealed class ProductionManagerFixture : IDisposable
        {
            private readonly string _root;
            private readonly DoController _do;
            private readonly AoController _ao;
            private readonly TwoDeviceAiAcquirer _acq;
            private readonly EpbDiskWriter _diskWriter;
            private readonly DiskWriterRecorderAdapter _recorder;
            private readonly IPowerSupplyCoordinator _powerSupply;
            private readonly ProductionActionOrder _actionOrder;
            private readonly object _receiptGate = new object();
            private readonly List<HighPriorityDoTelemetry> _receipts =
                new List<HighPriorityDoTelemetry>();
            private readonly List<int> _admissionRejectedChannels =
                new List<int>();
            private int _persistenceFlushCallCount;

            internal ProductionManagerFixture(
                IPowerSupplyCoordinator powerSupply = null)
            {
                _root = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "epb-stop-port-seam-" + Guid.NewGuid().ToString("N"));
                var config = new GlobalConfig
                {
                    AO = new AoConfig(),
                    DO = new DoConfig(),
                    Test = new TestConfig
                    {
                        TestName = "StopPortSeam",
                        StoreDir = _root,
                        TestTarget = 1,
                        TestPeriod = 0.1
                    }
                };
                // The production manager derives its affected scope from the
                // same configured DoController used for physical admission.
                config.DO.Epb.Add(new DoEpbRecord
                {
                    Enabled = true,
                    Channel = 4,
                    Pos = "Dev1/port0/line0",
                    Neg = "Dev1/port0/line1",
                    Default = "全关"
                });
                config.DO.Epb.Add(new DoEpbRecord
                {
                    Enabled = true,
                    Channel = 10,
                    Pos = "Dev2/port0/line0",
                    Neg = "Dev2/port0/line1",
                    Default = "全关"
                });
                _do = new DoController(config.DO);
                _do.ConfigureLogicalDeviceContextForAcceptance();
                _powerSupply = powerSupply;
                _actionOrder = (powerSupply as FailingPowerSupply)?.ActionOrder;
                _ao = new AoController(config.AO);
                _acq = new TwoDeviceAiAcquirer(
                    new AiConfigDetail
                    {
                        Records = new List<AiConfigDetailRecord>
                        {
                            new AiConfigDetailRecord
                            {
                                序号 = 1,
                                物理通道 = "Dev1/ai0",
                                参数名 = "EPB4_current",
                                单位 = "A",
                                变换斜率 = 1,
                                是否启用 = 1
                            },
                            new AiConfigDetailRecord
                            {
                                序号 = 2,
                                物理通道 = "Dev2/ai0",
                                参数名 = "EPB10_current",
                                单位 = "A",
                                变换斜率 = 1,
                                是否启用 = 1
                            }
                        }
                    },
                    2000,
                    100,
                    3);
                Manager = new EpbManager(
                    config,
                    _do,
                    _ao,
                    _acq,
                    Config.NullLogger.Instance,
                    powerSupply: powerSupply);
                _do.HighPriorityOffCompleted += telemetry =>
                {
                    if (telemetry == null) return;
                    _actionOrder?.Next("DO-receipt", telemetry.CommandId);
                    lock (_receiptGate)
                    {
                        _receipts.Add(telemetry);
                        Monitor.PulseAll(_receiptGate);
                    }
                };
                // The stop scope is taken from the same runtime energized
                // edge used by production Forward/Reverse/OFF paths.
                Manager.ObserveEnergizationEdge(4, true);
                Manager.ObserveEnergizationEdge(10, true);
                var dataRoot = System.IO.Path.Combine(_root, "Data");
                _diskWriter = new EpbDiskWriter(new DataRetentionPolicy
                {
                    DataStorePath = dataRoot,
                    IndexAndExportPath = dataRoot,
                    FileSizeMb = 1,
                    UseMemoryMapped = false
                });
                _recorder = new DiskWriterRecorderAdapter(_diskWriter);
                Manager.Recorder = _recorder;
                // The host normally registers this raw/persistence drain callback
                // during startup.  Keep the production StopAll path intact while
                // supplying that one host-owned boundary for the seam fixture.
                Manager.RegisterPausePersistenceFlush(
                    (boundaries, token) =>
                    {
                        Interlocked.Increment(ref _persistenceFlushCallCount);
                        return Task.CompletedTask;
                    });
            }

            internal EpbManager Manager { get; }

            internal IReadOnlyList<HighPriorityDoTelemetry> Receipts
            {
                get
                {
                    lock (_receiptGate)
                        return _receipts.ToArray();
                }
            }

            internal int PersistenceFlushCallCount =>
                Volatile.Read(ref _persistenceFlushCallCount);

            internal int PowerDisableCallCount =>
                (_powerSupply as FailingPowerSupply)?.DisableCallCount ?? 0;

            internal bool WaitForReceiptCountAtLeast(
                int count,
                TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                lock (_receiptGate)
                {
                    while (_receipts.Count < count)
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero ||
                            !Monitor.Wait(_receiptGate, remaining))
                            break;
                    }
                    return _receipts.Count >= count;
                }
            }

            internal bool WaitForSuccessfulReceiptChannels(
                IReadOnlyCollection<int> channels,
                TimeSpan timeout)
            {
                if (channels == null || channels.Count == 0) return true;
                var required = new HashSet<int>(channels);
                var deadline = DateTime.UtcNow + timeout;
                lock (_receiptGate)
                {
                    while (!required.All(channel => _receipts.Any(item =>
                               item != null && item.Channel == channel &&
                               item.Result && item.CommandId != Guid.Empty)))
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero ||
                            !Monitor.Wait(_receiptGate, remaining))
                            break;
                    }
                    return required.All(channel => _receipts.Any(item =>
                        item != null && item.Channel == channel &&
                        item.Result && item.CommandId != Guid.Empty));
                }
            }

            internal bool SawAdmissionReject(int channel)
            {
                lock (_receiptGate)
                    return _admissionRejectedChannels.Contains(channel);
            }

            internal DoController Do => _do;

            internal AoController Ao => _ao;

            internal void ConfigurePhysicalOff(IHighPriorityOffPhysicalWriter writer)
            {
                _do.HighPriorityOffPhysicalWriter = writer;
            }

            internal bool TrySubmitPhysicalOff(
                int channel,
                Action<HighPriorityDoTelemetry> completion,
                out Guid commandId)
            {
                var accepted = _do.TrySubmitEpbOffHighPriority(
                    channel, completion, out commandId);
                if (!accepted)
                {
                    lock (_receiptGate)
                    {
                        _admissionRejectedChannels.Add(channel);
                        Monitor.PulseAll(_receiptGate);
                    }
                }
                return accepted;
            }

            public void Dispose()
            {
                try { Manager?.ReleaseHardwareForRestart(); } catch { }
                try { _acq?.Dispose(); } catch { }
                try { _diskWriter?.Dispose(); } catch { }
                try { _ao?.Dispose(); } catch { }
                try { _do?.Dispose(); } catch { }
                try
                {
                    if (System.IO.Directory.Exists(_root))
                        System.IO.Directory.Delete(_root, true);
                }
                catch { }
            }
        }

        private sealed class RecordingAoLogger : Config.IAppLogger
        {
            private readonly object _gate = new object();
            private readonly List<string> _warnings = new List<string>();
            private readonly List<string> _errors = new List<string>();

            internal string[] Warnings
            {
                get { lock (_gate) return _warnings.ToArray(); }
            }

            internal string[] Errors
            {
                get { lock (_gate) return _errors.ToArray(); }
            }

            public void Info(string message, string category = null) { }

            public void Warn(string message, string category = null)
            {
                lock (_gate) _warnings.Add(message ?? string.Empty);
            }

            public void Error(string message, string category = null, Exception ex = null)
            {
                lock (_gate) _errors.Add(message ?? string.Empty);
            }

            internal void Clear()
            {
                lock (_gate)
                {
                    _warnings.Clear();
                    _errors.Clear();
                }
            }
        }

        private sealed class ManualClock : IStopSafetyClock
        {
            private readonly object _gate = new object();
            private readonly List<ScheduledDelay> _delays = new List<ScheduledDelay>();
            private DateTime _now = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

            public DateTime UtcNow
            {
                get { lock (_gate) return _now; }
            }

            public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    if (delay <= TimeSpan.Zero)
                        return Task.CompletedTask;
                    var item = new ScheduledDelay(_now + delay, cancellationToken);
                    _delays.Add(item);
                    return item.Task;
                }
            }

            internal void Advance(TimeSpan by)
            {
                AdvanceCore(by, releaseDueDelays: true);
            }

            /// <summary>
            /// Advances the production clock while retaining due runner
            /// delays.  Acceptance tests can therefore evaluate a real
            /// deadline tick before allowing the supervised runner to resume.
            /// </summary>
            internal void AdvanceWithoutReleasingDueDelays(TimeSpan by)
            {
                AdvanceCore(by, releaseDueDelays: false);
            }

            internal void ReleaseDueDelays()
            {
                List<ScheduledDelay> due;
                lock (_gate)
                {
                    due = _delays.Where(item => item.DueUtc <= _now).ToList();
                    foreach (var item in due) _delays.Remove(item);
                }
                foreach (var item in due) item.TrySetCompleted();
            }

            private void AdvanceCore(TimeSpan by, bool releaseDueDelays)
            {
                List<ScheduledDelay> due;
                lock (_gate)
                {
                    _now += by;
                    due = releaseDueDelays
                        ? _delays.Where(item => item.DueUtc <= _now).ToList()
                        : new List<ScheduledDelay>();
                    if (releaseDueDelays)
                        foreach (var item in due) _delays.Remove(item);
                }
                if (releaseDueDelays)
                    foreach (var item in due) item.TrySetCompleted();
            }

            private sealed class ScheduledDelay
            {
                private readonly TaskCompletionSource<bool> _completion =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                private readonly CancellationToken _cancellation;

                internal ScheduledDelay(DateTime dueUtc, CancellationToken cancellation)
                {
                    DueUtc = dueUtc;
                    _cancellation = cancellation;
                }

                internal DateTime DueUtc { get; }
                internal Task Task => _completion.Task;

                internal void TrySetCompleted()
                {
                    if (_cancellation.IsCancellationRequested)
                        _completion.TrySetCanceled();
                    else
                        _completion.TrySetResult(true);
                }
            }
        }
    }
}
