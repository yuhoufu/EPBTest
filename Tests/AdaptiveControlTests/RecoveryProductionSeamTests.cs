using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using IO.NI;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Deterministic production-coordinator tests.  The fake port replaces
    /// hardware/state-store calls only; admission, suspension, scope
    /// de-duplication, bind, start and terminal cleanup are the production
    /// RecoveryIncidentCoordinator implementation.
    /// </summary>
    internal static class RecoveryProductionSeamTests
    {
        private static void InfrastructureRescheduleRejectsStaleIdentity()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var current = Guid.NewGuid();
            type.GetField("_activeBatchId", flags).SetValue(manager, current);
            type.GetField("_runEpoch", flags).SetValue(manager, 12L);
            var schedule = type.GetMethod("ScheduleIsolatedInfrastructureRecovery", flags);
            // 不配置队列或硬件；过期请求必须在枚举通道和登记任务前返回。
            foreach (var identity in new[] { Tuple.Create(Guid.NewGuid(), 12L),
                         Tuple.Create(current, 11L), Tuple.Create(Guid.Empty, 12L) })
                schedule.Invoke(manager, new object[] { new[] { 1 }, "fixture", Guid.NewGuid(),
                    "fixture", 0L, identity.Item1, identity.Item2 });
            foreach (var pair in new[] { new object[] { current, null }, new object[] { null, 12L } })
            {
                var rejected = false;
                try
                {
                    schedule.Invoke(manager, new object[] { new[] { 1 }, "fixture", Guid.NewGuid(),
                        "fixture", 0L, pair[0], pair[1] });
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is ArgumentException)
                {
                    rejected = true;
                }
                if (!rejected) throw new InvalidOperationException("不成对的恢复身份未被拒绝");
            }
            foreach (var invalidEpoch in new[] { 0L, -1L })
            {
                type.GetField("_runEpoch", flags).SetValue(manager, invalidEpoch);
                schedule.Invoke(manager, new object[] { new[] { 1 }, "fixture", Guid.NewGuid(),
                    "fixture", 0L, current, invalidEpoch });
            }
        }

        internal static int RunAll()
        {
            var passed = 0;
            Run("隔离恢复重登记拒绝过期或不成对运行身份", InfrastructureRescheduleRejectsStaleIdentity, ref passed);
            Run("DAQ重试识别正常结束但OFF未确认的结果且不重试在途任务", PowerOffRetryUsesExecutionResult, ref passed);
            Run("电源安全确认缺少硬件时不得返回成功", PowerSafetyRejectsMissingHardware, ref passed);
            Run("DAQ迟到回滚在旧运行或已释放液压所有权时不访问硬件",
                DaqRollbackRejectsStaleRunBeforeHardware, ref passed);
            Run("Manager电源owner检查拒绝旧运行、撤权和退休身份",
                ManagerPowerOwnerChecksActualEntry, ref passed);
            Run("关联启动子恢复要求父预留存活且退休后不执行主体",
                StartupChildRequiresLiveParent, ref passed);
            Run("恢复安全范围重叠查询仅放行精确存活owner的业务成员",
                RecoverySafetyConflictUsesExactLiveOwner, ref passed);
            Run("Manager登记范围构造不把安全成员提升为续测owner",
                ManagerAdmissionScopesDoNotGrantSafetyMembersOwnership,
                ref passed);
            Run("恢复接替等待真实worker退出且必须确认登记退休",
                RecoveryHandoffWaitsForRetirement,
                ref passed);
            Run("恢复接替仅能重置同代同incident的StartBlocked",
                RecoveryHandoffTerminalResetIsExact,
                ref passed);
            Run("Manager实际硬件任务按物理组登记且禁用成员不漏组",
                ManagerHardwareActionTracksPhysicalGroups,
                ref passed);
            Run("Recovery既有生产事务名可登记且不放开任意Reset或Retry任务",
                ProductionIncidentNamesAreRecognized,
                ref passed);
            Run("Recovery reserve/factory/scheduler/bind/publish/observer故障均先OFF再安全终态",
                AdmissionFailuresFailClosed,
                ref passed);
            Run("Recovery真实worker在显式Start前不执行且漏终态自动断电收口",
                WorkerIsSuspendedUntilExplicitStart,
                ref passed);
            Run("Recovery显式Start故障仍按OFF→安全终态收口",
                StartFailureFailClosed,
                ref passed);
            Run("Recovery同步空Task/异步异常均由生产wrapper安全收口",
                WorkerBodyFailuresFailClosed,
                ref passed);
            Run("Recovery终态回调异常或部分终态由安全回退补齐",
                TerminalPublicationFailuresFailClosed,
                ref passed);
            Run("Recovery所有端口回调均在协调gate外执行",
                RecoveryCallbacksRunOutsideCoordinatorGate,
                ref passed);
            Run("Recovery Register部分写入后抛异常仍幂等补偿无孤儿",
                RegisterPartialWriteIsCompensated,
                ref passed);
            Run("Recovery重叠通道拒绝且不影响首incident，非重叠可并行",
                RecoveryChannelOverlapIsAtomic,
                ref passed);
            Run("Recovery终态已提交但旧任务未退出时保留范围，退出后允许整组接替",
                TerminalCommitRetainsScopeUntilWorkerExit,
                ref passed);
            Run("Recovery退出前终态变化必须重验，安全收尾失败保留登记且可重试",
                WorkerExitRevalidatesTerminal,
                ref passed);
            Run("Recovery物理安全组与逻辑owner集合严格分离",
                RecoveryPhysicalAndLogicalScopesAreSeparated,
                ref passed);
            Run("Recovery 1.2秒窗口64并发不同correlation只创建一个incident",
                ScopeBarrierDeduplicatesConcurrentSignals,
                ref passed);
            Run("Recovery 1000轮随机并发准入/Stop隔离/迟到终态无双owner或半恢复",
                RandomizedAdmissionStopAndLateTerminalInterleavings,
                ref passed);
            Run("DAQ事务最后一路准入阻塞时仍停留StaleDetected",
                DaqLastAdmissionBlockedDoesNotPublishSubmitted,
                ref passed);
            Run("DAQ事务全路准入后才发布Submitted",
                DaqAllAcceptedPublishesSubmitted,
                ref passed);
            Run("DAQ事务物理完成不足时不发布Confirmed",
                DaqPhysicalCompletionIsASeparateBarrier,
                ref passed);
            Run("DAQ DoOffConfirmed仅对三方同阶段提交允许幂等",
                DaqDoOffConfirmedAlreadyCommittedIsNarrow,
                ref passed);
            Run("DAQ事务拒绝或异常进入单次SafeIdle且不升级",
                DaqRejectOrThrowIsSafeIdleOnly,
                ref passed);
            Run("DAQ事务100并发SafeIdle只执行一次整组动作",
                DaqConcurrentSafeIdleIsSingleShot,
                ref passed);
            Run("DAQ事务终态部分失败只重试缺失通道",
                DaqPartialTerminalRetriesOnlyMissing,
                ref passed);
            Run("DAQ SafeIdle终态凭证失败由同一生产supervisor自动重试且不重复OFF",
                DaqSafeIdleTerminalReceiptIsSupervisedRetry,
                ref passed);
            Run("DAQ持续终态凭证失败保持SafeIdle且不升级",
                DaqPersistentTerminalReceiptFailureRemainsSafeIdle,
                ref passed);
            Run("EpbManager生产终态接线在owner取消后仍由同一owner监督重试",
                DaqTerminalCheckpointFailureIsSupervisedRetry,
                ref passed);
            Run("DAQ事务Committed checkpoint失败禁止Terminal",
                DaqCheckpointIsRequiredBeforeTerminal,
                ref passed);
            Run("RecoveryAggregateStore提交源深拷贝且Committed/Terminal版本严格递增",
                RecoveryAggregateStoreCopiesSourcesAndRetainsVersions,
                ref passed);
            Run("RecoveryAggregateStore并发8写8读十万次无混合快照",
                RecoveryAggregateStoreConcurrentCaptureIsStable,
                ref passed);
            Run("RecoveryAggregateStore operational infrastructure/power/timer同一提交版本且深拷贝",
                RecoveryAggregateStoreOperationalSourcesAreSingleRevision,
                ref passed);
            Run("DAQ截止后恢复上电不再投影为PowerDisablePending",
                DaqPostCutoffPowerOnIsNotDisablePending,
                ref passed);
            Run("Watchdog heartbeat缺失aggregate严格空快照且字段共用同一版本",
                HeartbeatAggregateSourceFailsClosed,
                ref passed);
            Run("生产CreateHeartbeat捕获入口对aggregate只读一次且复用同一版本",
                ProductionHeartbeatCaptureReadsOnce,
                ref passed);
            Run("EpbManager终态closure使用冻结transaction identity跨RunEpoch并收口owner",
                EpbManagerTerminalClosureUsesFrozenIdentity,
                ref passed);
            Run("EpbManager终态receipt首次失败后同一owner跨RunEpoch自动重试",
                EpbManagerTerminalClosureRetriesAfterEpochAdvance,
                ref passed);
            Run("EpbManager持续终态凭证失败保留TerminalClosureBlocked并进入heartbeat",
                EpbManagerTerminalClosureBlockedEvidenceIsObservable,
                ref passed);
            Run("EpbManager权威释放门禁拒绝Running/Learning/ResumeChecking并可在安全态恢复",
                EpbManagerTerminalAuthorityGateIsStrict,
                ref passed);
            return passed;
        }

        private static void EpbManagerTerminalClosureUsesFrozenIdentity()
        {
            using (var fixture = new EpbManagerTerminalFixture())
            {
                var transaction = fixture.CreateTransaction();
                var handle = fixture.Manager.BeginDaqTerminalClosureProduction(
                    fixture.Device,
                    fixture.RunId,
                    fixture.RunEpoch,
                    fixture.CorrelationId,
                    transaction);
                Assert(handle.RegistryLeaseBound && handle.RegistryLeaseActive,
                    "生产终态context未绑定活动registry lease。");
                handle.PreCancelWorker();
                handle.AdvanceGlobalRunEpoch();

                Assert(handle.TryFinalizeFailClosed("InjectedTerminalReceiptFailure"),
                    "全局RunEpoch前进或owner预取消后，同一transaction未完成终态收口。 ");
                var state = handle.GetState(4);
                Assert(state != null &&
                       EpbManager.IsExactDaqSafeTerminalState(
                           state,
                           fixture.RunId,
                           fixture.RunEpoch,
                           fixture.CorrelationId) &&
                       !handle.IsRegistered &&
                       !handle.RegistryLeaseActive &&
                       handle.IsCompletionCompleted,
                    "冻结identity终态收口后仍残留context/owner或非安全通道状态。");
            }
        }

        private static void EpbManagerTerminalAuthorityGateIsStrict()
        {
            using (var fixture = new EpbManagerTerminalFixture())
            {
                var handle = fixture.Manager.BeginDaqTerminalClosureProduction(
                    fixture.Device,
                    fixture.RunId,
                    fixture.RunEpoch,
                    fixture.CorrelationId,
                    fixture.CreateTransaction());
                Assert(handle.RegistryLeaseBound && handle.RegistryLeaseActive,
                    "严格权威门禁测试未建立活动registry lease。");

                foreach (var unsafeState in new[]
                         {
                             ChannelRuntimeState.Running,
                             ChannelRuntimeState.Learning,
                             ChannelRuntimeState.ResumeChecking
                         })
                {
                    handle.PublishAuthoritativeStateForRecoverySeam(unsafeState);
                    Assert(!handle.TryFinalizeFailClosed(
                               "AuthorityState=" + unsafeState),
                        "权威释放门禁错误接受了非安全态 " + unsafeState);
                    Assert(handle.IsRegistered && !handle.IsCompletionCompleted,
                        "非安全态拒绝后错误清理了owner/context。");
                    Assert(handle.RegistryLeaseActive,
                        "非安全态拒绝后错误释放了registry lease。");
                }

                // The authoritative source subsequently proves the exact
                // safe terminal with the frozen incident identity.  The same
                // production context must then close; no second worker or CTS
                // is created by the test.
                handle.PublishAuthoritativeStateForRecoverySeam(ChannelRuntimeState.StartBlocked);
                Assert(handle.TryFinalizeFailClosed("AuthorityStateRecovered"),
                    "权威状态恢复为StartBlocked后未能完成同一owner收口。");
                Assert(!handle.IsRegistered && handle.IsCompletionCompleted,
                    "安全态收口后仍残留owner/context。");
                Assert(!handle.RegistryLeaseActive,
                    "安全态收口后仍残留registry lease。");
            }
        }

        private static void EpbManagerTerminalClosureBlockedEvidenceIsObservable()
        {
            using (var fixture = new EpbManagerTerminalFixture())
            {
                var handle = fixture.Manager.BeginDaqTerminalClosureProduction(
                    fixture.Device,
                    fixture.RunId,
                    fixture.RunEpoch,
                    fixture.CorrelationId,
                    fixture.CreateTransaction(_ => false));
                Assert(handle.RegistryLeaseBound && handle.RegistryLeaseActive,
                    "持续失败测试未建立真实context registry lease。");

                handle.PreCancelWorker();
                handle.AdvanceGlobalRunEpoch();
                handle.CompleteCancelledClosureForRecoverySeam(
                    "InjectedPersistentTerminalCheckpointFailure");

                var retry = handle.TerminalRetryTask;
                Assert(retry != null && retry.Wait(TimeSpan.FromSeconds(20)),
                    "持续终态凭证失败未在有界监督期限内结束。");

                var result = handle.CompletionResult;
                var retained = handle.CaptureRetainedTerminalEvidence();
                var aggregate = handle.CaptureAggregate();
                var heartbeat = handle.CaptureHeartbeatChannelProjection(aggregate);
                var heartbeatSnapshot = handle.CaptureWatchdogSnapshot();
                var infrastructure = aggregate?.Infrastructure;
                var terminalState = handle.GetState(4);
                var heartbeatState = heartbeat?.FirstOrDefault(item => item.Channel == 4);

                Assert(result != null &&
                       string.Equals(
                           result.FailureKind,
                           "TerminalClosureBlocked",
                           StringComparison.Ordinal),
                    "持续receipt失败未产生TerminalClosureBlocked Completion结果。");
                Assert(retained != null && retained.TerminalClosureBlocked &&
                       retained.RunId == fixture.RunId &&
                       retained.RunEpoch == fixture.RunEpoch &&
                       retained.CorrelationId == fixture.CorrelationId &&
                       !string.IsNullOrWhiteSpace(retained.TerminalFailureReason) &&
                       !string.IsNullOrWhiteSpace(result.FailureReason),
                    "TerminalClosureBlocked retained snapshot未保留冻结incident身份/原因。" +
                    $" retainedNull={retained == null}; blocked={retained?.TerminalClosureBlocked};" +
                    $" run={retained?.RunId:N}/{retained?.RunEpoch}; corr={retained?.CorrelationId:N};" +
                    $" reason={retained?.TerminalFailureReason}; result={result?.FailureKind}/{result?.FailureReason}");
                Assert(aggregate != null && aggregate.IsStable && aggregate.Version > 0 &&
                       infrastructure != null &&
                       infrastructure.ActiveRecovery &&
                       infrastructure.RunId == fixture.RunId &&
                       infrastructure.RunEpoch == fixture.RunEpoch &&
                       infrastructure.CorrelationId == fixture.CorrelationId &&
                       infrastructure.RecoveryContext.IndexOf(
                           "TerminalClosureBlocked",
                           StringComparison.OrdinalIgnoreCase) >= 0 &&
                       aggregate.RegistryLeases.Count == 0,
                    "blocked retained事件未以冻结身份/无lease状态提交到aggregate。");
                Assert(heartbeatSnapshot != null &&
                       heartbeatSnapshot.Aggregate != null &&
                       heartbeatSnapshot.Aggregate.Version == aggregate.Version &&
                       RecoveryHeartbeatAggregateSource.CaptureVersion(aggregate) ==
                            aggregate.Version &&
                       heartbeatState != null &&
                       EpbManager.IsExactDaqSafeTerminalState(
                            heartbeatState,
                            fixture.RunId,
                            fixture.RunEpoch,
                            fixture.CorrelationId),
                     "heartbeat未复用同一aggregate版本或未观察到确切安全终态。" +
                     $" agg={aggregate?.Version}/{aggregate?.IsStable};" +
                     $" hbSnap={heartbeatSnapshot?.Aggregate?.Version};" +
                     $" hbVer={RecoveryHeartbeatAggregateSource.CaptureVersion(aggregate)};" +
                     $" channels={heartbeat?.Length};" +
                     $" state={(heartbeatState != null ? heartbeatState.State.ToString() : "<none>")};" +
                     $" stateId={(heartbeatState != null ? heartbeatState.RunId.ToString("N") : "<none>")}/" +
                     $"{(heartbeatState != null ? heartbeatState.RunEpoch.ToString() : "<none>")}/" +
                     $"{(heartbeatState != null ? heartbeatState.CorrelationId.ToString("N") : "<none>")}");
                Assert(!handle.IsRegistered && !handle.RegistryLeaseActive &&
                       handle.IsCompletionCompleted &&
                       EpbManager.IsExactDaqSafeTerminalState(
                           terminalState,
                           fixture.RunId,
                           fixture.RunEpoch,
                           fixture.CorrelationId),
                    "TerminalClosureBlocked后owner/context/lease或安全态门禁未按规则收口。");
            }
        }

        private static void EpbManagerTerminalClosureRetriesAfterEpochAdvance()
        {
            using (var fixture = new EpbManagerTerminalFixture())
            {
                var checkpointAttempts = 0;
                var handle = fixture.Manager.BeginDaqTerminalClosureProduction(
                    fixture.Device,
                    fixture.RunId,
                    fixture.RunEpoch,
                    fixture.CorrelationId,
                    fixture.CreateTransaction(attempt =>
                    {
                        var attemptNumber = Interlocked.Increment(ref checkpointAttempts);
                        return attemptNumber >= 2;
                    }));
                Assert(handle.RegistryLeaseBound && handle.RegistryLeaseActive,
                    "自动retry测试未建立真实context registry lease。");

                // CancelAll/RunEpochChanged revokes the long-running worker,
                // but the terminal safety transaction must remain eligible for
                // the same owner.  The first checkpoint is rejected; the
                // production supervisor immediately retries that exact
                // transaction and then closes the context/lease.
                handle.PreCancelWorker();
                handle.AdvanceGlobalRunEpoch();
                handle.CompleteCancelledClosureForRecoverySeam(
                    "InjectedFirstTerminalCheckpointFailure");

                var finalState = handle.GetState(4);
                var closed = !handle.IsRegistered &&
                             !handle.RegistryLeaseActive &&
                             handle.IsCompletionCompleted &&
                             EpbManager.IsExactDaqSafeTerminalState(
                                 finalState,
                                 fixture.RunId,
                                 fixture.RunEpoch,
                                 fixture.CorrelationId);
                Assert(checkpointAttempts >= 2 && closed,
                    "首次终态凭证失败后未由同一冻结owner跨RunEpoch完成自动retry闭环。" +
                    $" attempts={checkpointAttempts}; registered={handle.IsRegistered};" +
                    $" lease={handle.RegistryLeaseActive}; completed={handle.IsCompletionCompleted};" +
                    $" state={finalState?.State}; stateRun={finalState?.RunId:N}/{finalState?.RunEpoch};" +
                    $" stateCorr={finalState?.CorrelationId:N}");
            }
        }

        private sealed class EpbManagerTerminalFixture : IDisposable
        {
            internal readonly string Device = "Dev1";
            internal readonly Guid RunId = Guid.NewGuid();
            internal readonly long RunEpoch = 41;
            internal readonly Guid CorrelationId = Guid.NewGuid();
            internal readonly EpbManager Manager;
            private readonly DoController _do;
            private readonly AoController _ao;
            private readonly TwoDeviceAiAcquirer _acq;
            private readonly string _root;

            internal EpbManagerTerminalFixture()
            {
                _root = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "epb-terminal-seam-" + Guid.NewGuid().ToString("N"));
                var config = new GlobalConfig
                {
                    AO = new AoConfig(),
                    DO = new DoConfig(),
                    Test = new TestConfig
                    {
                        TestName = "TerminalClosureSeam",
                        StoreDir = _root,
                        TestTarget = 1,
                        TestPeriod = 0.1
                    }
                };
                var epbRecords = config.Test.EnsureEpbRecords(12);
                var epb4Record = epbRecords.Find(record => record.Id == 4);
                Assert(epbRecords.Count == 12 && epb4Record != null,
                    "EpbManagerTerminalFixture未建立完整12通道EpbRecords。");
                epb4Record.Enabled = true;
                Assert(epb4Record.Enabled,
                    "EpbManagerTerminalFixture前置条件EPB4未启用。");
                _do = new DoController(config.DO);
                _ao = new AoController(config.AO);
                var aiConfig = new AiConfigDetail
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
                };
                _acq = new TwoDeviceAiAcquirer(
                    aiConfig,
                    2000,
                    100,
                    3);
                Manager = new EpbManager(
                    config,
                    _do,
                    _ao,
                    _acq,
                    Config.NullLogger.Instance);
            }

            internal DaqRecoveryTransaction CreateTransaction(
                Func<int, bool> terminalCheckpointIsStable = null)
            {
                var terminalCheckpointAttempt = 0;
                return new DaqRecoveryTransaction(
                    new[] { 4 },
                    new DaqRecoveryTransaction.Port
                    {
                        TrySubmitOff = channel =>
                            new DaqRecoveryTransaction.OffReceipt(
                                channel,
                                Guid.NewGuid(),
                                true,
                                true,
                                string.Empty),
                        SubmitSafetyOff = _ => { },
                        DisablePower = _ => { },
                        PublishPhase = _ => { },
                        PublishTerminal = missing => missing,
                        PublishTerminalCheckpoint = version =>
                        {
                            var attempt = Interlocked.Increment(
                                ref terminalCheckpointAttempt);
                            var stable = terminalCheckpointIsStable == null ||
                                         terminalCheckpointIsStable(attempt);
                            return new DaqRecoveryTransaction.CheckpointReceipt(
                                stable ? version + 1 : version,
                                stable,
                                stable ? "TerminalSeam" : "InjectedTerminalCheckpointFailure");
                        }
                    });
            }

            public void Dispose()
            {
                try { Manager?.ReleaseHardwareForRestart(); }
                catch { }
                try { _acq?.Dispose(); }
                catch { }
                try { _ao?.Dispose(); }
                catch { }
                try { _do?.Dispose(); }
                catch { }
                try
                {
                    if (!string.IsNullOrWhiteSpace(_root) &&
                        System.IO.Directory.Exists(_root))
                        System.IO.Directory.Delete(_root, true);
                }
                catch { }
            }
        }

        private static void ProductionHeartbeatCaptureReadsOnce()
        {
            var count = 0;
            var expected = new WatchdogRecoverySnapshot
            {
                RunId = Guid.NewGuid(),
                RunEpoch = 12,
                Aggregate = new RecoveryAggregateStore().Capture()
            };
            var captured = WatchdogHeartbeatSourceCapture.CaptureOnce(() =>
            {
                Interlocked.Increment(ref count);
                return expected;
            });
            Assert(count == 1 &&
                   ReferenceEquals(captured.Snapshot, expected) &&
                   ReferenceEquals(captured.Aggregate, expected.Aggregate) &&
                   captured.AggregateVersion == expected.Aggregate.Version,
                "生产CreateHeartbeat入口未复用同一个snapshot/aggregate版本。");

            var nullCount = 0;
            var missing = WatchdogHeartbeatSourceCapture.CaptureOnce(() =>
            {
                Interlocked.Increment(ref nullCount);
                return new WatchdogRecoverySnapshot { Aggregate = null };
            });
            Assert(nullCount == 1 && missing.Snapshot != null &&
                   missing.Aggregate == null && missing.AggregateVersion == 0,
                "首次aggregate=null时生产入口发生了重读或未fail-closed。");
        }

        private static void HeartbeatAggregateSourceFailsClosed()
        {
            var store = new RecoveryAggregateStore();
            var runId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var state = new ChannelRuntimeStateChangedEvent
            {
                Channel = 4,
                State = ChannelRuntimeState.Recovering,
                ReasonCode = "heartbeat-source",
                TimestampUtc = now,
                RunId = runId,
                RunEpoch = 7,
                RecoveryOwnerKind = RecoveryOwnerKind.DaqRecovery,
                RecoveryOwnerId = Guid.NewGuid(),
                RecoveryOwnerGeneration = 7,
                RecoveryTargetPhase = RecoveryTargetPhase.Formal,
                Enabled = true
            };
            var aggregate = store.PublishOwnershipSource(
                new[] { state },
                Array.Empty<RecoveryContractSnapshot>(),
                Array.Empty<WatchdogRecoveryLeaseSnapshot>());
            var projected = RecoveryHeartbeatAggregateSource.CaptureChannelStates(aggregate);
            Assert(projected.Length == 1 && projected[0].Channel == 4 &&
                   RecoveryHeartbeatAggregateSource.CaptureVersion(aggregate) == aggregate.Version,
                "heartbeat通道状态或版本未来自同一已提交aggregate。");

            // The production source has no legacy dictionary/fallback input;
            // null therefore fails closed without any UI-state read.
            var missing = RecoveryHeartbeatAggregateSource.CaptureChannelStates(null);
            Assert(missing.Length == 0 &&
                   RecoveryHeartbeatAggregateSource.CaptureVersion(null) == 0,
                "aggregate缺失时heartbeat未严格空快照/版本0收口。");
        }

        private static void DaqLastAdmissionBlockedDoesNotPublishSubmitted()
        {
            var lastEntered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var seam = new DaqTransactionSeam
            {
                TrySubmit = channel =>
                {
                    if (channel == 10)
                    {
                        lastEntered.Set();
                        release.Wait(TimeSpan.FromSeconds(5));
                    }
                    return DaqRecoveryTransactionReceipt(channel);
                }
            };
            var transaction = seam.CreateTransaction(4, 10);
            var submit = Task.Run(() => transaction.SubmitOffBatch());
            Assert(lastEntered.Wait(TimeSpan.FromSeconds(2)), "最后一路准入未进入阻塞点。");
            Assert(transaction.Phase == DaqRecoveryPhase.StaleDetected,
                "最后一路准入尚未返回accepted却提前发布DoOffSubmitted。");
            release.Set();
            Assert(submit.GetAwaiter().GetResult() &&
                   transaction.Phase == DaqRecoveryPhase.DoOffSubmitted,
                "最后一路准入完成后未发布DoOffSubmitted。");
        }

        private static void DaqAllAcceptedPublishesSubmitted()
        {
            var seam = new DaqTransactionSeam();
            var transaction = seam.CreateTransaction(4, 5, 10);
            Assert(transaction.SubmitOffBatch(), "全路accepted未提交成功。");
            Assert(transaction.Phase == DaqRecoveryPhase.DoOffSubmitted &&
                   transaction.OffReceipts.Count == 3 &&
                   transaction.OffReceipts.All(receipt => receipt.Accepted),
                "DoOffSubmitted之前缺少完整准入receipt。");
        }

        private static void DaqPhysicalCompletionIsASeparateBarrier()
        {
            var seam = new DaqTransactionSeam();
            var transaction = seam.CreateTransaction(4, 5);
            Assert(transaction.SubmitOffBatch(), "准入未完成。");
            var receipts = transaction.OffReceipts.ToArray();
            Assert(transaction.ReportPhysicalCompletion(
                       receipts[0].Channel,
                       receipts[0].CommandId,
                       true),
                "首路物理完成未登记。");
            Assert(transaction.Phase == DaqRecoveryPhase.DoOffSubmitted,
                "单路物理完成却提前发布DoOffConfirmed。");
            Assert(transaction.ReportPhysicalCompletion(
                       receipts[1].Channel,
                       receipts[1].CommandId,
                       true) &&
                   transaction.Phase == DaqRecoveryPhase.DoOffConfirmed,
                "全部物理完成后未发布DoOffConfirmed。");
            var confirmed = transaction.OffReceipts.ToArray();
            Assert(confirmed.All(receipt => receipt.Evidence != null &&
                                             receipt.Evidence.ConfirmedOff &&
                                             receipt.Evidence.TargetKind ==
                                                 SafetyOffTargetKind.DigitalOutput),
                "DAQ断电完成未形成强类型ConfirmedOff硬件回执。");
        }

        private static void DaqDoOffConfirmedAlreadyCommittedIsNarrow()
        {
            var confirmed = DaqRecoveryPhase.DoOffConfirmed;
            Assert(EpbManager.IsAlreadyCommittedDaqRecoveryStage(
                    true,
                    confirmed,
                    confirmed,
                    confirmed,
                    confirmed),
                "Controller、进度和事务三方同为DoOffConfirmed时未幂等成功。");
            Assert(!EpbManager.IsAlreadyCommittedDaqRecoveryStage(
                    false,
                    confirmed,
                    confirmed,
                    confirmed,
                    confirmed),
                "未显式授权的重复阶段被放行。");
            Assert(!EpbManager.IsAlreadyCommittedDaqRecoveryStage(
                    true,
                    DaqRecoveryPhase.PowerOffSubmitted,
                    DaqRecoveryPhase.PowerOffSubmitted,
                    DaqRecoveryPhase.PowerOffSubmitted,
                    DaqRecoveryPhase.PowerOffSubmitted),
                "非DoOffConfirmed阶段被错误设为幂等。");
            Assert(!EpbManager.IsAlreadyCommittedDaqRecoveryStage(
                    true,
                    confirmed,
                    DaqRecoveryPhase.SafeIdle,
                    confirmed,
                    confirmed) &&
                   !EpbManager.IsAlreadyCommittedDaqRecoveryStage(
                    true,
                    confirmed,
                    confirmed,
                    DaqRecoveryPhase.PowerOffSubmitted,
                    confirmed) &&
                   !EpbManager.IsAlreadyCommittedDaqRecoveryStage(
                    true,
                    confirmed,
                    confirmed,
                    confirmed,
                    DaqRecoveryPhase.DoOffSubmitted),
                "SafeIdle、跨阶段进度或事务不一致被错误放行。");
        }

        private static void DaqRejectOrThrowIsSafeIdleOnly()
        {
            foreach (var mode in new[] { "reject", "throw" })
            {
                var seam = new DaqTransactionSeam
                {
                    TrySubmit = channel =>
                    {
                        if (channel == 10)
                        {
                            if (mode == "throw") throw new InvalidOperationException("submit");
                            return new DaqRecoveryTransaction.OffReceipt(
                                channel,
                                Guid.Empty,
                                false,
                                false,
                                "reject");
                        }
                        return DaqRecoveryTransactionReceipt(channel);
                    }
                };
                var transaction = seam.CreateTransaction(4, 10);
                Assert(!transaction.SubmitOffBatch() &&
                       transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.SafeIdle &&
                       transaction.SafeIdleDecisionCount == 1 &&
                       seam.SafetyOffCount == 1 &&
                       seam.PowerDisableCount == 1 &&
                       !transaction.SystemFault &&
                       !transaction.BatchRestart &&
                       !transaction.ProcessRestart,
                    $"{mode}未按单次SafeIdle收口：Outcome={transaction.CurrentOutcome}; " +
                    $"Off={seam.SafetyOffCount};Power={seam.PowerDisableCount}");
                var submitCountAfterSafeIdle = seam.TrySubmitCount;
                Assert(!transaction.RetryMissingOff() &&
                       seam.TrySubmitCount == submitCountAfterSafeIdle,
                    $"{mode} SafeIdle后RetryMissingOff仍提交了新的DO：" +
                    $"Before={submitCountAfterSafeIdle};After={seam.TrySubmitCount}");
            }
        }

        private static void DaqConcurrentSafeIdleIsSingleShot()
        {
            var seam = new DaqTransactionSeam();
            var transaction = seam.CreateTransaction(4, 5, 10);
            Assert(transaction.SubmitOffBatch(), "准入未完成。");
            var tasks = Enumerable.Range(0, 100)
                .Select(_ => Task.Run(() => transaction.RequestSafeIdle("permanent-loss")))
                .ToArray();
            Task.WaitAll(tasks);
            Assert(transaction.SafeIdleDecisionCount == 1 &&
                   seam.SafetyOffCount == 1 &&
                   seam.PowerDisableCount == 1 &&
                   transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.SafeIdle &&
                   !transaction.SystemFault &&
                   !transaction.BatchRestart &&
                   !transaction.ProcessRestart,
                $"100并发SafeIdle重复执行或发生升级：Decision={transaction.SafeIdleDecisionCount};" +
                $"Off={seam.SafetyOffCount};Power={seam.PowerDisableCount}");
        }

        private static void DaqPartialTerminalRetriesOnlyMissing()
        {
            var seam = new DaqTransactionSeam();
            var transaction = seam.CreateTransaction(4, 5, 10);
            CompleteDaqTransactionToRejoining(transaction);
            Assert(transaction.PublishCommittedCheckpoint(), "稳定Committed checkpoint未发布。");
            seam.TerminalBatchSize = 1;
            Assert(!transaction.TryTerminal() && transaction.MissingTerminalChannels.Count == 2,
                "部分终态成功未保留缺失通道。");
            Assert(seam.TerminalCalls.Count == 1 && seam.TerminalCalls[0].Count == 3,
                "首次终态调用没有覆盖完整受影响组。");
            Assert(transaction.TryTerminal() && transaction.MissingTerminalChannels.Count == 0,
                "第二次终态重试未补齐缺失通道。");
            Assert(seam.TerminalCalls.Count == 2 && seam.TerminalCalls[1].Count == 2,
                "重试终态错误地重复了已确认通道。");
            var submitCountAfterTerminal = seam.TrySubmitCount;
            Assert(!transaction.RetryMissingOff() &&
                   seam.TrySubmitCount == submitCountAfterTerminal,
                "Terminal后RetryMissingOff仍提交了新的DO。");
            Assert(transaction.TerminalVersion > transaction.CommittedVersion,
                "Terminal版本没有严格大于Committed版本。");
            Assert(transaction.TerminalAggregateVersion >
                       transaction.CommittedAggregateVersion &&
                   seam.LastTerminalStage == DaqRecoveryPhase.Terminal,
                "Terminal未取得Stage=Terminal且严格递增的终态aggregate凭证。");
        }

        private static void DaqCheckpointIsRequiredBeforeTerminal()
        {
            var seam = new DaqTransactionSeam { CheckpointStable = false };
            var transaction = seam.CreateTransaction(4, 10);
            CompleteDaqTransactionToRejoining(transaction);
            Assert(!transaction.PublishCommittedCheckpoint() &&
                   transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.Committed &&
                   !transaction.TryTerminal(),
                "checkpoint失败后仍允许Terminal。");
            seam.CheckpointStable = true;
            Assert(transaction.PublishCommittedCheckpoint() && transaction.TryTerminal(),
                "checkpoint恢复后未允许Terminal。");
            Assert(transaction.TerminalVersion > transaction.CommittedVersion,
                "稳定checkpoint后的Terminal版本未递增。");
        }

        private static void DaqSafeIdleTerminalReceiptIsSupervisedRetry()
        {
            var seam = new DaqTransactionSeam
            {
                FailTerminalCheckpointOnce = true,
                TrySubmit = channel => channel == 10
                    ? new DaqRecoveryTransaction.OffReceipt(
                        channel, Guid.Empty, false, false, "injected-safe-idle")
                    : DaqRecoveryTransactionReceipt(channel)
            };
            var transaction = seam.CreateTransaction(4, 10);
            Assert(!transaction.SubmitOffBatch() &&
                   transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.SafeIdle,
                "SafeIdle前置故障未形成单次安全分支。");
            var supervisor = new DaqRecoveryTerminalSupervisor();
            var completed = 0;
            supervisor.TryCompleteOrSchedule(
                () => transaction.TryTerminal(),
                () => transaction.CurrentOutcome != DaqRecoveryTransaction.Outcome.Terminal,
                () => Interlocked.Increment(ref completed),
                _ => { },
                CancellationToken.None,
                "SafeIdleTerminalRetryExhausted",
                maxAttempts: 4,
                initialDelayMs: 1,
                maxDelayMs: 4);
            supervisor.RetryTask.GetAwaiter().GetResult();
            Assert(completed == 1 &&
                   transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.Terminal &&
                   transaction.TerminalAggregateVersion > 0 &&
                   seam.TerminalCheckpointAttempts == 2 &&
                   seam.SafetyOffCount == 1 &&
                   seam.PowerDisableCount == 1 &&
                   !transaction.RetryMissingOff(),
                $"SafeIdle终态凭证未由同一生产supervisor完成：Outcome={transaction.CurrentOutcome};" +
                $"M={transaction.TerminalAggregateVersion};Attempts={seam.TerminalCheckpointAttempts};" +
                $"Off={seam.SafetyOffCount};Power={seam.PowerDisableCount}");
        }

        private static void DaqPersistentTerminalReceiptFailureRemainsSafeIdle()
        {
            var seam = new DaqTransactionSeam
            {
                FailTerminalCheckpointAlways = true,
                TrySubmit = channel => channel == 10
                    ? new DaqRecoveryTransaction.OffReceipt(
                        channel, Guid.Empty, false, false, "injected-safe-idle")
                    : DaqRecoveryTransactionReceipt(channel)
            };
            var transaction = seam.CreateTransaction(4, 10);
            Assert(!transaction.SubmitOffBatch() &&
                   transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.SafeIdle,
                "持续凭证失败测试未进入SafeIdle。");
            var exhausted = 0;
            var finished = 0;
            var supervisor = new DaqRecoveryTerminalSupervisor();
            supervisor.TryCompleteOrSchedule(
                () => transaction.TryTerminal(),
                () => transaction.CurrentOutcome != DaqRecoveryTransaction.Outcome.Terminal,
                () => { },
                _ => Interlocked.Increment(ref exhausted),
                CancellationToken.None,
                "PersistentTerminalReceipt;SafeIdleOnly",
                maxAttempts: 2,
                initialDelayMs: 1,
                maxDelayMs: 2,
                onFinished: () => Interlocked.Increment(ref finished));
            supervisor.RetryTask.GetAwaiter().GetResult();
            Assert(exhausted == 1 &&
                   finished == 1 &&
                   !supervisor.RetryScheduled &&
                   transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.SafeIdle &&
                   transaction.SafeIdleDecisionCount == 1 &&
                   seam.SafetyOffCount == 1 &&
                   seam.PowerDisableCount == 1 &&
                   !transaction.SystemFault && !transaction.BatchRestart &&
                   !transaction.ProcessRestart && !transaction.RetryMissingOff(),
                $"持续终态凭证失败错误升级或重复动作：Outcome={transaction.CurrentOutcome};" +
                $"Exhausted={exhausted};Off={seam.SafetyOffCount};Power={seam.PowerDisableCount}");
        }

        private static void DaqTerminalCheckpointFailureIsSupervisedRetry()
        {
            var seam = new DaqTransactionSeam
            {
                FailTerminalCheckpointOnce = true
            };
            var transaction = seam.CreateTransaction(4, 5, 10);
            CompleteDaqTransactionToRejoining(transaction);
            Assert(transaction.PublishCommittedCheckpoint(),
                "生产事务未取得Committed稳定aggregate凭证。");
            var committedAggregate = transaction.CommittedAggregateVersion;
            var preemptedOwnershipToken = new CancellationTokenSource();
            preemptedOwnershipToken.Cancel();
            var ownerReleased = 0;
            var finished = 0;
            var supervisor = new DaqRecoveryTerminalSupervisor();

            // This calls EpbManager's actual production terminal entry point,
            // not a test-only retry loop.  The ownership token may already be
            // cancelled by preemption, but the same owner must still retry
            // its failed terminal receipt and only then release its lease.
            Assert(!EpbManager.ScheduleDaqTerminalRetryProduction(
                    supervisor,
                    () => transaction.TryTerminal(),
                    () => transaction.CurrentOutcome != DaqRecoveryTransaction.Outcome.Terminal,
                    () => Interlocked.Increment(ref ownerReleased),
                    _ => { },
                    "DaqProductionTerminalRetryExhausted",
                    maxAttempts: 4,
                    initialDelayMs: 1,
                    maxDelayMs: 4,
                    onFinished: () => Interlocked.Increment(ref finished)),
                "首次终态凭证故障未由生产supervisor转入异步重试。");
            supervisor.RetryTask.GetAwaiter().GetResult();
            Assert(ownerReleased == 1 &&
                   finished == 1 &&
                   !supervisor.RetryScheduled &&
                   transaction.CurrentOutcome == DaqRecoveryTransaction.Outcome.Terminal &&
                   transaction.Phase == DaqRecoveryPhase.Terminal &&
                   transaction.TerminalAggregateVersion > committedAggregate &&
                   seam.TerminalCheckpointAttempts == 2 &&
                   seam.LastTerminalStage == DaqRecoveryPhase.Terminal,
                "自动重试未形成Stage=Terminal且M>N的终态aggregate凭证。");
            Assert(!transaction.SystemFault &&
                   !transaction.BatchRestart &&
                   !transaction.ProcessRestart,
                "终态凭证重试错误地触发了升级。");
            var submitCount = seam.TrySubmitCount;
            Assert(!transaction.RetryMissingOff() &&
                   seam.TrySubmitCount == submitCount &&
                   preemptedOwnershipToken.IsCancellationRequested,
                "自动Terminal后RetryMissingOff产生了新的DO receipt。");
        }

        private static void RecoveryAggregateStoreCopiesSourcesAndRetainsVersions()
        {
            var store = new RecoveryAggregateStore();
            var runId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var incidentId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var contract = new RecoveryContractSnapshot(
                incidentId,
                runId,
                1,
                ownerId,
                RecoveryOwnerKind.HydraulicGroupRecovery,
                RecoveryTargetPhase.Formal,
                "HydraulicSoftwareRecovery",
                now,
                now.AddSeconds(30),
                new[] { 4 });
            var state = new ChannelRuntimeStateChangedEvent
            {
                Channel = 4,
                State = ChannelRuntimeState.Recovering,
                ReasonCode = "Recovery",
                ReasonText = "source",
                AffectedChannels = new[] { 4 },
                TimestampUtc = now,
                CorrelationId = incidentId,
                RunId = runId,
                RunEpoch = 1,
                Enabled = true,
                RecoveryOwnerKind = RecoveryOwnerKind.HydraulicGroupRecovery,
                RecoveryOwnerId = ownerId,
                RecoveryOwnerGeneration = 1,
                RecoveryTargetPhase = RecoveryTargetPhase.Formal
            };
            var lease = new WatchdogRecoveryLeaseSnapshot(
                1,
                "HydraulicSoftwareRecovery",
                1,
                new[] { 4 },
                true,
                false);
            var logical = new LogicalQuiescenceSnapshot
            {
                ActiveBatchId = runId,
                DaqRecoveryCount = 1,
                ChannelProgress = new[]
                {
                    new WatchdogChannelProgressSnapshot { Channel = 4, State = "Recovering" }
                }
            };
            store.PublishOwnershipSource(
                new[] { state },
                new[] { contract },
                new[] { lease });
            store.PublishLogicalSource(logical);
            var committed = store.PublishDaqCheckpoint(
                1,
                DaqRecoveryPhase.Committed.ToString(),
                12,
                now,
                now.AddSeconds(30),
                DaqRecoveryPhase.Committed.ToString(),
                12,
                now,
                now.AddSeconds(30));

            state.State = ChannelRuntimeState.AlarmStopped;
            state.AffectedChannels[0] = 10;
            logical.ChannelProgress[0].State = "AlarmStopped";
            var capturedCommitted = store.Capture();
            Assert(capturedCommitted.IsStable &&
                   capturedCommitted.Version == committed.Version &&
                   capturedCommitted.ChannelStates.Count == 1 &&
                   capturedCommitted.ChannelStates[0].State == ChannelRuntimeState.Recovering &&
                   capturedCommitted.ChannelStates[0].AffectedChannels[0] == 4 &&
                   capturedCommitted.Logical.ChannelProgress[0].State == "Recovering",
                "RecoveryAggregateStore未隔离发布后的输入对象变异。");

            var terminal = store.PublishDaqTerminal(
                DaqRecoveryPhase.Terminal.ToString(),
                13,
                now.AddSeconds(1),
                now.AddSeconds(31));
            Assert(terminal.Stable && terminal.Version > committed.Version,
                "Terminal aggregate版本未严格大于Committed版本。");
            var capturedTerminal = store.Capture();
            Assert(capturedTerminal.RetainedCommittedProgressVersion == 12 &&
                   capturedTerminal.RetainedTerminalProgressVersion == 13 &&
                   capturedTerminal.RetainedTerminalStage == DaqRecoveryPhase.Terminal.ToString() &&
                   capturedTerminal.RetainedCommittedAggregateVersion > 0 &&
                   capturedTerminal.RetainedTerminalAggregateVersion >
                       capturedTerminal.RetainedCommittedAggregateVersion,
                "Committed/Terminal retained证据未同时保留。");

            capturedTerminal.ChannelStates[0].State = ChannelRuntimeState.Completed;
            Assert(store.Capture().ChannelStates[0].State == ChannelRuntimeState.Recovering,
                "Capture返回对象不是独立深拷贝。");
        }

        private static void RecoveryAggregateStoreConcurrentCaptureIsStable()
        {
            var store = new RecoveryAggregateStore();
            var errors = new ConcurrentQueue<string>();
            const int writers = 8;
            const int readers = 8;
            const int capturesPerReader = 12500;
            var start = new CountdownEvent(writers + readers);
            var tasks = new List<Task>(writers + readers);

            for (var writer = 0; writer < writers; writer++)
            {
                var writerIndex = writer;
                tasks.Add(Task.Run(() =>
                {
                    start.Signal();
                    Assert(start.Wait(TimeSpan.FromSeconds(10)), "aggregate writer barrier未就绪。");
                    var channel = writerIndex + 1;
                    for (var iteration = 0; iteration < 160; iteration++)
                    {
                        var runId = Guid.NewGuid();
                        var ownerId = Guid.NewGuid();
                        var incidentId = Guid.NewGuid();
                        var now = DateTime.UtcNow;
                        var contract = new RecoveryContractSnapshot(
                            incidentId,
                            runId,
                            1,
                            ownerId,
                            RecoveryOwnerKind.DaqRecovery,
                            RecoveryTargetPhase.Formal,
                            "DaqSoftwareRecovery",
                            now,
                            now.AddSeconds(30),
                            new[] { channel });
                        var recovering = new ChannelRuntimeStateChangedEvent
                        {
                            Channel = channel,
                            State = ChannelRuntimeState.Recovering,
                            ReasonCode = "ConcurrentRecovery",
                            RunId = runId,
                            RunEpoch = 1,
                            CorrelationId = incidentId,
                            Enabled = true,
                            RecoveryOwnerKind = RecoveryOwnerKind.DaqRecovery,
                            RecoveryOwnerId = ownerId,
                            RecoveryOwnerGeneration = 1,
                            RecoveryTargetPhase = RecoveryTargetPhase.Formal
                        };
                        var lease = new WatchdogRecoveryLeaseSnapshot(
                            iteration + 1L + writerIndex * 10000L,
                            "DaqSoftwareRecovery",
                            1,
                            new[] { channel },
                            true,
                            false);
                        store.PublishOwnershipSource(
                            new[] { recovering },
                            new[] { contract },
                            new[] { lease });
                        store.PublishDaqCheckpoint(
                            1,
                            DaqRecoveryPhase.Rejoining.ToString(),
                            iteration + 1,
                            now,
                            now.AddSeconds(30));
                        var terminal = recovering.Clone();
                        terminal.State = ChannelRuntimeState.Completed;
                        terminal.RecoveryOwnerKind = RecoveryOwnerKind.None;
                        terminal.RecoveryOwnerId = Guid.Empty;
                        terminal.RecoveryOwnerGeneration = 0;
                        terminal.RecoveryTargetPhase = RecoveryTargetPhase.None;
                        store.PublishOwnershipSource(
                            new[] { terminal },
                            Array.Empty<RecoveryContractSnapshot>(),
                            Array.Empty<WatchdogRecoveryLeaseSnapshot>());
                    }
                }));
            }

            for (var reader = 0; reader < readers; reader++)
            {
                tasks.Add(Task.Run(() =>
                {
                    start.Signal();
                    Assert(start.Wait(TimeSpan.FromSeconds(10)), "aggregate reader barrier未就绪。");
                    long previousVersion = -1;
                    for (var capture = 0; capture < capturesPerReader; capture++)
                    {
                        var snapshot = store.Capture();
                        if (snapshot.Version < previousVersion)
                            errors.Enqueue("aggregate版本回退：" + previousVersion + "->" + snapshot.Version);
                        previousVersion = snapshot.Version;
                        if (!snapshot.IsStable)
                        {
                            errors.Enqueue("出现不稳定aggregate快照。");
                            continue;
                        }
                        foreach (var state in snapshot.ChannelStates.Where(item =>
                                     item.State == ChannelRuntimeState.Recovering))
                        {
                            var contract = snapshot.Contracts.FirstOrDefault(item =>
                                item.RunId == state.RunId &&
                                item.RunEpoch == state.RunEpoch &&
                                item.OwnerId == state.RecoveryOwnerId &&
                                item.Channels.Contains(state.Channel));
                            var lease = snapshot.RegistryLeases.FirstOrDefault(item =>
                                item.RunEpoch == state.RunEpoch &&
                                item.IsBound && !item.IsTerminal &&
                                item.Channels.Contains(state.Channel));
                            if (contract == null || lease == null)
                                errors.Enqueue("Recovering缺少同身份contract/bound lease。");
                        }
                        foreach (var state in snapshot.ChannelStates.Where(item =>
                                     item.State != ChannelRuntimeState.Recovering))
                        {
                            if (snapshot.Contracts.Any(item => item.Channels.Contains(state.Channel)) ||
                                snapshot.RegistryLeases.Any(item => item.Channels.Contains(state.Channel)))
                                errors.Enqueue("终态仍残留active owner/lease。");
                        }
                    }
                }));
            }

            Assert(start.Wait(TimeSpan.FromSeconds(10)), "aggregate 8写8读 barrier未就绪。");
            Task.WaitAll(tasks.ToArray());
            Assert(errors.IsEmpty, "并发aggregate捕获不满足稳定/身份不变量：" +
                string.Join(";", errors.Take(5)));
        }

        private static void RecoveryAggregateStoreOperationalSourcesAreSingleRevision()
        {
            var store = new RecoveryAggregateStore();
            var infrastructure = new InfrastructureRecoverySource
            {
                ActiveRecovery = true,
                RunId = Guid.NewGuid(),
                RunEpoch = 7,
                RecoveryIncident = "incident-A",
                Stage = DaqRecoveryPhase.Rejoining.ToString(),
                ProgressVersion = 41,
                RecoveringChannels = new[] { 4, 10 },
                PermanentAlarmedChannels = new[] { 12 },
                PermanentAlarmReasons = new Dictionary<int, string> { [12] = "latched" }
            };
            var power = new PowerRecoverySource
            {
                PowerDisablePending = true,
                PowerOffUnconfirmed = true,
                PowerDisablePendingGroups = new[] { 2 },
                PowerDisableSinceUtcTicks = DateTime.UtcNow.Ticks
            };
            var timer = new TimerRecoverySource
            {
                OrphanPaused = true,
                PauseSinceUtcTicks = DateTime.UtcNow.Ticks,
                OrphanPausedChannels = new[] { 10 }
            };

            var committed = store.PublishOperationalSources(infrastructure, power, timer);
            infrastructure.Stage = "mutated-after-commit";
            infrastructure.RecoveringChannels[0] = 99;
            power.PowerDisablePendingGroups[0] = 99;
            timer.OrphanPausedChannels[0] = 99;

            var captured = store.Capture();
            Assert(captured.Version == committed.Version && captured.IsStable,
                "operational source提交后没有形成稳定单一版本。");
            Assert(captured.Infrastructure.ActiveRecovery &&
                   captured.Infrastructure.RunEpoch == 7 &&
                   captured.Infrastructure.Stage == DaqRecoveryPhase.Rejoining.ToString() &&
                   captured.Infrastructure.RecoveringChannels.SequenceEqual(new[] { 4, 10 }) &&
                   captured.Infrastructure.PermanentAlarmedChannels.SequenceEqual(new[] { 12 }) &&
                   captured.Infrastructure.PermanentAlarmReasons[12] == "latched",
                "infrastructure source未深拷贝或字段不完整。");
            Assert(captured.Power.PowerDisablePending &&
                   captured.Power.PowerDisablePendingGroups.SequenceEqual(new[] { 2 }) &&
                   captured.Timer.OrphanPaused &&
                   captured.Timer.OrphanPausedChannels.SequenceEqual(new[] { 10 }),
                "power/timer source未深拷贝或字段不完整。");

            var next = store.PublishOperationalSources(
                new InfrastructureRecoverySource
                {
                    ActiveRecovery = false,
                    RunId = captured.Infrastructure.RunId,
                    RunEpoch = 7,
                    Stage = DaqRecoveryPhase.Terminal.ToString(),
                    ProgressVersion = 42
                },
                new PowerRecoverySource { OutputsConfirmedOff = true },
                new TimerRecoverySource());
            Assert(next.Version > committed.Version &&
                   next.Infrastructure.Stage == DaqRecoveryPhase.Terminal.ToString() &&
                   next.Power.OutputsConfirmedOff &&
                   !next.Timer.OrphanPaused,
                "operational source更新没有生成新的整体版本。");
        }

        private static void DaqPostCutoffPowerOnIsNotDisablePending()
        {
            Assert(EpbManager.ShouldProjectPowerDisablePending(
                       cutoffCompleted: false,
                       disableTaskPending: true,
                       expectedOutputEnabled: false,
                       telemetryOutputEnabled: false,
                       orphanRecovery: false),
                "真实OFF任务在截止前未投影为PowerDisablePending。");
            Assert(EpbManager.ShouldProjectPowerDisablePending(
                       cutoffCompleted: false,
                       disableTaskPending: false,
                       expectedOutputEnabled: true,
                       telemetryOutputEnabled: true,
                       orphanRecovery: false),
                "截止前仍带电组未投影为PowerDisablePending。");
            Assert(!EpbManager.ShouldProjectPowerDisablePending(
                       cutoffCompleted: true,
                       disableTaskPending: false,
                       expectedOutputEnabled: true,
                       telemetryOutputEnabled: true,
                       orphanRecovery: false),
                "CutoffCompleted后的计划恢复上电仍被误报为旧关闭超时。");
            Assert(EpbManager.ShouldProjectPowerDisablePending(
                       cutoffCompleted: true,
                       disableTaskPending: false,
                       expectedOutputEnabled: true,
                       telemetryOutputEnabled: true,
                       orphanRecovery: true),
                "无owner孤儿恢复带电没有保持失效安全投影。");
        }

        private static void CompleteDaqTransactionToRejoining(DaqRecoveryTransaction transaction)
        {
            Assert(transaction.SubmitOffBatch(), "DAQ事务准入未完成。");
            foreach (var receipt in transaction.OffReceipts)
                Assert(transaction.ReportPhysicalCompletion(
                           receipt.Channel,
                           receipt.CommandId,
                           true) || transaction.Phase >= DaqRecoveryPhase.DoOffConfirmed,
                    "DAQ事务物理完成登记失败。");
            foreach (var phase in new[]
                     {
                         DaqRecoveryPhase.PowerOffSubmitted,
                         DaqRecoveryPhase.PowerOffConfirmed,
                         DaqRecoveryPhase.CutoffCompleted,
                         DaqRecoveryPhase.DaqRestartStarted,
                         DaqRecoveryPhase.FirstFreshBatch,
                         DaqRecoveryPhase.PressureRevalidated,
                         DaqRecoveryPhase.PersistenceBoundaryClosed,
                         DaqRecoveryPhase.Validating,
                         DaqRecoveryPhase.Rejoining
                     })
                Assert(transaction.TryAdvance(phase), "DAQ事务阶段推进失败：" + phase);
        }

        private static DaqRecoveryTransaction.OffReceipt DaqRecoveryTransactionReceipt(int channel)
            => new DaqRecoveryTransaction.OffReceipt(
                channel,
                Guid.NewGuid(),
                true,
                false,
                string.Empty);

        private static void AdmissionFailuresFailClosed()
        {
            foreach (var failure in new[]
                     {
                         "reserve", "factory", "scheduler", "bind", "publish", "observer"
                     })
            {
                var seam = new FakeSeam { Failure = failure };
                var coordinator = seam.CreateCoordinator();
                var result = Begin(
                    coordinator,
                    seam,
                    _ => () => Task.CompletedTask,
                    out var incident);

                Assert(result == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                       incident == null &&
                       coordinator.ActiveCount == 0 &&
                       seam.Registry.ActiveCount == 0,
                    $"故障{failure}未拒绝或残留active contract/lease。");
                Assert(seam.OffCount >= 1 && seam.TerminalCount == seam.ChannelCount,
                    $"故障{failure}未执行完整OFF→安全终态：OFF={seam.OffCount}; " +
                    $"Terminal={seam.TerminalCount}/{seam.ChannelCount}");
                Assert(seam.IndexOf("off") >= 0 &&
                       seam.IndexOf("safe-terminal") > seam.IndexOf("off"),
                    $"故障{failure}安全顺序错误：{seam.EventsText}");
            }

            var rejectedSeam = new FakeSeam();
            var rejected = rejectedSeam.CreateCoordinator().TryBegin(
                "HydraulicSoftwareRecovery",
                Guid.NewGuid(),
                1,
                RecoveryOwnerKind.HydraulicGroupRecovery,
                RecoveryTargetPhase.Formal,
                Guid.Empty,
                new[] { 4, 10 },
                _ => () => Task.CompletedTask,
                rejectedSeam.PublishRecovering,
                out _);
            Assert(rejected == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                   rejectedSeam.Events.IsEmpty,
                "无owner身份未在任何reserve/硬件动作前拒绝。");
        }

        private static void WorkerIsSuspendedUntilExplicitStart()
        {
            var seam = new FakeSeam();
            var coordinator = seam.CreateCoordinator();
            var bodyStarted = 0;
            var result = Begin(
                coordinator,
                seam,
                _ => () =>
                {
                    Interlocked.Increment(ref bodyStarted);
                    return Task.CompletedTask;
                },
                out var incident);

            Assert(result == RecoveryIncidentCoordinator.BeginResult.Created &&
                   incident != null && bodyStarted == 0,
                "Recovering发布前没有返回真实suspended worker，或body提前执行。");
            Assert(seam.IndexOf("register") >= 0 &&
                   seam.IndexOf("bind") > seam.IndexOf("register") &&
                   seam.IndexOf("publish") > seam.IndexOf("bind") &&
                   seam.IndexOf("observe") > seam.IndexOf("publish"),
                $"Reserve/Bind/Publish/Observe顺序错误：{seam.EventsText}");

            Assert(incident.Start(), "显式Start未获准执行真实worker。");
            AwaitWorker(incident.WorkerTask);
            Assert(bodyStarted == 1, "显式Start后真实body未且仅未执行一次。");
            Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0 &&
                   seam.OffCount == 1 && seam.TerminalCount == seam.ChannelCount &&
                   seam.LastTerminalReason == "RecoveryWorkerCompletedWithoutTerminal",
                "漏终态worker未自动执行OFF→SafeIdleFault并释放contract/lease。");
        }

        private static void WorkerBodyFailuresFailClosed()
        {
            foreach (var mode in new[] { "sync", "null", "async" })
            {
                var seam = new FakeSeam();
                var coordinator = seam.CreateCoordinator();
                var result = Begin(
                    coordinator,
                    seam,
                    _ =>
                    {
                        if (mode == "sync")
                            return (Func<Task>)(() => throw new InvalidOperationException("sync body"));
                        if (mode == "null")
                            return () => null;
                        return async () =>
                        {
                            await Task.Yield();
                            throw new InvalidOperationException("async body");
                        };
                    },
                    out var incident);

                Assert(result == RecoveryIncidentCoordinator.BeginResult.Created,
                    $"{mode} body未成功建立真实incident。");
                Assert(incident.Start(), $"{mode} body无法Start。");
                try { AwaitWorker(incident.WorkerTask); }
                catch (Exception) { }
                Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0 &&
                       seam.TerminalCount == seam.ChannelCount && seam.OffCount >= 1,
                    $"{mode} body异常后未安全收口：Active={coordinator.ActiveCount}; " +
                    $"Lease={seam.Registry.ActiveCount}; Terminal={seam.TerminalCount}/{seam.ChannelCount}");
                Assert(seam.LastTerminalReason?.StartsWith(
                           "RecoveryWorkerFailed:",
                           StringComparison.Ordinal) == true &&
                       !string.Equals(
                           seam.LastTerminalReason,
                           "RecoveryTerminalPublisherMissing",
                           StringComparison.Ordinal) &&
                       seam.LastTerminalDetail?.Contains("Incident=") == true &&
                       seam.LastTerminalDetail?.Contains("Scope=") == true,
                    $"{mode} body异常未保留真实终态原因：" +
                    $"Reason={seam.LastTerminalReason};Detail={seam.LastTerminalDetail}");
            }
        }

        private static void StartFailureFailClosed()
        {
            var seam = new FakeSeam { Failure = "start" };
            var coordinator = seam.CreateCoordinator();
            var result = Begin(
                coordinator,
                seam,
                _ => () => Task.CompletedTask,
                out var incident);
            Assert(result == RecoveryIncidentCoordinator.BeginResult.Created && incident != null,
                "Start故障测试未建立incident。");
            Assert(!incident.Start(), "Start故障注入仍返回成功。");
            Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0 &&
                   seam.OffCount >= 1 && seam.TerminalCount == seam.ChannelCount,
                "Start故障后未完成OFF、全通道安全终态和lease清理。");
        }

        private static void TerminalPublicationFailuresFailClosed()
        {
            foreach (var mode in new[] { "throw", "partial" })
            {
                var seam = new FakeSeam();
                var coordinator = seam.CreateCoordinator();
                RecoveryIncidentCoordinator.Incident current = null;
                var result = Begin(
                    coordinator,
                    seam,
                    _ => () =>
                    {
                        var completed = current.CompleteAfterTerminal(contract =>
                        {
                            seam.Events.Enqueue("terminal-callback");
                            if (mode == "throw")
                                throw new InvalidOperationException("terminal callback");
                            seam.MarkTerminal(contract, onlyFirst: true);
                        });
                        Assert(completed,
                            $"终态{mode}回调没有由生产安全回退补齐。");
                        return Task.CompletedTask;
                    },
                    out var incident);
                Assert(result == RecoveryIncidentCoordinator.BeginResult.Created,
                    $"终态{mode}测试未建立incident。");
                current = incident;
                Assert(incident.Start(), $"终态{mode}测试无法Start。");
                AwaitWorker(incident.WorkerTask);
                Assert(coordinator.ActiveCount == 0 &&
                       seam.Registry.ActiveCount == 0 &&
                       seam.TerminalCount == seam.ChannelCount,
                    $"终态{mode}没有由生产安全回退补齐：{seam.EventsText}");
                Assert(seam.IndexOf("off") >= 0 &&
                       seam.IndexOf("safe-terminal") > seam.IndexOf("off"),
                    $"终态{mode}未保持OFF先于安全终态。");
            }
        }

        private static void RecoveryCallbacksRunOutsideCoordinatorGate()
        {
            var seam = new FakeSeam { ProbeCoordinatorGate = true };
            var coordinator = seam.CreateCoordinator();
            var result = Begin(
                coordinator,
                seam,
                _ => () => Task.CompletedTask,
                out var incident);

            Assert(result == RecoveryIncidentCoordinator.BeginResult.Created &&
                   incident != null,
                "回调gate外测试未建立incident。");
            Assert(seam.ReentryTimeoutCount == 0,
                "至少一个生产端口回调仍在协调gate内调用：" + seam.EventsText);
            Assert(incident.Start(), "回调gate外测试无法Start。");
            AwaitWorker(incident.WorkerTask);
            Assert(seam.ReentryTimeoutCount == 0 &&
                   coordinator.ActiveCount == 0 &&
                   seam.Registry.ActiveCount == 0 &&
                   seam.TerminalCount == seam.ChannelCount,
                "回调重入或终态收口残留：" + seam.EventsText);
        }

        private static void RegisterPartialWriteIsCompensated()
        {
            var seam = new FakeSeam { Failure = "register-partial" };
            var coordinator = seam.CreateCoordinator();
            var result = Begin(
                coordinator,
                seam,
                _ => () => Task.CompletedTask,
                out var incident);

            Assert(result == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                   incident == null,
                "Register部分写入后抛异常未拒绝begin。");
            Assert(seam.RegisteredIncident == null &&
                   seam.UnregisterCount >= 1 &&
                   coordinator.ActiveCount == 0 &&
                   seam.Registry.ActiveCount == 0,
                "Register部分写入后存在孤儿contract/lease：" + seam.EventsText);
            Assert(seam.OffCount >= 1 &&
                   seam.TerminalCount == seam.ChannelCount,
                "Register部分写入后未完成OFF→全通道安全终态。");
        }

        private static void ManagerPowerOwnerChecksActualEntry()
        {
            var seam = new FakeSeam();
            var coordinator = seam.CreateCoordinator();
            var run = Guid.NewGuid();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var manager = (EpbManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(EpbManager));
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(EpbManager).GetField(name, flags).SetValue(manager, value);
            Set("_recoveryAdmissionGate", new object());
            Set("_recoveryIncidentCoordinator", coordinator);
            Set("_activeBatchId", run);
            Set("_runEpoch", 1L);
            var result = coordinator.TryBegin("AffectedGroupReset", run, 1,
                RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal, Guid.NewGuid(),
                new[] { 4 }, Enumerable.Range(1, 6), _ => () => release.Task,
                seam.PublishRecovering, out var incident);
            Assert(result == RecoveryIncidentCoordinator.BeginResult.Created, "测试恢复未登记。");
            var owner = new EpbManager.RecoveryIncidentHandle(manager, incident);
            var check = typeof(EpbManager).GetMethod("AssertPowerRecoveryOwnerCurrent", flags);
            bool Rejected(int channel)
            {
                try { check.Invoke(manager, new object[] { owner, new[] { channel } }); return false; }
                catch (System.Reflection.TargetInvocationException ex)
                    when (ex.InnerException is InvalidOperationException &&
                          ex.InnerException.Message == "PowerRetryRecoveryOwnerInvalid") { return true; }
            }
            try
            {
                Assert(Rejected(4), "未启动owner提前获得电源权限。");
                Assert(incident.Start() && !Rejected(4) && Rejected(5), "精确业务成员授权不正确。");
                Set("_activeBatchId", Guid.NewGuid());
                Assert(Rejected(4), "旧run电源owner未被拒绝。");
                Set("_activeBatchId", run);
                Set("_runEpoch", 2L);
                Assert(Rejected(4), "旧epoch电源owner未被拒绝。");
                Set("_runEpoch", 1L);
                Set("_energizationRevoked", 1);
                Assert(Rejected(4), "撤权后仍放行电源owner。");
                Set("_energizationRevoked", 0);
                Set("_processRestartRequired", 1);
                Assert(Rejected(4), "要求进程重启时仍放行电源owner。");
                Set("_processRestartRequired", 0);
                Assert(incident.CompleteAfterTerminal(seam.PublishTerminal) && Rejected(4),
                    "已提交终态但尚未退出的owner仍获准。");
            }
            finally { release.TrySetResult(true); }
            AwaitWorker(incident.WorkerTask);
            Assert(Rejected(4), "已退休owner仍获准。");
        }

        private static void StartupChildRequiresLiveParent()
        {
            foreach (var retireBeforeStart in new[] { false, true })
            {
                var seam = new FakeSeam();
                var coordinator = seam.CreateCoordinator();
                var run = Guid.NewGuid();
                Assert(coordinator.TryReserveStartup(run, 1, Enumerable.Range(1, 6), out var parent),
                    "父启动预留失败。");
                var parentWorker = new TaskCompletionSource<bool>();
                parent.RetireAfter(parentWorker.Task);
                var executed = 0;
                var bodyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var bodyRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                foreach (var wrongEpoch in new[] { false, true })
                {
                    var rejected = coordinator.TryBegin("AffectedGroupReset", run, wrongEpoch ? 2 : 1,
                        RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal, Guid.NewGuid(),
                        new[] { 4 }, wrongEpoch ? new[] { 4 } : new[] { 4, 7 },
                        _ => () => { Interlocked.Increment(ref executed); return Task.CompletedTask; },
                        seam.PublishRecovering, out _, startupParent: parent);
                    Assert(rejected == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                           executed == 0 && seam.RegisteredIncident == null,
                        "错误父代次或超出父范围的子恢复被登记。");
                }
                var result = coordinator.TryBegin("AffectedGroupReset", run, 1,
                    RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal, Guid.NewGuid(),
                    new[] { 4 }, Enumerable.Range(1, 6), _ => () =>
                    {
                        Interlocked.Increment(ref executed);
                        bodyEntered.TrySetResult(true);
                        return bodyRelease.Task;
                    }, seam.PublishRecovering, out var child, startupParent: parent);
                Assert(result == RecoveryIncidentCoordinator.BeginResult.Created,
                    "精确父预留无法登记关联恢复。");
                Assert(!coordinator.TryReserveStartup(run, 1, new[] { 5 }, out _),
                    "关联恢复放行了无关启动。");
                if (retireBeforeStart) parentWorker.SetResult(true);
                Assert(child.Start(), "子恢复启动信号未释放。");
                if (!retireBeforeStart)
                {
                    try
                    {
                        Assert(bodyEntered.Task.Wait(TimeSpan.FromSeconds(5)), "子恢复未进入主体。");
                        Assert(!coordinator.HasSafetyConflict(run, 1, new[] { 4 }, child) &&
                               coordinator.HasSafetyConflict(run, 1, new[] { 5 }, child) &&
                               coordinator.HasSafetyConflict(run, 1, new[] { 4 }),
                            "关联父例外扩大到安全成员或普通启动，或拒绝精确子owner。");
                        parentWorker.SetResult(true);
                        Assert(coordinator.HasSafetyConflict(run, 1, new[] { 4 }, child) &&
                               !coordinator.TryReserveStartup(run, 1, new[] { 4 }, out _),
                            "父退出后子owner例外未撤销或子任务占用提前释放。");
                    }
                    finally { bodyRelease.TrySetResult(true); }
                }
                try { child.WorkerTask.GetAwaiter().GetResult(); }
                catch (InvalidOperationException) when (retireBeforeStart) { }
                Assert(executed == (retireBeforeStart ? 0 : 1),
                    "父退休后执行了主体，或存活父的子恢复未执行。");
                Assert(coordinator.ActiveCount == 0, "子恢复未按安全终态收敛。");
                parentWorker.TrySetResult(true);
                Assert(!coordinator.IsStartupReservationCurrent(parent, run, 1, new[] { 4 }),
                    "父退出后仍保留启动身份。");
            }
        }

        private static void RecoverySafetyConflictUsesExactLiveOwner()
        {
            var seam = new FakeSeam();
            var coordinator = seam.CreateCoordinator();
            var run = Guid.NewGuid();
            Assert(!coordinator.TryReserveStartup(run, 1, new[] { 0, 4 }, out _) &&
                   !coordinator.TryReserveStartup(run, 1, Array.Empty<int>(), out _) &&
                   !coordinator.TryReserveStartup(Guid.Empty, 1, new[] { 4 }, out _) &&
                   !coordinator.TryReserveStartup(run, 0, new[] { 4 }, out _), "非法启动预留范围或身份被接受。");
            var startupScope = new[] { 4 };
            Assert(coordinator.TryReserveStartup(run, 1, startupScope, out var startup), "启动预留失败。");
            Assert(startup.RunId == run && startup.RunEpoch == 1, "启动预留未冻结运行身份。");
            Assert(coordinator.IsStartupReservationCurrent(startup, run, 1, new[] { 4 }) &&
                   !coordinator.IsStartupReservationCurrent(startup, run, 2, new[] { 4 }) &&
                   !coordinator.IsStartupReservationCurrent(startup, run, 1, new[] { 5 }) &&
                   !new FakeSeam().CreateCoordinator().IsStartupReservationCurrent(startup, run, 1, new[] { 4 }),
                "启动预留精确身份/范围校验失效。");
            startupScope[0] = 7;
            var startupWorker = new TaskCompletionSource<bool>();
            startup.RetireAfter(startupWorker.Task);
            Assert(!coordinator.TryReserveStartup(run, 1, new[] { 4 }, out _), "预留范围受调用方修改影响。");
            Assert(coordinator.HasSafetyConflict(run, 1, new[] { 4 }) &&
                   coordinator.HasSafetyConflict(Guid.NewGuid(), 2, new[] { 4 }) &&
                   !coordinator.HasSafetyConflict(run, 1, new[] { 7 }),
                "冲突查询遗漏启动预留或通过更换运行身份绕过物理占用。");
            Assert(coordinator.TryReserveStartup(run, 1, new[] { 7 }, out var independent), "不重叠启动被阻断。");
            independent.RetireAfter(Task.CompletedTask);
            var startupBlocked = coordinator.TryBegin("AffectedGroupReset", run, 1,
                RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal, Guid.NewGuid(),
                new[] { 4 }, Enumerable.Range(1, 6), _ => () => Task.CompletedTask,
                seam.PublishRecovering, out _);
            Assert(startupBlocked == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                   seam.RegisteredIncident == null, "启动预检尚未退出时恢复已登记。");
            Assert(!startupWorker.Task.Wait(1) && !coordinator.TryReserveStartup(run, 1, new[] { 4 }, out _),
                "观察超时释放了实际未退出的启动。");
            var duplicateRetirementRejected = false;
            try { startup.RetireAfter(Task.CompletedTask); }
            catch (InvalidOperationException) { duplicateRetirementRejected = true; }
            Assert(duplicateRetirementRejected && !coordinator.TryReserveStartup(run, 1, new[] { 4 }, out _),
                "替换退休任务提前释放预留。");
            startupWorker.SetResult(true);
            Assert(!coordinator.IsStartupReservationCurrent(startup, run, 1, new[] { 4 }), "已退休启动仍有授权身份。");
            Assert(!coordinator.HasSafetyConflict(run, 1, new[] { 4 }), "实际退出后查询仍保留旧启动占用。");
            Assert(coordinator.TryReserveStartup(run, 1, new[] { 4 }, out var replacement), "实际退出后预留未释放。");
            replacement.RetireAfter(Task.CompletedTask);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reservationObserved = false;
            seam.BeforeReserve = () =>
            {
                Assert(seam.RegisteredIncident == null &&
                       coordinator.HasSafetyConflict(run, 1, new[] { 5 }) &&
                       !coordinator.HasSafetyConflict(run, 1, new[] { 7 }),
                    "Reserve阶段的预留范围没有阻止冲突启动。");
                reservationObserved = true;
            };
            var result = coordinator.TryBegin("AffectedGroupReset", run, 1,
                RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal, Guid.NewGuid(),
                new[] { 4 }, Enumerable.Range(1, 6), _ => () => release.Task,
                seam.PublishRecovering, out var incident);
            Assert(reservationObserved && result == RecoveryIncidentCoordinator.BeginResult.Created,
                "未观察预留窗口或未建立安全范围事务。");
            try
            {
                Assert(!coordinator.TryReserveStartup(run, 1, new[] { 5 }, out _),
                    "恢复已预留整组时普通启动错误获准。");
                Assert(coordinator.HasSafetyConflict(Guid.NewGuid(), 2, new[] { 5 }) &&
                       !coordinator.HasSafetyConflict(Guid.NewGuid(), 2, new[] { 7 }),
                    "更换运行身份绕过尚未退休恢复的物理占用，或错误阻断另一组。");
                Assert(coordinator.HasSafetyConflict(run, 1, new[] { 5 }) &&
                       !coordinator.HasSafetyConflict(run, 1, new[] { 7 }) &&
                       coordinator.HasSafetyConflict(run, 1, new[] { 4 }, incident),
                    "未识别单通道物理重叠，或未启动owner提前获准。");
                Assert(incident.Start(), "owner无法启动。");
                Assert(!coordinator.HasSafetyConflict(run, 1, new[] { 4 }, incident) &&
                       coordinator.HasSafetyConflict(run, 1, new[] { 5 }, incident) &&
                       coordinator.HasSafetyConflict(run, 2, new[] { 4 }, incident) &&
                       coordinator.HasSafetyConflict(Guid.NewGuid(), 1, new[] { 4 }, incident),
                    "精确owner不能通行或安全成员/其它运行错误通行。");
                Assert(coordinator.HasSafetyConflict(run, 1, new[] { 0 }) &&
                       coordinator.HasSafetyConflict(run, 1, Array.Empty<int>()), "非法范围错误放行。");
                Assert(incident.CompleteAfterTerminal(seam.PublishTerminal) &&
                       !incident.WorkerTask.IsCompleted && coordinator.ActiveCount == 1 &&
                       coordinator.HasSafetyConflict(run, 1, new[] { 4 }, incident) &&
                       coordinator.HasSafetyConflict(run, 1, new[] { 5 }),
                    "终态已提交但worker未退出时，原owner仍可启动或安全范围提前释放。");
            }
            finally { release.TrySetResult(true); }
            AwaitWorker(incident.WorkerTask);
            Assert(!coordinator.HasSafetyConflict(run, 1, new[] { 5 }) &&
                   coordinator.HasSafetyConflict(run, 1, new[] { 4 }, incident),
                "终态后安全范围未释放，或旧owner仍可通行。");
        }

        private static void ManagerAdmissionScopesDoNotGrantSafetyMembersOwnership()
        {
            var fence = new ChannelExecutionFence();
            var neverEnabled = fence.Capture(10);
            Assert(fence.IsUnchanged(neverEnabled) && !fence.IsCurrent(neverEnabled),
                "未启用成员无法保留安全身份，或被授予执行权限。");
            var revoked = fence.Revoke(10);
            Assert(!fence.IsUnchanged(neverEnabled) && fence.IsUnchanged(revoked) &&
                   !fence.IsCurrent(revoked), "撤权身份变化未被检测或错误获得执行许可。");
            var authorized = fence.Authorize(10, 3);
            Assert(!fence.IsUnchanged(revoked) && fence.IsUnchanged(authorized) &&
                   fence.IsCurrent(authorized), "新授权未使旧安全观察失效。");
            fence.Authorize(10, 3);
            Assert(!fence.IsUnchanged(authorized) && !fence.IsUnchanged(default),
                "同run新代次或默认身份错误通过安全观察。");
            // EPB5 is enabled but deliberately absent from the caller's resume
            // cohort (for example, manually paused). EPB10 is disabled.
            var requested = new[] { 4, 4, 10, 0, 13 };
            var physical = new[] { 5, 10, 5, -1, 13 };
            var scopes = EpbManager.BuildRecoveryAdmissionScopes(
                requested, physical, channel => channel != 10);
            Assert(scopes.ownedChannels.SequenceEqual(new[] { 4 }) &&
                   scopes.safetyAffected.SequenceEqual(new[] { 4, 5, 10 }),
                "安全范围中的启用成员被提升为owner，或禁用成员丢失安全覆盖。");
            requested[0] = 12;
            physical[0] = 12;
            Assert(scopes.ownedChannels.SequenceEqual(new[] { 4 }) &&
                   scopes.safetyAffected.SequenceEqual(new[] { 4, 5, 10 }),
                "调用方数组变化改写已冻结的登记范围。");
            var legacy = EpbManager.BuildRecoveryAdmissionScopes(new[] { 4, 10 }, null,
                channel => channel == 4);
            Assert(legacy.ownedChannels.SequenceEqual(new[] { 4 }) &&
                   legacy.safetyAffected.SequenceEqual(new[] { 4, 10 }), "旧调用范围行为改变。");
            var safetyOnly = EpbManager.BuildRecoveryAdmissionScopes(null, new[] { 5 }, _ => true);
            Assert(safetyOnly.ownedChannels.Length == 0 &&
                   safetyOnly.safetyAffected.SequenceEqual(new[] { 5 }),
                "仅有安全范围时凭空建立续测owner。");
            Assert(EpbManager.GetAffectedHydraulicSafetyScope(new[] { 4, 4, 0, 13 })
                       .SequenceEqual(Enumerable.Range(1, 6)) &&
                   EpbManager.GetAffectedHydraulicSafetyScope(new[] { 10 })
                       .SequenceEqual(Enumerable.Range(7, 6)) &&
                   EpbManager.GetAffectedHydraulicSafetyScope(new[] { 4, 10 })
                       .SequenceEqual(Enumerable.Range(1, 12)) &&
                   EpbManager.GetAffectedHydraulicSafetyScope(null).Length == 0,
                "整组安全范围漏掉物理成员或混入未受影响组。");
            var wholeGroup = EpbManager.BuildRecoveryAdmissionScopes(new[] { 4 },
                EpbManager.GetAffectedHydraulicSafetyScope(new[] { 4 }), _ => true);
            Assert(wholeGroup.ownedChannels.SequenceEqual(new[] { 4 }) &&
                   wholeGroup.safetyAffected.SequenceEqual(Enumerable.Range(1, 6)),
                "全组安全登记错误扩大续测授权。");
            Assert(EpbManager.IsHydraulicResumeScopeCovered(1, new[] { 4 },
                new[] { Enumerable.Range(1, 3).ToArray(), Enumerable.Range(4, 3).ToArray() }, ch => ch == 4),
                "同组禁用成员的完整物理映射被拒绝。");
            Assert(!EpbManager.IsHydraulicResumeScopeCovered(1, new[] { 4 },
                new[] { Enumerable.Range(1, 5).ToArray() }, ch => ch == 4), "缺少禁用通道映射仍获准。");
            Assert(!EpbManager.IsHydraulicResumeScopeCovered(1, new[] { 4 },
                new[] { Enumerable.Range(1, 7).ToArray() }, ch => ch == 4), "禁用跨组成员仍获准被操作。");
            Assert(!EpbManager.IsHydraulicResumeScopeCovered(1, new[] { 4 },
                new[] { Enumerable.Range(1, 6).ToArray() }, ch => ch == 4 || ch == 5),
                "同组另一个启用通道未被协调仍获准恢复。");
            Assert(!EpbManager.IsHydraulicResumeScopeCovered(1, new[] { 0, 4 },
                new[] { Enumerable.Range(1, 6).ToArray() }, ch => ch == 4), "无效业务通道被忽略。");
            var contract = new RecoveryContractSnapshot(Guid.NewGuid(), Guid.NewGuid(), 1,
                Guid.NewGuid(), RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal,
                "ScopeTest", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1),
                new[] { 4 }, Enumerable.Range(1, 6));
            Assert(EpbManager.IsRecoveryExecutionScopeContained(contract, new[] { 4 },
                Enumerable.Range(1, 6).ToArray()), "已登记整组物理范围被拒绝。");
            Assert(!EpbManager.IsRecoveryExecutionScopeContained(contract, new[] { 4, 5 },
                Enumerable.Range(1, 6).ToArray()), "安全成员被擅自提升为续测成员。");
            Assert(!EpbManager.IsRecoveryExecutionScopeContained(contract, new[] { 4 },
                new[] { 4, 7 }), "未登记的物理成员进入执行范围。");
            Assert(!EpbManager.IsRecoveryExecutionScopeContained(contract, new[] { 4 },
                new[] { 1, 2 }), "续测成员未被物理安全范围覆盖。");
            Assert(!EpbManager.IsRecoveryExecutionScopeContained(null, new[] { 4 }, new[] { 4 }) &&
                   !EpbManager.IsRecoveryExecutionScopeContained(contract, Array.Empty<int>(), new[] { 4 }),
                "缺少契约或空续测范围获准执行。");
        }

        private static void PowerOffRetryUsesExecutionResult()
        {
            Assert(EpbManager.ShouldRetryPowerOffTask(null, 1), "缺失任务未重试");
            var pending = new TaskCompletionSource<PowerSafetyDisableResult>();
            Assert(!EpbManager.ShouldRetryPowerOffTask(pending.Task, 1), "在途 OFF 被重复提交");
            pending.SetResult(new PowerSafetyDisableResult { ElectricalGroupId = 1, Outcome = PowerSafetyDisableOutcome.TimedOut });
            Assert(EpbManager.ShouldRetryPowerOffTask(pending.Task, 1), "正常结束的超时回执永久阻塞重试");
            Assert(EpbManager.ShouldRetryPowerOffTask(Task.FromResult<PowerSafetyDisableResult>(null), 1), "空回执未重试");
            var now = DateTime.UtcNow;
            var receipt = new PowerSafetyDisableResult
            {
                ElectricalGroupId = 1, OperationGeneration = 2,
                ConfirmedOff = true, Outcome = PowerSafetyDisableOutcome.ConfirmedOff,
                StartedUtc = now, CompletedUtc = now
            };
            Assert(!EpbManager.ShouldRetryPowerOffTask(Task.FromResult(receipt), 1), "有效 OFF 被重复提交");
            Assert(EpbManager.ShouldRetryPowerOffTask(Task.FromResult(receipt), 2), "其它组回执阻止重试");
            receipt.OperationGeneration = 0;
            Assert(EpbManager.ShouldRetryPowerOffTask(Task.FromResult(receipt), 1), "无执行身份回执阻止重试");
            var cancelled = new TaskCompletionSource<PowerSafetyDisableResult>();
            cancelled.SetCanceled();
            Assert(EpbManager.ShouldRetryPowerOffTask(cancelled.Task, 1), "取消任务未重试");
            var failed = Task.FromException<PowerSafetyDisableResult>(new InvalidOperationException("test"));
            Assert(EpbManager.ShouldRetryPowerOffTask(failed, 1), "异常任务未重试");
            var observed = failed.Exception;
        }

        private static void PowerSafetyRejectsMissingHardware()
        {
            var manager = (EpbManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(EpbManager));
            var method = typeof(EpbManager).GetMethod("ConfirmElectricalGroupOffSafetyAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var task = (Task<PowerSafetyDisableResult>)method.Invoke(manager, new object[] { 1, "missing-hardware-test" });
            var result = task.GetAwaiter().GetResult();
            Assert(!result.ConfirmedOff && result.Error == "PowerOffHardwareMissing", "缺少电源对象被当成已实际断电确认");
            var receipts = typeof(EpbManager).GetMethod("HasDaqRecoveryPowerOffReceipts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var contextType = typeof(EpbManager).GetNestedType("DaqAutoRecoveryContext", System.Reflection.BindingFlags.NonPublic);
            var context = Activator.CreateInstance(contextType, true);
            Assert(!(bool)receipts.Invoke(manager, new object[] { null }) &&
                   !(bool)receipts.Invoke(manager, new[] { context }),
                "缺少DAQ上下文或电源硬件时回执检查错误通过");
            var correlation = Guid.NewGuid();
            contextType.GetField("CorrelationId").SetValue(context, correlation);
            contextType.GetField("RunEpoch").SetValue(context, 2L);
            contextType.GetField("RecoveryEpoch").SetValue(context, 3L);
            typeof(EpbManager).GetField("_powerSupply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(manager, System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PowerSupplyCoordinator)));
            foreach (var channels in new[] { Array.Empty<int>(), new[] { 13 } })
            {
                contextType.GetField("AffectedChannels").SetValue(context, channels);
                Assert(!(bool)receipts.Invoke(manager, new[] { context }), "空或无效通道范围通过回执检查");
            }
            var config = new GlobalConfig { Test = new TestConfig() };
            var electrical = new ElectricalGroup { Id = 1 };
            electrical.Members.Add(4);
            config.Test.Groups.Add(electrical);
            typeof(EpbManager).GetField("_cfg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(manager, config);
            contextType.GetField("AffectedChannels").SetValue(context, new[] { 4, 5 });
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "部分通道漏映射仍通过回执检查");
            contextType.GetField("AffectedChannels").SetValue(context, new[] { 4 });
            contextType.GetField("RecoveryEpoch").SetValue(context, 3L);
            var receiptType = typeof(EpbManager).GetNestedType("DaqRecoveryPowerOffReceipt", System.Reflection.BindingFlags.NonPublic);
            var receipt = Activator.CreateInstance(receiptType, true);
            receiptType.GetField("GroupId").SetValue(receipt, 1);
            receiptType.GetField("RecoveryEpoch").SetValue(receipt, 3L);
            receiptType.GetField("TaskCompleted").SetValue(receipt, true);
            receiptType.GetField("OutputConfirmedOff").SetValue(receipt, true);
            var evidence = new SafetyOffReceipt { Status = SafetyOffEvidenceStatus.ConfirmedOff,
                TargetKind = SafetyOffTargetKind.PowerSupplyGroup, TargetId = 1, CorrelationId = correlation, RunEpoch = 2 };
            receiptType.GetField("Evidence").SetValue(receipt, evidence);
            var executedUtc = DateTime.UtcNow;
            var execution = new PowerSafetyDisableResult { ElectricalGroupId = 1, OperationGeneration = 7,
                StartedUtc = executedUtc, CompletedUtc = executedUtc, ConfirmedOff = true,
                Outcome = PowerSafetyDisableOutcome.ConfirmedOff };
            var executionTask = Task.FromResult(execution);
            receiptType.GetField("PowerOperationEpoch").SetValue(receipt, 7L);
            receiptType.GetField("SubmittedUtc").SetValue(receipt, executedUtc);
            receiptType.GetField("TaskCompletedUtc").SetValue(receipt, executedUtc);
            evidence.OperationGeneration = 7;
            evidence.SubmittedUtc = executedUtc;
            evidence.CompletedUtc = executedUtc;
            receiptType.GetField("ExecutionTask").SetValue(receipt, executionTask);
            var taskMap = new Dictionary<int, Task<PowerSafetyDisableResult>> { [1] = executionTask };
            contextType.GetField("PowerDisableTasksByGroup").SetValue(context, taskMap);
            var map = (System.Collections.IDictionary)contextType.GetField("PowerOffReceipts").GetValue(context);
            map.Add(1, receipt);
            Assert((bool)receipts.Invoke(manager, new[] { context }), "完整当前代次有效回执被错误拒绝");
            taskMap[1] = Task.FromResult(new PowerSafetyDisableResult());
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "新任务发布后旧任务回执仍获准通过");
            taskMap[1] = executionTask;
            execution.ConfirmedOff = false;
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "实际任务未确认却因投影成功而通过");
            execution.ConfirmedOff = true;
            evidence.OperationGeneration = 8;
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "投影代次与实际执行不符仍通过");
            evidence.OperationGeneration = 7;
            evidence.CompletedUtc = executedUtc.AddSeconds(1);
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "投影时间与实际执行不符仍通过");
            evidence.CompletedUtc = executedUtc;
            var pendingExecution = new TaskCompletionSource<PowerSafetyDisableResult>();
            taskMap[1] = pendingExecution.Task;
            receiptType.GetField("ExecutionTask").SetValue(receipt, pendingExecution.Task);
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "在途任务因完成标志而通过");
            taskMap[1] = executionTask;
            receiptType.GetField("ExecutionTask").SetValue(receipt, executionTask);
            evidence.TargetId = 2;
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "其它电源组回执被复用");
            evidence.TargetId = 1;
            evidence.CorrelationId = Guid.NewGuid();
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "其它恢复关联的回执被复用");
            evidence.CorrelationId = correlation;
            evidence.RunEpoch = 1;
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "旧运行回执被复用");
            evidence.RunEpoch = 2;
            receiptType.GetField("PowerOffPending").SetValue(receipt, true);
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "待确认回执被算成完成");
            receiptType.GetField("PowerOffPending").SetValue(receipt, false);
            receiptType.GetField("RecoveryEpoch").SetValue(receipt, 2L);
            Assert(!(bool)receipts.Invoke(manager, new[] { context }), "旧代次回执通过当前恢复检查");
        }

        private static void DaqRollbackRejectsStaleRunBeforeHardware()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = (EpbManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(EpbManager));
            var contextType = typeof(EpbManager).GetNestedType("DaqAutoRecoveryContext", System.Reflection.BindingFlags.NonPublic);
            var context = Activator.CreateInstance(contextType, true);
            var currentRun = Guid.NewGuid();
            typeof(EpbManager).GetField("_activeBatchId", flags).SetValue(manager, currentRun);
            typeof(EpbManager).GetField("_runEpoch", flags).SetValue(manager, 2L);
            var coordinator = new HydraulicRecoveryOwnershipCoordinator();
            using (var lease = coordinator.AcquireAsync(1, "rollback-test", RecoveryOwnerPriority.Daq,
                1000, CancellationToken.None).GetAwaiter().GetResult())
            {
                contextType.GetField("Ownerships").SetValue(context, new[] { lease });
                foreach (var condition in new[] { "old-run", "old-epoch", "released" })
                {
                    contextType.GetField("RunId").SetValue(context, condition == "old-run" ? Guid.NewGuid() : currentRun);
                    contextType.GetField("RunEpoch").SetValue(context, condition == "old-epoch" ? 1L : 2L);
                    contextType.GetField("OwnershipReleased").SetValue(context, condition == "released" ? 1 : 0);
                    // All hardware and runtime dictionaries deliberately remain null:
                    // the actual production method must reject before touching them.
                    typeof(EpbManager).GetMethod("RollbackDaqRecoveryRejoinSafety", flags)
                        .Invoke(manager, new[] { context, "RejoinAndCommit", "late-failure" });
                }
                contextType.GetField("RunId").SetValue(context, currentRun);
                contextType.GetField("RunEpoch").SetValue(context, 2L);
                contextType.GetField("OwnershipReleased").SetValue(context, 0);
                Assert(coordinator.CancelGroup(1) && lease.Token.IsCancellationRequested,
                    "未建立真实液压所有权撤权条件");
                typeof(EpbManager).GetMethod("RollbackDaqRecoveryRejoinSafety", flags)
                    .Invoke(manager, new[] { context, "RejoinAndCommit", "revoked-owner" });
            }
        }

        private static void RecoveryHandoffWaitsForRetirement()
        {
            foreach (var outcome in new[] { "success", "fault", "cancel" })
            foreach (var retired in new[] { false, true })
            {
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var checks = 0;
                var starts = 0;
                var handoff = EpbManager.RunAfterRecoveryWorkerExitAsync(completion.Task,
                    () => { checks++; return retired; },
                    () => { starts++; return Task.CompletedTask; });
                try
                {
                    Assert(!handoff.IsCompleted && checks == 0 && starts == 0,
                        "原任务未退出就检查退休或启动后继。");
                    if (outcome == "fault") completion.TrySetException(new InvalidOperationException("injected"));
                    else if (outcome == "cancel") completion.TrySetCanceled();
                    else completion.TrySetResult(true);
                    Assert(handoff.GetAwaiter().GetResult() == retired && checks == 1 &&
                           starts == (retired ? 1 : 0), "任务终态被误作登记已退休。");
                }
                finally { completion.TrySetResult(true); }
            }
        }

        private static void RecoveryHandoffTerminalResetIsExact()
        {
            var run = Guid.NewGuid();
            var retired = new RecoveryContractSnapshot(Guid.NewGuid(), run, 4, Guid.NewGuid(),
                RecoveryOwnerKind.HydraulicGroupRecovery, RecoveryTargetPhase.Formal,
                "HydraulicSoftwareRecovery", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), new[] { 4 });
            var successor = new RecoveryContractSnapshot(Guid.NewGuid(), run, 4, Guid.NewGuid(),
                RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal,
                "AffectedGroupReset", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), new[] { 4, 5 });
            var current = new ChannelRuntimeStateChangedEvent { Channel = 4, RunId = run,
                RunEpoch = 4, State = ChannelRuntimeState.StartBlocked, CorrelationId = retired.IncidentId };
            Assert(EpbManager.CanResetRetiredRecoveryTerminal(retired, successor, current),
                "精确退休终态不能交给后继。");
            foreach (var state in new[] { ChannelRuntimeState.AlarmStopped, ChannelRuntimeState.Paused,
                         ChannelRuntimeState.InterlockStopped, ChannelRuntimeState.SystemFault })
            {
                current.State = state;
                Assert(!EpbManager.CanResetRetiredRecoveryTerminal(retired, successor, current),
                    "接替覆盖了非所属StartBlocked终态。");
            }
            current.State = ChannelRuntimeState.StartBlocked;
            current.CorrelationId = Guid.NewGuid();
            Assert(!EpbManager.CanResetRetiredRecoveryTerminal(retired, successor, current), "错误incident获准复位。");
            current.CorrelationId = retired.IncidentId;
            current.RunEpoch++;
            Assert(!EpbManager.CanResetRetiredRecoveryTerminal(retired, successor, current), "新代次获准由旧任务复位。");
            current.RunEpoch = 4;
            current.Channel = 5;
            Assert(!EpbManager.CanResetRetiredRecoveryTerminal(retired, successor, current), "扩组成员被当成原任务终态复位。");
        }

        private static void ManagerHardwareActionTracksPhysicalGroups()
        {
            using (var fixture = new EpbManagerTerminalFixture())
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var track = typeof(EpbManager).GetMethod("TrackRecoveryHardwareAction", flags);
                var field = typeof(EpbManager).GetField("_affectedGroupStageActions", flags);
                Assert(track != null && field != null, "生产硬件动作登记接线缺失。");
                var ledgers = (ConcurrentDictionary<int, RecoveryStageTaskRegistry>)field.GetValue(fixture.Manager);
                foreach (var outcome in new[] { "success", "fault", "cancel" })
                {
                    var completion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        // EPB10 is disabled in this fixture, but belongs to the
                        // second affected physical group and must still be tracked.
                        var returned = (Task)track.Invoke(fixture.Manager,
                            new object[] { new[] { 4, 4, 10 }, completion.Task });
                        Assert(ReferenceEquals(returned, completion.Task),
                            "登记替换了实际动作Task，可能丢失其真实终态。");
                        Assert(ledgers.Count == 2 && ledgers[1].HasPending && ledgers[2].HasPending,
                            "实际动作未同时覆盖两个物理组，或按启用状态漏掉组2。");
                        if (outcome == "fault") completion.TrySetException(new InvalidOperationException("injected"));
                        else if (outcome == "cancel") completion.TrySetCanceled();
                        else completion.TrySetResult(true);
                        try { completion.Task.GetAwaiter().GetResult(); }
                        catch (InvalidOperationException) when (outcome == "fault") { }
                        catch (OperationCanceledException) when (outcome == "cancel") { }
                        Assert(SpinWait.SpinUntil(() => !ledgers[1].HasPending && !ledgers[2].HasPending, 5000),
                            "动作真实终态后物理组登记未释放：" + outcome);
                    }
                    finally { completion.TrySetResult(true); }
                }
            }
        }

        private static void WorkerExitRevalidatesTerminal()
        {
            foreach (var blockTerminal in new[] { false, true })
            {
                var seam = new FakeSeam();
                var coordinator = seam.CreateCoordinator();
                RecoveryIncidentCoordinator.Incident current = null;
                var result = Begin(coordinator, seam, _ => () =>
                {
                    Assert(current.CompleteAfterTerminal(seam.PublishTerminal),
                        "初次终态未被接受。");
                    // Model a late body/finally write after the earlier proof.
                    lock (seam.StateGate)
                    {
                        seam.Terminal.Clear();
                        foreach (var channel in current.Contract.Channels)
                            seam.Recovering.Add(channel);
                    }
                    if (blockTerminal) seam.Failure = "terminal-persistent";
                    return Task.CompletedTask;
                }, out var incident);
                Assert(result == RecoveryIncidentCoordinator.BeginResult.Created,
                    "未建立退出重验测试任务。");
                current = incident;
                Assert(incident.Start(), "退出重验测试任务未启动。");
                AwaitWorker(incident.WorkerTask);
                Assert(seam.OffCount > 0 &&
                       seam.LastTerminalReason == "RecoveryTerminalChangedBeforeWorkerExit",
                    "退出时沿用了失效终态，没有请求安全收尾。");
                if (blockTerminal)
                {
                    Assert(coordinator.ActiveCount == 1 && seam.Registry.ActiveCount == 1 &&
                           seam.UnregisterCount == 0 && incident.TerminalPublished == 0,
                        "退出重验失败仍释放了登记。");
                    seam.Failure = null;
                    Assert(incident.CompleteAfterTerminal(seam.PublishTerminal),
                        "安全终态可提交后，退出任务不能重试释放。");
                }
                Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0 &&
                       seam.TerminalCount == seam.ChannelCount,
                    "退出重验成功后未完成终态释放。");
            }
        }

        private static void ProductionIncidentNamesAreRecognized()
        {
            foreach (var name in new[] { "AffectedGroupReset", "RecoverableWarningRetry",
                         "StartupPositioningFailureIncident" })
            {
                var seam = new FakeSeam();
                var coordinator = seam.CreateCoordinator();
                var result = coordinator.TryBegin(name, Guid.NewGuid(), 1,
                    RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal,
                    Guid.NewGuid(), new[] { 4 }, _ => () => Task.CompletedTask,
                    seam.PublishRecovering, out var incident);
                Assert(result == RecoveryIncidentCoordinator.BeginResult.Created && incident.Start(),
                    "生产恢复任务名被登记器拒绝：" + name);
                AwaitWorker(incident.WorkerTask);
                Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0,
                    "生产恢复任务结束后残留登记：" + name);
            }
            foreach (var name in new[] { "", "Reset", "Retry", "Incident", "OrdinaryWorker" })
                Assert(!RecoveryTaskRegistry.IsRecoveryOperation(name),
                    "非恢复任务被过宽的命名规则接纳：" + name);
        }

        private static void TerminalCommitRetainsScopeUntilWorkerExit()
        {
            var seam = new FakeSeam();
            var coordinator = seam.CreateCoordinator();
            var runId = Guid.NewGuid();
            var terminalAccepted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowExit = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            RecoveryIncidentCoordinator.Incident current = null;
            var result = Begin(coordinator, seam, _ => async () =>
            {
                terminalAccepted.TrySetResult(current.CompleteAfterTerminal(
                    contract => seam.MarkTerminal(contract, onlyFirst: false)));
                await allowExit.Task.ConfigureAwait(false);
            }, out var first, runId, Guid.NewGuid(), new[] { 4 });
            Assert(result == RecoveryIncidentCoordinator.BeginResult.Created, "未建立旧任务。");
            current = first;
            try
            {
                Assert(first.Start(), "旧任务无法启动。");
                Assert(terminalAccepted.Task.Wait(5000) && terminalAccepted.Task.Result,
                    "旧任务未提交可验证终态。");
                Assert(!first.WorkerTask.IsCompleted && coordinator.ActiveCount == 1 &&
                       seam.Registry.ActiveCount == 1 && first.TerminalPublished == 0,
                    "终态提交提前释放仍在执行的旧任务登记。");
                var reserves = seam.ReserveCount;
                var overlap = coordinator.TryBegin("AffectedGroupReset", runId, 1,
                    RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal,
                    Guid.NewGuid(), new[] { 4, 5 }, _ => () => Task.CompletedTask,
                    seam.PublishRecovering, out var rejected);
                Assert(overlap == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                       rejected == null && seam.ReserveCount == reserves,
                    "旧任务尚未退出时整组接替取得了重叠范围。");
            }
            finally
            {
                allowExit.TrySetResult(true);
                AwaitWorker(first.WorkerTask);
            }
            Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0,
                "旧任务退出后登记未自动释放。");
            var next = coordinator.TryBegin("AffectedGroupReset", runId, 1,
                RecoveryOwnerKind.AffectedGroupRecovery, RecoveryTargetPhase.Formal,
                Guid.NewGuid(), new[] { 4, 5 }, _ => () => Task.CompletedTask,
                seam.PublishRecovering, out var successor);
            Assert(next == RecoveryIncidentCoordinator.BeginResult.Created && successor.Start(),
                "旧任务退出后整组接替未能执行。");
            AwaitWorker(successor.WorkerTask);
            Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0,
                "整组接替结束后残留登记。");
        }

        private static void RecoveryChannelOverlapIsAtomic()
        {
            var seam = new FakeSeam();
            var coordinator = seam.CreateCoordinator();
            var runId = Guid.NewGuid();
            var firstResult = Begin(
                coordinator,
                seam,
                _ => () => Task.CompletedTask,
                out var first,
                runId,
                Guid.NewGuid(),
                new[] { 4 });
            Assert(firstResult == RecoveryIncidentCoordinator.BeginResult.Created &&
                   first != null,
                "首个单通道incident未创建。");
            var reserveBefore = seam.ReserveCount;
            var offBefore = seam.OffCount;

            var overlap = coordinator.TryBegin(
                "PowerSupplySoftwareRecovery",
                runId,
                1,
                RecoveryOwnerKind.PowerRecovery,
                RecoveryTargetPhase.Formal,
                Guid.NewGuid(),
                new[] { 4, 10 },
                _ => () => Task.CompletedTask,
                seam.PublishRecovering,
                out var rejectedIncident);
            Assert(overlap == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                   rejectedIncident == null &&
                   seam.ReserveCount == reserveBefore &&
                   seam.OffCount == offBefore,
                "重叠通道未在Reserve前原子拒绝，或误伤首incident。");

            var disjoint = coordinator.TryBegin(
                "PowerSupplySoftwareRecovery",
                runId,
                1,
                RecoveryOwnerKind.PowerRecovery,
                RecoveryTargetPhase.Formal,
                Guid.NewGuid(),
                new[] { 10 },
                _ => () => Task.CompletedTask,
                seam.PublishRecovering,
                out var second);
            Assert(disjoint == RecoveryIncidentCoordinator.BeginResult.Created &&
                   second != null &&
                   seam.ReserveCount == reserveBefore + 1,
                "非重叠通道未允许并行incident。");

            Assert(first.Start() && second.Start(), "并行incident无法Start。");
            AwaitWorker(first.WorkerTask);
            AwaitWorker(second.WorkerTask);
            Assert(coordinator.ActiveCount == 0 &&
                   seam.Registry.ActiveCount == 0 &&
                   seam.TerminalCount == seam.ChannelCount,
                "重叠门禁测试终态后仍残留contract/lease。");

            var successorResult = coordinator.TryBegin(
                "PowerSupplySoftwareRecovery", runId, 1,
                RecoveryOwnerKind.PowerRecovery, RecoveryTargetPhase.Formal,
                Guid.NewGuid(), new[] { 4, 10 },
                _ => () => Task.CompletedTask, seam.PublishRecovering,
                out var successor);
            Assert(successorResult == RecoveryIncidentCoordinator.BeginResult.Created &&
                   successor != null && coordinator.ActiveCount == 1,
                "旧执行体退出并完成终态后，扩大范围的新任务未能登记。");
            var stalePublisherCalled = false;
            Assert(!first.CompleteAfterTerminal(_ => stalePublisherCalled = true) &&
                   !stalePublisherCalled && coordinator.ActiveCount == 1 &&
                   seam.Registry.ActiveCount == 1,
                "旧任务迟到终态误撤销新范围任务或发布旧状态。");
            Assert(successor.Start(), "新范围任务无法启动。");
            AwaitWorker(successor.WorkerTask);
            Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0,
                "扩大范围任务结束后残留登记。");
        }

        private static void RecoveryPhysicalAndLogicalScopesAreSeparated()
        {
            var seam = new FakeSeam();
            var coordinator = seam.CreateCoordinator();
            var result = coordinator.TryBegin(
                "PowerSupplySoftwareRecovery",
                Guid.NewGuid(),
                1,
                RecoveryOwnerKind.PowerRecovery,
                RecoveryTargetPhase.Formal,
                Guid.NewGuid(),
                new[] { 11, 12 },
                new[] { 10, 11, 12 },
                _ => () => Task.CompletedTask,
                seam.PublishRecovering,
                out var incident);
            Assert(result == RecoveryIncidentCoordinator.BeginResult.Created &&
                   incident != null &&
                   incident.Contract.OwnedChannels.SequenceEqual(new[] { 11, 12 }) &&
                   incident.Contract.SafetyAffectedChannels.SequenceEqual(new[] { 10, 11, 12 }) &&
                   incident.TaskLease.Channels.SequenceEqual(new[] { 11, 12 }),
                "禁用EPB10仍被登记为逻辑恢复owner，或物理整组范围丢失");
            Assert(incident.Start(), "拆分集合incident无法启动");
            AwaitWorker(incident.WorkerTask);
            Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0 &&
                   seam.TerminalCount == seam.ChannelCount,
                "拆分集合incident终态后仍残留合同或租约");

            var failing = new FakeSeam { Failure = "publish" };
            var failingCoordinator = failing.CreateCoordinator();
            var rejected = failingCoordinator.TryBegin(
                "PowerSupplySoftwareRecovery",
                Guid.NewGuid(),
                1,
                RecoveryOwnerKind.PowerRecovery,
                RecoveryTargetPhase.Formal,
                Guid.NewGuid(),
                new[] { 11, 12 },
                new[] { 10, 11, 12 },
                _ => () => Task.CompletedTask,
                failing.PublishRecovering,
                out _);
            Assert(rejected == RecoveryIncidentCoordinator.BeginResult.Rejected &&
                   failing.LastOffContract != null &&
                   failing.LastOffContract.SafetyAffectedChannels.SequenceEqual(new[] { 10, 11, 12 }) &&
                   failing.Terminal.OrderBy(channel => channel).SequenceEqual(new[] { 11, 12 }),
                "合约补偿未对物理整组OFF并仅对启用owner发布终态");
        }

        private static void ScopeBarrierDeduplicatesConcurrentSignals()
        {
            var seam = new FakeSeam();
            var coordinator = seam.CreateCoordinator();
            var runId = Guid.NewGuid();
            var ready = new CountdownEvent(64);
            var release = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 64)
                .Select(index => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    release.Wait();
                    return Begin(
                        coordinator,
                        seam,
                        _ => () => Task.CompletedTask,
                        out var incident,
                        runId,
                        Guid.NewGuid());
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
                .ToArray();
            Assert(ready.Wait(TimeSpan.FromSeconds(5)), "64并发barrier未就绪。");
            release.Set();
            Task.WaitAll(tasks);

            var created = tasks.Count(task => task.Result == RecoveryIncidentCoordinator.BeginResult.Created);
            var existing = tasks.Count(task => task.Result == RecoveryIncidentCoordinator.BeginResult.ExistingSameScope);
            Assert(created == 1 && existing == 63 && seam.RegisterCount == 1,
                $"64并发scope去重失败：Created={created}; Existing={existing}; " +
                $"Register={seam.RegisterCount}; Events={seam.EventsText}");
            Assert(coordinator.ActiveCount == 1 && seam.Registry.ActiveCount == 1,
                "1.2秒窗口内唯一incident的contract/lease未保持。");

            Thread.Sleep(1200);
            Assert(coordinator.ActiveCount == 1 && seam.Registry.ActiveCount == 1,
                "1.2秒同incident窗口内owner/lease提前消失。");

            var incident = seam.LastIncident;
            Assert(incident != null && incident.Start(), "唯一incident无法显式Start。");
            AwaitWorker(incident.WorkerTask);
            Assert(coordinator.ActiveCount == 0 && seam.Registry.ActiveCount == 0 &&
                   seam.TerminalCount == seam.ChannelCount,
                "唯一incident终态后仍残留contract/lease。");
        }

        private static void RandomizedAdmissionStopAndLateTerminalInterleavings()
        {
            var random = new Random(20260829);
            for (var round = 0; round < 1000; round++)
            {
                var seam = new FakeSeam();
                var coordinator = seam.CreateCoordinator();
                var runId = Guid.NewGuid();
                var outcomes = new RecoveryIncidentCoordinator.BeginResult[2];
                var incidents = new RecoveryIncidentCoordinator.Incident[2];
                var mode = random.Next(3);
                Func<RecoveryContractSnapshot, Func<Task>> workerFactory = _ =>
                {
                    if (mode == 1)
                        return () => throw new InvalidOperationException("randomized-worker");
                    if (mode == 2)
                        return async () => await Task.Yield();
                    return () => Task.CompletedTask;
                };

                Parallel.Invoke(
                    () => outcomes[0] = Begin(
                        coordinator,
                        seam,
                        workerFactory,
                        out incidents[0],
                        runId,
                        Guid.NewGuid()),
                    () => outcomes[1] = Begin(
                        coordinator,
                        seam,
                        workerFactory,
                        out incidents[1],
                        runId,
                        Guid.NewGuid()));

                Assert(outcomes.Count(item =>
                           item == RecoveryIncidentCoordinator.BeginResult.Created) == 1 &&
                       outcomes.Count(item =>
                           item == RecoveryIncidentCoordinator.BeginResult.ExistingSameScope) == 1 &&
                       coordinator.ActiveCount == 1 &&
                       seam.Registry.ActiveCount == 1 &&
                       seam.RegisterCount == 1,
                    "随机交错产生双owner或准入未原子化，round=" + round);

                var incident = incidents.FirstOrDefault(item => item != null) ?? seam.LastIncident;
                Assert(incident != null, "随机交错没有返回唯一incident，round=" + round);

                // Model StopAll's epoch fence in roughly half the schedules.
                // The old worker is still allowed to finish for audit, but its
                // late terminal may not re-enter the active registry.
                var superseded = random.Next(2) == 0;
                if (superseded)
                {
                    var quarantine = seam.Registry.SupersedeThroughEpoch(
                        1,
                        "RandomizedStopFence");
                    Assert(quarantine.Length == 1 && seam.Registry.ActiveCount == 0,
                        "Stop代次栅栏未隔离唯一旧owner，round=" + round);
                }

                Assert(incident.Start(), "随机交错incident无法Start，round=" + round);
                try { AwaitWorker(incident.WorkerTask); }
                catch (InvalidOperationException) when (mode == 1) { }

                Assert(coordinator.ActiveCount == 0 &&
                       seam.Registry.ActiveCount == 0 &&
                       seam.RegisteredIncident == null &&
                       seam.TerminalCount == seam.ChannelCount &&
                       seam.Recovering.Count == 0 &&
                       seam.OffCount >= 1,
                    "随机交错遗留owner/半恢复/非安全终态，round=" + round +
                    ";superseded=" + superseded + ";events=" + seam.EventsText);
            }
        }

        private static RecoveryIncidentCoordinator.BeginResult Begin(
            RecoveryIncidentCoordinator coordinator,
            FakeSeam seam,
            Func<RecoveryContractSnapshot, Func<Task>> workerFactory,
            out RecoveryIncidentCoordinator.Incident incident,
            Guid? runId = null,
            Guid? ownerId = null,
            int[] channels = null)
        {
            if (seam.Failure == "factory")
                workerFactory = _ => throw new InvalidOperationException("factory");
            return coordinator.TryBegin(
                "HydraulicSoftwareRecovery",
                runId ?? Guid.NewGuid(),
                1,
                RecoveryOwnerKind.HydraulicGroupRecovery,
                RecoveryTargetPhase.Formal,
                ownerId ?? Guid.NewGuid(),
                channels ?? new[] { 4, 10 },
                workerFactory,
                seam.PublishRecovering,
                out incident);
        }

        private static void AwaitWorker(Task worker)
        {
            if (worker == null) throw new InvalidOperationException("worker为空");
            worker.GetAwaiter().GetResult();
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

        private sealed class FakeSeam
        {
            internal readonly RecoveryTaskRegistry Registry = new RecoveryTaskRegistry();
            internal readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();
            internal readonly HashSet<int> Recovering = new HashSet<int>();
            internal readonly HashSet<int> Terminal = new HashSet<int>();
            internal readonly object StateGate = new object();
            internal readonly int[] Channels = { 4, 10 };
            internal string Failure;
            internal int RegisterCount;
            internal int ReserveCount;
            internal Action BeforeReserve;
            internal int OffCount;
            internal int UnregisterCount;
            internal bool ProbeCoordinatorGate;
            internal int ReentryTimeoutCount;
            internal RecoveryIncidentCoordinator.Incident RegisteredIncident;
            internal RecoveryIncidentCoordinator Coordinator;
            internal RecoveryIncidentCoordinator.Incident LastIncident;
            internal RecoveryContractSnapshot LastOffContract;
            internal string LastTerminalReason;
            internal string LastTerminalDetail;

            internal int ChannelCount => Channels.Length;
            internal int TerminalCount
            {
                get
                {
                    lock (StateGate) return Terminal.Count;
                }
            }

            internal string EventsText => string.Join(",", Events.ToArray());

            internal int IndexOf(string value)
            {
                var snapshot = Events.ToArray();
                return Array.IndexOf(snapshot, value);
            }

            internal RecoveryIncidentCoordinator CreateCoordinator()
            {
                var coordinator = new RecoveryIncidentCoordinator(
                    new object(),
                    new RecoveryIncidentCoordinator.Port
                    {
                        Reserve = contract =>
                        {
                            BeforeReserve?.Invoke();
                            ProbeGate("reserve");
                            Events.Enqueue("reserve");
                            Interlocked.Increment(ref ReserveCount);
                            if (Failure == "reserve")
                                throw new InvalidOperationException("reserve");
                            return Registry.Reserve(contract);
                        },
                        Schedule = body =>
                        {
                            ProbeGate("schedule");
                            Events.Enqueue("schedule");
                            if (Failure == "scheduler") return null;
                            if (Failure == "scheduler-throw")
                                throw new InvalidOperationException("scheduler");
                            return Task.Run(body);
                        },
                        Bind = (lease, worker) =>
                        {
                            ProbeGate("bind");
                            Events.Enqueue("bind");
                            if (Failure == "bind") return false;
                            return lease.TryBind(worker);
                        },
                        Observe = (incident, worker) =>
                        {
                            ProbeGate("observe");
                            Events.Enqueue("observe");
                            if (Failure == "observer")
                                throw new InvalidOperationException("observer");
                            LastIncident = incident;
                        },
                        PublishRecovering = PublishRecovering,
                        CommandOff = (contract, reason) =>
                        {
                            ProbeGate("off");
                            Events.Enqueue("off");
                            LastOffContract = contract?.Clone();
                            Interlocked.Increment(ref OffCount);
                        },
                        PublishSafeTerminal = (contract, reason, detail) =>
                        {
                            ProbeGate("safe-terminal");
                            Events.Enqueue("safe-terminal");
                            LastTerminalReason = reason;
                            LastTerminalDetail = detail;
                            MarkTerminal(contract);
                        },
                        IsRecoveringPublished = contract =>
                        {
                            ProbeGate("is-recovering");
                            lock (StateGate)
                                return contract.Channels.All(channel => Recovering.Contains(channel));
                        },
                        IsTerminalCommitted = contract =>
                        {
                            ProbeGate("is-terminal");
                            lock (StateGate)
                                return contract.Channels.All(channel => Terminal.Contains(channel));
                        },
                        IsRegistered = contract =>
                        {
                            ProbeGate("is-registered");
                            return RegisteredIncident != null &&
                                   RegisteredIncident.Contract.IncidentId == contract.IncidentId;
                        },
                        Start = incident =>
                        {
                            ProbeGate("start");
                            Events.Enqueue("start");
                            if (Failure == "start") return false;
                            return incident.ReleaseStartSignal();
                        },
                        Register = incident =>
                        {
                            ProbeGate("register");
                            Events.Enqueue("register");
                            Interlocked.Increment(ref RegisterCount);
                            if (Failure == "register-partial")
                            {
                                RegisteredIncident = incident;
                                LastIncident = incident;
                                throw new InvalidOperationException("register partial write");
                            }
                            RegisteredIncident = incident;
                            LastIncident = incident;
                        },
                        Unregister = incident =>
                        {
                            ProbeGate("unregister");
                            Events.Enqueue("unregister");
                            Interlocked.Increment(ref UnregisterCount);
                            if (RegisteredIncident != null &&
                                (incident == null ||
                                 RegisteredIncident.Contract.IncidentId ==
                                 incident.Contract.IncidentId))
                                RegisteredIncident = null;
                        },
                        Fault = (stage, error) =>
                        {
                            ProbeGate("fault:" + stage);
                            Events.Enqueue("fault:" + stage);
                        }
                    });
                Coordinator = coordinator;
                return coordinator;
            }

            internal void PublishRecovering(RecoveryContractSnapshot contract)
            {
                ProbeGate("publish");
                Events.Enqueue("publish");
                if (Failure == "publish")
                    throw new InvalidOperationException("publish");
                lock (StateGate)
                {
                    foreach (var channel in contract.Channels)
                        Recovering.Add(channel);
                }
            }

            internal void PublishTerminal(RecoveryContractSnapshot contract)
            {
                Events.Enqueue("terminal");
                MarkTerminal(contract);
            }

            internal void MarkTerminal(RecoveryContractSnapshot contract, bool onlyFirst = false)
            {
                if (Failure == "terminal-persistent") return;
                lock (StateGate)
                {
                    foreach (var channel in contract.Channels.Take(onlyFirst ? 1 : int.MaxValue))
                    {
                        Recovering.Remove(channel);
                        Terminal.Add(channel);
                    }
                }
            }

            private void ProbeGate(string callback)
            {
                if (!ProbeCoordinatorGate || Coordinator == null)
                    return;
                var completed = Task.Run(() =>
                {
                    Coordinator.CaptureActive();
                    return true;
                }).Wait(TimeSpan.FromMilliseconds(500));
                if (!completed)
                    Interlocked.Increment(ref ReentryTimeoutCount);
                Events.Enqueue("reenter:" + callback);
            }
        }

        private sealed class DaqTransactionSeam
        {
            internal Func<int, DaqRecoveryTransaction.OffReceipt> TrySubmit;
            internal readonly ConcurrentQueue<DaqRecoveryPhase> Phases =
                new ConcurrentQueue<DaqRecoveryPhase>();
            internal readonly List<List<int>> TerminalCalls = new List<List<int>>();
            internal readonly object TerminalGate = new object();
            internal int SafetyOffCount;
            internal int PowerDisableCount;
            internal int TerminalBatchSize;
            internal bool CheckpointStable = true;
            internal int TrySubmitCount;
            internal int TerminalCheckpointAttempts;
            internal bool FailTerminalCheckpointOnce;
            internal bool FailTerminalCheckpointAlways;
            internal DaqRecoveryPhase LastTerminalStage = DaqRecoveryPhase.StaleDetected;
            private long _aggregateVersion = 100;

            internal DaqRecoveryTransaction CreateTransaction(params int[] channels)
            {
                return new DaqRecoveryTransaction(
                    channels,
                    new DaqRecoveryTransaction.Port
                    {
                        TrySubmitOff = channel => TrySubmit == null
                            ? CountedReceipt(channel)
                            : CountedTrySubmit(channel),
                        PublishPhase = phase =>
                        {
                            Phases.Enqueue(phase);
                            if (phase == DaqRecoveryPhase.Terminal)
                                LastTerminalStage = phase;
                        },
                        SubmitSafetyOff = _ => Interlocked.Increment(ref SafetyOffCount),
                        DisablePower = _ => Interlocked.Increment(ref PowerDisableCount),
                        PublishCheckpoint = version =>
                            new DaqRecoveryTransaction.CheckpointReceipt(
                                NextAggregateVersion(version),
                                CheckpointStable,
                                CheckpointStable ? "stable" : "blocked"),
                        PublishTerminal = missing =>
                        {
                            var requested = (missing ?? Array.Empty<int>()).ToList();
                            lock (TerminalGate)
                            {
                                TerminalCalls.Add(requested);
                                if (TerminalBatchSize > 0 && TerminalCalls.Count == 1)
                                    return requested.Take(TerminalBatchSize).ToArray();
                            }
                            return requested.ToArray();
                        },
                        // Mirrors EpbManager.CreateDaqRecoveryTransaction:
                        // this receipt is requested only after the production
                        // phase publisher has made Stage=Terminal observable.
                        PublishTerminalCheckpoint = version =>
                        {
                            Interlocked.Increment(ref TerminalCheckpointAttempts);
                            if (LastTerminalStage != DaqRecoveryPhase.Terminal)
                                return new DaqRecoveryTransaction.CheckpointReceipt(
                                    NextAggregateVersion(version),
                                    false,
                                    "StageNotTerminal");
                            if (FailTerminalCheckpointAlways)
                                return new DaqRecoveryTransaction.CheckpointReceipt(
                                    NextAggregateVersion(version),
                                    false,
                                    "InjectedPersistentTerminalFailure");
                            if (FailTerminalCheckpointOnce)
                            {
                                FailTerminalCheckpointOnce = false;
                                return new DaqRecoveryTransaction.CheckpointReceipt(
                                    NextAggregateVersion(version),
                                    false,
                                    "InjectedFirstTerminalFailure");
                            }
                            return new DaqRecoveryTransaction.CheckpointReceipt(
                                NextAggregateVersion(version),
                                true,
                                "Stage=Terminal;Stable=true");
                        }
                    });
            }

            private DaqRecoveryTransaction.OffReceipt CountedTrySubmit(int channel)
            {
                Interlocked.Increment(ref TrySubmitCount);
                return TrySubmit(channel);
            }

            private DaqRecoveryTransaction.OffReceipt CountedReceipt(int channel)
            {
                Interlocked.Increment(ref TrySubmitCount);
                return DaqRecoveryTransactionReceipt(channel);
            }

            private long NextAggregateVersion(long minimum)
            {
                while (true)
                {
                    var current = Interlocked.Read(ref _aggregateVersion);
                    var next = Math.Max(current + 1, minimum + 1);
                    if (Interlocked.CompareExchange(
                            ref _aggregateVersion,
                            next,
                            current) == current)
                        return next;
                }
            }
        }

    }
}
