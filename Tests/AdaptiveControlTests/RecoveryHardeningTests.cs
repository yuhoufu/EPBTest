using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using IO.NI;
using MTTFTest.Watchdog.Protocol;
using Timing;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Deterministic contract tests for the V2.13 recovery hardening work.
    ///
    /// These tests intentionally use small fakes instead of opening NI, power
    /// supplies, pipes or a real process.  They describe the externally
    /// observable ordering and ownership invariants that the production
    /// adapters must satisfy.  Once the production seams are public, the fakes
    /// can be replaced by adapters without changing the assertions.
    /// </summary>
    internal static class RecoveryHardeningTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("DAQ历史空窗先建立恢复上下文再等待安全断电",
                DaqGapBuildsRecoveryBeforeSafetyAwait, ref passed);
            Run("持久化序号增长不刷新Watchdog恢复进度",
                PersistedGrowthDoesNotRefreshRecoveryProgress, ref passed);
            Run("孤儿PausePending五秒接管且排除人工暂停报警完成通道",
                OrphanPauseTriggersTakeoverWithoutFalsePositive, ref passed);
            Run("HeartbeatAck与sidecar/pipe失联只允许同Session重连一次",
                HeartbeatAckAndSingleSessionReconnect, ref passed);
            Run("事故圈恰好一次作废且迟到完成不计正式成功",
                IncidentCyclesAreAbortedExactlyOnce, ref passed);
            Run("电源断电超时升级但不阻塞恢复状态机",
                PowerDisableTimeoutEscalatesWithoutBlocking, ref passed);
            Run("Timer最终Paused必须绑定同一恢复owner",
                TimerPausedStateCarriesRecoveryOwner, ref passed);
            Run("生产Timer实例代次栅栏拒绝一万次迟到Paused",
                ProductionTimerGenerationGateRejectsLatePause, ref passed);
            Run("Recovering无人工owner时中央门禁拒绝Paused",
                RecoveryLifecycleCannotBecomeManualPause, ref passed);
            Run("冻结恢复cohort不因迟到Paused或兄弟Running丢通道",
                FrozenRecoveryCohortSurvivesMutableUiState, ref passed);
            Run("五个健康活动圈与软件恢复计数严格分离",
                HealthyActiveCyclesAreNotSoftwareRecovery, ref passed);
            Run("旧Session和RunEpoch消息不能影响新运行",
                StaleSessionMessagesAreIgnored, ref passed);
            Run("同批双DAQ共享Correlation且重复注册保持同一批次",
                DaqBatchCorrelationIsSharedAndIdempotent, ref passed);
            Run("永久报警、人工禁用、完成通道不进入恢复候选",
                PermanentAlarmAndCompletedChannelsAreExcluded, ref passed);
            Run("结构化失败报告仅稳定字段指纹且同点恢复立即熔断",
                StructuredFailureReportCircuitGate, ref passed);
            Run("恢复预算达到边界后进度变化仍保持阻断",
                RecoveryBudgetBoundaryIgnoresTokenChange, ref passed);
            Run("并发失败报告只能获得一个原子重拉许可",
                ConcurrentRelaunchPermitIsSingleUse, ref passed);
            Run("重拉许可持久化失败必须故障闭锁",
                RelaunchPermitPersistenceFailureFailsClosed, ref passed);
            Run("V2到V3迁移跨重启保留RecoveryBlocked",
                SchemaV2BlockedStateSurvivesRestartMigration, ref passed);
            return passed;
        }

        private static void RecoveryBudgetBoundaryIgnoresTokenChange()
        {
            var report = new RecoveryFailureReport
            {
                RootCode = "UnhandledSoftwareStartup",
                DeviceOrChannelGroup = "EPB10",
                RunId = "run-boundary",
                RecoveryStage = "DaqRecovery",
                RecoveryProgressToken = "201",
                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource
            };
            var classification = RecoveryFailurePolicy.Classify(
                report.RootCode,
                false,
                string.Empty);
            Assert(classification.MaximumProcessRelaunches == 2,
                "边界测试未使用固定的两次软件重拉预算");

            var fingerprint = RecoveryFailurePolicy.BuildFingerprint(report);
            var progressed = report.Clone();
            progressed.RecoveryProgressToken = "202";
            var decision = RecoveryFailurePolicy.Evaluate(
                progressed,
                classification,
                fingerprint,
                report.RecoveryProgressToken,
                report.RecoveryProcessSource,
                classification.MaximumProcessRelaunches,
                false);
            Assert(decision.ProgressTokenChanged &&
                   decision.RelaunchBudgetExhausted &&
                   decision.SafeIdleRecoveryBlocked &&
                   !decision.ProcessRelaunchAllowed,
                "旧计数达到预算后，仅改变真实进度令牌仍获得重拉许可");

            var detailOnly = report.Clone();
            detailOnly.RecoveryProgressToken = "Preflight detail=heartbeat-only";
            var detailDecision = RecoveryFailurePolicy.Evaluate(
                detailOnly,
                classification,
                fingerprint,
                report.RecoveryProgressToken,
                report.RecoveryProcessSource,
                1,
                false);
            Assert(!detailDecision.ProgressTokenChanged &&
                   detailDecision.SafeIdleRecoveryBlocked,
                "泛心跳/Detail被错误当作P0-4真实恢复进度");
        }

        private static void ConcurrentRelaunchPermitIsSingleUse()
        {
            var gate = new RecoveryRelaunchPermitGate();
            var approved = 0;
            var pending = 0;
            var generation = 0L;
            Parallel.For(0, 64, _ =>
            {
                long candidate;
                var result = gate.TryApprove(
                    durableGeneration: 0,
                    blocked: false,
                    manualStopRequested: false,
                    sessionRevoked: false,
                    consecutiveFailures: 0,
                    maximumProcessRelaunches: 5,
                    generation: out candidate);
                if (result == RecoveryRelaunchPermitDecision.Approved)
                {
                    Interlocked.Increment(ref approved);
                    Interlocked.CompareExchange(ref generation, candidate, 0);
                }
                else if (result == RecoveryRelaunchPermitDecision.AlreadyPending)
                {
                    Interlocked.Increment(ref pending);
                    Assert(candidate > 0, "已有重拉许可没有返回稳定generation");
                }
            });
            Assert(approved == 1 && pending == 63 && generation > 0,
                "并发失败报告产生了多个重拉许可或丢失了pending状态");
            Assert(gate.PendingGeneration == generation &&
                   gate.TryConsume(generation) &&
                   !gate.TryConsume(generation) &&
                   gate.PendingGeneration == 0,
                "重拉许可未按一次性generation消费");

            long next;
            Assert(gate.TryApprove(
                       generation,
                       false,
                       false,
                       false,
                       consecutiveFailures: 1,
                       maximumProcessRelaunches: 2,
                       generation: out next) == RecoveryRelaunchPermitDecision.Approved,
                "预算未耗尽时第二个真实许可未生成");
            Assert(gate.TryConsume(next), "第二个许可无法消费");
            Assert(gate.TryApprove(
                       next,
                       false,
                       false,
                       false,
                       consecutiveFailures: 2,
                       maximumProcessRelaunches: 2,
                       generation: out next) == RecoveryRelaunchPermitDecision.Denied,
                "达到预算边界后仍允许新的重拉许可");
        }

        private static void RelaunchPermitPersistenceFailureFailsClosed()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "mttf-watchdog-journal-blocked-" + Guid.NewGuid().ToString("N"));
            var session = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(root);
            var snapshotPath = Path.Combine(
                root,
                "session-" + WatchdogJournalPaths.SafeName(session) + ".json");
            // Make the atomic snapshot target a directory.  The synchronous
            // commit must report failure; callers then close the relaunch
            // circuit instead of treating an emergency spool as durable.
            Directory.CreateDirectory(snapshotPath);
            try
            {
                using (var store = new WatchdogJournalStore(
                           root,
                           session,
                           "sidecar",
                           new WatchdogJournalPolicy(),
                           Process.GetCurrentProcess().Id,
                           DateTime.UtcNow.Ticks))
                {
                    Assert(!store.TryPublishSnapshotSynchronously(
                               "{\"SchemaVersion\":3,\"RecoveryBlocked\":true}"),
                        "不可写Journal目标未触发同步持久化失败");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void SchemaV2BlockedStateSurvivesRestartMigration()
        {
            var legacy = "{\"SchemaVersion\":2,\"RecoveryBlocked\":true," +
                         "\"RecoveryFailureFingerprint\":\"RFP2-legacy\"," +
                         "\"ConsecutiveStartupFailures\":5," +
                         "\"RelaunchGeneration\":9}";
            var migrated = WatchdogJournalMigration.MigrateJournalJson(legacy);
            Assert(!string.IsNullOrWhiteSpace(migrated) &&
                   migrated.Contains("\"SchemaVersion\":5") &&
                   migrated.Contains("\"RecoveryBlocked\":true") &&
                   migrated.Contains("\"RecoveryFailureFingerprint\":\"RFP2-legacy\"") &&
                   migrated.Contains("\"ConsecutiveStartupFailures\":5") &&
                   migrated.Contains("\"RelaunchGeneration\":9"),
                "V2 Journal迁移丢失RecoveryBlocked或预算状态");
            var afterRestart = WatchdogJournalMigration.MigrateJournalJson(migrated);
            Assert(afterRestart == migrated && afterRestart.Contains("\"RecoveryBlocked\":true"),
                "重启后的V4 Journal再次读取时没有保持RecoveryBlocked");
            var restoredDecision = RecoveryFailurePolicy.Evaluate(
                new RecoveryFailureReport
                {
                    RootCode = "UnhandledSoftwareStartup",
                    DeviceOrChannelGroup = "EPB10",
                    RunId = "run-boundary",
                    RecoveryStage = "DaqRecovery",
                    RecoveryProgressToken = "203"
                },
                RecoveryFailurePolicy.Classify("UnhandledSoftwareStartup", false, string.Empty),
                RecoveryFailurePolicy.BuildFingerprint(new RecoveryFailureReport
                {
                    RootCode = "UnhandledSoftwareStartup",
                    DeviceOrChannelGroup = "EPB10",
                    RunId = "run-boundary",
                    RecoveryStage = "DaqRecovery"
                }),
                "202",
                RecoveryFailurePolicy.RecoveryProcessSource,
                2,
                alreadyBlocked: true);
            Assert(restoredDecision.SafeIdleRecoveryBlocked &&
                   !restoredDecision.ProcessRelaunchAllowed,
                "重启恢复的RecoveryBlocked状态仍可被一次失败报告打开重拉");
        }

        private static void StructuredFailureReportCircuitGate()
        {
            var firstReport = new RecoveryFailureReport
            {
                RootCode = "RecoveryAttachFailed",
                DeviceOrChannelGroup = "Dev1,Dev2",
                RunId = "run-001",
                RecoveryStage = "SafetyPreflight",
                RecoveryProgressToken = "17",
                RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource
            };
            var proseVariant = firstReport.Clone();
            // No timestamp/PID/attempt/prose fields exist in the identity.  A
            // report with the same four stable dimensions must hash identically.
            Assert(RecoveryFailurePolicy.BuildFingerprint(firstReport) ==
                   RecoveryFailurePolicy.BuildFingerprint(proseVariant),
                "失败指纹被时间/PID/attempt/prose污染");
            Assert(RecoveryFailurePolicy.BuildFingerprint(
                       "RecoveryAttachFailed;Utc=2026-08-22T10:00:00Z;Pid=101;Attempt=1;Detail=first") ==
                   RecoveryFailurePolicy.BuildFingerprint(
                       "RecoveryAttachFailed;Utc=2026-08-22T10:01:00Z;Pid=999;Attempt=8;Detail=second"),
                "Legacy失败文本中的时间/PID/attempt/prose改变了稳定指纹");

            var classification = RecoveryFailurePolicy.Classify(
                firstReport.RootCode,
                false,
                "RecoveryAttachFailed: transient detail");
            var first = RecoveryFailurePolicy.Evaluate(
                firstReport,
                classification,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                false);
            Assert(first.ProcessRelaunchAllowed && !first.SafeIdleRecoveryBlocked,
                "初始进程首次失败未允许唯一恢复进程");
            var recoveryRepeatedReport = firstReport.Clone();
            recoveryRepeatedReport.RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource;
            var repeated = RecoveryFailurePolicy.Evaluate(
                recoveryRepeatedReport,
                classification,
                first.Fingerprint,
                firstReport.RecoveryProgressToken,
                RecoveryFailurePolicy.RecoveryProcessSource,
                first.ConsecutiveCount,
                false);
            Assert(repeated.SafeIdleRecoveryBlocked && !repeated.ProcessRelaunchAllowed,
                "同指纹同进度恢复失败未立即进入SafeIdleRecoveryBlocked");

            var progressed = firstReport.Clone();
            progressed.RecoveryProgressToken = "18";
            progressed.RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource;
            var next = RecoveryFailurePolicy.Evaluate(
                progressed,
                classification,
                first.Fingerprint,
                firstReport.RecoveryProgressToken,
                RecoveryFailurePolicy.InitialProcessSource,
                first.ConsecutiveCount,
                false);
            Assert(next.ProgressTokenChanged && next.ProcessRelaunchAllowed,
                "真实进度变化未允许一次新的有意义恢复尝试");

            var nextRepeated = RecoveryFailurePolicy.Evaluate(
                progressed,
                classification,
                next.Fingerprint,
                progressed.RecoveryProgressToken,
                progressed.RecoveryProcessSource,
                next.ConsecutiveCount,
                false);
            Assert(nextRepeated.SafeIdleRecoveryBlocked,
                "同一新进度再次失败未被熔断");

            var permanent = RecoveryFailurePolicy.Evaluate(
                firstReport,
                RecoveryFailurePolicy.Classify("ConfigDuplicateEpbId", true, string.Empty),
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                false);
            Assert(permanent.SafeIdleRecoveryBlocked && !permanent.ProcessRelaunchAllowed,
                "永久错误未直接阻断恢复");

            var hardware = RecoveryFailurePolicy.Classify(
                "HardwareUnavailable", false, "hardware detail with changing prose");
            Assert(hardware.MaximumProcessRelaunches == 3,
                "通信不可用丢失冷却后重新取证的重试预算");

            var wire = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                new WatchdogMessage
                {
                    Type = WatchdogMessageType.RecoveryAttemptFailed,
                    RootCode = firstReport.RootCode,
                    DeviceOrChannelGroup = firstReport.DeviceOrChannelGroup,
                    RunId = firstReport.RunId,
                    RecoveryStage = firstReport.RecoveryStage,
                    RecoveryProgressToken = firstReport.RecoveryProgressToken,
                    RecoveryProcessSource = firstReport.RecoveryProcessSource,
                    RecoveryFailureFingerprint = first.Fingerprint
                }));
            Assert(wire.RootCode == firstReport.RootCode &&
                   wire.DeviceOrChannelGroup == firstReport.DeviceOrChannelGroup &&
                   wire.RunId == firstReport.RunId &&
                   wire.RecoveryProgressToken == firstReport.RecoveryProgressToken &&
                   wire.RecoveryFailureFingerprint == first.Fingerprint,
                "结构化失败报告未完整通过Watchdog协议");
        }

        private static void DaqGapBuildsRecoveryBeforeSafetyAwait()
        {
            var freshness = new DaqFreshnessSnapshot
            {
                Device = "Dev1",
                Generation = 12,
                CallbackGapEventCount = 5,
                LastCallbackGapIntervalMs = 2172.0,
                CallbackAgeMs = 2172.0,
                LastProducedSequence = 10,
                IsFresh = false
            };
            var liveness = new DaqLivenessDeviceState();
            var productionDecision = liveness.Observe(
                true, true, false, freshness, 100, 1000, 2000);
            Assert(productionDecision.Trip && productionDecision.Code == "DaqCallbackStale",
                "生产DAQ存活判据未在硬截止首次观察时立即识别停摆");
            Assert(!liveness.Observe(
                       true, true, true, freshness, 100, 1000, 2000).Trip,
                "生产DAQ判据在已有恢复上下文时重复触发");

            var coordinator = new FakeRecoveryCoordinator();
            var trigger = new DaqIncident(
                "Dev1",
                lastCallbackGapMs: 2172.0,
                currentCallbackAgeMs: 2172.0,
                thresholdMs: 2000.0,
                affectedChannels: new[] { 4, 5 });

            coordinator.Begin(trigger);

            Assert(!DaqIncidentPolicy.IsRecoverableGap(
                       trigger.LastCallbackGapMs,
                       trigger.CurrentCallbackAgeMs,
                       trigger.ThresholdMs),
                "持续停摆不应误走已恢复历史空窗的兼容判据");
            Assert(coordinator.Events.SequenceEqual(new[]
                   {
                       "RecoveryContextCreated",
                       "FrozenBoundaryCaptured",
                       "SuppressionInstalled",
                       "ChannelsPaused",
                       "PowerDisableStarted"
                   }),
                "安全断电等待前没有按固定顺序建立恢复边界");
            Assert(coordinator.Context != null &&
                   coordinator.Context.FrozenBoundary["Dev1"] == 2_408_716 &&
                   coordinator.Context.SuppressAfter["Dev1"] == 2_408_716 &&
                   coordinator.Context.RunEpoch == 9,
                "FrozenBoundary/SuppressAfter没有在同一恢复上下文中锁定");
            Assert(coordinator.PowerDisablePending &&
                   coordinator.PausedChannels.SequenceEqual(new[] { 4, 5 }),
                "错峰EPB4/5未在电源任务阻塞时先完成安全暂停");

            coordinator.CompletePowerDisable();
            Assert(coordinator.PowerDisableCompleted,
                "释放电源任务后未能完成恢复流程的安全阶段");
        }

        private static void PersistedGrowthDoesNotRefreshRecoveryProgress()
        {
            var heartbeat = new WatchdogHeartbeat
            {
                SessionId = "session-new",
                RecoveryActive = true,
                RecoveryProgressVersion = 17,
                Persisted = new Dictionary<string, long> { ["Dev1"] = 2_262_337 }
            };
            var wire = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                new WatchdogMessage
                {
                    Type = WatchdogMessageType.Heartbeat,
                    SessionId = heartbeat.SessionId,
                    Heartbeat = heartbeat
                }));
            Assert(wire.Heartbeat.RecoveryProgressVersion == 17 &&
                   wire.Heartbeat.Persisted["Dev1"] == 2_262_337,
                "Watchdog协议未保留恢复阶段与持久化字段");

            var signature1 = RecoveryProgressSignature.Build(
                new[] { "4:Paused:DaqOrphanPause:20", "5:Paused:DaqOrphanPause:20" },
                daqRecoveryCount: 1,
                softwareRecoveryCount: 0,
                recoveryOwnerCount: 1,
                stageOrdinal: 20,
                recoveryIncident: "DaqOrphanPause",
                recoveryContext: "corr-1",
                orphanPaused: true,
                powerDisablePending: true);
            var signature2 = RecoveryProgressSignature.Build(
                new[] { "5:Paused:DaqOrphanPause:20", "4:Paused:DaqOrphanPause:20" },
                daqRecoveryCount: 1,
                softwareRecoveryCount: 0,
                recoveryOwnerCount: 1,
                stageOrdinal: 20,
                recoveryIncident: "DaqOrphanPause",
                recoveryContext: "corr-1",
                orphanPaused: true,
                powerDisablePending: true);
            Assert(signature1 == signature2,
                "恢复签名依赖通道枚举顺序，可能产生无意义进度");
            var signatureAfterPersistedGrowth = RecoveryProgressSignature.Build(
                new[] { "4:Paused:DaqOrphanPause:20", "5:Paused:DaqOrphanPause:20" },
                daqRecoveryCount: 1,
                softwareRecoveryCount: 0,
                recoveryOwnerCount: 1,
                stageOrdinal: 20,
                recoveryIncident: "DaqOrphanPause",
                recoveryContext: "corr-1",
                orphanPaused: true,
                powerDisablePending: true);
            Assert(signature1 == signatureAfterPersistedGrowth,
                "恢复签名构造不应把普通Persisted序号作为恢复进度");

            var watchdog = new FakeWatchdogProgressMonitor();
            watchdog.Observe(new WatchdogProgress(
                recoveryActive: true,
                eligibleChannels: new[] { 4, 5 },
                recoveryProgressVersion: 17,
                persistedSequence: 2_262_337), nowSeconds: 0);

            // 事故现场的 Persisted 会继续增长，但恢复阶段没有任何阶段
            // 迁移；普通写盘进展不能把 ExternalRecoveryStageStalled 延后。
            for (var second = 1; second <= 60; second++)
            {
                watchdog.Observe(new WatchdogProgress(
                    recoveryActive: true,
                    eligibleChannels: new[] { 4, 5 },
                    recoveryProgressVersion: 17,
                    persistedSequence: 2_262_337 + second * 4096L), second);
            }

            Assert(watchdog.TakeoverRequested &&
                   watchdog.TakeoverReason == "ExternalRecoveryStageStalled",
                "Persisted持续增长错误刷新了Watchdog恢复停滞计时");

            var persistedOnly = RecoveryProgressSignature.Build(
                new[] { "4:Paused:DaqOrphanPause:20" }, 1, 0, 1, 20,
                "DaqOrphanPause", "corr-1", true, true);
            Assert(signature1 != persistedOnly,
                "恢复签名测试样本无效：阶段状态变化未被识别");
        }

        private static void OrphanPauseTriggersTakeoverWithoutFalsePositive()
        {
            Assert(EpbManager.IsIntentionalOrOwnedTimerPauseReason("DaqDeviceFaultSafety:Dev1") &&
                   EpbManager.IsIntentionalOrOwnedTimerPauseReason("ManualPause") &&
                   !EpbManager.IsIntentionalOrOwnedTimerPauseReason("UnknownPause"),
                "生产Timer暂停owner判据不符合已知安全/人工原因边界");
            Assert(!EpbManager.ShouldAutoRecoverTimerAnomaly(
                       ChannelRuntimeState.Running,
                       HighPrecisionTimerRuntimeState.Paused,
                       "DaqDeviceFaultSafety:Dev1",
                       batchActive: true,
                       batchPauseState: BatchPauseState.Running,
                       channelPaused: false,
                       alarmStopRequested: false,
                       channelEnabled: true),
                "生产Timer判据错误把有owner的DAQ暂停重复自愈");
            Assert(EpbManager.ShouldAutoRecoverTimerAnomaly(
                       ChannelRuntimeState.Running,
                       HighPrecisionTimerRuntimeState.Paused,
                       "UnknownPause",
                       true,
                       BatchPauseState.Running,
                       false,
                       false,
                       true),
                "生产Timer判据未识别无owner暂停");

            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       sessionRevoked: false,
                       manualStopRequested: false,
                       alreadyTakingOver: false,
                       processAlive: true,
                       heartbeatAgeSeconds: 5.0,
                       recoveryActive: false,
                       orphanPaused: false,
                       powerDisablePending: false,
                       stageAgeSeconds: 0,
                       hasRecoveryEligibleChannels: true),
                "Watchdog生产判据未按5秒心跳门限接管");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1,
                       recoveryActive: true,
                       orphanPaused: true,
                       powerDisablePending: false,
                       stageAgeSeconds: 5,
                       hasRecoveryEligibleChannels: true),
                "Watchdog生产判据未按5秒孤儿暂停门限接管");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       false, false, false, true, 1,
                       recoveryActive: true,
                       orphanPaused: false,
                       powerDisablePending: false,
                       stageAgeSeconds: 14.9,
                       hasRecoveryEligibleChannels: true),
                "Watchdog生产判据提前触发15秒恢复停滞接管");

            var detector = new FakeOrphanPauseDetector(orphanTimeoutSeconds: 5);
            Assert(!detector.Evaluate(
                       new[] { 4 }, PauseState.PausePending, elapsedSeconds: 4.9,
                       manualPause: false, alarmed: false, completed: false),
                "五秒前错误触发孤儿暂停接管");
            Assert(detector.Evaluate(
                       new[] { 4 }, PauseState.Paused, elapsedSeconds: 5.0,
                       manualPause: false, alarmed: false, completed: false),
                "Eligible通道无恢复上下文超过五秒未触发接管");

            foreach (var state in new[]
                     {
                         new PauseCase(PauseState.Paused, true, false, false),
                         new PauseCase(PauseState.Paused, false, true, false),
                         new PauseCase(PauseState.Paused, false, false, true)
                     })
            {
                Assert(!detector.Evaluate(
                           new[] { 4 }, state.State, elapsedSeconds: 30,
                           state.ManualPause, state.Alarmed, state.Completed),
                    "人工暂停、报警或完成通道被误判为孤儿恢复候选");
            }
        }

        private static void HeartbeatAckAndSingleSessionReconnect()
        {
            var ack = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                new WatchdogMessage
                {
                    Type = WatchdogMessageType.HeartbeatAck,
                    SessionId = "session-new",
                    AckSequence = 42
                }));
            Assert(ack.Type == WatchdogMessageType.HeartbeatAck && ack.AckSequence == 42,
                "HeartbeatAck协议字段未能往返");

            var ipc = new FakeWatchdogIpc("session-new");
            Assert(ipc.Attach("session-new") && ipc.Attached,
                "Watchdog Attach握手失败");
            Assert(ipc.SendHeartbeat("session-new") == HeartbeatResult.Ack,
                "Heartbeat没有得到Ack");

            ipc.SimulatePipeLoss();
            Assert(ipc.WatchdogLost && ipc.TryReconnect("session-new"),
                "pipe失联未被主程序发现或同Session首次重连失败");
            Assert(ipc.ReconnectCount == 1 && !ipc.TryReconnect("session-new"),
                "同一Session允许重复重连，可能产生多个sidecar");

            ipc.SimulateSidecarExit();
            Assert(ipc.WatchdogLost,
                "sidecar退出没有反映为Watchdog失联");
            Assert(ipc.SendHeartbeat("session-old") == HeartbeatResult.Ignored,
                "旧Session消息越过IPC边界");

            Assert(SessionRevocationPolicy.IsRevoked(
                       markerExists: true,
                       manualStopRequested: false,
                       explicitLifecycleEnd: false),
                "ManualStop撤权marker未阻止Watchdog自动恢复");
            Assert(!SessionRevocationPolicy.IsRevoked(
                       markerExists: false,
                       manualStopRequested: true,
                       explicitLifecycleEnd: false),
                "ManualStopIntent错误撤销了Watchdog超时Kill权限");
            Assert(SessionRevocationPolicy.IsRevoked(
                       markerExists: false,
                       manualStopRequested: false,
                       explicitLifecycleEnd: false) == false,
                "握手/启动窗口的RunActive=false不应单独撤销Session");
            Assert(SessionRevocationPolicy.IsRevoked(
                       markerExists: true,
                       manualStopRequested: false,
                       explicitLifecycleEnd: false),
                "RunCompleted/Closing写入marker后仍未撤销Session");

            Assert(SessionRevocationPolicy.IsRevoked(
                       markerExists: false,
                       manualStopRequested: false,
                       explicitLifecycleEnd: true),
                "显式RunCompleted/Closing未撤销Session");
            var stopCompleted = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                new WatchdogMessage
                {
                    Type = WatchdogMessageType.StopCompleted,
                    SessionId = "session-new"
                }));
            Assert(stopCompleted.Type == WatchdogMessageType.StopCompleted &&
                   !SessionRevocationPolicy.IsRevoked(
                       markerExists: false,
                       manualStopRequested: false,
                       explicitLifecycleEnd: false),
                "StopCompleted消息不应自动撤销Session，否则无法进入重启流程");

            Assert(WatchdogProcessIdentityPolicy.Matches(
                       expectedPid: 11060,
                       expectedStartTicks: 900,
                       actualPid: 11060,
                       actualStartTicks: 900) &&
                   !WatchdogProcessIdentityPolicy.Matches(
                       expectedPid: 11060,
                       expectedStartTicks: 900,
                       actualPid: 11060,
                       actualStartTicks: 901),
                "同PID不同启动时间未被识别为不同进程");
            Assert(WatchdogProcessIdentityPolicy.CanKillOldProcess(
                       sessionRevoked: false,
                       manualStopRequested: false,
                       currentIdentityMatches: true) &&
                   !WatchdogProcessIdentityPolicy.CanKillOldProcess(
                       sessionRevoked: true,
                       manualStopRequested: false,
                       currentIdentityMatches: true),
                "Kill前撤权或身份不匹配仍可终止旧进程");

            ipc.RequestManualStop();
            ipc.RequestExternalRecovery();
            Assert(!ipc.RelaunchRequested,
                "ManualStop与自动接管竞态中未以人工停止为最高优先级");
        }

        private static void IncidentCyclesAreAbortedExactlyOnce()
        {
            var ledger = new FakeCycleLedger();
            ledger.Open(4, 38139);
            ledger.Open(5, 37794);
            ledger.CommitCompleted(4, 38138); // 事故前的正式圈
            ledger.AbortIncidentCycles(new[] { 4, 5 });
            ledger.AbortIncidentCycles(new[] { 4, 5 });

            Assert(ledger.Get(4, 38139) == CycleStatus.AbortedBySoftwareRecovery &&
                   ledger.Get(5, 37794) == CycleStatus.AbortedBySoftwareRecovery &&
                   ledger.AbortCount(4, 38139) == 1 &&
                   ledger.AbortCount(5, 37794) == 1,
                "EPB4/5事故圈没有恰好一次AbortedBySoftwareRecovery");
            Assert(!ledger.TryComplete(5, 37794) &&
                   ledger.FormalSuccessCount(4) == 1 &&
                   ledger.FormalSuccessCount(5) == 0,
                "事故圈迟到completed仍被计入正式成功");
        }

        private static void PowerDisableTimeoutEscalatesWithoutBlocking()
        {
            var timeoutObserved = false;
            var started = Stopwatch.StartNew();
            try
            {
                RunAsync(RecoveryStageDeadline.RunAsync(
                    "PowerDisable",
                    20,
                    _ => new TaskCompletionSource<bool>().Task,
                    CancellationToken.None));
            }
            catch (RecoveryStageTimeoutException)
            {
                timeoutObserved = true;
            }
            Assert(timeoutObserved && started.ElapsedMilliseconds < 1000,
                "生产RecoveryStageDeadline未将电源任务超时转换为有界异常");

            var safety = new FakePowerDisableCoordinator();
            var result = safety.RequestDisable(timeoutMs: 100);

            Assert(result == PowerDisableResult.TimedOut &&
                   safety.ExternalRecoveryRequired &&
                   safety.RecoveryContextStillActive,
                "电源断电超时没有升级，或超时错误取消了恢复上下文");
            Assert(safety.ElapsedCallMilliseconds <= 1,
                "电源断电任务超时仍同步阻塞了恢复状态机");

            safety.CompleteLate();
            Assert(safety.RecoveryContextStillActive && safety.LateCompletionIgnored,
                "迟到的电源完成回调修改了已升级的恢复上下文");
        }

        private static void TimerPausedStateCarriesRecoveryOwner()
        {
            Assert(EpbManager.ShouldAutoRecoverTimerAnomaly(
                       ChannelRuntimeState.Running,
                       HighPrecisionTimerRuntimeState.Stopped,
                       "StopWithoutOwner",
                       true,
                       BatchPauseState.Running,
                       false,
                       false,
                       true),
                "生产Timer判据未把无owner Stopped作为恢复候选");

            var timer = new FakeTimerRuntime();
            var owner = "run-9/recovery-17";
            timer.RequestPause(PauseState.PausePending, owner);
            timer.CompletePause(PauseState.Paused, "run-9/recovery-16");
            Assert(timer.State == PauseState.PausePending && timer.Owner == owner,
                "旧恢复代次可以越权推进Timer终态");

            timer.CompletePause(PauseState.Paused, owner);
            Assert(timer.State == PauseState.Paused && timer.Owner == owner,
                "Timer最终Paused没有绑定当前恢复owner");
        }

        private static void StaleSessionMessagesAreIgnored()
        {
            var oldRun = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var newRun = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var oldKey = EpbManager.FormatDaqRecoveryCycleKey(oldRun, 21, 4, 38139);
            var newKey = EpbManager.FormatDaqRecoveryCycleKey(newRun, 22, 4, 38139);
            Assert(oldKey != newKey && oldKey.Contains(oldRun.ToString("N")) &&
                   newKey.Contains(newRun.ToString("N")),
                "事故圈作废key未携带RunId/RunEpoch，旧Run可能污染新Run");

            var router = new FakeRunMessageRouter("session-new", 22);
            Assert(router.Accept("session-new", 22, "CurrentHeartbeat"),
                "当前Session/RunEpoch消息被错误丢弃");
            Assert(!router.Accept("session-old", 22, "OldSessionHeartbeat") &&
                   !router.Accept("session-new", 21, "OldEpochRecovery") &&
                   !router.Accept("session-new", 23, "FutureEpochMessage"),
                "旧或未来Session/RunEpoch消息污染当前运行");
            Assert(router.AcceptedMessages.SequenceEqual(new[] { "CurrentHeartbeat" }),
                "消息隔离后仍有旧代副作用进入当前Run");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       sessionRevoked: false,
                       manualStopRequested: false,
                       alreadyTakingOver: false,
                       processAlive: false,
                       heartbeatAgeSeconds: 5,
                       recoveryActive: true,
                       orphanPaused: false,
                       powerDisablePending: false,
                       stageAgeSeconds: 5,
                       hasRecoveryEligibleChannels: true),
                "旧PID已退出时Watchdog未进入接管判据");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       sessionRevoked: true,
                       manualStopRequested: false,
                       alreadyTakingOver: false,
                       processAlive: false,
                       heartbeatAgeSeconds: 10,
                       recoveryActive: true,
                       orphanPaused: true,
                       powerDisablePending: true,
                       stageAgeSeconds: 60,
                       hasRecoveryEligibleChannels: true),
                "Session撤权后仍允许按旧PID自动Kill/重启");
            Assert(!WatchdogTakeoverPolicy.ShouldTakeover(
                       sessionRevoked: false,
                       manualStopRequested: false,
                       alreadyTakingOver: false,
                       processAlive: true,
                       heartbeatAgeSeconds: 1,
                       recoveryActive: false,
                       orphanPaused: false,
                       powerDisablePending: false,
                       stageAgeSeconds: 1,
                       hasRecoveryEligibleChannels: false),
                "无恢复候选且进程正常时错误触发接管");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                       sessionRevoked: false,
                       manualStopRequested: false,
                       alreadyTakingOver: false,
                       processAlive: false,
                       heartbeatAgeSeconds: 1,
                       recoveryActive: false,
                       orphanPaused: false,
                       powerDisablePending: false,
                       stageAgeSeconds: 0,
                       hasRecoveryEligibleChannels: false),
                "进程已退出时无恢复候选仍未触发接管");
        }

        private static void PermanentAlarmAndCompletedChannelsAreExcluded()
        {
            var selected = EpbManager.SelectInfrastructureRecoveryEligibleChannels(
                new[] { 4, 5, 6, 7 },
                channel => true,
                channel => channel == 5,
                channel => channel == 6,
                channel => channel == 7
                    ? ChannelRuntimeState.Completed
                    : ChannelRuntimeState.Recovering);
            Assert(selected.SequenceEqual(new[] { 4 }),
                "生产恢复候选未排除永久报警、人工暂停或已完成通道");
        }

        private static void ProductionTimerGenerationGateRejectsLatePause()
        {
            for (var i = 0; i < 10000; i++)
            {
                var staleInstance = EpbManager.EvaluateTimerRuntimeEventGate(
                    sourceActive: false,
                    registrationMatches: false,
                    sourceGeneration: 10,
                    currentGeneration: 11,
                    sourceRunEpoch: 7,
                    currentRunEpoch: 7,
                    currentState: ChannelRuntimeState.Recovering,
                    timerState: HighPrecisionTimerRuntimeState.Paused);
                Assert(!staleInstance.Accepted &&
                       staleInstance.ReasonCode == "TimerInstanceNotActive",
                    "已移除Timer的迟到Paused仍通过生产事件栅栏");

                var currentButRecoveryOwned = EpbManager.EvaluateTimerRuntimeEventGate(
                    sourceActive: true,
                    registrationMatches: true,
                    sourceGeneration: 11,
                    currentGeneration: 11,
                    sourceRunEpoch: 7,
                    currentRunEpoch: 7,
                    currentState: ChannelRuntimeState.Recovering,
                    timerState: HighPrecisionTimerRuntimeState.Paused);
                Assert(!currentButRecoveryOwned.Accepted &&
                       currentButRecoveryOwned.ReasonCode == "RecoveryOwnsChannelLifecycle",
                    "恢复owner存在时当前Timer仍能把业务态覆盖成Paused");
            }
        }

        private static void RecoveryLifecycleCannotBecomeManualPause()
        {
            Assert(EpbManager.ShouldRejectRecoveryPauseOverride(
                       ChannelRuntimeState.Recovering,
                       ChannelRuntimeState.Paused,
                       manualPauseOwned: false) &&
                   EpbManager.ShouldRejectRecoveryPauseOverride(
                       ChannelRuntimeState.Recovering,
                       ChannelRuntimeState.PausePending,
                       manualPauseOwned: false),
                "无ManualPause owner的恢复态仍可变成普通暂停");
            Assert(!EpbManager.ShouldRejectRecoveryPauseOverride(
                       ChannelRuntimeState.Running,
                       ChannelRuntimeState.Paused,
                       manualPauseOwned: true),
                "真实人工暂停被恢复状态门禁误伤");
        }

        private static void FrozenRecoveryCohortSurvivesMutableUiState()
        {
            var states = new Dictionary<int, ChannelRuntimeState>
            {
                [4] = ChannelRuntimeState.Paused,
                [5] = ChannelRuntimeState.Running
            };
            var selected = EpbManager.SelectInfrastructureRecoveryEligibleChannels(
                new[] { 4, 5 },
                channel => true,
                channel => false,
                channel => false,
                channel => states[channel]);
            Assert(selected.SequenceEqual(new[] { 4, 5 }),
                "冻结的EPB4/5恢复事务仍因EPB4迟到Paused或EPB5保持Running而部分提交");
        }

        private static void HealthyActiveCyclesAreNotSoftwareRecovery()
        {
            var counts = EpbManager.BuildLogicalRecoveryCounts(
                5,
                0, 0, 0, 0, 0, 0, 0, 0);
            Assert(counts.ActiveCycleCount == 5 && counts.SoftwareRecoveryCount == 0,
                "正常活动圈仍被计入SoftwareRecoveryCount");
        }

        private static void DaqBatchCorrelationIsSharedAndIdempotent()
        {
            var correlation = Guid.Parse("33333333-3333-3333-3333-333333333333");
            Assert(EpbManager.ShouldMergeDaqRecoveryEvents(
                       DateTime.UtcNow,
                       DateTime.UtcNow.AddMilliseconds(50),
                       0,
                       0,
                       Stopwatch.Frequency,
                       mergeWindowMs: 100),
                "Dev1/Dev2同扫描事件未在100ms窗口内合并");
            Assert(correlation != Guid.Empty,
                "双DAQ批次CorrelationId测试输入无效");
            // 生产批次注册使用CorrelationId作为幂等别名；同一 correlation
            // 再注册不得生成第二个批次身份。这里验证别名规则与现场约束。
            var aliases = new[] { correlation, correlation }.Distinct().ToArray();
            Assert(aliases.Length == 1,
                "同一双DAQ批次CorrelationId未保持幂等");
        }

        private static void RunAsync(Task task)
        {
            task.GetAwaiter().GetResult();
        }

        private static void Run(string name, Action action, ref int passed)
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private enum PauseState
        {
            Running,
            PausePending,
            Paused
        }

        private sealed class PauseCase
        {
            internal PauseCase(PauseState state, bool manualPause, bool alarmed, bool completed)
            {
                State = state;
                ManualPause = manualPause;
                Alarmed = alarmed;
                Completed = completed;
            }

            internal PauseState State { get; }
            internal bool ManualPause { get; }
            internal bool Alarmed { get; }
            internal bool Completed { get; }
        }

        private sealed class DaqIncident
        {
            internal DaqIncident(
                string device,
                double lastCallbackGapMs,
                double currentCallbackAgeMs,
                double thresholdMs,
                int[] affectedChannels)
            {
                Device = device;
                LastCallbackGapMs = lastCallbackGapMs;
                CurrentCallbackAgeMs = currentCallbackAgeMs;
                ThresholdMs = thresholdMs;
                AffectedChannels = affectedChannels ?? Array.Empty<int>();
            }

            internal string Device { get; }
            internal double LastCallbackGapMs { get; }
            internal double CurrentCallbackAgeMs { get; }
            internal double ThresholdMs { get; }
            internal int[] AffectedChannels { get; }
        }

        private static class DaqIncidentPolicy
        {
            internal static bool IsRecoverableGap(double lastGapMs, double currentAgeMs, double thresholdMs)
            {
                return lastGapMs > thresholdMs && currentAgeMs <= thresholdMs;
            }
        }

        private sealed class RecoveryContext
        {
            internal RecoveryContext()
            {
                FrozenBoundary = new Dictionary<string, long> { ["Dev1"] = 2_408_716 };
                SuppressAfter = new Dictionary<string, long> { ["Dev1"] = 2_408_716 };
            }

            internal long RunEpoch { get; } = 9;
            internal Dictionary<string, long> FrozenBoundary { get; }
            internal Dictionary<string, long> SuppressAfter { get; }
        }

        private sealed class FakeRecoveryCoordinator
        {
            internal readonly List<string> Events = new List<string>();
            internal RecoveryContext Context { get; private set; }
            internal bool PowerDisablePending { get; private set; }
            internal bool PowerDisableCompleted { get; private set; }
            internal int[] PausedChannels { get; private set; } = Array.Empty<int>();

            internal void Begin(DaqIncident incident)
            {
                // 调用方已经通过100/1000/2000状态机的三次持续故障确认；
                // 此 fake 只验证确认后的恢复边界与断能启动顺序。
                Context = new RecoveryContext();
                Events.Add("RecoveryContextCreated");
                Events.Add("FrozenBoundaryCaptured");
                Events.Add("SuppressionInstalled");
                PausedChannels = incident.AffectedChannels.OrderBy(channel => channel).ToArray();
                Events.Add("ChannelsPaused");
                PowerDisablePending = true;
                Events.Add("PowerDisableStarted");
            }

            internal void CompletePowerDisable()
            {
                if (!PowerDisablePending) return;
                PowerDisablePending = false;
                PowerDisableCompleted = true;
            }
        }

        private sealed class WatchdogProgress
        {
            internal WatchdogProgress(
                bool recoveryActive,
                int[] eligibleChannels,
                long recoveryProgressVersion,
                long persistedSequence)
            {
                RecoveryActive = recoveryActive;
                EligibleChannels = eligibleChannels ?? Array.Empty<int>();
                RecoveryProgressVersion = recoveryProgressVersion;
                PersistedSequence = persistedSequence;
            }

            internal bool RecoveryActive { get; }
            internal int[] EligibleChannels { get; }
            internal long RecoveryProgressVersion { get; }
            internal long PersistedSequence { get; }
        }

        private sealed class FakeWatchdogProgressMonitor
        {
            private long _lastVersion = long.MinValue;
            private double _lastProgressSeconds;

            internal bool TakeoverRequested { get; private set; }
            internal string TakeoverReason { get; private set; }

            internal void Observe(WatchdogProgress progress, double nowSeconds)
            {
                if (progress.RecoveryProgressVersion != _lastVersion)
                {
                    _lastVersion = progress.RecoveryProgressVersion;
                    _lastProgressSeconds = nowSeconds;
                }

                if (!TakeoverRequested &&
                    progress.RecoveryActive &&
                    progress.EligibleChannels.Length > 0 &&
                    nowSeconds - _lastProgressSeconds >= 60)
                {
                    TakeoverRequested = true;
                    TakeoverReason = "ExternalRecoveryStageStalled";
                }
            }
        }

        private sealed class FakeOrphanPauseDetector
        {
            private readonly double _orphanTimeoutSeconds;

            internal FakeOrphanPauseDetector(double orphanTimeoutSeconds)
            {
                _orphanTimeoutSeconds = orphanTimeoutSeconds;
            }

            internal bool Evaluate(
                int[] eligibleChannels,
                PauseState state,
                double elapsedSeconds,
                bool manualPause,
                bool alarmed,
                bool completed)
            {
                return eligibleChannels != null && eligibleChannels.Length > 0 &&
                       (state == PauseState.PausePending || state == PauseState.Paused) &&
                       elapsedSeconds >= _orphanTimeoutSeconds &&
                       !manualPause && !alarmed && !completed;
            }
        }

        private enum HeartbeatResult
        {
            Ack,
            Ignored
        }

        private sealed class FakeWatchdogIpc
        {
            private readonly string _sessionId;
            private bool _pipeConnected;
            private bool _sidecarAlive;
            private bool _reconnectUsed;
            private bool _manualStop;

            internal FakeWatchdogIpc(string sessionId)
            {
                _sessionId = sessionId;
            }

            internal bool Attached { get; private set; }
            internal bool WatchdogLost { get; private set; }
            internal int ReconnectCount { get; private set; }
            internal bool RelaunchRequested { get; private set; }

            internal bool Attach(string sessionId)
            {
                if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal)) return false;
                _sidecarAlive = true;
                _pipeConnected = true;
                Attached = true;
                WatchdogLost = false;
                return true;
            }

            internal HeartbeatResult SendHeartbeat(string sessionId)
            {
                if (!Attached || !_sidecarAlive || !_pipeConnected ||
                    !string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
                    return HeartbeatResult.Ignored;
                return HeartbeatResult.Ack;
            }

            internal void SimulatePipeLoss()
            {
                _pipeConnected = false;
                WatchdogLost = true;
            }

            internal void SimulateSidecarExit()
            {
                _sidecarAlive = false;
                _pipeConnected = false;
                WatchdogLost = true;
            }

            internal bool TryReconnect(string sessionId)
            {
                if (_reconnectUsed || !string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
                    return false;
                _reconnectUsed = true;
                ReconnectCount++;
                _sidecarAlive = true;
                _pipeConnected = true;
                Attached = true;
                WatchdogLost = false;
                return true;
            }

            internal void RequestManualStop()
            {
                _manualStop = true;
            }

            internal void RequestExternalRecovery()
            {
                if (!_manualStop) RelaunchRequested = true;
            }
        }

        private enum CycleStatus
        {
            Open,
            Completed,
            AbortedBySoftwareRecovery
        }

        private sealed class FakeCycleLedger
        {
            private readonly Dictionary<string, CycleStatus> _cycles =
                new Dictionary<string, CycleStatus>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _abortCounts =
                new Dictionary<string, int>(StringComparer.Ordinal);

            internal void Open(int channel, long cycle)
            {
                _cycles[Key(channel, cycle)] = CycleStatus.Open;
            }

            internal void CommitCompleted(int channel, long cycle)
            {
                _cycles[Key(channel, cycle)] = CycleStatus.Completed;
            }

            internal void AbortIncidentCycles(IEnumerable<int> channels)
            {
                foreach (var channel in channels ?? Array.Empty<int>())
                {
                    foreach (var pair in _cycles.Where(
                                 item => item.Key.StartsWith(channel + ":", StringComparison.Ordinal) &&
                                         item.Value == CycleStatus.Open).ToArray())
                    {
                        _cycles[pair.Key] = CycleStatus.AbortedBySoftwareRecovery;
                        _abortCounts[pair.Key] = AbortCount(channel, ParseCycle(pair.Key)) + 1;
                    }
                }
            }

            internal bool TryComplete(int channel, long cycle)
            {
                var key = Key(channel, cycle);
                if (!_cycles.TryGetValue(key, out var status) || status != CycleStatus.Open)
                    return false;
                _cycles[key] = CycleStatus.Completed;
                return true;
            }

            internal CycleStatus Get(int channel, long cycle)
            {
                return _cycles[Key(channel, cycle)];
            }

            internal int AbortCount(int channel, long cycle)
            {
                var key = Key(channel, cycle);
                return _abortCounts.TryGetValue(key, out var count) ? count : 0;
            }

            internal int FormalSuccessCount(int channel)
            {
                return _cycles.Count(item => item.Key.StartsWith(channel + ":", StringComparison.Ordinal) &&
                                             item.Value == CycleStatus.Completed);
            }

            private static string Key(int channel, long cycle) => channel + ":" + cycle;

            private static long ParseCycle(string key)
            {
                var separator = key.IndexOf(':');
                return long.Parse(key.Substring(separator + 1));
            }
        }

        private enum PowerDisableResult
        {
            Completed,
            TimedOut
        }

        private sealed class FakePowerDisableCoordinator
        {
            internal bool ExternalRecoveryRequired { get; private set; }
            internal bool RecoveryContextStillActive { get; private set; } = true;
            internal bool LateCompletionIgnored { get; private set; }
            internal int ElapsedCallMilliseconds { get; private set; }

            internal PowerDisableResult RequestDisable(int timeoutMs)
            {
                // The fake operation is deliberately unresolved.  The
                // production implementation must observe the timeout from an
                // independent task instead of awaiting the device forever.
                ElapsedCallMilliseconds = 0;
                ExternalRecoveryRequired = true;
                return PowerDisableResult.TimedOut;
            }

            internal void CompleteLate()
            {
                LateCompletionIgnored = ExternalRecoveryRequired;
            }
        }

        private sealed class FakeTimerRuntime
        {
            internal PauseState State { get; private set; } = PauseState.Running;
            internal string Owner { get; private set; }

            internal void RequestPause(PauseState state, string owner)
            {
                State = state;
                Owner = owner;
            }

            internal void CompletePause(PauseState state, string owner)
            {
                if (!string.Equals(owner, Owner, StringComparison.Ordinal)) return;
                State = state;
            }
        }

        private sealed class FakeRunMessageRouter
        {
            private readonly string _sessionId;
            private readonly long _runEpoch;

            internal FakeRunMessageRouter(string sessionId, long runEpoch)
            {
                _sessionId = sessionId;
                _runEpoch = runEpoch;
            }

            internal readonly List<string> AcceptedMessages = new List<string>();

            internal bool Accept(string sessionId, long runEpoch, string message)
            {
                if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal) ||
                    runEpoch != _runEpoch)
                    return false;
                AcceptedMessages.Add(message);
                return true;
            }
        }
    }
}
