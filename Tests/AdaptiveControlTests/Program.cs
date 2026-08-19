using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using Controller.Adaptive;
using Controller.Alarm;
using DataOperation;
using IO.NI;
using Timing;
using NullLogger = Config.NullLogger;

namespace AdaptiveControlTests
{
    internal static class Program
    {
        private static int _passed;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0].Equals("--replay", StringComparison.OrdinalIgnoreCase))
                    return ReplayCsv(args[1]);
                if (args.Length == 1 &&
                    args[0].Equals("--recovery-coordination", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += RecoveryCoordinationTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--telemetry-storage", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += PowerSupplyTelemetryRecorderTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--power-supply", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += PowerSupplyCoordinatorTests.RunAll();
                    _passed += PswTcpClientTimeoutTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--historical-storage", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += HistoricalStorageBudgetTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--recovery-hardening", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += RecoveryHardeningTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--stop-watchdog-hardening", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += StopWatchdogHardeningTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--watchdog-journal", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += WatchdogJournalStorageTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--daq-realtime", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += DaqRealtimeControlTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--epb-record-normalization", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += EpbRecordNormalizationTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--incident-session", StringComparison.OrdinalIgnoreCase))
                {
                    Run("Incident SessionKey/60秒窗口合并", IncidentSessionPolicyTests.SessionKeyAndWindowMerge);
                    Run("Incident 重证据门禁不抑制摘要", IncidentSessionPolicyTests.HeavyGatesPreserveSummary);
                    Run("Incident phase receipt 与终态 manifest", IncidentSessionPolicyTests.TerminalManifestAndRetention);
                    Run("Incident 配置与storm阈值", IncidentSessionPolicyTests.ConfigDefaultsAndStormLevels);
                    Run("Incident 保留10个且跳过active/legacy/corrupt", IncidentSessionPolicyTests.RetentionKeepsLatestTenAndSkipsUnsafe);
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--cycle-lifecycle", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += CycleAttemptLifecycleTests.RunAll();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--do-command-ring", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += DaqRealtimeControlTests.RunDoCommandRingRegression();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--field-clock", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += DaqRealtimeControlTests.RunFieldClockRegression();
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--persistence-load", StringComparison.OrdinalIgnoreCase))
                {
                    Run(
                        "六通道2kHz双DAQ真实写盘实时负载",
                        DaqPersistenceCoordinatorTests.SixChannelRealtimePersistenceStaysAhead);
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--persistence-cutoff", StringComparison.OrdinalIgnoreCase))
                {
                    Run(
                        "持久化准入与主动截止在线性化提交点闭合",
                        DaqPersistenceCoordinatorTests.AdmissionCutoffLinearizesBeforeQueueCommit);
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 2 &&
                    args[0].Equals("--persistence-soak", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var soakSeconds))
                        throw new ArgumentException("--persistence-soak 需要整数秒数。");
                    Run(
                        $"六通道2kHz双DAQ真实写盘持续{soakSeconds}秒",
                        () => DaqPersistenceCoordinatorTests
                            .SixChannelRealtimePersistenceStaysAhead(soakSeconds));
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--do-timing-model", StringComparison.OrdinalIgnoreCase))
                {
                    Run("DO四时刻序列化与P95重载", ProfilePersistence);
                    Run("DO完成延迟分布进入控流模型且单圈更新限幅", DoCompletionLatencyFeedsCutoffModel);
                    Run("DO四时刻模型克隆保持深拷贝", DoTimingProfileCloneIsDeep);
                    Run("版本5模型原位兼容升级且保留学习历史", VersionFiveProfileMigratesWithoutReset);
                    Run("旧控制策略模型留档失效并按版本6重新学习", VersionOneProfileMigrates);
                    Run("DO四时刻进入报警重建证据", AlarmControlEvidenceIsReconstructable);
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }
                if (args.Length == 1 &&
                    args[0].Equals("--incident-10358-029", StringComparison.OrdinalIgnoreCase))
                {
                    _passed += RecoveryCoordinationTests.RunAll();
                    _passed += RecoveryHardeningTests.RunAll();
                    _passed += ProjectLogStoreTests.RunRealtimeIsolationRegression();
                    _passed += HydraulicGroupCoordinatorTests.RunRecoveryEvidenceRegression();
                    Run("峰值偏差只比较同一证据时间窗", PeakEvidenceMismatchRequiresComparableWindow);
                    Run("不可比较时间窗不冒充后台处理滞后", NonComparablePeakWindowIsNotProcessingLag);
                    Run("软预警完整证据按通道类别执行600秒限频", WarningSnapshotFullEvidenceIntervalIsEnforced);
                    Run("峰值排空墙钟等待不计入证据尾差", PeakDrainDelayDoesNotInvalidateEvidence);
                    Run("六通道并发封口不产生墙钟峰值误判", SixChannelPeakDrainIsConsistent);
                    Run("软件自愈连续三次无进展后熔断", SoftwareSelfHealingStopsAfterThreeAttempts);
                    Run("人工停止可覆盖旧启动受阻显示", ManualStopReplacesStartBlocked);
                    Run("学习期报警不得越权创建正式Timer", AlarmRecoveryWaitsForFormalCommit);
                    Run("禁用通道拒绝组级恢复状态污染", DisabledChannelStateIsNormalized);
                    Run("启动受阻提交要求执行资源全部清场", TerminalStateRequiresExecutionQuiescence);
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }

                _passed += CycleAttemptLifecycleTests.RunAll();
                _passed += PowerSupplyTelemetryRecorderTests.RunAll();
                _passed += HistoricalStorageBudgetTests.RunAll();
                _passed += WatchdogJournalStorageTests.RunAll();
                _passed += StopWatchdogHardeningTests.RunAll();
                Run("正常夹紧", NormalClamp);
                Run("学习尾部提前量后预测夹紧", LearnedTailLeadPredictsClamp);
                Run("低斜率不提前误触发", LowSlopeDoesNotPredictEarly);
                Run("未识别负载上升前到阈值立即断电但不误报硬故障", ThresholdBeforeLoadRiseBecomesFastRiseCandidate);
                Run("完整速率证据确认快速夹紧不误报曲线顺序故障", FullRateRapidClampBeforeLoadRiseIsAccepted);
                Run("夹紧阈值必须连续三样本确认", ClampNeedsThreeSamples);
                Run("稳定模型后连续50圈仍记录正向空行程", StableProfileKeepsLearningForFiftyCycles);
                Run("正式控制仍按程序级期限拦截无负载上升", ForwardLoadRiseDeadlineFaults);
                Run("正向低平台200ms立即断电并软预警", ForwardCurrentRiseStallWarnsAndCutsPower);
                Run("14.6A近目标平台200ms软完成", NearTargetPlateauCompletesWithWarning);
                Run("13.9A短平台恢复后不误停", LowPlateauRecoversBeforeFaultWindow);
                Run("13.9A持续平台按低目标预警完成", Sustained139AmpPlateauWarns);
                Run("EPB10第28圈全数据峰值回放", Epb10Cycle28FullRatePeakReplay);
                Run("正向正常爬升穿越半目标值不误报平台", ForwardRampThroughHalfTargetDoesNotFault);
                Run("反向动态释放", ReverseRelease);
                Run("反向17ms采样节拍仍可释放", ReverseReleaseWithSeventeenMillisecondCadence);
                Run("反向释放窗口忽略孤立毛刺", ReverseReleaseIgnoresSparseOutliers);
                Run("污染模型下反向下降沿不误判低电流平台", ReverseDecayWithPollutedProfileWaitsForPlateau);
                Run("现场EPB8和EPB9曲线可释放", MeasuredReverseFixturesRelease);
                Run("反向3到9A平台按模型期限硬停", SustainedReverseLoadFaultsAtProgressDeadline);
                Run("反向低电流平台200ms确认释放", ReverseLowPlateauReleasesInOneWindow);
                Run("反向3A配置带不被稳定模型收紧", ReverseConfiguredReleaseBandOverridesStableModel);
                Run("反向3A边界严格判定", ReverseConfiguredReleaseBoundary);
                Run("预释放6.5A平台按15A目标不误报", PreReleaseNormalPlatformUsesForwardReference);
                Run("预释放持续超过9A触发高平台保护", PreReleaseHighPlatformStillFaults);
                Run("预释放失败阻止学习阶段", PreReleaseFailureBlocksLearning);
                Run("启动定位仅双源过流确认为硬件故障", StartupPositioningRequiresIndependentHardwareEvidence);
                Run("启动定位涌流后连续三点高电流确认", StartupPositioningConfirmsHighCurrentAfterInrush);
                Run("启动定位真正过流优先于终点确认", StartupPositioningOverCurrentTakesPriority);
                Run("启动定位拆分进展期限与绝对上电上限", StartupPositioningSeparatesForwardTimingBudgets);
                Run("10358-029四路启动快照不再在3秒误停", StartupPositioningFieldSnapshotsSurviveThreeSeconds);
                Run("启动定位反向不复用正式循环历史期限", StartupPositioningSeparatesReverseTimingBudgets);
                Run("10358-029四路启动反向快照均可释放", StartupPositioningReverseFieldSnapshotsRelease);
                Run("保持阶段不误报断流", HoldDoesNotFault);
                Run("三样本过流快速断电但不单次永久报警", ThreeSampleOverCurrent);
                Run("开路检测", OpenCircuit);
                Run("DAQ断流", DaqStale);
                Run("单点噪声不误停", NoiseSpike);
                Run("绝对上电超限", AbsoluteOnTime);
                Run("异常高电流平台", AbnormalHighPlateau);
                Run("终态断电先于阻塞诊断发布", TerminalOffPrecedesBlockingDiagnostics);
                Run("DO失败与电流未清零触发组级联锁", OffFailureEscalatesToPowerGroup);
                Run("断电电流在窗口内清零不联锁且超时只失败一次", OffCurrentPollingWindow);
                Run("DAQ陈旧时不得把冻结电流判为未清零", StaleOffCurrentIsUnverifiable);
                Run("反向残余负电流不能误判为断电清零", NegativeOffCurrentDoesNotClear);
                Run("断电清零阈值适配现场零偏且保持安全上限", OffCurrentThresholdTracksTrustedBaseline);
                Run("项目XML不再保存程序级安全参数", ProjectXmlIgnoresProgramSafetySettings);
                Run("项目数据保留配置默认回退与保存回读", ProjectRetentionConfigDefaultsValidationAndRoundTrip);
                Run("EXE安全配置缺失非法时使用安全默认值", ProgramSafetySettingsValidation);
                Run("程序安全配置快照包含值与来源", ProgramSafetySnapshotIsAuditable);
                Run("报警配置加载不可恢复连续阈值", AlarmConfigLoadsForwardStallConfirmation);
                Run("软预警按通道类别保留30次且Unlimited不删除", WarningSnapshotRetentionModes);
                Run("软预警完整证据按通道类别执行600秒限频", WarningSnapshotFullEvidenceIntervalIsEnforced);
                Run("软预警默认轻量JSONL且包含完整运行身份", WarningScalarEvidenceIsDefaultAndAuditable);
                Run("软预警千次风暴仅允许一个运行一个等待并合并重复", WarningSnapshotFloodIsStrictlyBounded);
                Run("普通故障按尝试圈连续3次确认且单圈去重", GenericFaultConfirmationUsesAttemptCycles);
                Run("普通故障成功圈清零且通道故障码隔离", GenericFaultConfirmationResetsAndIsolates);
                Run("电流硬故障与已确认专用策略不二次计数", ImmediateAndPreconfirmedFaultClassification);
                Run("定时器自身取消不记录ERROR", TimerOwnedCancellationIsNotError);
                Run("峰值证据连续3圈且有效圈清零", PeakEvidenceMismatchRequiresThreeCycles);
                Run("峰值偏差只比较同一证据时间窗", PeakEvidenceMismatchRequiresComparableWindow);
                Run("不可比较时间窗不冒充后台处理滞后", NonComparablePeakWindowIsNotProcessingLag);
                Run("峰值排空墙钟等待不计入证据尾差", PeakDrainDelayDoesNotInvalidateEvidence);
                Run("六通道并发封口不产生墙钟峰值误判", SixChannelPeakDrainIsConsistent);
                Run("软件自愈连续三次无进展后熔断", SoftwareSelfHealingStopsAfterThreeAttempts);
                Run("报警界面提示不暴露英文故障码", AlarmMessagesAreLocalized);
                Run("UI配置并发保存保持有效XML", ConcurrentUiConfigSaveIsAtomic);
                Run("UI勾选保存防抖并保留最终状态", UiConfigUpdateIsDebounced);
                Run("旧项目100ms断电清零配置自动迁移", LegacyShortOffTimeoutIsMigrated);
                Run("旧项目液压安全节点使用默认值并在保存时补齐", LegacyHydraulicSafetyDefaultsAreCompleted);
                Run("液压容差缺省10bar且显式配置不迁移", HydraulicPressureToleranceDefaults);
                Run("液压目标压力正负10bar判定", HydraulicPressureToleranceWindow);
                Run("液压建压超时提示按失败方向区分", HydraulicBuildTimeoutGuidance);
                Run("液压启动仅软件代次异常进入自愈", HydraulicStartupRecoveryClassification);
                Run("模型原子保存与重载", ProfilePersistence);
                Run("DO完成延迟分布进入控流模型且单圈更新限幅", DoCompletionLatencyFeedsCutoffModel);
                Run("DO四时刻模型克隆保持深拷贝", DoTimingProfileCloneIsDeep);
                Run("版本5模型原位兼容升级且保留学习历史", VersionFiveProfileMigratesWithoutReset);
                Run("后台任务监督记录身份异常并可限时收口", TaskSupervisorTracksFaultsAndDrains);
                Run("控流模型五圈收敛到目标带", CutoffModelConvergesWithinFiveCycles);
                Run("峰值系统偏差用于提前断电补偿", PeakBiasCorrectionIsLearned);
                Run("峰值超过目标2A连续8圈才达到确认值", OvershootStreakRequiresConsecutiveCycles);
                Run("重新开始清除瞬态连续计数但保留学习模型", RestartClearsOnlyTransientStreaks);
                Run("软件自愈循环持续重试直到成功", SoftwareSelfHealingRetriesUntilSuccess);
                Run("软件自愈持续失败可由停止令牌取消", SoftwareSelfHealingPersistentFailureIsCancelable);
                Run("控制异常区分软件自愈与真实安全故障", CycleExceptionRecoveryClassification);
                Run("软件恢复圈明确作废且不算成功", SoftwareRecoveryOutcomeIsNotSuccess);
                Run("非关键观察者异常不影响后续订阅者", NonCriticalObserverFailureDoesNotPropagate);
                Run("报警显示自愈不跨运行代次或新报警", AlarmIndicatorRecoveryIsRunBounded);
                Run("学习资格局部故障不穿透整批等待", FaultIsolatedPhaseKeepsHealthySiblingRunning);
                Run("人工停止仍可取消整个学习资格阶段", PhaseOwnerCancellationStillEscapesIsolation);
                Run("作废学习尝试完整回滚自适应模型", DiscardedLearningAttemptRestoresAdaptiveProfile);
                Run("SaveWithReceipt失败立即回滚runner事务", SaveWithReceiptFailureRestoresRunnerTransaction);
                Run("回滚致命且receipt失败仍保留原异常", FatalPersistenceReceiptFailurePreservesOriginal);
                Run("正式圈异常不得复用上一圈成功结果", FormalCycleRejectsStaleSuccessOutcome);
                Run("正式圈必须控制与落盘均成功才计数", FormalCycleRequiresPersistenceCommitToCount);
                Run("正向低平台连续8圈确认且正常圈清零", ForwardStallStreakRequiresFiveCycles);
                _passed += NonRecoverableAlarmPolicyTests.RunAll();
                Run("旧控制策略模型留档失效并按版本6重新学习", VersionOneProfileMigrates);
                Run("损坏模型回退", CorruptProfileFallback);
                Run("周期超限不追赶且圈号连续", TimerDoesNotCatchUp);
                Run("优雅暂停等待当前圈结束且阻止下一圈", TimerGracefulPauseWaitsForCurrentCycle);
                Run("计划等待窗口内暂停不误启动下一圈", TimerPauseDuringPlannedDelayBlocksNextCycle);
                Run("Timer暂停事件立即纠正运行态", TimerPauseStateCorrectsRunningStatus);
                Run("Timer失活看门狗识别停止和陈旧心跳", TimerRuntimeWatchdogDetectsStoppedAndStale);
                Run("非人工Timer异常允许自动重建续测", TimerAnomalyRecoveryEligibility);
                Run("Timer和组恢复三次失败后只熔断一次并整批重建", TimerRecoveryRetryAndCircuitBreaker);
                Run("只有卡钳通道硬件故障允许锁存停机", ExternalEquipmentFaultsRemainRecoverable);
                Run("外部设备恢复不得拉起报警禁用或人工暂停通道", InfrastructureRecoveryFiltersStoppedChannels);
                Run("可恢复卡钳故障允许三次自启且硬件锁存禁止自启", RecoverableChannelRestartPolicy);
                Run("同进程暂停不因时长增加资格门禁", PauseResumeFiveMinutePolicy);
                Run("单通道恢复按当前公共正式槽重入", PausedChannelRejoinsCurrentSharedFormalSlot);
                Run("批次暂停和恢复预检期间DAQ自愈不得越权恢复定时器", DaqRecoveryRespectsBatchPausePolicy);
                Run("旧报警码不再阻止单通道重启", ChannelAlarmResumePolicy);
                Run("全部停机态均允许单通道重新开始", ChannelStoppedStatesAreRestartable);
                Run("项目禁用通道不得通过RUN按钮重新启动", DisabledChannelCannotRestart);
                Run("新运行复位报警停机锁存", AlarmStopLatchResetsForNewRun);
                Run("报警状态要求CSV和BIN同时存在", AlarmRequiresCsvAndBinFiles);
                Run("错峰部分通道重新编号", StaggerPartialSelection);
                Run("错峰同组全选", StaggerFullGroup);
                Run("错峰不同组并行", StaggerAcrossGroups);
                Run("错峰计划配置快照", StaggerPlanIsImmutableSnapshot);
                Run("连续圈保持固定墙钟相位", StaggerPhaseRemainsFixedAcrossCycles);
                Run("错峰重复组ID被拒绝", StaggerRejectsDuplicateGroupId);
                Run("错峰重复归组被拒绝", StaggerRejectsDuplicateMembership);
                Run("错峰漏配通道被拒绝", StaggerRejectsMissingChannel);
                Run("错峰越界通道被拒绝", StaggerRejectsOutOfRangeChannel);
                Run("错峰零步长被拒绝", StaggerRejectsZeroDelta);
                Run("错峰最大相位越周期被拒绝", StaggerRejectsPhaseBeyondPeriod);
                Run("错峰单通道相位为零", StaggerSingleChannelStartsAtZero);
                Run("错峰任务不等待前相位完成", StaggerExecutorDoesNotSerialize);
                Run("正式圈液压950ms达标后仍保持800ms相位", FormalStaggerSurvivesLateHydraulicQualification);
                Run("正式圈液压早于第二相位时不同时补发", FormalStaggerDoesNotCollapseBeforeSecondPhase);
                Run("正式圈连续500圈相位无累积漂移", FormalStaggerHasNoCumulativeDrift);
                Run("EPB5报警且EPB4联锁状态保持锁存", RuntimeStateDistinguishesSourceAndInterlock);
                Run("DAQ恢复不得解除报警停机状态", RuntimeStateRecoveryCannotClearAlarm);
                Run("基础设施恢复只解除系统故障不解除卡钳报警", InfrastructureRecoveryOnlyClearsSystemFault);
                Run("新运行预检后可复位旧停机状态", RuntimeStateResetsOnlyForNewRun);
                Run("人工停止可覆盖旧启动受阻显示", ManualStopReplacesStartBlocked);
                Run("学习期报警不得越权创建正式Timer", AlarmRecoveryWaitsForFormalCommit);
                Run("禁用通道拒绝组级恢复状态污染", DisabledChannelStateIsNormalized);
                Run("非运行终态禁止继续加电", TerminalRuntimeStatesRejectEnergization);
                Run("启动受阻提交要求执行资源全部清场", TerminalStateRequiresExecutionQuiescence);
                Run("状态版本阻止迟到报警覆盖新运行状态", RuntimeStateRevisionRejectsLateUiDelivery);
                Run("安全退出区分物理安全、持久化边界和可重启条件", StopSafetyExitPolicy);
                Run("停止持久化边界要求Raw发布、写盘和队列同时闭合", StopPersistenceBoundaryPolicy);
                Run("活动圈与终态重试未收口时禁止同进程重启", ActiveCycleBlocksInProcessRestart);
                Run("会话闭合必须同时清空活动表和缓存表", SessionClosureRequiresAllRuntimeStoresIdle);
                Run("迟到旧运行撤销不得清除新运行授权", RunAuthorizationRevocationIsRunBounded);
                Run("迟到旧运行不得登记新运行自动恢复", AutomaticRecoveryRequiresExactRunIdentity);
                Run("Dev1与Dev2有界队列容量互不影响", DaqBoundedQueuesAreIndependent);
                Run("12通道并发首次创建运行对象", TwelveChannelsCreateRuntimesConcurrently);
                Run("12通道错过锚点仍保留800ms相位", OverdueTwelveChannelReleaseCreatesSafely);
                Run("人工停止取消不记为批量启动异常", ManualCancellationIsExpected);
                Run("同通道并发只创建一个运行对象", SameChannelCreatesExactlyOneRuntime);
                Run("12通道启动停止抢占不损坏运行表", ConcurrentStartStopDoesNotCorruptRuntimeStore);
                Run("12通道连续启动停止不残留运行对象", TwelveChannelsRestartWithoutRuntimeLeaks);
                Run("重新开始等待旧启动尾声后才允许新启动", FreshRestartJoinsOldStartupCleanup);
                Run("DO追踪缓冲按运行过滤并限时", DoTraceBufferFiltersRunAndAge);
                Run("报警辅助证据包含计划和DO时序", AlarmControlEvidenceIsReconstructable);
                Run("报警圈证据默认仅报警通道且同组开关不跨组", AlarmCycleEvidenceSelectionIsGroupScoped);
                Run("同组硬故障仅停止故障通道", HardFaultDoesNotStopSiblingChannel);
                Run("2000Hz样本时间严格递增5000 ticks", TwoKilohertzSampleTimestamps);
                Run("DAQ追赶回调不造成相邻批时间重叠", CatchUpCallbackDoesNotOverlapBatches);
                Run("峰值令牌拒绝跨圈和跨运行身份", PeakCaptureTokenRejectsCrossCycleIdentity);
                Run("峰值证据水印不受正负1.5秒系统校时影响", PeakWatermarkIgnoresWallClockJump);
                Run("峰值封口允许120至250毫秒后台排空", PeakDrainBudgetCoversObservedQueueDelay);
                Run("DAQ重叠回调保持时间分配与入队同序", OverlappingCallbacksCommitInTimestampOrder);
                Run("采集重启重建高精度时基", HighResolutionClockReset);
                Run("新项目清零且不改旧项目", NewProjectIsIsolatedAndReset);
                _passed += EpbRecordNormalizationTests.RunAll();
                Run("进度摘要默认选择最小已启动通道", InitialSummarySelectsFirstStarted);
                Run("进度摘要完成后切换且全完成保持", SummaryAdvancesAfterCompletion);
                Run("EPB勾选仅按设置到电源到曲线单向传播", EpbSelectionPropagatesOneWay);
                Run("DHMS运行时间格式", DhmsFormatting);
                Run("固定随机种子10万圈耐久仿真", HundredThousandCycleDurabilitySimulation);
                Run("UI十万条日志生产保持512有界且50行批量消费", UiLogFloodStaysBoundedAndBatched);
                Run("持久化积压低水位后自动恢复", DaqPersistenceCoordinatorTests.PauseAndRecoverAfterLowWater);
                Run("持久化硬容量背压保留全部批次且只发布一次故障", DaqPersistenceCoordinatorTests.HardCapacityKeepsRealFaultCode);
                Run("DAQ代次切换不丢弃已接收持久化FIFO", DaqPersistenceCoordinatorTests.GenerationChangePreservesAcceptedFifo);
                Run("磁盘50至1500ms暂停均不反压生产且单次暂停后恢复", DaqPersistenceCoordinatorTests.DiskPauseMatrixRemainsBoundedAndRecovers);
                Run("写盘超时保留原批次且存储恢复后按序补写", DaqPersistenceCoordinatorTests.RecoveryTimeoutRetainsBatchUntilStorageReturns);
                Run("同步写永久阻塞由独立看门狗单次升级且解除后收口", DaqPersistenceCoordinatorTests.WriteStallWatchdogPublishesOnceAndRetainsBatch);
                Run("主动截止不撤销已写入耐久前缀且不阻塞健康后续流量", DaqPersistenceCoordinatorTests.DurablePrefixAllowsHealthyLaterTrafficButRejectsSuppression);
                Run("冻结边界与SuppressAfter原子安装且禁止扩大", DaqPersistenceCoordinatorTests.CutoffInstallIsImmutableAndRejectsExpansion);
                Run("持久化抑制证据按恢复窗口拆分并累计", DaqPersistenceCoordinatorTests.SuppressionEvidenceResetsPerWindowAndAccumulates);
                Run("活动圈上限事件携带EPB圈号和限制", DaqPersistenceCoordinatorTests.ActiveCycleLimitPublishesLifecycleIdentity);
                Run("持久化诊断观察者异常不重复写盘", DaqPersistenceCoordinatorTests.DiagnosticObserverFailureDoesNotRetryWrite);
                Run("映射故障进程内自愈不触发DAQ停机", DaqPersistenceCoordinatorTests.MappingFailureRecoversBeforeSafetyPause);
                Run("写盘短视图延迟映射与事务切换", DaqPersistenceCoordinatorTests.DiskWriterUsesLazyTransactionalViewsAndCanResetThem);
                Run("新进程收口历史running圈", DaqPersistenceCoordinatorTests.DiskWriterClosesInterruptedRunningCyclesOnStartup);
                Run("六通道2kHz双DAQ真实写盘实时负载", DaqPersistenceCoordinatorTests.SixChannelRealtimePersistenceStaysAhead);
                _passed += DaqRealtimeControlTests.RunAll();
                _passed += CalibrationMathTests.RunAll();
                _passed += HydraulicGroupCoordinatorTests.RunAll();
                _passed += PowerSupplyCoordinatorTests.RunAll();
                _passed += PswTcpClientTimeoutTests.RunAll();
                _passed += ProjectLogStoreTests.RunAll();
                _passed += RecoveryCoordinationTests.RunAll();
                Console.WriteLine($"PASS {_passed}/{_passed}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static int ReplayCsv(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("回放 CSV 不存在。", path);

            var segments = ReadActiveSegments(path);
            if (segments.Count < 2)
                throw new InvalidOperationException($"需要至少两个上电脉冲，实际识别到 {segments.Count} 个。");

            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 10_000, 15, 2, 3);
            var forward = ReplaySegment(machine, segments[0], 0);
            Console.WriteLine(
                $"FORWARD stage={forward.Stage} clamp={forward.ClampReached} fault={forward.HardFault} " +
                $"reason={forward.Reason}");
            if (!forward.ClampReached || forward.HardFault) return 2;

            machine.MarkHold();
            var reverseStartMs = segments[0].Count;
            machine.ArmReverse(Tick(reverseStartMs), 100, 5_000, 3, 3);
            var reverse = ReplaySegment(machine, segments[1], reverseStartMs);
            Console.WriteLine(
                $"REVERSE stage={reverse.Stage} released={reverse.ReleaseCompleted} fault={reverse.HardFault} " +
                $"reason={reverse.Reason}");
            return reverse.ReleaseCompleted && !reverse.HardFault ? 0 : 3;
        }

        private static List<List<double>> ReadActiveSegments(string path)
        {
            var result = new List<List<double>>();
            List<double> active = null;
            using (var reader = new StreamReader(path))
            {
                var header = reader.ReadLine();
                if (header == null) return result;
                var columns = header.Split(',');
                var currentIndex = Array.FindIndex(
                    columns,
                    x => x.Trim().Equals("EpbCurrent", StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0) throw new InvalidDataException("CSV 缺少 EpbCurrent 列。");

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var cells = line.Split(',');
                    if (cells.Length <= currentIndex ||
                        !double.TryParse(
                            cells[currentIndex],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var current))
                        continue;

                    current = Math.Abs(current);
                    if (current > 0.2)
                    {
                        if (active == null) active = new List<double>();
                        active.Add(current);
                    }
                    else if (active != null)
                    {
                        if (active.Count >= 20) result.Add(active);
                        active = null;
                    }
                }
            }

            if (active != null && active.Count >= 20) result.Add(active);
            return result;
        }

        private static EpbAdaptiveDecision ReplaySegment(
            EpbAdaptiveCurrentStateMachine machine,
            List<double> samples,
            int startMs)
        {
            var last = new EpbAdaptiveDecision();
            for (var i = 0; i < samples.Count; i++)
            {
                // 现场 CSV 为约 2 kHz；使用 0.5ms 的 Stopwatch tick 回放。
                var tick = Stopwatch.Frequency +
                           (long)((startMs + i * 0.5) * Stopwatch.Frequency / 1000.0);
                var decision = machine.OnSample(tick, samples[i]);
                if (decision.ClampReached || decision.ReleaseCompleted || decision.HardFault)
                    last = decision;
                if (decision.ClampReached || decision.ReleaseCompleted || decision.HardFault)
                    break;
            }

            return last;
        }

        private static void NormalClamp()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 300, 10, _ => 1.0);
            var decision = Feed(machine, 310, 900, 10, ms => 1.0 + (ms - 310) * 0.025);
            Assert(decision.ClampReached, "未识别夹紧");
            Assert(!decision.HardFault, "正常夹紧被判硬故障");
        }

        private static void LearnedTailLeadPredictsClamp()
        {
            var profile = StableProfile();
            Assert(
                profile.TryAddCutoffObservation(15.0, 14.5, 0.05, 15.0, out var learnedLead),
                "有效控流观测未被模型接受");
            Assert(Math.Abs(learnedLead - 10.0) < 0.01, "等效尾部时间计算错误");

            var machine = new EpbAdaptiveCurrentStateMachine(profile);
            machine.ArmForward(Tick(0), 100, 6000, 15.0, 2.0, 3.0);
            Feed(machine, 0, 300, 10, _ => 1.0);
            var decision = Feed(machine, 302, 800, 2, ms => 1.0 + (ms - 302) * 0.05);

            Assert(decision.ClampReached && !decision.HardFault, "学习提前量后未完成预测夹紧");
            Assert(
                decision.CutoffReason == "PredictedPeak",
                $"学习提前量未走预测触发：Reason={decision.CutoffReason} " +
                $"Cutoff={decision.CutoffCurrentA:F3} Predicted={decision.PredictedPeakA:F3} " +
                $"Slope={decision.EstimatedSlopeAperMs:F4} Lead={decision.PredictionLeadMs:F2}");
            Assert(decision.CutoffCurrentA < 15.0, "预测控制没有在目标前断电");
            Assert(Math.Abs(decision.PredictedPeakA - 15.0) <= 0.35, "预测峰值未落入目标控制带");
        }

        private static void LowSlopeDoesNotPredictEarly()
        {
            var profile = StableProfile();
            profile.TryAddCutoffObservation(15.0, 14.5, 0.05, 15.0, out _);
            var machine = new EpbAdaptiveCurrentStateMachine(profile);
            machine.ArmForward(Tick(0), 100, 6000, 15.0, 2.0, 3.0);
            Feed(machine, 0, 300, 10, _ => 1.0);
            var decision = Feed(machine, 310, 1800, 10, ms => 1.0 + (ms - 310) * 0.0005);
            Assert(!decision.ClampReached && !decision.HardFault, "低斜率波形被预测算法提前误触发");
        }

        private static void ThresholdBeforeLoadRiseBecomesFastRiseCandidate()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 90, 10, _ => 1.0);
            var candidate = machine.OnSample(Tick(100), 15.0);
            Assert(
                candidate.ClampReached && !candidate.HardFault && candidate.SoftWarning &&
                candidate.CutoffReason == "FastRiseCandidate" &&
                candidate.Reason.Contains("AwaitingFullRateEvidence"),
                "未形成负载上升曲线时到达阈值未立即断电，或仍被状态顺序误报为硬故障");
        }

        private static void ClampNeedsThreeSamples()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 300, 10, _ => 1.0);
            Feed(machine, 310, 780, 10, ms => 1.0 + (ms - 310) * 0.025);
            var first = machine.OnSample(Tick(790), 14.2);
            var second = machine.OnSample(Tick(800), 14.1);
            Assert(!first.ClampReached && !second.ClampReached,
                "夹紧阈值单点或两点即被错误判定成功");
            var third = machine.OnSample(Tick(810), 14.0);
            Assert(third.ClampReached, "夹紧阈值连续三样本后仍未确认");
        }

        private static void StableProfileKeepsLearningForFiftyCycles()
        {
            var profile = StableProfile();
            for (var cycle = 0; cycle < 50; cycle++)
            {
                var machine = new EpbAdaptiveCurrentStateMachine(profile);
                machine.ArmForward(Tick(0), 100, 6000, 15.0, 1.0, 3.0);
                Feed(machine, 0, 260, 10, _ => 1.0 + cycle * 0.001);
                var clamp = Feed(
                    machine,
                    270,
                    1000,
                    10,
                    ms => 1.0 + cycle * 0.001 + (ms - 270) * 0.03);

                Assert(clamp.ClampReached && !clamp.HardFault, $"稳定模型第{cycle + 1}圈未夹紧");
                Assert(
                    machine.ObservedForwardEmptyA > 0,
                    $"稳定模型第{cycle + 1}圈正向空行程观测仍为0");
                profile.AddSuccessfulCycle(
                    machine.ObservedForwardEmptyA,
                    1.0,
                    clamp.ElapsedMs,
                    1200);
            }

            Assert(profile.ValidSampleCount == 55, "稳定模型后50圈没有持续增加有效样本数");
            Assert(profile.ForwardEmptyHistoryA.Count == 30, "空行程滚动历史没有保持容量上限");
        }

        private static void ForwardLoadRiseDeadlineFaults()
        {
            // 正式控制未提供启动定位覆盖值时，仍使用程序级3000ms进展期限。
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6500, 15, 1, 3);
            EpbAdaptiveDecision terminal = null;
            for (var ms = 0; ms <= 5500; ms += 10)
            {
                var decision = machine.OnSample(Tick(ms), 1.0);
                if (!decision.HardFault) continue;
                terminal = decision;
                break;
            }

            Assert(
                terminal != null &&
                terminal.Reason.Contains("ForwardLoadRiseNotStarted") &&
                terminal.ElapsedMs >= 3000 &&
                terminal.ElapsedMs <= 3050,
                "首次完全释放后的空行程未按3000ms进展期限硬停");
        }

        private static void ForwardCurrentRiseStallWarnsAndCutsPower()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);
            Feed(machine, 0, 1000, 10, _ => 1.0);
            var ramp = Feed(
                machine,
                1010,
                2500,
                10,
                ms => Math.Min(13.6, 1.0 + (ms - 1000) * 0.0085));
            Assert(!ramp.ClampReached && !ramp.HardFault, "正向13.6A平台形成前已误判终态");

            var transient = Feed(machine, 2510, 2620, 10, _ => 13.6);
            Assert(
                !transient.HardFault && !transient.ClampReached,
                "不足200ms的正向平台被提前断电");

            var warning = Feed(machine, 2630, 2850, 10, _ => 13.6);
            Assert(
                warning.ClampReached &&
                warning.SoftWarning &&
                !warning.HardFault &&
                warning.CutoffReason == "LowTargetPlateau" &&
                warning.Reason.Contains("ClampReachedLowTargetPlateau") &&
                warning.WindowSpanMs >= 200 &&
                warning.ElapsedMs <= 2760,
                "正向低平台未在完整200ms确认窗口后断电并软预警");
        }

        private static void NearTargetPlateauCompletesWithWarning()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);
            Feed(machine, 0, 1000, 10, _ => 1.0);
            var ramp = Feed(
                machine,
                1010,
                2600,
                10,
                ms => Math.Min(14.6, 1.0 + (ms - 1000) * 0.0085));
            Assert(!ramp.HardFault, "14.6A平台形成前已误判硬故障");

            var completed = Feed(machine, 2610, 2900, 10, _ => 14.6);
            Assert(
                completed.ClampReached &&
                completed.SoftWarning &&
                !completed.HardFault &&
                completed.CutoffReason == "NearTargetPlateau" &&
                completed.Reason.Contains("ClampReachedNearTargetPlateau"),
                "14.6A近目标平台未在200ms后安全断电并记软预警");
        }

        private static void LowPlateauRecoversBeforeFaultWindow()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);
            Feed(machine, 0, 1000, 10, _ => 1.0);
            Feed(
                machine,
                1010,
                2600,
                10,
                ms => Math.Min(13.9, 1.0 + (ms - 1000) * 0.0085));

            var shortPlateau = Feed(machine, 2610, 2660, 10, _ => 13.9);
            Assert(
                !shortPlateau.HardFault && !shortPlateau.ClampReached,
                "13.9A不足200ms的短平台被误停");

            var recovered = Feed(
                machine,
                2670,
                3100,
                10,
                ms => Math.Min(15.0, 13.9 + (ms - 2660) * 0.01));
            Assert(
                recovered.ClampReached && !recovered.HardFault,
                "13.9A短平台恢复上升后未能正常夹紧");
        }

        private static void Epb10Cycle28FullRatePeakReplay()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);
            Feed(machine, 0, 1000, 10, _ => 1.0);
            Feed(
                machine,
                1010,
                2600,
                10,
                ms => Math.Min(14.6, 1.0 + (ms - 1000) * 0.0085));

            // 现场第28圈：10ms控制值约14.6A，但2kHz原始峰值达到15.301A。
            var decision = machine.OnSample(Tick(2610), 14.6, 15.301);
            Assert(
                decision.ClampReached &&
                !decision.HardFault &&
                decision.Stage == EpbCurrentStage.ClampReached &&
                decision.CutoffReason == "FullRateTarget" &&
                Math.Abs(decision.ObservedFullRatePeakA - 15.301) < 0.0001 &&
                !decision.Reason.Contains("ForwardCurrentRiseStalled"),
                "EPB10第28圈未按15.301A全数据峰值正常断电");
        }

        private static void Sustained139AmpPlateauWarns()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);
            Feed(machine, 0, 1000, 10, _ => 1.0);
            Feed(
                machine,
                1010,
                2600,
                10,
                ms => Math.Min(13.9, 1.0 + (ms - 1000) * 0.0085));

            var warning = Feed(machine, 2610, 3000, 10, _ => 13.9);
            Assert(
                warning.ClampReached &&
                warning.SoftWarning &&
                !warning.HardFault &&
                warning.CutoffReason == "LowTargetPlateau" &&
                warning.WindowSpanMs >= 200,
                "低于14.2A的13.9A平台持续200ms后未断电并转为软预警");
        }

        private static void ForwardRampThroughHalfTargetDoesNotFault()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);
            Feed(machine, 0, 90, 10, _ => 0.05);
            var ramp = Feed(
                machine,
                100,
                700,
                10,
                ms => 6.0 + (ms - 100) * 0.004);

            Assert(
                !ramp.HardFault,
                "持续上升的正常正向电流在穿越7.5A时被误判为高电流平台");
        }

        private static void ReverseRelease()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 250, 10, _ => 1.0);
            Feed(machine, 260, 900, 10, ms => 1.0 + (ms - 260) * 0.03);
            machine.MarkHold();
            machine.ArmReverse(Tick(1000), 100, 3500, 3.0, 3.0);
            var released = Feed(machine, 1000, 1900, 10, ms => ms < 1250 ? 6.0 - (ms - 1000) * 0.02 : 1.0);
            Assert(released.ReleaseCompleted, "反向低负载稳定后未释放");
        }

        private static void ReverseReleaseWithSeventeenMillisecondCadence()
        {
            var machine = NewReverseMachine(StableProfile());

            EpbAdaptiveDecision released = null;
            for (var ms = 0; ms <= 1500; ms += 17)
            {
                var decision = machine.OnSample(Tick(ms), 1.0);
                if (decision.ReleaseCompleted || decision.HardFault)
                {
                    released = decision;
                    break;
                }
            }

            Assert(released != null && released.ReleaseCompleted && !released.HardFault,
                "17ms采样节拍下稳定窗口被错误判为覆盖不足");
            Assert(released.WindowSpanMs >= 120 && released.WindowSampleCount >= 8,
                "释放决策缺少有效窗口覆盖诊断");
        }

        private static void PreReleaseNormalPlatformUsesForwardReference()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmReverse(Tick(0), 0, 3000, 3.0, 0, 15.0);
            var platform = Feed(machine, 0, 1200, 10, _ => 6.502);
            Assert(!platform.HardFault, "6.5A 正常卸载平台误触发高平台保护");

            var released = Feed(machine, 1210, 2200, 10, _ => 1.0);
            Assert(released.ReleaseCompleted && !released.HardFault,
                "6.5A 正常平台后未能确认进入反向空行程");
        }

        private static void PreReleaseHighPlatformStillFaults()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(StableProfile());
            machine.ArmReverse(Tick(0), 0, 3000, 3.0, 0, 15.0);
            var fault = Feed(machine, 0, 1200, 10, _ => 9.2);
            Assert(
                fault.HardFault &&
                fault.Reason.Contains("AbnormalHighCurrentPlateau") &&
                fault.Reason.Contains("threshold=9.000"),
                "持续超过9A的反向平台未触发保护或阈值不正确");
        }

        private static void PreReleaseFailureBlocksLearning()
        {
            var learningStarted = false;
            try
            {
                EpbManager.EnsurePreReleaseBatchSucceeded(new[] { 9 });
                learningStarted = true;
            }
            catch (InvalidOperationException ex)
            {
                Assert(ex.Message.Contains("9") && ex.Message.Contains("拒绝进入学习/正式阶段"),
                    "预释放失败异常未包含阻断上下文");
            }

            Assert(!learningStarted, "预释放失败后仍进入学习阶段");
        }

        private static void FullRateRapidClampBeforeLoadRiseIsAccepted()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 90, 10, _ => 1.0);
            var decision = machine.OnSample(Tick(100), 15.0, 15.2);
            Assert(
                decision.ClampReached && !decision.HardFault && decision.SoftWarning &&
                decision.CutoffReason == "FullRateRapidClamp",
                "2kHz完整速率证据已确认达标时仍被误报为ThresholdBeforeLoadRise硬故障");
        }

        private static void StartupPositioningRequiresIndependentHardwareEvidence()
        {
            Assert(
                EpbManager.ClassifyStartupPositioningFailure(false, false) ==
                FaultClassification.SoftwareTransient,
                "普通启动定位失败被误判为硬件故障");
            Assert(
                EpbManager.ClassifyStartupPositioningFailure(true, false) ==
                FaultClassification.SoftwareTransient,
                "仅DAQ过流证据时不应确认硬件故障");
            Assert(
                EpbManager.ClassifyStartupPositioningFailure(false, true) ==
                FaultClassification.SoftwareTransient,
                "仅PSU证据且无启动过流时不应确认硬件故障");
            Assert(
                EpbManager.ClassifyStartupPositioningFailure(true, true) ==
                FaultClassification.HardwareConfirmed,
                "DAQ过流与新鲜PSU证据并存时未确认硬件故障");
            Assert(
                EpbManager.ClassifyStartupPositioningFailure(false, false, true) ==
                FaultClassification.SoftwareTransient,
                "启动定位输出关闭失败被错误锁存为卡钳硬件故障");
        }

        private static void StartupPositioningConfirmsHighCurrentAfterInrush()
        {
            var detector = new StartupPositioningCurrentClassifier(100, 14.2, 17.0, 3);
            Assert(detector.Evaluate(20, 18.0) == StartupCurrentClassification.None,
                "涌流忽略窗口内的高电流被错误确认");
            Assert(detector.Evaluate(100, 15.1) == StartupCurrentClassification.None,
                "单个高电流样本被错误确认");
            Assert(detector.Evaluate(102, 15.3) == StartupCurrentClassification.None,
                "两个高电流样本被错误确认");
            Assert(detector.Evaluate(104, 15.2) == StartupCurrentClassification.HighCurrentConfirmed,
                "涌流后连续三点高电流未确认启动位置");
        }

        private static void StartupPositioningOverCurrentTakesPriority()
        {
            var detector = new StartupPositioningCurrentClassifier(100, 14.2, 17.0, 3);
            detector.Evaluate(100, -17.5);
            detector.Evaluate(102, -17.6);
            Assert(detector.Evaluate(104, -17.7) == StartupCurrentClassification.OverCurrent,
                "真正连续过流被错误当成机械终点完成");
        }

        private static void StartupPositioningSeparatesForwardTimingBudgets()
        {
            var limits = new EpbAdaptiveSafetyLimits
            {
                ForwardProgressConfirmMs = 200,
                ForwardProgressDeadlineMs = 3000
            };
            var channels = new[]
            {
                new { Channel = 4, Median = 2482.0, Mad = 57.0 },
                new { Channel = 5, Median = 2867.0, Mad = 39.0 },
                new { Channel = 9, Median = 2842.0, Mad = 40.0 },
                new { Channel = 10, Median = 3346.0, Mad = 141.0 }
            };

            foreach (var item in channels)
            {
                var profile = new EpbAdaptiveProfile
                {
                    Channel = item.Channel,
                    ForwardClampMedianMs = item.Median,
                    ForwardClampMadMs = item.Mad,
                    ValidSampleCount = 5
                };
                var budget = EpbCycleRunner.ResolveStartupForwardTiming(
                    15_000,
                    100,
                    limits,
                    profile,
                    projectForwardLimitMs: 3000);

                Assert(budget.ProgramProgressDeadlineMs == 3000,
                    $"EPB{item.Channel}程序级期限审计值错误");
                Assert(budget.ProjectForwardLimitMs == 3000,
                    $"EPB{item.Channel}项目旧时限未保留为审计值");
                Assert(budget.EffectiveProgressDeadlineMs >= 5000,
                    $"EPB{item.Channel}启动定位仍会在3秒附近误停");
                Assert(budget.AbsoluteOnTimeMs == 9000,
                    $"EPB{item.Channel}启动定位未使用15秒周期对应的9秒绝对硬上限");
                Assert(budget.EffectiveProgressDeadlineMs < budget.AbsoluteOnTimeMs,
                    $"EPB{item.Channel}进展期限与绝对硬上限未真正拆分");
            }
        }

        private static void StartupPositioningFieldSnapshotsSurviveThreeSeconds()
        {
            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Fixtures",
                "20260804_10358-029_startup_positioning.txt");
            var rows = ReadStartupFixture(path);
            var expectedChannels = new[] { 4, 5, 9, 10 };
            Assert(expectedChannels.All(rows.ContainsKey), "10358-029启动定位夹具缺少报警通道");

            foreach (var channel in expectedChannels)
            {
                var points = rows[channel];
                var profile = StartupFieldProfile(channel);
                var limits = new EpbAdaptiveSafetyLimits
                {
                    ForwardProgressConfirmMs = 200,
                    ForwardProgressDeadlineMs = 3000
                };
                var budget = EpbCycleRunner.ResolveStartupForwardTiming(
                    15_000,
                    100,
                    limits,
                    profile,
                    projectForwardLimitMs: 3000);
                var machine = new EpbAdaptiveCurrentStateMachine(profile);
                machine.ArmForward(
                    Tick(0),
                    100,
                    budget.AbsoluteOnTimeMs,
                    15,
                    2,
                    3,
                    limits,
                    budget.EffectiveProgressDeadlineMs);

                EpbAdaptiveDecision last = null;
                foreach (var point in points)
                {
                    last = machine.OnSample(Tick(point.ElapsedMs), point.CurrentA);
                    Assert(!last.HardFault,
                        $"EPB{channel}现场启动快照在{point.ElapsedMs}ms仍被误判：{last.Reason}");
                }

                Assert(last != null && last.ElapsedMs >= 2780,
                    $"EPB{channel}现场启动快照覆盖不足，无法验证原报警边界");
            }
        }

        private static void StartupPositioningSeparatesReverseTimingBudgets()
        {
            var limits = new EpbAdaptiveSafetyLimits
            {
                ReverseProgressConfirmMs = 200,
                ReverseProgressDeadlineMs = 2500
            };
            var profile = StartupReverseFieldProfile(4);
            var budget = EpbCycleRunner.ResolveStartupReverseTiming(
                100,
                limits,
                profile,
                3000,
                3.0);

            Assert(budget.ProgramProgressDeadlineMs == 2500,
                "启动定位反向程序级进展期限错误");
            Assert(budget.LearnedProgressDeadlineMs == 2001,
                "EPB4正式循环历史释放期限审计值错误");
            Assert(budget.EffectiveProgressDeadlineMs == 2500,
                "启动定位仍被正式循环历史释放期限提前截断");
            Assert(budget.AbsoluteOnTimeMs == 3000,
                "启动定位反向绝对上电上限不再是3000ms");
            Assert(Math.Abs(budget.ReleaseThresholdA - 3.0) < 1e-9,
                "启动定位反向释放阈值不再是项目3A带宽");

            var machine = new EpbAdaptiveCurrentStateMachine(profile);
            machine.ArmReverse(
                Tick(0),
                100,
                budget.AbsoluteOnTimeMs,
                budget.ReleaseThresholdA,
                3.0,
                15.0,
                limits,
                1000,
                budget.EffectiveProgressDeadlineMs);
            var beforeProgramDeadline = Feed(machine, 0, 2200, 10, _ => 4.5);
            Assert(!beforeProgramDeadline.ReleaseCompleted && !beforeProgramDeadline.HardFault,
                "启动定位反向仍在EPB4历史2001ms期限附近提前硬停");
            var terminal = Feed(machine, 2210, 2600, 10, _ => 4.5);
            Assert(
                terminal.HardFault &&
                terminal.Reason.Contains("ReverseCurrentDecayStalled") &&
                terminal.ElapsedMs >= 2500 && terminal.ElapsedMs <= 2520,
                "启动定位反向未在程序级2500ms进展期限硬停");
        }

        private static void StartupPositioningReverseFieldSnapshotsRelease()
        {
            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Fixtures",
                "20260804_10358-029_startup_reverse_release.csv");
            var rows = ReadReverseFixture(path);
            var expectedReleaseMs = new Dictionary<int, int>
            {
                [4] = 485,
                [5] = 501,
                [9] = 394,
                [10] = 470
            };
            Assert(expectedReleaseMs.Keys.All(rows.ContainsKey),
                "10358-029启动反向夹具缺少报警通道");

            foreach (var expected in expectedReleaseMs)
            {
                var machine = new EpbAdaptiveCurrentStateMachine(
                    StartupReverseFieldProfile(expected.Key));
                machine.ArmReverse(
                    Tick(0),
                    100,
                    3000,
                    3.0,
                    3.0,
                    15.0,
                    new EpbAdaptiveSafetyLimits(),
                    1000,
                    2500);

                EpbAdaptiveDecision terminal = null;
                foreach (var point in rows[expected.Key])
                {
                    var decision = machine.OnSample(Tick(point.ElapsedMs), point.CurrentA);
                    if (!decision.ReleaseCompleted && !decision.HardFault) continue;
                    terminal = decision;
                    break;
                }

                Assert(
                    terminal != null && terminal.ReleaseCompleted && !terminal.HardFault,
                    $"EPB{expected.Key}启动反向现场快照仍未识别释放：{terminal?.Reason}");
                Assert(Math.Abs(terminal.ReleaseThresholdA - 3.0) < 1e-9,
                    $"EPB{expected.Key}现场回放生效阈值不是3A");
                Assert(
                    Math.Abs(terminal.ElapsedMs - expected.Value) <= 40 &&
                    terminal.ElapsedMs <= 600,
                    $"EPB{expected.Key}现场回放释放时点异常：{terminal.ElapsedMs}ms");
            }
        }

        private static void ReverseReleaseIgnoresSparseOutliers()
        {
            var machine = NewReverseMachine(StableProfile());

            EpbAdaptiveDecision released = null;
            var index = 0;
            for (var ms = 0; ms <= 1800; ms += 17, index++)
            {
                var current = index > 0 && index % 20 == 0 ? 5.0 : 1.0;
                var decision = machine.OnSample(Tick(ms), current);
                if (decision.ReleaseCompleted || decision.HardFault)
                {
                    released = decision;
                    break;
                }
            }

            Assert(released != null && released.ReleaseCompleted && !released.HardFault,
                "孤立电流毛刺导致反向释放候选被持续清零");
            Assert(released.WindowP90A <= released.ReleaseThresholdA,
                "释放时稳健P90仍高于学习阈值");
        }

        private static void ReverseDecayWithPollutedProfileWaitsForPlateau()
        {
            var profile = new EpbAdaptiveProfile
            {
                Channel = 8,
                ValidSampleCount = 10,
                ReverseEmptyCurrentA = 2.495,
                ReverseEmptyMadA = 1.339,
                ReverseReleaseMedianMs = 1256,
                ReverseReleaseMadMs = 322
            };
            var machine = NewReverseMachine(profile);

            var decay = Feed(
                machine,
                0,
                1200,
                10,
                ms => ms < 100
                    ? 0.05
                    : Math.Max(0.70, 8.0 - (ms - 100) * (7.30 / 1100.0)));
            Assert(
                !decay.ReleaseCompleted && !decay.HardFault,
                "反向电流仍在下降时被污染模型误判为稳定低电流平台");

            var released = Feed(machine, 1210, 1600, 10, _ => 0.70);
            Assert(
                released.ReleaseCompleted &&
                !released.HardFault &&
                released.ReleaseThresholdA <= 3.0 + 1e-9 &&
                Math.Abs(released.EstimatedSlopeAperMs) <= 0.001,
                "污染模型下未等待到3A以下的平坦低电流平台再断电");
        }

        private static void MeasuredReverseFixturesRelease()
        {
            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Fixtures",
                "20260730_reverse_release_curves.csv");
            var rows = ReadReverseFixture(path);
            Assert(rows.ContainsKey(8) && rows.ContainsKey(9), "现场回归夹具缺少EPB8或EPB9");

            AssertFixtureReleases(
                8,
                rows[8],
                reverseEmptyA: 0.6140579823,
                reverseMadA: 0.0480287044,
                validSampleCount: 14);
            AssertFixtureReleases(
                9,
                rows[9],
                reverseEmptyA: 1.9260636231,
                reverseMadA: 0.1285986643,
                validSampleCount: 5);
        }

        private static void SustainedReverseLoadFaultsAtProgressDeadline()
        {
            var machine = NewReverseMachine(StableProfile());

            EpbAdaptiveDecision last = null;
            for (var ms = 0; ms <= 3500; ms += 17)
            {
                last = machine.OnSample(Tick(ms), 4.5);
                Assert(!last.ReleaseCompleted, "持续高负载被误判为已经释放");
                if (last.HardFault) break;
            }

            if (last == null || !last.HardFault)
                last = machine.CheckWatchdog(Tick(3500));
            Assert(
                last.HardFault &&
                last.Reason.Contains("ReverseCurrentDecayStalled") &&
                last.ElapsedMs <= 1520,
                "持续3到9A反向平台仍等待绝对上电时限");
        }

        private static void ReverseLowPlateauReleasesInOneWindow()
        {
            var machine = NewReverseMachine(StableProfile());
            var released = Feed(machine, 0, 600, 10, _ => 1.0);
            Assert(
                released.ReleaseCompleted &&
                !released.HardFault &&
                released.ReleaseCandidateElapsedMs >= 200 &&
                released.ElapsedMs <= 220,
                "低于3A的稳定反向平台没有在单个200ms窗口后断电");
        }

        private static void ReverseConfiguredReleaseBandOverridesStableModel()
        {
            var machine = NewReverseMachine(StartupReverseFieldProfile(4));
            var released = Feed(machine, 0, 600, 10, _ => 1.65);
            Assert(
                released.ReleaseCompleted &&
                !released.HardFault &&
                Math.Abs(released.ReleaseThresholdA - 3.0) < 1e-9,
                "稳定画像仍把项目3A释放带收紧到历史空载电流附近");
        }

        private static void ReverseConfiguredReleaseBoundary()
        {
            var atBoundary = NewReverseMachine(StartupReverseFieldProfile(4));
            var released = Feed(atBoundary, 0, 600, 10, _ => 3.0);
            Assert(released.ReleaseCompleted && !released.HardFault,
                "稳定3A平台未按项目释放边界放行");

            var aboveBoundary = NewReverseMachine(StartupReverseFieldProfile(4));
            var pending = Feed(aboveBoundary, 0, 600, 10, _ => 3.01);
            Assert(!pending.ReleaseCompleted && !pending.HardFault,
                "高于3A的稳定平台被错误识别为已经释放");
        }

        private static void HoldDoesNotFault()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 250, 10, _ => 1.0);
            Feed(machine, 260, 900, 10, ms => 1.0 + (ms - 260) * 0.03);
            machine.MarkHold();
            var sample = machine.OnSample(Tick(4000), 0);
            var watchdog = machine.CheckWatchdog(Tick(5000));
            Assert(!sample.HardFault && !watchdog.HardFault, "断电保持阶段误报硬故障");
        }

        private static void ThreeSampleOverCurrent()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            machine.OnSample(Tick(0), 1);
            machine.OnSample(Tick(10), 18);
            machine.OnSample(Tick(20), 18);
            var fault = machine.OnSample(Tick(30), 18);
            Assert(!fault.HardFault && fault.ClampReached &&
                   fault.CutoffReason == "FastOverCurrentCutoff",
                "三样本过流未执行快速正向断电，或仍被错误升级为单次永久报警");
        }

        private static void OpenCircuit()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            EpbAdaptiveDecision last = null;
            for (var ms = 0; ms <= 350; ms += 10)
            {
                last = machine.OnSample(Tick(ms), 0.01);
                if (last.HardFault) break;
            }
            Assert(last != null && last.HardFault && last.Reason.Contains("OpenCircuit"), "开路未触发");
        }

        private static void DaqStale()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            machine.OnSample(Tick(10), 1);
            var fault = machine.CheckWatchdog(Tick(120));
            Assert(fault.HardFault && fault.Reason.Contains("DaqSampleStale"), "DAQ断流未触发");
        }

        private static void NoiseSpike()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 200, 10, _ => 1.0);
            var spike = machine.OnSample(Tick(210), 18.5);
            var recovered = machine.OnSample(Tick(220), 1.1);
            Assert(!spike.HardFault && !recovered.HardFault, "单点噪声误触发硬故障");
        }

        private static void AbsoluteOnTime()
        {
            var machine = NewMachine();
            machine.ArmForward(
                Tick(0),
                100,
                6000,
                15,
                1,
                3,
                new EpbAdaptiveSafetyLimits { ForwardProgressDeadlineMs = 6000 });
            Feed(machine, 0, 5990, 10, _ => 1.0);
            var fault = machine.OnSample(Tick(6000), 1.0);
            Assert(fault.HardFault && fault.Reason.Contains("AbsoluteOnTime"), "绝对时限未触发");
        }

        private static void AbnormalHighPlateau()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            machine.MarkHold();
            machine.ArmReverse(Tick(0), 100, 5000, 3, 3);
            var fault = Feed(machine, 0, 1200, 10, _ => 10.0);
            Assert(fault.HardFault && fault.Reason.Contains("AbnormalHighCurrentPlateau"),
                "异常高电流平台未触发");
        }

        private static void TerminalOffPrecedesBlockingDiagnostics()
        {
            var order = new List<string>();
            using (var publishEntered = new ManualResetEventSlim(false))
            using (var releasePublish = new ManualResetEventSlim(false))
            {
                var decision = new EpbAdaptiveDecision
                {
                    HardFault = true,
                    Stage = EpbCurrentStage.Faulted,
                    Reason = "ForwardCurrentRiseStalled"
                };
                var dispatch = Task.Run(() =>
                    EpbCycleRunner.DispatchAdaptiveDecisionInSafetyOrder(
                        decision,
                        () => order.Add("off"),
                        () =>
                        {
                            order.Add("publish");
                            publishEntered.Set();
                            releasePublish.Wait();
                        },
                        () => order.Add("handle")));

                Assert(publishEntered.Wait(1000), "阻塞诊断发布未进入");
                Assert(order.Count >= 2 && order[0] == "off" && order[1] == "publish",
                    "终态断电没有先于诊断发布执行");
                releasePublish.Set();
                Assert(dispatch.Wait(1000), "终态调度未完成");
                Assert(order.SequenceEqual(new[] { "off", "publish", "handle" }),
                    "终态调度顺序不正确");
            }
        }

        private static void ProjectXmlIgnoresProgramSafetySettings()
        {
            var source = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "MTTfTest",
                "Config",
                "TestConfig.xml"));
            var directory = CreateTempDir();
            var target = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var legacyXml = File.ReadAllText(source)
                    .Replace(
                        "<Channel>9</Channel>",
                        "<Channel>9</Channel>" +
                        "<ForwardProgressConfirmMs>200</ForwardProgressConfirmMs>" +
                        "<ForwardProgressDeadlineMs>3000</ForwardProgressDeadlineMs>" +
                        "<ReverseProgressConfirmMs>100</ReverseProgressConfirmMs>" +
                        "<OffCurrentClearTimeoutMs>100</OffCurrentClearTimeoutMs>");
                File.WriteAllText(target, legacyXml);
                var config = ConfigLoader.LoadTest(target, NullLogger.Instance);
                var channel = config.EpbCycleRunner.GetRunnerChannel(9);
                Assert(
                    channel.ForwardProgressConfirmMs == 200 &&
                    channel.ForwardProgressDeadlineMs == 3000,
                    "旧项目安全字段未能兼容读取");

                ConfigLoader.SaveTest(target, config);
                var saved = File.ReadAllText(target);
                Assert(
                    !saved.Contains("<ForwardProgressConfirmMs>") &&
                    !saved.Contains("<ForwardProgressDeadlineMs>") &&
                    !saved.Contains("<ReverseProgressConfirmMs>") &&
                    !saved.Contains("<OffCurrentClearTimeoutMs>"),
                    "项目保存仍在回写程序级安全参数");

                var program = EpbProgramSafetySettings.FromAppSettings(
                    new System.Collections.Specialized.NameValueCollection(),
                    NullLogger.Instance);
                Assert(
                    program.ForwardProgressConfirmMs == 200 &&
                    program.ForwardProgressDeadlineMs == 3000 &&
                    program.ReverseProgressConfirmMs == 200 &&
                    program.OffCurrentClearTimeoutMs == 1000,
                    "程序级安全策略未采用已恢复的200/3000默认值");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void ProjectRetentionConfigDefaultsValidationAndRoundTrip()
        {
            var source = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
            var directory = CreateTempDir();
            var target = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var xml = File.ReadAllText(source);
                var start = xml.IndexOf("<DataStorageRetention>", StringComparison.Ordinal);
                var end = xml.IndexOf("</DataStorageRetention>", StringComparison.Ordinal);
                if (start >= 0 && end > start)
                    xml = xml.Remove(start, end + "</DataStorageRetention>".Length - start);
                File.WriteAllText(target, xml);
                var missing = ConfigLoader.LoadTest(target, NullLogger.Instance);
                Assert(
                    missing.DataStorageRetention.Latest.RetentionMode == StorageRetentionMode.Count &&
                    missing.DataStorageRetention.Latest.RetainStopPackagesPerChannel == 10,
                    "缺少Latest保留节点时未采用Count/10默认值");

                missing.DataStorageRetention.Latest.RetentionMode = StorageRetentionMode.Unlimited;
                missing.DataStorageRetention.Latest.RetainStopPackagesPerChannel = 25;
                ConfigLoader.SaveTest(target, missing);
                var reloaded = ConfigLoader.LoadTest(target, NullLogger.Instance);
                Assert(
                    reloaded.DataStorageRetention.Latest.RetentionMode == StorageRetentionMode.Unlimited &&
                    reloaded.DataStorageRetention.Latest.RetainStopPackagesPerChannel == 25,
                    "Latest保留配置保存回读不一致");

                var missingFieldXml = File.ReadAllText(target)
                    .Replace(" RetentionMode=\"Unlimited\"", string.Empty)
                    .Replace(" RetainStopPackagesPerChannel=\"25\"", string.Empty);
                File.WriteAllText(target, missingFieldXml);
                var missingFieldLogger = new CollectingLogger();
                var missingFields = ConfigLoader.LoadTest(target, missingFieldLogger);
                Assert(
                    missingFields.DataStorageRetention.Latest.RetentionMode == StorageRetentionMode.Count &&
                    missingFields.DataStorageRetention.Latest.RetainStopPackagesPerChannel == 10 &&
                    missingFieldLogger.Warnings.Count == 0,
                    "Latest缺少单个字段时未静默采用默认值");

                ConfigLoader.SaveTest(target, reloaded);
                var invalidXml = File.ReadAllText(target)
                    .Replace("RetentionMode=\"Unlimited\"", "RetentionMode=\"1\"")
                    .Replace("RetainStopPackagesPerChannel=\"25\"", "RetainStopPackagesPerChannel=\"0\"");
                File.WriteAllText(target, invalidXml);
                var logger = new CollectingLogger();
                var invalid = ConfigLoader.LoadTest(target, logger);
                Assert(
                    invalid.DataStorageRetention.Latest.RetentionMode == StorageRetentionMode.Count &&
                    invalid.DataStorageRetention.Latest.RetainStopPackagesPerChannel == 10 &&
                    logger.Warnings.Count >= 2,
                    "Latest非法配置未回退并记录警告");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void ProgramSafetySettingsValidation()
        {
            var missing = EpbProgramSafetySettings.FromAppSettings(
                new System.Collections.Specialized.NameValueCollection(),
                NullLogger.Instance);
            Assert(
                missing.ForwardProgressConfirmMs == 200 &&
                Math.Abs(missing.ForwardMinimumRiseSlopeAperMs - 0.001) < 1e-9 &&
                missing.ForwardProgressDeadlineMs == 3000 &&
                missing.ForwardNearTargetConfirmMs == 200 &&
                Math.Abs(missing.ForwardAcceptableUndershootA - 0.8) < 1e-9 &&
                missing.ReverseProgressConfirmMs == 200 &&
                missing.ReverseProgressDeadlineMs == 2500 &&
                Math.Abs(missing.OffCurrentClearThresholdA - 0.1) < 1e-9 &&
                missing.OffCurrentClearTimeoutMs == 1000 &&
                Math.Abs(missing.PeakEvidenceMismatchToleranceA - 1.0) < 1e-9 &&
                missing.PeakEvidenceMaximumLagMs == 100,
                "缺少EXE配置时未使用完整编译安全默认值");

            var invalidValues = new System.Collections.Specialized.NameValueCollection
            {
                ["EpbForwardProgressConfirmMs"] = "20",
                ["EpbForwardMinimumRiseSlopeAperMs"] = "not-a-number",
                ["EpbForwardProgressDeadlineMs"] = "300",
                ["EpbForwardNearTargetConfirmMs"] = "20",
                ["EpbForwardAcceptableUndershootA"] = "-1",
                ["EpbReverseProgressConfirmMs"] = "100",
                ["EpbReverseProgressDeadlineMs"] = "300",
                ["EpbOffCurrentClearThresholdA"] = "0",
                ["EpbOffCurrentClearTimeoutMs"] = "100",
                ["EpbPeakEvidenceMismatchToleranceA"] = "0.5",
                ["EpbPeakEvidenceMaximumLagMs"] = "1"
            };
            var normalized = EpbProgramSafetySettings.FromAppSettings(
                invalidValues,
                NullLogger.Instance);
            Assert(
                normalized.ForwardProgressConfirmMs == 200 &&
                Math.Abs(normalized.ForwardMinimumRiseSlopeAperMs - 0.001) < 1e-9 &&
                normalized.ForwardProgressDeadlineMs == 3000 &&
                normalized.ForwardNearTargetConfirmMs == 100 &&
                Math.Abs(normalized.ForwardAcceptableUndershootA - 0.8) < 1e-9 &&
                normalized.ReverseProgressConfirmMs == 200 &&
                normalized.ReverseProgressDeadlineMs == 2500 &&
                Math.Abs(normalized.OffCurrentClearThresholdA - 0.1) < 1e-9 &&
                normalized.OffCurrentClearTimeoutMs == 1000 &&
                Math.Abs(normalized.PeakEvidenceMismatchToleranceA - 1.0) < 1e-9 &&
                normalized.PeakEvidenceMaximumLagMs == 100,
                "非法EXE安全参数未按安全下限钳制");
        }

        private static void ProgramSafetySnapshotIsAuditable()
        {
            var directory = CreateTempDir();
            try
            {
                var values = new System.Collections.Specialized.NameValueCollection
                {
                    ["EpbForwardProgressConfirmMs"] = "1200",
                    ["EpbForwardProgressDeadlineMs"] = "6000"
                };
                var settings = EpbProgramSafetySettings.FromAppSettings(
                    values,
                    NullLogger.Instance);
                var path = settings.SaveEffectiveSnapshot(directory, NullLogger.Instance);
                var xml = File.ReadAllText(path);
                Assert(
                    xml.Contains($"policyVersion=\"{EpbProgramSafetySettings.SafetyPolicyVersion}\"") &&
                    xml.Contains("key=\"EpbForwardProgressConfirmMs\" value=\"1200\" source=\"appSettings\"") &&
                    xml.Contains("key=\"EpbForwardProgressDeadlineMs\" value=\"6000\" source=\"appSettings\"") &&
                    xml.Contains("key=\"EpbReverseProgressConfirmMs\" value=\"200\" source=\"compiled-default\"") &&
                    xml.Contains("key=\"EpbPeakEvidenceMismatchToleranceA\" value=\"1\" source=\"compiled-default\"") &&
                    xml.Contains("key=\"EpbPeakEvidenceMaximumLagMs\" value=\"100\" source=\"compiled-default\""),
                    "程序安全快照未完整记录策略版本、生效值和来源");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void AlarmCycleEvidenceSelectionIsGroupScoped()
        {
            var first = new ElectricalGroup { Id = 1 };
            first.Members.AddRange(new[] { 4, 5, 6 });
            var second = new ElectricalGroup { Id = 2 };
            second.Members.AddRange(new[] { 7, 8, 9 });
            var groups = new[] { first, second };
            Assert(
                EpbManager.ResolveAlarmCycleEvidenceChannels(4, groups, false)
                    .SequenceEqual(new[] { 4 }),
                "同组开关关闭时报警圈证据包含了其他通道");
            Assert(
                EpbManager.ResolveAlarmCycleEvidenceChannels(4, groups, true)
                    .SequenceEqual(new[] { 4, 5, 6 }),
                "同组开关开启时未仅增加同电源组通道");
            Assert(
                !EpbManager.ResolveAlarmCycleEvidenceChannels(4, groups, true).Contains(7),
                "报警圈证据错误包含不同电源组通道");
        }

        private static void AlarmConfigLoadsForwardStallConfirmation()
        {
            var source = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "MTTfTest",
                "Config",
                "AlarmConfig.xml"));
            var loaded = AlarmConfigLoader.Load(source);
            Assert(
                loaded.Behavior.AdaptiveForwardStallConfirmCycles == 8 &&
                loaded.Behavior.AdaptiveOvershootConfirmCycles == 8 &&
                Math.Abs(loaded.Behavior.AdaptivePermanentOvershootDeltaA - 2.0) < 1e-9 &&
                loaded.Behavior.PeakEvidenceMismatchConfirmCycles == 3 &&
                loaded.Behavior.GenericFaultConfirmCycles == 3 &&
                loaded.WarningSnapshots.Enabled &&
                !loaded.WarningSnapshots.FullEvidenceEnabled &&
                loaded.WarningSnapshots.SaveCsv &&
                loaded.WarningSnapshots.SaveBin &&
                loaded.WarningSnapshots.HardAlarmLastNCycles == 10 &&
                !loaded.WarningSnapshots.HardAlarmIncludeSameElectricalGroup &&
                loaded.WarningSnapshots.HardAlarmSameGroupLastNCycles == 10 &&
                loaded.WarningSnapshots.SoftWarningRetentionMode == StorageRetentionMode.Count &&
                loaded.WarningSnapshots.SoftWarningRetainCountPerChannelCode == 30 &&
                loaded.WarningSnapshots.SoftWarningQuotaMb == 0 &&
                loaded.WarningSnapshots.DiskFreeWarningMb == 10240 &&
                loaded.WarningSnapshots.FullEvidenceQueueCapacity == 1 &&
                loaded.WarningSnapshots.ScalarEvidenceQueueCapacity == 4096,
                "AlarmConfig.xml 未加载连续阈值或预警快照安全默认值");

            var directory = CreateTempDir();
            try
            {
                var target = Path.Combine(directory, "AlarmConfig.xml");
                File.WriteAllText(
                    target,
                    File.ReadAllText(source).Replace(
                        "SoftWarningRetentionMode=\"Count\"",
                        "SoftWarningRetentionMode=\"1\""));
                var logger = new CollectingLogger();
                var invalid = AlarmConfigLoader.Load(target, logger);
                Assert(
                    invalid.WarningSnapshots.SoftWarningRetentionMode == StorageRetentionMode.Count &&
                    logger.Warnings.Count >= 1,
                    "AlarmConfig数字模式未按非法值回退并告警");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void WarningSnapshotRetentionModes()
        {
            var directory = CreateTempDir();
            try
            {
                for (var i = 0; i < 31; i++)
                {
                    var timeToken = (120000000 + i).ToString("D9", CultureInfo.InvariantCulture);
                    var eventDirectory = Path.Combine(
                        directory,
                        $"20260807_{timeToken}-Cycle{i + 1:D6}-Streak1of3");
                    Directory.CreateDirectory(eventDirectory);
                    File.WriteAllText(Path.Combine(eventDirectory, "cycle.csv"), "csv");
                    File.WriteAllText(Path.Combine(eventDirectory, "cycle.bin"), "bin");
                    File.WriteAllText(Path.Combine(eventDirectory, "warning-metadata.json"), "{}");
                    File.WriteAllText(Path.Combine(eventDirectory, "checksums.sha256"), "hash");
                }
                var unknown = Path.Combine(directory, "unknown-event");
                Directory.CreateDirectory(unknown);
                EpbManager.EnforceSoftWarningCountRetention(
                    directory,
                    new WarningSnapshotConfig
                    {
                        SoftWarningRetentionMode = StorageRetentionMode.Count,
                        SoftWarningRetainCountPerChannelCode = 30,
                        SaveCsv = true,
                        SaveBin = true
                    });
                Assert(
                    Directory.GetDirectories(directory)
                        .Count(path => Path.GetFileName(path) != "unknown-event") == 30,
                    "第31次软预警后未只保留最新30次");
                Assert(Directory.Exists(unknown), "软预警未知目录被错误删除");

                EpbManager.EnforceSoftWarningCountRetention(
                    directory,
                    new WarningSnapshotConfig
                    {
                        SoftWarningRetentionMode = StorageRetentionMode.Unlimited,
                        SoftWarningRetainCountPerChannelCode = 1
                    });
                Assert(Directory.GetDirectories(directory).Length == 31,
                    "软预警Unlimited模式错误删除目录");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void GenericFaultConfirmationUsesAttemptCycles()
        {
            var tracker = new FaultConfirmationTracker();
            var first = tracker.Observe("Channel:4", "DaqSampleStale", 1001, 3);
            var duplicate = tracker.Observe("Channel:4", "DaqSampleStale", 1001, 3);
            var second = tracker.Observe("Channel:4", "DaqSampleStale", 1002, 3);
            var third = tracker.Observe("Channel:4", "DaqSampleStale", 1003, 3);
            Assert(
                first.Streak == 1 &&
                duplicate.DuplicateAttempt && duplicate.Streak == 1 &&
                second.Streak == 2 &&
                third.Streak == 3 &&
                third.Disposition == FaultConfirmationDisposition.ConfirmedRecoverableAlarm,
                "普通故障未按唯一尝试圈连续3次确认");
        }

        private static void GenericFaultConfirmationResetsAndIsolates()
        {
            var tracker = new FaultConfirmationTracker();
            tracker.Observe("Channel:4", "OpenCircuit", 1, 3);
            tracker.Observe("Channel:4", "OpenCircuit", 2, 3);
            var otherCode = tracker.Observe("Channel:4", "AbsoluteOnTime", 2, 3);
            var otherChannel = tracker.Observe("Channel:5", "OpenCircuit", 2, 3);
            tracker.ResetScope("Channel:4");
            var reset = tracker.Observe("Channel:4", "OpenCircuit", 3, 3);
            Assert(
                otherCode.Streak == 1 && otherChannel.Streak == 1 && reset.Streak == 1,
                "故障确认状态未按作用域和故障码隔离或成功圈清零失败");
        }

        private static void ImmediateAndPreconfirmedFaultClassification()
        {
            Assert(EpbManager.IsImmediateCurrentHardFault("AdaptiveHardFault OverCurrent3Samples"),
                "实时三采样过流未归入首次硬停");
            Assert(!EpbManager.IsImmediateCurrentHardFault(
                    "ForwardPeakOvershoot2AConfirmed Peak=18.1A Streak=8/8"),
                "完整峰值累计报警被错误归入快速单次硬停");
            Assert(EpbManager.IsAlreadyCycleConfirmedFault(
                    "ForwardLowPlateauConfirmed Streak=8/8"),
                "专用连续圈策略被重复进入通用3圈确认");
            Assert(!EpbManager.IsImmediateCurrentHardFault("OpenCircuit"),
                "普通非电流故障被误归入首次硬停");
        }

        private static void TimerOwnedCancellationIsNotError()
        {
            var logger = new CollectingLogger();
            var timer = new HighPrecisionTimer(1000, OverrunPolicy.AlignToWallClock, logger);
            var started = new ManualResetEventSlim(false);
            var task = timer.StartAsync(null, 0, async (_, token) =>
            {
                started.Set();
                await Task.Delay(10000, token).ConfigureAwait(false);
                return true;
            });
            Assert(started.Wait(2000), "定时器测试任务未启动");
            timer.Stop();
            Assert(task.Wait(3000), "定时器停止后未退出");
            Assert(logger.Errors.Count == 0, "定时器自身取消仍被记录为ERROR");
        }

        private static void PeakCaptureTokenRejectsCrossCycleIdentity()
        {
            var runId = Guid.NewGuid();
            var active = new PeakCaptureToken
            {
                CaptureId = Guid.NewGuid(), TestRunId = runId, Channel = 8,
                CycleNumber = 11383, StartUtc = DateTime.UtcNow
            };
            var same = new PeakCaptureToken
            {
                CaptureId = active.CaptureId, TestRunId = runId, Channel = 8,
                CycleNumber = 11383, StartUtc = active.StartUtc
            };
            var stale = new PeakCaptureToken
            {
                CaptureId = active.CaptureId, TestRunId = runId, Channel = 8,
                CycleNumber = 11382, StartUtc = active.StartUtc
            };
            Assert(TwoDeviceAiAcquirer.IsPeakCaptureIdentityMatch(active, same),
                "完全一致的峰值令牌被拒绝");
            Assert(!TwoDeviceAiAcquirer.IsPeakCaptureIdentityMatch(active, stale),
                "跨圈陈旧峰值令牌未被拒绝");
            stale.CycleNumber = active.CycleNumber;
            stale.TestRunId = Guid.NewGuid();
            Assert(!TwoDeviceAiAcquirer.IsPeakCaptureIdentityMatch(active, stale),
                "跨运行峰值令牌未被拒绝");
        }

        private static void PeakWatermarkIgnoresWallClockJump()
        {
            const long frequency = 10000000;
            const long start = 50000000;
            foreach (var wallJumpMs in new[] { -1500, 1500 })
            {
                var wallBefore = DateTime.UtcNow;
                var wallAfter = wallBefore.AddMilliseconds(wallJumpMs);
                var watermark = new PeakCaptureWatermark();
                watermark.Arm(7, 100, start);
                Assert(watermark.Observe(7, 101, start + 100000),
                    "开始后的同代次样本未纳入证据窗");
                watermark.Freeze(7, 102, start + 1000000);
                Assert(!watermark.Observe(8, 999, start + 9999999),
                    "不同DAQ代次样本混入当前峰值窗");
                Assert(!watermark.IsCutoffCovered,
                    "不同代次样本错误推进截止水印");
                Assert(!watermark.Observe(7, 103, start + 1100000),
                    "截止后的样本被错误纳入峰值");
                Assert(watermark.IsCutoffCovered,
                    $"墙钟跳变{wallJumpMs}ms改变了单调水印覆盖判定：{wallBefore:O}->{wallAfter:O}");
                Assert(watermark.GetEvidenceTailLagMs(frequency) >= 0,
                    "单调尾差出现负值");
            }
        }

        private static void PeakDrainBudgetCoversObservedQueueDelay()
        {
            Assert(TwoDeviceAiAcquirer.SelectPeakDrainTimeoutMs(100, 16) >= 250,
                "100ms旧参数仍截断合法的120~250ms后台排队");
            Assert(TwoDeviceAiAcquirer.SelectPeakDrainTimeoutMs(0, 60) >= 290,
                "排空预算未随DAQ批周期扩展");
            Assert(TwoDeviceAiAcquirer.SelectPeakDrainTimeoutMs(5000, 16) == 1000,
                "峰值排空预算缺少有界上限");

            var watermark = new PeakCaptureWatermark();
            const long frequency = 10000000;
            watermark.Arm(3, 10, frequency);
            watermark.Freeze(3, 11, frequency + 1000000);
            // 模拟后台在220ms后才处理到截止后的下一批；到达延迟不改变样本单调时间。
            watermark.Observe(3, 12, frequency + 1100000);
            Assert(watermark.IsCutoffCovered, "220ms后台延迟后的同代次水印未能正常封口");
        }

        private static void LegacyShortOffTimeoutIsMigrated()
        {
            var source = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "MTTfTest",
                "Config",
                "TestConfig.xml"));
            var directory = CreateTempDir();
            var target = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var legacyXml = File.ReadAllText(source)
                    .Replace(
                        "<OffCurrentClearTimeoutMs>1000</OffCurrentClearTimeoutMs>",
                        "<OffCurrentClearTimeoutMs>100</OffCurrentClearTimeoutMs>");
                File.WriteAllText(target, legacyXml);

                var config = ConfigLoader.LoadTest(target, NullLogger.Instance);
                var migrated = config.EpbCycleRunner.GetRunnerChannel(8);
                Assert(migrated.OffCurrentClearTimeoutMs == 1000,
                    "旧项目的100ms断电清零超时未在加载时迁移到1000ms");

                var normalized = new EpbAdaptiveSafetyLimits
                {
                    OffCurrentClearTimeoutMs = 100
                }.Normalized();
                Assert(normalized.OffCurrentClearTimeoutMs == 1000,
                    "运行时安全参数仍允许100ms旧值穿透");

                ConfigLoader.SaveTest(target, config);
                var reloaded = ConfigLoader.LoadTest(target, NullLogger.Instance)
                    .EpbCycleRunner
                    .GetRunnerChannel(8);
                Assert(reloaded.OffCurrentClearTimeoutMs == 1000,
                    "迁移后的1000ms断电清零超时未持久化");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void LegacyHydraulicSafetyDefaultsAreCompleted()
        {
            var source = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
            var directory = CreateTempDir();
            var target = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var xml = File.ReadAllText(source);
                foreach (var element in new[]
                         {
                             "BuildTimeoutMs", "BuildStableMs", "PressureSampleMaxAgeMs", "PressureToleranceBar",
                             "HoldDropToleranceBar", "HoldDropConfirmMs", "BarrierTimeoutMs"
                         })
                {
                    xml = System.Text.RegularExpressions.Regex.Replace(
                        xml,
                        $@"\s*<{element}>.*?</{element}>",
                        string.Empty);
                }
                File.WriteAllText(target, xml);
                var loaded = ConfigLoader.LoadTest(target, NullLogger.Instance);
                Assert(loaded.Hydraulics.All(x =>
                        x.BuildTimeoutMs == 5000 && x.BuildStableMs == 200 &&
                        x.PressureSampleMaxAgeMs == 100 &&
                        Math.Abs(x.PressureToleranceBar - 10) < 1e-9 &&
                        Math.Abs(x.HoldDropToleranceBar - 10) < 1e-9 &&
                        x.HoldDropConfirmMs == 1000 &&
                        Math.Abs(x.EffectiveHoldDropToleranceBar - 10) < 1e-9 &&
                        x.EffectiveHoldDropConfirmMs == 1000 && x.BarrierTimeoutMs == 0),
                    "旧项目缺少液压安全节点时未使用安全默认值");
                ConfigLoader.SaveTest(target, loaded);
                var saved = File.ReadAllText(target);
                Assert(saved.Contains("<BuildTimeoutMs>5000</BuildTimeoutMs>") &&
                       saved.Contains("<PressureToleranceBar>10</PressureToleranceBar>") &&
                       saved.Contains("<PressureSampleMaxAgeMs>100</PressureSampleMaxAgeMs>") &&
                       saved.Contains("<BarrierTimeoutMs>0</BarrierTimeoutMs>"),
                    "保存旧项目时未补齐液压安全节点");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void HydraulicPressureToleranceDefaults()
        {
            var source = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
            var directory = CreateTempDir();
            var target = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var xml = File.ReadAllText(source);
                xml = new System.Text.RegularExpressions.Regex(
                        "<PressureToleranceBar>10</PressureToleranceBar>")
                    .Replace(xml, "<PressureToleranceBar>5</PressureToleranceBar>", 1);
                File.WriteAllText(target, xml);

                var loaded = ConfigLoader.LoadTest(target, NullLogger.Instance);
                Assert(Math.Abs(loaded.Hydraulics[0].PressureToleranceBar - 5) < 1e-9,
                    "显式配置的±5bar容差被错误迁移");
                Assert(Math.Abs(loaded.Hydraulics[1].PressureToleranceBar - 10) < 1e-9,
                    "未修改的模板容差不再是±10bar");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void HydraulicPressureToleranceWindow()
        {
            Assert(HydraulicController.IsPressureWithinTarget(75.983, 70, 10),
                "现场75.983bar应在默认±10bar窗口内");
            Assert(HydraulicController.IsPressureWithinTarget(60, 70, 10),
                "容差窗口下边界应合格");
            Assert(HydraulicController.IsPressureWithinTarget(80, 70, 10),
                "容差窗口上边界应合格");
            Assert(!HydraulicController.IsPressureWithinTarget(59.999, 70, 10),
                "低于容差窗口不应合格");
            Assert(!HydraulicController.IsPressureWithinTarget(80.001, 70, 10),
                "高于容差窗口不应合格");
        }

        private static void HydraulicBuildTimeoutGuidance()
        {
            var below = new HydraulicBuildTimeoutException(1, 70, 10, 55, 5000, "BelowToleranceWindow");
            var above = new HydraulicBuildTimeoutException(2, 70, 10, 85, 5000, "AboveToleranceWindow");
            var stale = new HydraulicBuildTimeoutException(2, 70, 10, double.NaN, 5000,
                "PressureSampleStale AgeMs=150.0");

            Assert(below.Message.Contains("brake-fluid level/leakage") &&
                   !below.Message.Contains("pressure regulator/control valve"),
                "低压超时未给出泄漏和供压方向提示");
            Assert(above.Message.Contains("pressure regulator/control valve") &&
                   !above.Message.Contains("caliper cracks"),
                "超调超时仍错误提示检查卡钳泄漏");
            Assert(stale.Message.Contains("pressure-sensor wiring") &&
                   stale.Message.Contains("DAQ sampling"),
                "压力采样异常未给出传感器和DAQ方向提示");
        }

        private static void OffFailureEscalatesToPowerGroup()
        {
            var escalations = 0;
            var commandResult = EpbCycleRunner.ExecuteTerminalOffWithEscalation(
                () => false,
                () => escalations++);
            Assert(!commandResult && escalations == 1, "DO关闭失败未触发组级联锁");

            var currentCleared = EpbCycleRunner.VerifyOffCurrentOrEscalate(
                0.35,
                0.1,
                () => escalations++);
            Assert(!currentCleared && escalations == 2, "断电后电流未清零未触发组级联锁");

            var validClear = EpbCycleRunner.VerifyOffCurrentOrEscalate(
                0.05,
                0.1,
                () => escalations++);
            Assert(validClear && escalations == 2, "电流已清零仍错误触发组级联锁");
        }

        private static void OffCurrentPollingWindow()
        {
            var reads = 0;
            var clears = EpbCycleRunner.PollOffCurrentUntilClearAsync(
                    () => ++reads < 4 ? 0.35 : 0.05,
                    0.1,
                    200,
                    20,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(clears.Cleared && clears.CurrentA <= 0.1 && reads == 4,
                "断电电流在轮询窗口内清零仍被判定失败");

            var timedOut = EpbCycleRunner.PollOffCurrentUntilClearAsync(
                    () => 0.35,
                    0.1,
                    60,
                    20,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var escalations = 0;
            var cleared = EpbCycleRunner.VerifyOffCurrentOrEscalate(
                timedOut.CurrentA,
                0.1,
                () => escalations++);
            Assert(!timedOut.Cleared && !cleared && escalations == 1,
                "断电电流持续超限未在窗口结束后只触发一次联锁");
        }

        private static void NegativeOffCurrentDoesNotClear()
        {
            var result = EpbCycleRunner.PollOffCurrentUntilClearAsync(
                    () => -15.0,
                    0.1,
                    40,
                    10,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(!result.Cleared, "反向残余负电流被有符号比较误判为已清零");
        }

        private static void StaleOffCurrentIsUnverifiable()
        {
            var reads = 0;
            var recovered = EpbCycleRunner.PollOffCurrentUntilClearAsync(
                    () => ++reads < 4
                        ? new EpbCycleRunner.OffCurrentSample(0.35, false, 147_000)
                        : new EpbCycleRunner.OffCurrentSample(0.05, true, 5),
                    0.1,
                    200,
                    10,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(recovered.Cleared && recovered.SampleFresh && reads == 4,
                "DAQ恢复后的最新断电电流未替换陈旧缓存样本");

            var result = EpbCycleRunner.PollOffCurrentUntilClearAsync(
                    () => new EpbCycleRunner.OffCurrentSample(0.35, false, 147_000),
                    0.1,
                    60,
                    10,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(!result.Cleared && !result.SampleFresh && result.ElapsedMs >= 40,
                "DAQ持续陈旧时未等待到有界超时，或被误分类为真实电流未清零");
        }

        private static void OffCurrentThresholdTracksTrustedBaseline()
        {
            var fieldThreshold = EpbCycleRunner.ResolveOffCurrentClearThreshold(
                0.1,
                0.109919);
            Assert(Math.Abs(fieldThreshold - 0.159919) < 1e-9,
                "现场约0.11A零偏未转换为带裕量的有效清零阈值");
            Assert(0.111596 <= fieldThreshold,
                "现场断电后已回到基线的电流仍会被误判");
            Assert(0.35 > fieldThreshold,
                "真正未清零的电流被零偏阈值掩盖");

            var untrustedHighBaseline = EpbCycleRunner.ResolveOffCurrentClearThreshold(
                0.1,
                0.35);
            Assert(Math.Abs(untrustedHighBaseline - 0.1) < 1e-9,
                "异常高的上电前电流被错误信任");

            var cappedThreshold = EpbCycleRunner.ResolveOffCurrentClearThreshold(
                0.1,
                0.20);
            Assert(Math.Abs(cappedThreshold - 0.25) < 1e-9,
                "零偏自适应阈值未保持0.25A安全上限");

            var explicitHigherThreshold = EpbCycleRunner.ResolveOffCurrentClearThreshold(
                0.3,
                0.10);
            Assert(Math.Abs(explicitHigherThreshold - 0.3) < 1e-9,
                "项目显式配置的更高阈值被自适应逻辑错误降低");
        }

        private static void ProfilePersistence()
        {
            var dir = CreateTempDir();
            try
            {
                var store = new EpbAdaptiveProfileStore(dir);
                var profile = store.GetOrCreate(10);
                for (var i = 0; i < 5; i++)
                    profile.AddSuccessfulCycle(1.0 + i * 0.01, 0.8 + i * 0.01, 3000 + i * 10, 1200 + i * 5);
                var decisionUtc = new DateTime(2026, 8, 10, 1, 2, 3, DateTimeKind.Utc);
                profile.TryAddCutoffObservation(
                    15.0,
                    14.5,
                    0.05,
                    15.0,
                    new EpbDoTimingObservation
                    {
                        DecisionUtc = decisionUtc,
                        DoWriteStartedUtc = decisionUtc.AddMilliseconds(1),
                        DoWriteCompletedUtc = decisionUtc.AddMilliseconds(3),
                        CurrentClearedUtc = decisionUtc.AddMilliseconds(8)
                    },
                    out _);
                profile.UpdateForwardStallStreak(true);
                profile.UpdateForwardStallStreak(true);
                store.Save(profile);

                var loaded = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(loaded.ValidSampleCount == 5, "模型样本数未持久化");
                Assert(loaded.IsStable, "五圈后模型未进入稳定状态");
                Assert(loaded.ModelVersion == 6, "控流模型未保存为版本6");
                Assert(loaded.ValidCutoffSampleCount == 1, "控流样本数未持久化");
                Assert(loaded.ConsecutiveForwardStallCount == 2, "正向低平台连续计数未持久化");
                Assert(Math.Abs(loaded.ForwardCutoffLeadMedianMs - 10.0) < 0.01,
                    "控流提前时间未持久化");
                Assert(Math.Abs(loaded.ForwardDoCompletionMedianMs - 3.0) < 0.01,
                    "DO完成延迟未持久化");
                Assert(Math.Abs(loaded.ForwardDoCompletionP95Ms - 3.0) < 0.01 &&
                       Math.Abs(loaded.ForwardDoStartDelayP95Ms - 1.0) < 0.01 &&
                       Math.Abs(loaded.ForwardDoWriteP95Ms - 2.0) < 0.01 &&
                       Math.Abs(loaded.ForwardCurrentClearP95Ms - 5.0) < 0.01,
                    "DO四时刻分布或P95未持久化");
                Assert(loaded.ValidDoTimingSampleCount == 1 &&
                       loaded.ValidCurrentClearSampleCount == 1 &&
                       loaded.LastForwardDoTiming?.DecisionUtc == decisionUtc &&
                       loaded.LastForwardDoTiming?.CurrentClearedUtc == decisionUtc.AddMilliseconds(8),
                    "DO四时刻样本计数或最后因果时间线未重载");
                Assert(File.Exists(Path.Combine(dir, "EpbAdaptiveProfiles.xml")), "模型文件不存在");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void CutoffModelConvergesWithinFiveCycles()
        {
            var profile = StableProfile();
            const double targetA = 15.0;
            const double slopeAperMs = 0.05;
            const double physicalTailLeadMs = 8.0;
            var leadMs = 2.0 / slopeAperMs;
            var errors = new List<double>();

            for (var cycle = 0; cycle < 5; cycle++)
            {
                var cutoffCurrentA = targetA - slopeAperMs * leadMs;
                var actualPeakA = cutoffCurrentA + slopeAperMs * physicalTailLeadMs;
                errors.Add(actualPeakA - targetA);
                Assert(
                    profile.TryAddCutoffObservation(
                        targetA,
                        cutoffCurrentA,
                        slopeAperMs,
                        actualPeakA,
                        out _),
                    $"第{cycle + 1}圈控流观测未写入");
                leadMs = profile.ForwardCutoffLeadMedianMs;
            }

            Assert(profile.ValidCutoffSampleCount == 5, "五圈控流样本数错误");
            Assert(Math.Abs(errors[errors.Count - 1]) <= 0.3, "五圈后峰值误差未进入±0.3A");
            Assert(errors.TrueForAll(x => x <= 0.8), "正常学习波形出现超过+0.8A超调");
        }

        private static void PeakBiasCorrectionIsLearned()
        {
            var profile = StableProfile();
            for (var i = 0; i < 5; i++)
            {
                Assert(
                    profile.TryAddCutoffObservation(15.0, 14.0, 0.01, 15.5, out _),
                    "峰值偏差样本未写入");
            }

            Assert(
                Math.Abs(profile.GetForwardPeakBiasCorrectionA() - 0.5) < 0.001,
                "连续正偏差未形成预测峰值补偿");
        }

        private static void OvershootStreakRequiresConsecutiveCycles()
        {
            var profile = StableProfile();
            for (var cycle = 1; cycle <= 8; cycle++)
                Assert(profile.UpdateForwardPermanentOvershootStreak(true) == cycle,
                    $"永久过冲第{cycle}圈计数错误");
            Assert(profile.UpdateForwardPermanentOvershootStreak(false) == 0,
                "峰值回到目标+2A以内后连续计数未清零");
        }

        private static void DoCompletionLatencyFeedsCutoffModel()
        {
            var fastDo = StableProfile();
            var slowDo = StableProfile();
            var baseUtc = new DateTime(2026, 8, 10, 2, 0, 0, DateTimeKind.Utc);
            const double targetA = 15.0;
            const double cutoffA = 14.0;
            const double slope = 0.05;
            const double physicalTailMs = 7.0;
            var coarseClockTiming = EpbCycleRunner.BuildAdaptiveDoTimingObservation(
                baseUtc,
                baseUtc,
                3.0,
                1.0);
            Assert(coarseClockTiming.TryGetDurations(
                       out var coarseStartMs,
                       out var coarseWriteMs,
                       out var coarseTotalMs,
                       out _) &&
                   Math.Abs(coarseStartMs - 2.0) < 0.01 &&
                   Math.Abs(coarseWriteMs - 1.0) < 0.01 &&
                   Math.Abs(coarseTotalMs - 3.0) < 0.01,
                "物理完成UTC在粗粒度墙钟下未能结合单调遥测重建DO四时刻");
            for (var i = 0; i < 5; i++)
            {
                Assert(
                    fastDo.TryAddCutoffObservation(
                        targetA,
                        cutoffA,
                        slope,
                        cutoffA + slope * (physicalTailMs + 2.0),
                        NewDoTiming(baseUtc.AddSeconds(i), 1.0, 2.0, 6.0),
                        out _),
                    "快速DO延迟样本被拒绝");
                Assert(
                    slowDo.TryAddCutoffObservation(
                        targetA,
                        cutoffA,
                        slope,
                        cutoffA + slope * (physicalTailMs + 8.0),
                        NewDoTiming(baseUtc.AddSeconds(i), 6.0, 8.0, 16.0),
                        out _),
                    "慢速DO延迟样本被拒绝");
            }

            Assert(Math.Abs(fastDo.ForwardPhysicalTailMedianMs - physicalTailMs) < 0.01,
                "快速DO模型未分离物理尾升");
            Assert(Math.Abs(slowDo.ForwardPhysicalTailMedianMs - physicalTailMs) < 0.01,
                "慢速DO模型未分离物理尾升");
            Assert(Math.Abs(fastDo.ForwardCutoffLeadMedianMs - 9.0) < 0.01,
                "快速DO的预测提前量不正确");
            Assert(Math.Abs(slowDo.ForwardCutoffLeadMedianMs - 15.0) < 0.01,
                "慢速DO的预测提前量未包含实测延迟");
            Assert(Math.Abs(fastDo.ForwardDoCompletionP95Ms - 2.0) < 0.01 &&
                   Math.Abs(fastDo.ForwardDoStartDelayP95Ms - 1.0) < 0.01 &&
                   Math.Abs(fastDo.ForwardDoWriteP95Ms - 1.0) < 0.01 &&
                   Math.Abs(fastDo.ForwardCurrentClearP95Ms - 4.0) < 0.01,
                $"每通道DO四时刻P95未按独立分量学习：" +
                $"Total={fastDo.ForwardDoCompletionP95Ms:F3} " +
                $"Start={fastDo.ForwardDoStartDelayP95Ms:F3} " +
                $"Write={fastDo.ForwardDoWriteP95Ms:F3} " +
                $"Clear={fastDo.ForwardCurrentClearP95Ms:F3}");
            Assert(fastDo.ValidDoTimingSampleCount == 5 &&
                   fastDo.ValidCurrentClearSampleCount == 5,
                "DO四时刻或电流清零有效样本数错误");

            fastDo.TryAddCutoffObservation(
                targetA,
                cutoffA,
                slope,
                cutoffA + slope * (physicalTailMs + 80.0),
                NewDoTiming(baseUtc.AddSeconds(6), 70.0, 80.0, 580.0),
                out _);
            Assert(fastDo.ForwardDoCompletionHistoryMs.Last() <= 7.001,
                "单圈DO调度尖峰未被鲁棒步长限幅");
            Assert(fastDo.ForwardDoStartDelayHistoryMs.Last() <= 6.001 &&
                   fastDo.ForwardDoWriteHistoryMs.Last() <= 6.001 &&
                   fastDo.ForwardCurrentClearHistoryMs.Last() <= 9.001 &&
                   fastDo.ForwardDoCompletionP95Ms <= 7.001,
                "DO分量或P95被单圈尖峰无界污染");

            var validTimingCount = fastDo.ValidDoTimingSampleCount;
            fastDo.TryAddCutoffObservation(
                targetA,
                cutoffA,
                slope,
                cutoffA + slope * physicalTailMs,
                new EpbDoTimingObservation
                {
                    DecisionUtc = baseUtc.AddSeconds(7),
                    DoWriteStartedUtc = baseUtc.AddSeconds(7).AddMilliseconds(-1),
                    DoWriteCompletedUtc = baseUtc.AddSeconds(7).AddMilliseconds(2)
                },
                out _);
            Assert(fastDo.ValidDoTimingSampleCount == validTimingCount,
                "时序倒置的四时刻证据错误进入DO延迟模型");
            Assert(fastDo.ModelVersion == 6, "DO延迟模型未升级到版本6");
        }

        private static EpbDoTimingObservation NewDoTiming(
            DateTime decisionUtc,
            double writeStartedMs,
            double writeCompletedMs,
            double currentClearedMs)
        {
            return new EpbDoTimingObservation
            {
                DecisionUtc = decisionUtc,
                DoWriteStartedUtc = decisionUtc.AddMilliseconds(writeStartedMs),
                DoWriteCompletedUtc = decisionUtc.AddMilliseconds(writeCompletedMs),
                CurrentClearedUtc = decisionUtc.AddMilliseconds(currentClearedMs)
            };
        }

        private static void DoTimingProfileCloneIsDeep()
        {
            var profile = StableProfile();
            var decisionUtc = new DateTime(2026, 8, 10, 2, 30, 0, DateTimeKind.Utc);
            profile.TryAddCutoffObservation(
                15.0,
                14.0,
                0.05,
                14.5,
                NewDoTiming(decisionUtc, 1, 3, 9),
                out _);
            var clone = profile.Clone();

            profile.ForwardDoCompletionHistoryMs[0] = 99;
            profile.ForwardDoStartDelayHistoryMs[0] = 98;
            profile.ForwardDoWriteHistoryMs[0] = 97;
            profile.ForwardCurrentClearHistoryMs[0] = 96;
            profile.LastForwardDoTiming.CurrentClearedUtc = decisionUtc.AddSeconds(10);

            Assert(Math.Abs(clone.ForwardDoCompletionHistoryMs[0] - 3) < 0.01 &&
                   Math.Abs(clone.ForwardDoStartDelayHistoryMs[0] - 1) < 0.01 &&
                   Math.Abs(clone.ForwardDoWriteHistoryMs[0] - 2) < 0.01 &&
                   Math.Abs(clone.ForwardCurrentClearHistoryMs[0] - 6) < 0.01,
                "DO分量历史在Clone后仍共享可变列表");
            Assert(clone.LastForwardDoTiming.CurrentClearedUtc == decisionUtc.AddMilliseconds(9),
                "最后DO四时刻证据在Clone后仍共享可变对象");
        }

        private static void TaskSupervisorTracksFaultsAndDrains()
        {
            var logger = new CollectingLogger();
            var supervisor = new TaskSupervisor(logger);
            var runId = Guid.NewGuid();
            supervisor.Observe(
                Task.Run((Action)(() => throw new InvalidOperationException("supervised-boom"))),
                "FaultingEvidenceExport",
                runId,
                5);
            Assert(SpinWait.SpinUntil(() => supervisor.ActiveCount == 0, 2000),
                "故障后台任务未从活动清单收口");
            Assert(SpinWait.SpinUntil(() => logger.Errors.Count > 0, 2000),
                "故障后台任务未记录异常");
            Assert(logger.Errors.Any(message =>
                    message.Contains("FaultingEvidenceExport") &&
                    message.Contains(runId.ToString("N")) &&
                    message.Contains("EPB=5") &&
                    message.Contains("supervised-boom")),
                "后台任务异常缺少任务名、RunId、通道或根异常");

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.Observe(completion.Task, "DrainProbe", runId, 4);
            Assert(supervisor.Snapshot().Any(item =>
                    item.Operation == "DrainProbe" && item.RunId == runId && item.Channel == 4),
                "活动任务快照缺少身份元数据");
            supervisor.Observe(
                Task.Run(async () =>
                {
                    await Task.Delay(30).ConfigureAwait(false);
                    completion.TrySetResult(true);
                }),
                "DrainCompleter",
                runId,
                4);
            Assert(supervisor.DrainAsync(1000).GetAwaiter().GetResult(),
                "后台任务未在限时内排空");
            Assert(supervisor.ActiveCount == 0, "排空后仍残留后台任务");

            for (var i = 0; i < 1000; i++)
                supervisor.Observe(Task.CompletedTask, "AlreadyCompleted", runId, 4);
            Assert(supervisor.DrainAsync(1000).GetAwaiter().GetResult() &&
                   supervisor.ActiveCount == 0,
                "已完成任务的观察延续未在排空门禁内严格收口");
        }

        private static void RestartClearsOnlyTransientStreaks()
        {
            var profile = StableProfile();
            profile.ConsecutiveDeviationCount = 3;
            profile.ConsecutiveForwardOvershootCount = 5;
            profile.ConsecutivePeakEvidenceMismatchCount = 3;
            profile.ConsecutiveForwardStallCount = 5;
            var samples = profile.ValidSampleCount;
            var forwardMedian = profile.ForwardClampMedianMs;
            var history = profile.ForwardClampHistoryMs.ToArray();

            Assert(profile.ResetTransientFaultStreaks(), "存在旧连续计数时未报告清理动作");
            Assert(profile.ConsecutiveDeviationCount == 0 &&
                   profile.ConsecutiveForwardOvershootCount == 0 &&
                   profile.ConsecutivePeakEvidenceMismatchCount == 0 &&
                   profile.ConsecutiveForwardStallCount == 0,
                "重新开始后仍继承上一运行的瞬态连续计数");
            Assert(profile.ValidSampleCount == samples &&
                   Math.Abs(profile.ForwardClampMedianMs - forwardMedian) < 0.001 &&
                   profile.ForwardClampHistoryMs.SequenceEqual(history),
                "清理瞬态计数时破坏了已学习的稳定模型");
            Assert(!profile.ResetTransientFaultStreaks(), "空计数重复清理不应制造模型变更");
        }

        private static void SoftwareSelfHealingRetriesUntilSuccess()
        {
            var attempts = 0;
            var retryCallbacks = 0;
            var result = SoftwareSelfHealingLoop.RunAsync(
                    (attempt, token) =>
                    {
                        attempts++;
                        if (attempt < 4)
                            throw new SoftwareSelfHealingRetryException("transient-" + attempt);
                        return Task.CompletedTask;
                    },
                    (attempt, ex, token) =>
                    {
                        retryCallbacks++;
                        Assert(ex.Message == "transient-" + attempt, "自愈回调未收到对应尝试证据");
                        return Task.CompletedTask;
                    },
                    _ => 0,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert(result == 4 && attempts == 4 && retryCallbacks == 3,
                "软件自愈没有保持持续重试或成功后仍继续尝试");
        }

        private static void SoftwareSelfHealingPersistentFailureIsCancelable()
        {
            using var cts = new CancellationTokenSource();
            var attempts = 0;
            var canceled = false;
            try
            {
                SoftwareSelfHealingLoop.RunAsync(
                        (attempt, token) =>
                        {
                            attempts++;
                            throw new SoftwareSelfHealingRetryException("persistent");
                        },
                        (attempt, ex, token) =>
                        {
                            if (attempt == 3) cts.Cancel();
                            return Task.CompletedTask;
                        },
                        _ => 0,
                        cts.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert(canceled && attempts == 3,
                "持续软件故障未能由停止令牌及时终止，或取消后仍继续尝试");
        }

        private static void FaultIsolatedPhaseKeepsHealthySiblingRunning()
        {
            var isolated = 0;
            var channelCanceled = 0;
            var healthyCompleted = 0;
            using var memberStop = new CancellationTokenSource();
            memberStop.Cancel();

            Task.WhenAll(
                    FaultIsolatedPhaseWork.RunAsync(
                        () => Task.FromException(new InvalidOperationException("hydraulic-group-fault")),
                        CancellationToken.None,
                        (ex, canceled) =>
                        {
                            Assert(!canceled && ex.Message == "hydraulic-group-fault",
                                "资源组异常的隔离分类错误");
                            Interlocked.Increment(ref isolated);
                        }),
                    FaultIsolatedPhaseWork.RunAsync(
                        () => Task.FromCanceled(memberStop.Token),
                        CancellationToken.None,
                        (ex, canceled) =>
                        {
                            Assert(canceled, "通道停止令牌被错误当成整批取消");
                            Interlocked.Increment(ref channelCanceled);
                        }),
                    FaultIsolatedPhaseWork.RunAsync(
                        async () =>
                        {
                            await Task.Delay(10).ConfigureAwait(false);
                            Interlocked.Increment(ref healthyCompleted);
                        },
                        CancellationToken.None,
                        (ex, canceled) => throw new InvalidOperationException(
                            "健康通道不应进入隔离回调", ex)))
                .GetAwaiter()
                .GetResult();

            Assert(isolated == 1 && channelCanceled == 1 && healthyCompleted == 1,
                "局部故障穿透Task.WhenAll或阻止了健康学习/资格任务完成");
        }

        private static void PhaseOwnerCancellationStillEscapesIsolation()
        {
            using var phase = new CancellationTokenSource();
            phase.Cancel();
            var isolated = 0;
            var canceled = false;
            try
            {
                FaultIsolatedPhaseWork.RunAsync(
                        () => Task.FromCanceled(phase.Token),
                        phase.Token,
                        (ex, memberCanceled) => Interlocked.Increment(ref isolated))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert(canceled && isolated == 0,
                "人工停止/重新开始令牌被局部故障隔离吞掉，整批无法及时退出");
        }

        private static void DiscardedLearningAttemptRestoresAdaptiveProfile()
        {
            var baseline = StableProfile();
            baseline.ConsecutiveDeviationCount = 2;
            baseline.ConsecutiveForwardOvershootCount = 3;
            baseline.ConsecutivePeakEvidenceMismatchCount = 1;
            baseline.ConsecutiveForwardStallCount = 4;
            baseline.TryAddCutoffObservation(15, 13.5, 0.05, 15.5, out _);
            var working = baseline.Clone();

            working.AddSuccessfulCycle(2.4, 2.3, 3100, 2800);
            working.UpdateForwardOvershootStreak(2.0, 0.5);
            working.UpdatePeakEvidenceMismatchStreak(true);
            working.UpdateForwardStallStreak(true);
            working.TryAddCutoffObservation(15, 12.5, 0.04, 16.0, out _);
            Assert(working.ValidSampleCount != baseline.ValidSampleCount,
                "测试前置条件无效：模拟学习尝试没有改变模型");

            working.RestoreFrom(baseline);
            Assert(working.ValidSampleCount == baseline.ValidSampleCount &&
                   working.ValidCutoffSampleCount == baseline.ValidCutoffSampleCount &&
                   working.ConsecutiveDeviationCount == baseline.ConsecutiveDeviationCount &&
                   working.ConsecutiveForwardOvershootCount == baseline.ConsecutiveForwardOvershootCount &&
                   working.ConsecutivePeakEvidenceMismatchCount == baseline.ConsecutivePeakEvidenceMismatchCount &&
                   working.ConsecutiveForwardStallCount == baseline.ConsecutiveForwardStallCount &&
                   working.ForwardEmptyHistoryA.SequenceEqual(baseline.ForwardEmptyHistoryA) &&
                   working.ReverseEmptyHistoryA.SequenceEqual(baseline.ReverseEmptyHistoryA) &&
                   working.ForwardClampHistoryMs.SequenceEqual(baseline.ForwardClampHistoryMs) &&
                   working.ReverseReleaseHistoryMs.SequenceEqual(baseline.ReverseReleaseHistoryMs) &&
                   working.ForwardCutoffLeadHistoryMs.SequenceEqual(baseline.ForwardCutoffLeadHistoryMs) &&
                   working.ForwardPeakErrorHistoryA.SequenceEqual(baseline.ForwardPeakErrorHistoryA),
                "作废学习尝试后模型、历史或瞬态连续计数未完整回滚");

            baseline.ForwardEmptyHistoryA.Add(99);
            Assert(!working.ForwardEmptyHistoryA.Contains(99), "模型恢复后仍与快照共享可变历史集合");
        }

        private static void SaveWithReceiptFailureRestoresRunnerTransaction()
        {
            var dir = CreateTempDir();
            try
            {
                var store = new EpbAdaptiveProfileStore(dir);
                var baseline = StableProfile();
                baseline.Channel = 1;
                store.Save(baseline);

                var runner = new TransactionalRunner(baseline);
                var modelBeforeLogicalCycle = runner.CaptureAdaptiveProfile();
                var changed = runner.CaptureAdaptiveProfile();
                changed.AddSuccessfulCycle(2.4, 2.3, 3100, 2800);
                runner.ReplaceModel(changed);

                var injected = false;
                EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = stage =>
                {
                    if (stage == "AfterReplaceBeforeReadback" && !injected)
                    {
                        injected = true;
                        throw new IOException("injected model readback failure");
                    }
                };
                try
                {
                    var threw = false;
                    try { store.SaveWithReceipt(changed); }
                    catch (IOException) { threw = true; }
                    Assert(threw, "SaveWithReceipt故障未向事务层抛出");
                    EpbManager.RestoreRunnerAdaptiveProfile(runner, modelBeforeLogicalCycle);
                }
                finally { EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = null; }

                var restored = runner.CaptureAdaptiveProfile();
                Assert(restored.ValidSampleCount == modelBeforeLogicalCycle.ValidSampleCount &&
                       restored.ValidCutoffSampleCount == modelBeforeLogicalCycle.ValidCutoffSampleCount &&
                       restored.ForwardEmptyHistoryA.SequenceEqual(modelBeforeLogicalCycle.ForwardEmptyHistoryA),
                    "SaveWithReceipt失败后runner仍保留未提交学习模型");
                Assert(File.Exists(store.FilePath), "SaveWithReceipt失败后模型文件缺失");
                var reloaded = new EpbAdaptiveProfileStore(dir).GetOrCreate(1);
                Assert(reloaded.ValidSampleCount == baseline.ValidSampleCount,
                    "SaveWithReceipt失败后磁盘模型未回滚");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void FatalPersistenceReceiptFailurePreservesOriginal()
        {
            var dir = CreateTempDir();
            try
            {
                var store = new EpbAdaptiveProfileStore(dir);
                var baseline = StableProfile();
                baseline.Channel = 1;
                store.Save(baseline);
                var beforeBytes = File.ReadAllBytes(store.FilePath);

                var changed = baseline.Clone();
                changed.AddSuccessfulCycle(2.4, 2.3, 3100, 2800);
                EpbAdaptiveProfilePersistenceFatalException fatal = null;
                EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = stage =>
                {
                    if (stage == "AfterReplaceBeforeReadback")
                        throw new IOException("injected model readback failure");
                    if (stage == "BeforeRollback")
                        throw new IOException("injected model rollback failure");
                };
                try
                {
                    store.SaveWithReceipt(changed);
                }
                catch (EpbAdaptiveProfilePersistenceFatalException ex)
                {
                    fatal = ex;
                }
                finally
                {
                    EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = null;
                }

                Assert(fatal != null, "模型回滚失败未抛出致命持久化异常");
                Assert(fatal.IsFatal && fatal.WriteFailure is IOException &&
                       fatal.RollbackFailure is IOException,
                    "致命持久化异常未保留写入/回滚故障证据");
                Assert(fatal is OperationCanceledException &&
                       !EpbCycleRunner.IsSoftwareRecoveryException(fatal),
                    "致命持久化异常被错误分类为可自愈重试");
                Assert(File.Exists(fatal.BackupPath),
                    "回滚失败后未保留可供恢复的模型备份");
                Assert(File.ReadAllBytes(fatal.BackupPath).SequenceEqual(beforeBytes),
                    "回滚失败后保留的备份不是提交前模型字节");

                Exception receiptFailure = null;
                var returned = EpbManager.PreserveLearningPersistenceFailure(
                    fatal,
                    () => throw new IOException("injected failed-receipt write"),
                    ex => receiptFailure = ex);
                Assert(object.ReferenceEquals(returned, fatal),
                    "失败receipt异常覆盖了原始模型持久化致命异常");
                Assert(receiptFailure is IOException,
                    "失败receipt写入异常未被独立记录");
                Assert(!EpbManager.IsFormalCycleCountable(false, true),
                    "模型持久化致命后仍被视为Successful正式圈");

            }
            finally
            {
                EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void FormalCycleRequiresPersistenceCommitToCount()
        {
            Assert(EpbManager.IsFormalCycleCountable(true, true),
                "控制和落盘均成功的正式圈未允许计数");
            Assert(!EpbManager.IsFormalCycleCountable(true, false),
                "落盘未确认的正式圈被错误计为成功");
            Assert(!EpbManager.IsFormalCycleCountable(false, true),
                "控制失败但落盘成功的正式圈被错误计数");
            Assert(!EpbManager.IsFormalCycleCountable(false, false),
                "控制和落盘均失败的正式圈被错误计数");
        }

        private static void SoftwareRecoveryOutcomeIsNotSuccess()
        {
            var outcome = EpbCycleOutcome.SoftwareRecovery(
                EpbCurrentStage.Faulted,
                "transient");
            Assert(outcome.Kind == EpbCycleOutcomeKind.SoftwareRecovery,
                "软件恢复圈未使用独立终态");
            Assert(!outcome.IsSuccess,
                "软件恢复圈被错误视为成功圈");
            Assert(!EpbManager.IsFormalCycleCountable(outcome.IsSuccess, true),
                "软件恢复圈在落盘成功时仍被错误计数");
        }

        private static void CycleExceptionRecoveryClassification()
        {
            var key = new HydraulicGenerationKey(
                Guid.NewGuid(),
                1,
                HydraulicPhaseKind.Formal,
                1);
            Assert(EpbCycleRunner.IsSoftwareRecoveryException(new NullReferenceException("state")),
                "普通软件状态异常未进入自愈");
            Assert(EpbCycleRunner.IsSoftwareRecoveryException(
                    new ObjectDisposedException("old run")),
                "旧运行对象残留异常未进入自愈");
            Assert(EpbCycleRunner.CanDiscardForSoftwareRecovery(
                    new ObjectDisposedException("old run"),
                    true),
                "软件异常在确认断电后未允许丢圈自愈");
            Assert(!EpbCycleRunner.CanDiscardForSoftwareRecovery(
                    new ObjectDisposedException("old run"),
                    false),
                "未确认断电时仍允许丢圈继续");
            Assert(EpbCycleRunner.IsSoftwareRecoveryException(
                    new HydraulicBarrierTimeoutException(key, 1000, new[] { 4 })),
                "液压同步屏障异常未进入自愈");

            Assert(!EpbCycleRunner.IsSoftwareRecoveryException(
                    new EpbOutputCommandException(4, "Forward")),
                "真实电机输出命令失败被软件自愈吞掉");
            Assert(EpbCycleRunner.IsSoftwareRecoveryException(
                    new PowerSupplyEnergizationPermitException(
                        4,
                        2,
                        "TelemetryOutputDisabled")),
                "电源许可缺失仍被当作卡钳硬件故障累计");
            Assert(EpbCycleRunner.ShouldReclassifyOpenCircuit(
                    "OpenCircuitOrOutputFault I=0.016A",
                    infrastructureTransition: true),
                "设备恢复窗口内近零电流未改判为软件恢复圈");
            Assert(!EpbCycleRunner.ShouldReclassifyOpenCircuit(
                    "OpenCircuitOrOutputFault I=0.016A",
                    infrastructureTransition: false),
                "正常供电下真实开路被软件恢复吞掉");
            Assert(EpbCycleRunner.IsSoftwareRecoveryException(
                    new HydraulicBuildTimeoutException(
                        1, 70, 10, 55, 5000, "BelowToleranceWindow", 2, 3)),
                "未满3代次的建压不足未进入软件自愈");
            Assert(EpbCycleRunner.IsSoftwareRecoveryException(
                    new HydraulicBuildTimeoutException(
                        1, 70, 10, 70, 5000, "StabilizingWithinTolerance")),
                "已在建压窗口但稳定计时未完成被错误确认成硬件故障");
            Assert(!EpbCycleRunner.IsSoftwareRecoveryException(
                    new HydraulicBuildTimeoutException(
                        1, 70, 10, 55, 5000, "BelowToleranceWindow", 3, 3)),
                "连续3代次建压不足仍被软件自愈吞掉");
            Assert(EpbCycleRunner.IsSoftwareRecoveryException(
                    new HydraulicPressureLostException(
                        1,
                        1,
                        50,
                        60,
                        HydraulicPressureFailureReason.StaleSample,
                        20)),
                "陈旧压力样本未进入软件自愈");
            Assert(!EpbCycleRunner.IsSoftwareRecoveryException(
                    new HydraulicPressureLostException(
                        1,
                        3,
                        50,
                        60,
                        HydraulicPressureFailureReason.BelowMinimum,
                        20,
                        3,
                        3)),
                "连续3代次新鲜低压仍被软件自愈吞掉");
            Assert(!EpbCycleRunner.IsSoftwareRecoveryException(
                    new HydraulicReleaseTimeoutException(1, 20, 5, 5000)),
                "真实释压失败被软件自愈吞掉");
        }

        private static void NonCriticalObserverFailureDoesNotPropagate()
        {
            var delivered = 0;
            var failures = 0;
            Action<int> observers = _ => throw new InvalidOperationException("UI failed");
            observers += value => delivered += value;

            var succeeded = NonCriticalObserver.Invoke(
                observers,
                1,
                _ => failures++);

            Assert(succeeded == 1, "观察者成功计数错误");
            Assert(failures == 1, "失败观察者未被独立报告");
            Assert(delivered == 1, "前一个观察者异常阻止了后续观察者");
        }

        private static void AlarmIndicatorRecoveryIsRunBounded()
        {
            var runId = Guid.NewGuid();
            Assert(EpbManager.CanRetryAlarmIndicatorClear(
                    runId,
                    runId,
                    ChannelRuntimeState.Running),
                "当前运行中的报警显示自愈被错误禁止");
            Assert(EpbManager.CanRetryAlarmIndicatorClear(
                    runId,
                    runId,
                    ChannelRuntimeState.WarningRunning),
                "当前预警运行中的报警显示自愈被错误禁止");
            Assert(!EpbManager.CanRetryAlarmIndicatorClear(
                    runId,
                    runId,
                    ChannelRuntimeState.AlarmStopped),
                "新报警后旧清除任务仍可继续清灯");
            Assert(!EpbManager.CanRetryAlarmIndicatorClear(
                    runId,
                    Guid.NewGuid(),
                    ChannelRuntimeState.Running),
                "旧运行的报警清除任务越过了新运行代次");
        }

        private static void FormalCycleRejectsStaleSuccessOutcome()
        {
            Assert(EpbManager.IsFormalControlSucceeded(true, true),
                "本次正常返回且结果成功的正式圈未被接受");
            Assert(!EpbManager.IsFormalControlSucceeded(false, true),
                "本次抛异常后错误复用了上一圈的成功结果");
            Assert(!EpbManager.IsFormalControlSucceeded(true, false),
                "本次结果失败但工作返回值为真时被错误接受");
        }

        private static void HydraulicStartupRecoveryClassification()
        {
            var key = new HydraulicGenerationKey(
                Guid.NewGuid(),
                1,
                HydraulicPhaseKind.PreRelease,
                1);
            Assert(EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new HydraulicBarrierTimeoutException(key, 1000, new[] { 4 })),
                "液压屏障缺员未进入软件代次自愈");
            Assert(EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new InvalidOperationException("Hydraulic generation members are immutable")),
                "旧液压成员不可变异常未进入软件代次自愈");
            Assert(EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new HydraulicBuildTimeoutException(
                        1, 70, 10, 55, 5000, "BelowToleranceWindow", 2, 3)),
                "未满3代次的建压不足未进入软件代次自愈");
            Assert(EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new HydraulicBuildTimeoutException(
                        1, 70, 10, 70, 5000, "StabilizingWithinTolerance")),
                "已在建压窗口但稳定计时未完成未进入软件代次自愈");
            Assert(!EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new HydraulicBuildTimeoutException(
                        1, 70, 10, 55, 5000, "BelowToleranceWindow", 3, 3)),
                "连续3代次建压不足被错误吞入软件自愈");
            Assert(EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new HydraulicPressureLostException(
                        1,
                        1,
                        70,
                        60,
                        HydraulicPressureFailureReason.StaleSample,
                        500)),
                "陈旧压力样本未进入软件代次自愈");
            Assert(!EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new HydraulicPressureLostException(
                        1,
                        3,
                        50,
                        60,
                        HydraulicPressureFailureReason.BelowMinimum,
                        20,
                        3,
                        3)),
                "连续3代次新鲜低压被错误吞入软件自愈");
            Assert(!EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new HydraulicReleaseTimeoutException(1, 20, 5, 5000)),
                "真实释压超时被错误吞入软件自愈");
            Assert(!EpbManager.IsHydraulicSoftwareRecoveryCandidate(
                    new InvalidOperationException("generic startup failure")),
                "普通异常被错误识别为液压代次异常");
        }

        private static void PeakEvidenceMismatchRequiresThreeCycles()
        {
            var profile = StableProfile();
            Assert(profile.UpdatePeakEvidenceMismatchStreak(true) == 1,
                "峰值证据首次偏差计数错误");
            Assert(profile.UpdatePeakEvidenceMismatchStreak(true) == 2,
                "峰值证据第2圈偏差计数错误");
            Assert(!EpbCycleRunner.IsPeakEvidenceMismatchConfirmed(2, 3),
                "峰值证据第2圈被过早升级为硬故障");
            Assert(profile.UpdatePeakEvidenceMismatchStreak(true) == 3,
                "峰值证据第3圈未达到确认值");
            Assert(EpbCycleRunner.IsPeakEvidenceMismatchConfirmed(3, 3),
                "峰值证据第3圈未升级为硬故障");
            Assert(profile.UpdatePeakEvidenceMismatchStreak(false) == 0,
                "有效匹配圈未清零峰值证据偏差计数");
        }

        private static void PeakEvidenceMismatchRequiresComparableWindow()
        {
            var evidenceThrough = new DateTime(
                2026, 8, 8, 0, 12, 49, 100, DateTimeKind.Utc);
            Assert(
                EpbCycleRunner.IsPeakEvidenceWindowComparable(
                    evidenceThrough,
                    evidenceThrough.AddMilliseconds(-0.5)),
                "已包含在快速快照中的完整峰值被错误判为不可比较");
            Assert(
                !EpbCycleRunner.IsPeakEvidenceWindowComparable(
                    evidenceThrough,
                    evidenceThrough.AddMilliseconds(15)),
                "快速判定后才入账的完整峰值仍参与偏差硬故障计数");
            Assert(
                !EpbCycleRunner.IsPeakEvidenceWindowComparable(
                    DateTime.MinValue,
                    evidenceThrough),
                "缺少快速证据截止时间时仍参与偏差硬故障计数");
        }

        private static void NonComparablePeakWindowIsNotProcessingLag()
        {
            var evidenceThrough = new DateTime(
                2026, 8, 8, 0, 12, 49, 100, DateTimeKind.Utc);
            Assert(
                !EpbCycleRunner.IsPeakEvidenceLagExceeded(double.NaN, 100),
                "缺少可量化尾差时被错误判为处理滞后");
            Assert(
                !EpbCycleRunner.IsPeakEvidenceLagExceeded(97.5, 100),
                "阈值内尾差被错误判为处理滞后");
            Assert(
                EpbCycleRunner.IsPeakEvidenceLagExceeded(100.1, 100),
                "真实超过阈值的尾差未触发处理滞后");
            var diagnostics = new List<AdaptiveWarningCode>();
            var diagnostic = EpbCycleRunner.EvaluateCompletedPeakEvidence(
                evidenceThrough,
                evidenceThrough.AddMilliseconds(150),
                25,
                100,
                out var comparableWindow);
            Assert(!comparableWindow,
                "完整峰值晚150ms时错误参与快速/完整峰值偏差比较");
            if (diagnostic.HasValue) diagnostics.Add(diagnostic.Value);
            Assert(!diagnostic.HasValue,
                "真实处理滞后仅25ms时被150ms物理峰值时差误报为后台卡顿");

            diagnostic = EpbCycleRunner.EvaluateCompletedPeakEvidence(
                evidenceThrough,
                evidenceThrough.AddMilliseconds(150),
                101,
                100,
                out comparableWindow);
            if (diagnostic.HasValue) diagnostics.Add(diagnostic.Value);
            Assert(
                diagnostics.Count == 1 &&
                diagnostics[0] == AdaptiveWarningCode.PeakEvidenceLagWarning,
                "101ms真实处理滞后未产生唯一且独立的峰值滞后诊断");
            Assert(
                EpbCycleRunner.ClassifyPeakEvidenceDiagnostic(
                    DateTime.MinValue,
                    evidenceThrough,
                    double.NaN,
                    100) == AdaptiveWarningCode.PeakEvidenceTimestampMissing,
                "时间戳缺失未产生独立诊断代码");
        }

        private static void WarningSnapshotFullEvidenceIntervalIsEnforced()
        {
            var first = new DateTime(2026, 8, 8, 1, 0, 0, DateTimeKind.Utc);
            Assert(
                !EpbManager.IsWarningSnapshotIntervalElapsed(first, first.AddSeconds(599.9), 600),
                "600秒内重复软预警仍被允许导出完整证据");
            Assert(
                EpbManager.IsWarningSnapshotIntervalElapsed(first, first.AddSeconds(600), 600),
                "达到600秒后未重新允许完整证据");
            Assert(
                !EpbManager.IsWarningSnapshotIntervalElapsed(first, first.AddSeconds(-1), 600),
                "系统时钟回退时错误放开了重复完整证据");
        }

        private static void WarningScalarEvidenceIsDefaultAndAuditable()
        {
            var cfg = new WarningSnapshotConfig();
            Assert(cfg.Enabled && !cfg.FullEvidenceEnabled &&
                   cfg.ScalarEvidenceQueueCapacity == 4096,
                "软预警未默认采用轻量标量证据，可能重新复制大量整圈文件");

            var identity = RuntimeBuildIdentity.Capture();
            var json = EpbManager.BuildWarningScalarJson(
                new EpbManager.WarningScalarEvidence
                {
                    RunId = Guid.NewGuid(),
                    Channel = 8,
                    CycleNumber = 123,
                    AttemptId = 456,
                    OccurredUtc = DateTime.UtcNow,
                    Code = "PeakEvidenceLagWarning",
                    EvidenceLagMs = 101.25,
                    PersistenceQueueDepth = 7,
                    PeakCurrentA = 15.5,
                    TargetCurrentA = 15,
                    PeakErrorA = 0.5,
                    Streak = 1,
                    ConfirmThreshold = 3,
                    Reason = "test"
                },
                identity);
            Assert(json.Contains("\"lagMs\":101.25") &&
                   json.Contains("\"persistenceQueueDepth\":7") &&
                   json.Contains("\"productVersion\"") &&
                   json.Contains("\"processId\"") &&
                   json.Contains("\"executablePath\"") &&
                   json.Contains("\"executableSha256\"") &&
                   json.Contains("\"gitCommit\"") &&
                   json.Contains("\"buildUtc\"") &&
                   json.Contains("\"releaseConfigSha256\""),
                "软预警JSONL缺少lag、队列深度或可审计运行身份");
        }

        private static void WarningSnapshotFloodIsStrictlyBounded()
        {
            var gate = new WarningSnapshotWorkGate();
            var now = new DateTime(2026, 8, 8, 1, 0, 0, DateTimeKind.Utc);
            Assert(gate.TryQueue("run:4:1:PeakLag", "4:PeakLag", now, 600, 1),
                "首个完整证据任务未准入");
            Assert(gate.TryStart("run:4:1:PeakLag"), "首个完整证据任务未进入运行态");
            Assert(!gate.TryQueue("run:4:1:PeakLag", "4:PeakLag", now, 600, 1),
                "同一幂等键被重复准入");
            Assert(gate.TryQueue("run:5:1:Other", "5:Other", now, 600, 1),
                "唯一等待位未准入");

            var accepted = 0;
            Parallel.For(0, 1000, index =>
            {
                if (gate.TryQueue(
                        $"run:{index}:2:storm",
                        $"{index}:storm",
                        now,
                        600,
                        1))
                    Interlocked.Increment(ref accepted);
            });
            Assert(accepted == 0, $"预警风暴越过唯一等待位：Accepted={accepted}");
            Assert(gate.RunningCount == 1 && gate.PendingCount == 1 && gate.ActiveJobCount == 2,
                $"预警任务门控不满足1运行+1等待：Running={gate.RunningCount} " +
                $"Pending={gate.PendingCount} Active={gate.ActiveJobCount}");

            gate.Complete("run:4:1:PeakLag");
            Assert(gate.TryStart("run:5:1:Other"), "等待任务未能接替运行");
            gate.Complete("run:5:1:Other");
            Assert(!gate.TryQueue("run:4:2:PeakLag", "4:PeakLag", now.AddSeconds(599), 600, 1),
                "600秒内同类别完整证据未合并");
            Assert(gate.TryQueue("run:4:3:PeakLag", "4:PeakLag", now.AddSeconds(600), 600, 1),
                "达到600秒后完整证据仍被拒绝");
            Assert(gate.TryStart("run:4:3:PeakLag"), "限频到期任务未进入运行态");
            gate.Complete("run:4:3:PeakLag");
            Assert(gate.ActiveJobCount == 0 && gate.PendingCount == 0 && gate.RunningCount == 0,
                "预警任务全部结束后仍有活动/等待残留");
        }

        private static void PeakDrainDelayDoesNotInvalidateEvidence()
        {
            var cutoffUtc = DateTime.UtcNow;
            var capture = new IO.NI.PeakCaptureResult
            {
                IsMatched = true,
                IsCutoffCovered = true,
                LogicalCutoffUtc = cutoffUtc,
                ProcessedThroughUtc = cutoffUtc.AddMilliseconds(1),
                DrainCompletedUtc = cutoffUtc.AddMilliseconds(110),
                DrainElapsedMs = 110,
                QualityReason = "Qualified",
                Peak = new IO.NI.TwoDeviceAiAcquirer.EpbCurrentPeak
                {
                    Channel = 4,
                    MaxAmp = 22.5,
                    SampleCount = 1000,
                    LastSampleAt = cutoffUtc.AddMilliseconds(-0.5).ToLocalTime()
                }
            };

            Thread.Sleep(110);
            Assert(
                EpbCycleRunner.IsFullRatePeakCaptureValid(capture, 100, out var tailLagMs),
                "合法峰值因封口后的真实等待被误判为陈旧。 ");
            Assert(tailLagMs >= 0 && tailLagMs < 2,
                $"峰值尾差未使用逻辑截止口径：{tailLagMs:F3}ms");

            capture.IsCutoffCovered = false;
            Assert(
                !EpbCycleRunner.IsFullRatePeakCaptureValid(capture, 100, out _),
                "全速率水印未覆盖截止点时仍被判为有效证据。 ");
        }

        private static void SixChannelPeakDrainIsConsistent()
        {
            var checks = Enumerable.Range(1, 6).Select(async channel =>
            {
                var cutoffUtc = DateTime.UtcNow;
                var capture = new IO.NI.PeakCaptureResult
                {
                    IsMatched = true,
                    IsCutoffCovered = true,
                    LogicalCutoffUtc = cutoffUtc,
                    ProcessedThroughUtc = cutoffUtc.AddMilliseconds(0.5),
                    DrainCompletedUtc = cutoffUtc.AddMilliseconds(102 + channel),
                    DrainElapsedMs = 102 + channel,
                    QualityReason = "Qualified",
                    Peak = new IO.NI.TwoDeviceAiAcquirer.EpbCurrentPeak
                    {
                        Channel = channel,
                        MaxAmp = 20 + channel,
                        SampleCount = 400,
                        LastSampleAt = cutoffUtc.AddMilliseconds(-0.5).ToLocalTime()
                    }
                };
                await Task.Delay(102 + channel).ConfigureAwait(false);
                return EpbCycleRunner.IsFullRatePeakCaptureValid(capture, 100, out _);
            }).ToArray();

            var valid = Task.WhenAll(checks).GetAwaiter().GetResult();
            Assert(valid.All(value => value),
                "六通道合法截止窗在并发排空后出现 FullRatePeakInvalid。 ");
        }

        private static void SoftwareSelfHealingStopsAfterThreeAttempts()
        {
            var attempts = 0;
            var cleanups = 0;
            try
            {
                SoftwareSelfHealingLoop.RunAsync(
                        (attempt, token) =>
                        {
                            Interlocked.Increment(ref attempts);
                            throw new SoftwareSelfHealingRetryException("same-state");
                        },
                        (attempt, ex, token) =>
                        {
                            Interlocked.Increment(ref cleanups);
                            return Task.CompletedTask;
                        },
                        _ => 0,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException("自愈循环未在第三次相同失败后熔断。 ");
            }
            catch (SoftwareSelfHealingExhaustedException ex)
            {
                Assert(ex.Attempts == 3, "熔断异常未记录三次尝试。 ");
                Assert(attempts == 3 && cleanups == 3,
                    $"熔断次数不正确 Attempts={attempts} Cleanups={cleanups}");
            }
        }

        private static void AlarmMessagesAreLocalized()
        {
            var message = AlarmMessageLocalizer.ToUserMessage(
                "AdaptiveHardFault PeakEvidenceMismatch QuickPeak=15.0A " +
                "FullRatePeak=17.0A Streak=3/3");
            Assert(message.Contains("峰值证据连续偏差") &&
                   message.Contains("快速峰值=15.0A") &&
                   message.Contains("完整数据峰值=17.0A") &&
                   !message.Contains("PeakEvidenceMismatch") &&
                   !message.Contains("AdaptiveHardFault"),
                "峰值证据报警未转换为完整中文提示");

            var staleRelease = AlarmMessageLocalizer.ToUserMessage(
                "HydraulicReleaseTimeout Hydraulic=1 LastPressure=69.390bar " +
                "Detail=PressureSampleStaleSample AgeMs=147250.2");
            Assert(staleRelease.Contains("压力采样数据过期") &&
                   staleRelease.Contains("无法确认释压状态") &&
                   !staleRelease.Contains("泄漏"),
                "陈旧压力释压故障仍给出误导性液压提示");

            var nearTargetPlateau = AlarmMessageLocalizer.ToUserMessage(
                "ClampReachedNearTargetPlateau Peak=14.617A I=13.927A " +
                "Floor=14.200A Target=15.000A Slope=0.000857A/ms ConfirmMs=220ms");
            Assert(nearTargetPlateau.Contains("接近目标的高负载平台") &&
                   nearTargetPlateau.Contains("峰值=14.617A") &&
                   nearTargetPlateau.Contains("当前电流=13.927A") &&
                   nearTargetPlateau.Contains("合格下限=14.200A") &&
                   nearTargetPlateau.Contains("平台斜率=0.000857A/ms") &&
                   nearTargetPlateau.Contains("确认时长=220ms") &&
                   !nearTargetPlateau.Contains("系统检测到异常"),
                "近目标平台预警未转换为可操作的中文提示");

            var fastRise = AlarmMessageLocalizer.ToUserMessage(
                "FastRiseCandidate Peak=15.100A AwaitingFullRateEvidence");
            Assert(fastRise.Contains("快速夹紧候选已断开正向供电") &&
                   !fastRise.Contains("FastRiseCandidate"),
                "快速夹紧候选仍暴露英文码，或未明确已经执行安全断电");
        }

        private static void ConcurrentUiConfigSaveIsAtomic()
        {
            var directory = CreateTempDir();
            var path = Path.Combine(directory, "UIConfig.xml");
            try
            {
                var tasks = Enumerable.Range(0, 20).Select(index => Task.Run(() =>
                {
                    var cfg = new UiConfig();
                    var control = cfg.GetOrAddForm("Main").GetOrAdd("CheckEpbA1");
                    control.Checked = (index & 1) == 0;
                    ConfigLoader.SaveUI(path, cfg);
                })).ToArray();
                Task.WaitAll(tasks);

                var document = new System.Xml.XmlDocument();
                document.Load(path);
                Assert(document.DocumentElement?.Name == "UiConfig",
                    "并发保存后UI配置不是有效XML");
                Assert(Directory.GetFiles(directory, "*.tmp").Length == 0,
                    "并发保存后遗留临时文件");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void UiConfigUpdateIsDebounced()
        {
            var directory = CreateTempDir();
            var path = Path.Combine(directory, "UIConfig.xml");
            try
            {
                var cfg = new UiConfig();
                for (var i = 0; i < 1000; i++)
                    ConfigLoader.UpdateUIChecked(
                        path, cfg, "Main", "CheckEpbA1", (i & 1) == 1);
                var debouncersField = typeof(ConfigLoader).GetField(
                    "UiSaveDebouncers",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic);
                var debouncers = debouncersField?.GetValue(null);
                var countProperty = debouncers?.GetType().GetProperty("Count");
                var activeDebouncers = (int)(countProperty?.GetValue(debouncers) ?? -1);
                Assert(activeDebouncers == 1,
                    $"1000次UI更新未合并为单一计时器：Active={activeDebouncers}");
                Thread.Sleep(800);
                var loaded = ConfigLoader.LoadUI(path);
                Assert(loaded.GetOrAddForm("Main").GetOrAdd("CheckEpbA1").Checked,
                    "防抖保存未保留最后一次勾选状态");
                Assert(Directory.GetFiles(directory, "*.tmp").Length == 0,
                    "防抖保存遗留临时文件");
                activeDebouncers = (int)(countProperty?.GetValue(debouncers) ?? -1);
                Assert(activeDebouncers == 0,
                    $"UI防抖保存完成后仍残留计时器：Active={activeDebouncers}");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void ForwardStallStreakRequiresFiveCycles()
        {
            var profile = StableProfile();
            for (var cycle = 1; cycle <= 7; cycle++)
            {
                Assert(
                    profile.UpdateForwardStallStreak(true) == cycle,
                    $"正向低平台第{cycle}圈连续计数错误");
                Assert(
                    profile.ConsecutiveForwardStallCount < 8,
                    $"正向低平台第{cycle}圈被过早确认为硬故障");
                Assert(
                    !EpbCycleRunner.IsForwardStallConfirmed(
                        profile.ConsecutiveForwardStallCount,
                        8),
                    $"正向低平台第{cycle}圈被升级策略过早确认");
            }

            Assert(
                profile.UpdateForwardStallStreak(true) == 8,
                "正向低平台第8圈未达到硬故障确认值");
            Assert(
                EpbCycleRunner.IsForwardStallConfirmed(
                    profile.ConsecutiveForwardStallCount,
                    8),
                "正向低平台第8圈未被升级策略确认");
            Assert(
                profile.UpdateForwardStallStreak(false) == 0,
                "正常或近目标圈未清零正向低平台连续计数");
            Assert(
                profile.UpdateForwardStallStreak(true) == 1,
                "清零后的下一次低平台未从1重新计数");
        }

        private static void VersionFiveProfileMigratesWithoutReset()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "EpbAdaptiveProfiles.xml");
                File.WriteAllText(
                    path,
                    "<EpbAdaptiveProfiles ModelVersion=\"5\">" +
                    "<Profile Channel=\"10\" ModelVersion=\"5\">" +
                    "<ForwardEmptyCurrentA>1.1</ForwardEmptyCurrentA>" +
                    "<ReverseEmptyCurrentA>0.9</ReverseEmptyCurrentA>" +
                    "<ForwardClampMedianMs>3000</ForwardClampMedianMs>" +
                    "<ReverseReleaseMedianMs>1200</ReverseReleaseMedianMs>" +
                    "<ValidSampleCount>5</ValidSampleCount>" +
                    "<ValidCutoffSampleCount>3</ValidCutoffSampleCount>" +
                    "<ValidDoCompletionSampleCount>3</ValidDoCompletionSampleCount>" +
                    "<ForwardDoCompletionHistoryMs>" +
                    "<Value>2</Value><Value>4</Value><Value>6</Value>" +
                    "</ForwardDoCompletionHistoryMs>" +
                    "</Profile></EpbAdaptiveProfiles>");

                var store = new EpbAdaptiveProfileStore(dir);
                var loaded = store.GetOrCreate(10);
                Assert(loaded.ModelVersion == 6 && loaded.IsStable &&
                       loaded.ValidSampleCount == 5 && loaded.ValidCutoffSampleCount == 3,
                    "兼容的版本5模型在升级到版本6时被错误清空");
                Assert(loaded.ValidDoCompletionSampleCount == 3 &&
                       loaded.ForwardDoCompletionHistoryMs.SequenceEqual(new[] { 2.0, 4.0, 6.0 }) &&
                       Math.Abs(loaded.ForwardDoCompletionMedianMs - 4.0) < 0.01 &&
                       Math.Abs(loaded.ForwardDoCompletionP95Ms - 6.0) < 0.01,
                    "版本5 DO历史未迁移为median/P95");
                Assert(loaded.ForwardDoStartDelayHistoryMs.Count == 0 &&
                       loaded.ForwardDoWriteHistoryMs.Count == 0 &&
                       loaded.ForwardCurrentClearHistoryMs.Count == 0,
                    "版本5缺失的分量历史被迁移逻辑伪造");
                Assert(Directory.GetFiles(dir, "*.pre-v6.*").Length == 0,
                    "兼容版本5模型被错误按不兼容策略留档失效");

                store.Save(loaded);
                var reloaded = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(reloaded.ModelVersion == 6 && reloaded.ValidSampleCount == 5 &&
                       Math.Abs(reloaded.ForwardDoCompletionP95Ms - 6.0) < 0.01,
                    "版本5兼容升级结果未能按版本6稳定重载");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void VersionOneProfileMigrates()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "EpbAdaptiveProfiles.xml");
                File.WriteAllText(
                    path,
                    "<EpbAdaptiveProfiles ModelVersion=\"1\">" +
                    "<Profile Channel=\"10\" ModelVersion=\"1\">" +
                    "<ForwardEmptyCurrentA>1.1</ForwardEmptyCurrentA>" +
                    "<ReverseEmptyCurrentA>0.9</ReverseEmptyCurrentA>" +
                    "<ForwardClampMedianMs>3000</ForwardClampMedianMs>" +
                    "<ReverseReleaseMedianMs>1200</ReverseReleaseMedianMs>" +
                    "<ValidSampleCount>5</ValidSampleCount>" +
                    "<ForwardEmptyHistoryA><Value>1.1</Value></ForwardEmptyHistoryA>" +
                    "<ReverseEmptyHistoryA><Value>0.9</Value></ReverseEmptyHistoryA>" +
                    "<ForwardClampHistoryMs><Value>3000</Value></ForwardClampHistoryMs>" +
                    "<ReverseReleaseHistoryMs><Value>1200</Value></ReverseReleaseHistoryMs>" +
                    "</Profile></EpbAdaptiveProfiles>");

                var store = new EpbAdaptiveProfileStore(dir);
                var loaded = store.GetOrCreate(10);
                Assert(!loaded.IsStable && loaded.ValidSampleCount == 0,
                    "旧控制策略模型未失效，可能继续复用污染基线");
                Assert(Math.Abs(loaded.ForwardEmptyCurrentA) < 0.001,
                    "旧控制策略空行程基线未清除");
                Assert(loaded.ValidCutoffSampleCount == 0 && !loaded.HasCutoffPrediction,
                    "版本1模型错误地产生控流学习数据");
                Assert(Directory.GetFiles(dir, "*.pre-v6.*").Length == 1,
                    "旧控制策略模型失效前未保留审计副本");

                for (var i = 0; i < 5; i++)
                    loaded.AddSuccessfulCycle(1.0, 0.8, 3000, 1200);
                loaded.TryAddCutoffObservation(15.0, 14.5, 0.05, 15.0, out _);
                store.Save(loaded);
                var migrated = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(migrated.ModelVersion == 6, "旧模型重新学习后未升级为版本6");
                Assert(migrated.ValidSampleCount == 5 && migrated.ValidCutoffSampleCount == 1,
                    "模型升级后的重新学习样本或控流样本错误");
                Assert(migrated.ConsecutiveForwardStallCount == 0,
                    "旧模型迁移时错误产生正向低平台连续计数");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void CorruptProfileFallback()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "EpbAdaptiveProfiles.xml"), "<broken>");
                var loaded = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(loaded.ValidSampleCount == 0, "损坏模型未回退为空模型");
                Assert(Directory.GetFiles(dir, "*.corrupt.*").Length == 1, "损坏模型未保留副本");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void TimerDoesNotCatchUp()
        {
            var starts = new List<long>();
            var sw = Stopwatch.StartNew();
            var timer = new HighPrecisionTimer(100, OverrunPolicy.AlignToWallClock);
            timer.StartAsync(
                    3,
                    0,
                    async (cycle, token) =>
                    {
                        starts.Add(sw.ElapsedMilliseconds);
                        if (cycle == 1) await System.Threading.Tasks.Task.Delay(250, token);
                        return true;
                    })
                .GetAwaiter()
                .GetResult();

            Assert(starts.Count == 3, "超限后丢失了应完成的圈数");
            Assert(starts[1] - starts[0] >= 280, "超限后发生追赶式连续上电");
            Assert(starts[2] > starts[1], "后续圈没有按未来边界执行");
        }

        private static void TimerGracefulPauseWaitsForCurrentCycle()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var cycles = 0;
            var timer = new HighPrecisionTimer(50, OverrunPolicy.AlignToWallClock);
            var run = timer.StartAsync(
                null,
                0,
                async (cycle, token) =>
                {
                    Interlocked.Increment(ref cycles);
                    entered.Set();
                    if (cycle == 1)
                    {
                        while (!release.IsSet)
                            await Task.Delay(5, token);
                    }
                    return true;
                });

            Assert(entered.Wait(1000), "首圈未进入");
            var pause = timer.PauseAfterCurrentCycleAsync();
            Assert(!pause.Wait(30), "当前圈未结束时暂停被提前确认");
            release.Set();
            Assert(pause.Wait(1000), "当前圈结束后未进入暂停");
            var pausedCycles = Volatile.Read(ref cycles);
            Thread.Sleep(180);
            Assert(pausedCycles == 1 && Volatile.Read(ref cycles) == 1,
                "优雅暂停后仍启动了新圈");
            Assert(timer.PauseAfterCurrentCycleAsync().Wait(100),
                "重复暂停已经暂停的定时器发生阻塞");

            timer.ResumeAtUtcBoundary(DateTime.UtcNow.AddMilliseconds(50));
            Assert(SpinWait.SpinUntil(() => Volatile.Read(ref cycles) >= 2, 1000),
                "恢复后没有从未来锚点进入下一圈");
            timer.Stop();
            Assert(run.Wait(1000), "定时器停止超时");
        }

        private static void TimerPauseDuringPlannedDelayBlocksNextCycle()
        {
            var firstCompleted = new ManualResetEventSlim(false);
            var cycles = 0;
            var timer = new HighPrecisionTimer(250, OverrunPolicy.AlignToWallClock);
            var run = timer.StartAsync(
                null,
                0,
                (cycle, token) =>
                {
                    Interlocked.Increment(ref cycles);
                    if (cycle == 1) firstCompleted.Set();
                    return Task.FromResult(true);
                });

            Assert(firstCompleted.Wait(1000), "首圈未完成");
            Thread.Sleep(20);
            var pause = timer.PauseAfterCurrentCycleAsync();
            Assert(pause.Wait(500), "等待下一计划时刻期间未立即封住下一圈");
            Thread.Sleep(320);
            Assert(Volatile.Read(ref cycles) == 1, "暂停竞态导致下一圈误启动");
            timer.Stop();
            Assert(run.Wait(1000), "定时器停止超时");
        }

        private static void TimerPauseStateCorrectsRunningStatus()
        {
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var states = new List<HighPrecisionTimerRuntimeState>();
            var timer = new HighPrecisionTimer(50, OverrunPolicy.AlignToWallClock);
            timer.StateChanged += update =>
            {
                lock (states) states.Add(update.State);
            };
            var run = timer.StartAsync(null, 0, async (_, token) =>
            {
                entered.Set();
                while (!release.IsSet) await Task.Delay(5, token);
                return true;
            });

            Assert(entered.Wait(1000), "Timer状态测试首圈未启动");
            timer.Pause("ChannelGracefulPause");
            Assert(timer.RuntimeState == HighPrecisionTimerRuntimeState.PausePending,
                "圈内Pause未公开PausePending状态");
            release.Set();
            Assert(SpinWait.SpinUntil(
                    () => timer.RuntimeState == HighPrecisionTimerRuntimeState.Paused,
                    1000),
                "当前圈结束后Timer未公开Paused状态");
            Assert(timer.IsPaused && timer.PauseReason == "ChannelGracefulPause" && timer.PauseUtc.HasValue,
                "Timer暂停健康字段不完整");
            Assert(SpinWait.SpinUntil(
                    () =>
                    {
                        lock (states)
                            return states.Contains(HighPrecisionTimerRuntimeState.PausePending) &&
                                   states.Contains(HighPrecisionTimerRuntimeState.Paused);
                    },
                    1000),
                "Timer未发布完整暂停状态事件");

            var decision = EpbManager.EvaluateTimerRuntimeHealth(
                ChannelRuntimeState.Running,
                timer,
                DateTime.UtcNow,
                30000);
            Assert(decision.Action == TimerRuntimeHealthAction.PublishPaused,
                "Timer已暂停但通道仍运行时未要求纠正UI状态");
            timer.Stop();
            Assert(run.Wait(1000), "Timer状态测试停止超时");
        }

        private static void TimerRuntimeWatchdogDetectsStoppedAndStale()
        {
            var stopped = new HighPrecisionTimer(100, OverrunPolicy.AlignToWallClock);
            var stoppedDecision = EpbManager.EvaluateTimerRuntimeHealth(
                ChannelRuntimeState.Running,
                stopped,
                DateTime.UtcNow,
                30000);
            Assert(stoppedDecision.Action == TimerRuntimeHealthAction.BeginSelfHealing &&
                   stoppedDecision.ReasonCode == "TimerNotRunning",
                "通道仍显示运行但Timer未启动时未要求自动自恢复");

            var firstCycle = new ManualResetEventSlim(false);
            var timer = new HighPrecisionTimer(1000, OverrunPolicy.AlignToWallClock);
            var run = timer.StartAsync(null, 0, (_, __) =>
            {
                firstCycle.Set();
                return Task.FromResult(true);
            });
            Assert(firstCycle.Wait(1000), "心跳测试首圈未完成");
            var staleNow = (timer.LastCycleCompletedUtc ?? DateTime.UtcNow).AddSeconds(31);
            var staleDecision = EpbManager.EvaluateTimerRuntimeHealth(
                ChannelRuntimeState.WarningRunning,
                timer,
                staleNow,
                30000);
            Assert(staleDecision.Action == TimerRuntimeHealthAction.BeginSelfHealing &&
                   staleDecision.ReasonCode == "TimerHeartbeatStale",
                "运行Timer超过阈值无新圈时未要求自动自恢复");
            var ignored = EpbManager.EvaluateTimerRuntimeHealth(
                ChannelRuntimeState.Recovering,
                timer,
                staleNow,
                30000);
            Assert(ignored.Action == TimerRuntimeHealthAction.None,
                "自恢复状态被看门狗错误覆盖");
            timer.Stop();
            Assert(run.Wait(1000), "心跳测试停止超时");
        }

        private static void TimerAnomalyRecoveryEligibility()
        {
            Assert(EpbManager.ShouldAutoRecoverTimerAnomaly(
                    ChannelRuntimeState.Running,
                    HighPrecisionTimerRuntimeState.Faulted,
                    "TimeoutException",
                    true,
                    BatchPauseState.Running,
                    false,
                    false,
                    true),
                "软件Timer故障未允许自动重建");
            Assert(EpbManager.ShouldAutoRecoverTimerAnomaly(
                    ChannelRuntimeState.WarningRunning,
                    HighPrecisionTimerRuntimeState.Paused,
                    "Unspecified",
                    true,
                    BatchPauseState.Running,
                    false,
                    false,
                    true),
                "无归属Timer暂停未允许自动重建");
            Assert(!EpbManager.ShouldAutoRecoverTimerAnomaly(
                    ChannelRuntimeState.Running,
                    HighPrecisionTimerRuntimeState.Paused,
                    "ChannelGracefulPause",
                    true,
                    BatchPauseState.Running,
                    true,
                    false,
                    true),
                "人工暂停被错误自动拉起");
            Assert(!EpbManager.ShouldAutoRecoverTimerAnomaly(
                    ChannelRuntimeState.AlarmStopped,
                    HighPrecisionTimerRuntimeState.Stopped,
                    "Stop",
                    true,
                    BatchPauseState.Running,
                    false,
                    true,
                    true),
                "明确卡钳硬件报警被错误自动拉起");
            Assert(!EpbManager.ShouldAutoRecoverTimerAnomaly(
                    ChannelRuntimeState.Running,
                    HighPrecisionTimerRuntimeState.Faulted,
                    "TimeoutException",
                    true,
                    BatchPauseState.Paused,
                    false,
                    false,
                    true),
                "批次人工暂停期间被错误自动拉起");
            Assert(!EpbManager.ShouldAutoRecoverTimerAnomaly(
                    ChannelRuntimeState.Running,
                    HighPrecisionTimerRuntimeState.Faulted,
                    "TimeoutException",
                    true,
                    BatchPauseState.Running,
                    false,
                    false,
                    false),
                "项目已禁用卡钳被错误自动拉起");
        }

        private static void TimerRecoveryRetryAndCircuitBreaker()
        {
            Assert(EpbManager.SelectTimerRecoveryRetryDelayMs(1) == 1000,
                "Timer自恢复首次退避错误");
            Assert(EpbManager.SelectTimerRecoveryRetryDelayMs(2) == 2000,
                "Timer自恢复第二次退避错误");
            Assert(EpbManager.SelectTimerRecoveryRetryDelayMs(3) == 5000,
                "Timer自恢复第三次退避错误");
            Assert(EpbManager.SelectTimerRecoveryRetryDelayMs(4) == 10000,
                "Timer自恢复第四次退避错误");
            Assert(EpbManager.SelectTimerRecoveryRetryDelayMs(5) == 30000 &&
                   EpbManager.SelectTimerRecoveryRetryDelayMs(500) == 30000,
                "通用恢复退避上限未限制为30秒");
            Assert(!EpbManager.ShouldEscalateSoftwareRecovery(1) &&
                   !EpbManager.ShouldEscalateSoftwareRecovery(2),
                "软件自愈在阈值前被过早熔断");
            Assert(EpbManager.ShouldEscalateSoftwareRecovery(3) &&
                   EpbManager.ShouldEscalateSoftwareRecovery(100),
                "软件自愈达到三次后仍会无限停留在自维护");

            var runId = Guid.NewGuid();
            var gate = new SoftwareRecoveryEscalationGate();
            var opened = 0;
            Parallel.For(0, 1000, _ =>
            {
                if (gate.TryOpen(runId)) Interlocked.Increment(ref opened);
            });
            Assert(opened == 1 && gate.IsOpen(runId),
                $"同一运行的并发恢复熔断不是single-flight：Opened={opened}");
            gate.Reset();
            Assert(gate.TryOpen(runId) && !gate.TryOpen(runId),
                "新运行复位后恢复熔断器未重新允许一次整批重建");

            var faultCorrelationId = Guid.NewGuid();
            var fault = EpbManager.CreateSoftwareRecoveryCircuitFault(
                runId,
                new[] { 5, 4, 5, 99 },
                "Injected",
                faultCorrelationId);
            Assert(fault.Code == "SoftwareRecoveryCircuitOpen" &&
                   fault.RunId == runId &&
                   fault.CorrelationId == faultCorrelationId &&
                   fault.CorrelationId != fault.RunId &&
                   fault.Scope == FaultScope.Global &&
                   fault.Classification == FaultClassification.SystemFault &&
                   fault.RecoveryPolicy == FaultRecoveryPolicy.UnattendedBatchRecycle &&
                   fault.AffectedChannels.SequenceEqual(new[] { 4, 5 }),
                "软件恢复熔断未显式隔离 RunId/CorrelationId 或未路由到无人值守整批重建");
        }

        private static void ExternalEquipmentFaultsRemainRecoverable()
        {
            Assert(!EpbManager.ShouldAutoRecoverExternalEquipmentFault(FaultScope.Channel),
                "卡钳通道级硬件故障被错误归入外部设备自动恢复");
            Assert(EpbManager.ShouldAutoRecoverExternalEquipmentFault(FaultScope.DaqGroup),
                "DAQ设备故障未保持自动恢复资格");
            Assert(EpbManager.ShouldAutoRecoverExternalEquipmentFault(FaultScope.HydraulicGroup),
                "液压设备故障未保持自动恢复资格");
            Assert(EpbManager.ShouldAutoRecoverExternalEquipmentFault(FaultScope.ElectricalGroup),
                "程控电源设备故障未保持自动恢复资格");
        }

        private static void InfrastructureRecoveryFiltersStoppedChannels()
        {
            var states = new Dictionary<int, ChannelRuntimeState>
            {
                [1] = ChannelRuntimeState.Recovering,
                [2] = ChannelRuntimeState.AlarmStopped,
                [3] = ChannelRuntimeState.SystemFault,
                [4] = ChannelRuntimeState.Recovering,
                [5] = ChannelRuntimeState.ManualStopped,
                [6] = ChannelRuntimeState.Recovering
            };
            var selected = EpbManager.SelectInfrastructureRecoveryEligibleChannels(
                Enumerable.Range(1, 6),
                channel => channel != 4,
                channel => channel == 2,
                channel => channel == 6,
                channel => states[channel]);
            Assert(selected.SequenceEqual(new[] { 1, 3 }),
                "外部设备恢复未严格过滤报警、禁用、人工停止或暂停通道");
        }

        private static void RecoverableChannelRestartPolicy()
        {
            Assert(EpbManager.CanContinueRecoverableChannelRestart(true, false, false),
                "可恢复卡钳故障未保持无人值守重试资格");
            Assert(!EpbManager.CanContinueRecoverableChannelRestart(false, false, false),
                "项目禁用通道被错误允许自动启动");
            Assert(!EpbManager.CanContinueRecoverableChannelRestart(true, true, false),
                "人工停止通道被错误允许自动启动");
            Assert(!EpbManager.CanContinueRecoverableChannelRestart(true, false, true),
                "明确卡钳硬件锁存被错误允许自动启动");
            Assert(!EpbManager.ShouldEscalateSoftwareRecovery(2) &&
                   EpbManager.ShouldEscalateSoftwareRecovery(3),
                "可恢复卡钳资格复核没有在三次失败后切换为整批软件重建");
        }

        private static void PauseResumeFiveMinutePolicy()
        {
            var paused = new DateTime(2026, 8, 5, 1, 2, 3, DateTimeKind.Utc);
            Assert(EpbManager.CanResumeWithoutQualification(paused, paused.AddMinutes(5)),
                "恰好5分钟被错误要求资格复核");
            Assert(EpbManager.CanResumeWithoutQualification(paused, paused.AddHours(24)),
                "同进程长暂停仍被历史时长门禁阻挡");
            Assert(EpbManager.CanResumeWithoutQualification(paused, paused.AddSeconds(-1)),
                "墙钟回拨被错误用作同进程继续门禁");
        }

        private static void PausedChannelRejoinsCurrentSharedFormalSlot()
        {
            var t0 = new DateTime(2026, 8, 6, 11, 0, 0, DateTimeKind.Utc);
            var now = t0.AddMilliseconds(47_500);
            var slot = EpbManager.SelectSharedFormalRejoinSlot(t0, now, 15_000);

            Assert(slot == 4,
                $"暂停通道未跳过已经失效的旧圈槽：Expected=4 Actual={slot}");
            Assert(t0.AddMilliseconds(slot * 15_000d) > now,
                "重新加入点不是未来的完整公共槽");
            Assert(EpbManager.SelectSharedFormalRejoinSlot(t0, now, 15_000) == slot,
                "同一恢复时刻的通道未取得相同公共槽");
        }

        private static void DaqRecoveryRespectsBatchPausePolicy()
        {
            Assert(!EpbManager.ShouldHoldDaqRecoveredChannelsForBatchPause(BatchPauseState.Idle),
                "空闲态被误判为批次暂停");
            Assert(!EpbManager.ShouldHoldDaqRecoveredChannelsForBatchPause(BatchPauseState.Running),
                "运行态DAQ恢复被错误保持暂停");
            foreach (var state in new[]
                     {
                         BatchPauseState.PausePending,
                         BatchPauseState.Paused,
                         BatchPauseState.ResumeChecking,
                         BatchPauseState.Qualification,
                         BatchPauseState.Stopping
                     })
                Assert(EpbManager.ShouldHoldDaqRecoveredChannelsForBatchPause(state),
                    $"{state} 期间DAQ恢复仍可能越权恢复定时器");

            Assert(EpbManager.GetDaqRecoveredHeldRuntimeState(BatchPauseState.Paused) ==
                   ChannelRuntimeState.Paused,
                "安全暂停时DAQ恢复后的通道状态错误");
            Assert(EpbManager.GetDaqRecoveredHeldRuntimeState(BatchPauseState.ResumeChecking) ==
                   ChannelRuntimeState.ResumeChecking,
                "恢复预检时DAQ恢复后的通道状态错误");
        }

        private static void ChannelAlarmResumePolicy()
        {
            Assert(EpbManager.IsChannelAlarmCodeRecoverable("OpenCircuit"),
                "单通道开路修复后未允许资格复核");
            Assert(EpbManager.IsChannelAlarmCodeRecoverable("ForwardProgressDeadline"),
                "单通道动作超时修复后未允许资格复核");
            Assert(EpbManager.IsChannelAlarmCodeRecoverable("DaqSampleStale"),
                "旧DAQ故障码仍在阻止新一次实时预检");
            Assert(EpbManager.IsChannelAlarmCodeRecoverable("HydraulicBuildTimeout"),
                "旧液压故障码仍在阻止新一次启动定位");
            Assert(EpbManager.IsChannelAlarmCodeRecoverable("OffCurrentNotCleared"),
                "旧断电故障码仍在阻止重启");
            Assert(EpbManager.IsChannelAlarmCodeRecoverable("SharedPowerLimiting"),
                "旧共享电源故障码仍在阻止重启");
            Assert(EpbManager.IsChannelAlarmCodeRecoverable(string.Empty),
                "缺少旧故障码时不应锁死重启");
        }

        private static void ChannelStoppedStatesAreRestartable()
        {
            var restartable = new[]
            {
                ChannelRuntimeState.AlarmStopped,
                ChannelRuntimeState.InterlockStopped,
                ChannelRuntimeState.ManualStopped,
                ChannelRuntimeState.StartBlocked,
                ChannelRuntimeState.SystemFault
            };
            foreach (var state in restartable)
                Assert(EpbManager.IsChannelStateRestartable(state),
                    $"{state} 仍被单通道重新开始入口阻挡");

            Assert(!EpbManager.IsChannelStateRestartable(ChannelRuntimeState.Running),
                "运行中的通道被误识别为可重新开始停机态");
            Assert(!EpbManager.IsChannelStateRestartable(ChannelRuntimeState.Recovering),
                "正在自愈的通道允许了并发重新开始");
            Assert(!EpbManager.IsChannelStateRestartable(ChannelRuntimeState.Completed),
                "已完成目标的通道被允许重新开始");
        }

        private static void DisabledChannelCannotRestart()
        {
            Assert(!EpbManager.CanOperateConfiguredChannel(
                       false,
                       ChannelRuntimeState.AlarmStopped,
                       out var disabledReason) &&
                   disabledReason.Contains("取消启用"),
                "项目Enabled=false时仍允许从报警停机态重新启动");
            Assert(EpbManager.CanOperateConfiguredChannel(
                       true,
                       ChannelRuntimeState.AlarmStopped,
                       out var allowedReason) &&
                   string.IsNullOrEmpty(allowedReason),
                "项目重新勾选启用后仍不能进入完整恢复预检");
        }

        private static void AlarmStopLatchResetsForNewRun()
        {
            var latch = new ChannelAlarmStopLatch();

            latch.BeginRun(10);
            Assert(latch.TryRequestStop(10), "本次运行的首次报警未获得停机权");
            Assert(latch.IsStopRequested(10), "首次报警后锁存未置位");
            Assert(!latch.TryRequestStop(10), "同一次运行中的重复报警未被去重");

            latch.BeginRun(10);
            Assert(!latch.IsStopRequested(10), "新运行未清除上一次报警锁存");
            Assert(latch.TryRequestStop(10), "新运行中的首次报警仍被旧锁存抑制");
        }

        private static void AlarmRequiresCsvAndBinFiles()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "EPBTest_AlarmSnapshotEvidence_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var stem = Path.Combine(directory, "EPB10_Cycle_017161");
                File.WriteAllText(stem + ".csv", "header");
                Assert(
                    !AlarmSnapshotFileEvidence.HasCsvAndBin(directory, 10, 17161),
                    "只有CSV时不应允许写alarm");

                File.WriteAllBytes(stem + ".bin", Array.Empty<byte>());
                Assert(
                    AlarmSnapshotFileEvidence.HasCsvAndBin(directory, 10, 17161),
                    "CSV和BIN均存在时应允许写alarm");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void StaggerPartialSelection()
        {
            var plan = ElectricalStaggerPlanner.Build(
                new[] { 8, 9 },
                new[] { NewElectricalGroup(3, 800, 7, 8, 9) },
                15_000);

            Assert(plan.Get(8).SelectedIndexInGroup == 0 && plan.Get(8).PhaseMs == 0,
                "EPB8未按已选通道重新编号为0相位");
            Assert(plan.Get(9).SelectedIndexInGroup == 1 && plan.Get(9).PhaseMs == 800,
                "EPB9未按已选通道重新编号为800ms相位");
        }

        private static void StaggerFullGroup()
        {
            var plan = ElectricalStaggerPlanner.Build(
                new[] { 9, 7, 8 },
                new[] { NewElectricalGroup(3, 800, 7, 8, 9) },
                15_000);

            Assert(plan.Get(7).PhaseMs == 0, "EPB7相位错误");
            Assert(plan.Get(8).PhaseMs == 800, "EPB8相位错误");
            Assert(plan.Get(9).PhaseMs == 1600, "EPB9相位错误");
        }

        private static void StaggerAcrossGroups()
        {
            var plan = ElectricalStaggerPlanner.Build(
                new[] { 8, 9, 10, 12 },
                new[]
                {
                    NewElectricalGroup(3, 800, 7, 8, 9),
                    NewElectricalGroup(4, 800, 10, 11, 12)
                },
                15_000);

            Assert(plan.Get(8).PhaseMs == 0 && plan.Get(10).PhaseMs == 0,
                "不同电源组首通道未并行使用0相位");
            Assert(plan.Get(9).PhaseMs == 800 && plan.Get(12).PhaseMs == 800,
                "不同电源组第二通道未使用800ms相位");
        }

        private static void StaggerPlanIsImmutableSnapshot()
        {
            var group = NewElectricalGroup(3, 800, 7, 8, 9);
            var plan = ElectricalStaggerPlanner.Build(new[] { 8, 9 }, new[] { group }, 15_000);

            group.StaggerMs = 1200;
            group.Members.Clear();

            Assert(plan.Get(8).StaggerMs == 800 && plan.Get(9).PhaseMs == 800,
                "配置修改污染了已生成的错峰计划");
        }

        private static void StaggerPhaseRemainsFixedAcrossCycles()
        {
            var plan = ElectricalStaggerPlanner.Build(
                new[] { 8, 9 },
                new[] { NewElectricalGroup(3, 800, 7, 8, 9) },
                15_000);
            var anchor = new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc);

            for (var cycle = 0; cycle < 5; cycle++)
            {
                var epb8 = plan.GetDueUtc(anchor, 8, cycle);
                var epb9 = plan.GetDueUtc(anchor, 9, cycle);
                Assert((epb9 - epb8).TotalMilliseconds == 800,
                    $"第{cycle + 1}圈相位差发生漂移");
                if (cycle > 0)
                    Assert((epb8 - plan.GetDueUtc(anchor, 8, cycle - 1)).TotalMilliseconds == 15_000,
                        $"第{cycle + 1}圈未保持固定墙钟周期");
            }
        }

        private static void StaggerRejectsDuplicateGroupId()
        {
            AssertStaggerError(
                new[] { 1, 4 },
                new[]
                {
                    NewElectricalGroup(1, 800, 1, 2, 3),
                    NewElectricalGroup(1, 800, 4, 5, 6)
                },
                15_000,
                "电气组ID 1 重复");
        }

        private static void StaggerRejectsDuplicateMembership()
        {
            AssertStaggerError(
                new[] { 3 },
                new[]
                {
                    NewElectricalGroup(1, 800, 1, 2, 3),
                    NewElectricalGroup(2, 800, 3, 4, 5)
                },
                15_000,
                "EPB3 同时属于");
        }

        private static void StaggerRejectsMissingChannel()
        {
            AssertStaggerError(
                new[] { 8 },
                new[] { NewElectricalGroup(4, 800, 10, 11, 12) },
                15_000,
                "EPB8 未配置");
        }

        private static void StaggerRejectsOutOfRangeChannel()
        {
            AssertStaggerError(
                new[] { 0 },
                new[] { NewElectricalGroup(5, 800, 0) },
                15_000,
                "超出允许范围");
            AssertStaggerError(
                new[] { 13 },
                new[] { NewElectricalGroup(5, 800, 13) },
                15_000,
                "超出允许范围");
        }

        private static void StaggerRejectsZeroDelta()
        {
            AssertStaggerError(
                new[] { 8, 9 },
                new[] { NewElectricalGroup(3, 0, 7, 8, 9) },
                15_000,
                "StaggerMs 必须大于0");
            AssertStaggerError(
                new[] { 8, 9 },
                new[] { NewElectricalGroup(3, -1, 7, 8, 9) },
                15_000,
                "StaggerMs 必须大于0");
        }

        private static void StaggerRejectsPhaseBeyondPeriod()
        {
            AssertStaggerError(
                new[] { 7, 8, 9 },
                new[] { NewElectricalGroup(3, 800, 7, 8, 9) },
                1600,
                "必须小于试验周期");
        }

        private static void StaggerSingleChannelStartsAtZero()
        {
            var plan = ElectricalStaggerPlanner.Build(
                new[] { 12 },
                new[] { NewElectricalGroup(4, 800, 10, 11, 12) },
                15_000);

            Assert(plan.Get(12).SelectedIndexInGroup == 0 && plan.Get(12).PhaseMs == 0,
                "单通道启动仍保留了物理工位空相位");
        }

        private static void StaggerExecutorDoesNotSerialize()
        {
            var plan = ElectricalStaggerPlanner.Build(
                new[] { 8, 9 },
                new[] { NewElectricalGroup(3, 60, 7, 8, 9) },
                1000);
            var sw = Stopwatch.StartNew();
            var starts = new Dictionary<int, long>();
            var ends = new Dictionary<int, long>();
            var gate = new object();

            ElectricalStaggerExecutor.RunAsync(
                    new[] { 8, 9 },
                    plan,
                    DateTime.UtcNow.AddMilliseconds(20),
                    async (channel, token) =>
                    {
                        lock (gate) starts[channel] = sw.ElapsedMilliseconds;
                        await System.Threading.Tasks.Task.Delay(channel == 8 ? 220 : 20, token);
                        lock (gate) ends[channel] = sw.ElapsedMilliseconds;
                    },
                    System.Threading.CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert(starts[9] - starts[8] >= 35, "后一相位没有按计划延迟");
            Assert(starts[9] < ends[8], "后一相位等待前一任务完成，错峰退化成串行");
        }

        private static void FormalStaggerSurvivesLateHydraulicQualification()
        {
            var wall = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
            var qualifiedAnchor = wall.AddMilliseconds(950);
            var window = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                qualifiedAnchor,
                wall,
                15_000,
                800);

            var first = window.GetDueUtc(0);
            var second = window.GetDueUtc(800);
            Assert((second - first).TotalMilliseconds == 800,
                "液压资格晚于第二个旧相位后，最终DO计划未保持800ms");
            Assert(window.DeadlineUtc == wall.AddMilliseconds(15_000),
                "正常建压完成后未保持本圈统一墙钟截止点");
        }

        private static void FormalStaggerDoesNotCollapseBeforeSecondPhase()
        {
            var wall = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
            var window = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                wall.AddMilliseconds(400),
                wall,
                15_000,
                800);

            Assert(window.GetDueUtc(0) == wall.AddMilliseconds(400),
                "零相位未锚定到液压资格后的共享时刻");
            Assert(window.GetDueUtc(800) == wall.AddMilliseconds(1200),
                "第二相位在资格完成时被提前同时补发");

            var late = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                wall.AddMilliseconds(14_900),
                wall,
                15_000,
                800);
            Assert(late.GetDueUtc(0) == wall.AddMilliseconds(15_000) &&
                   late.GetDueUtc(800) == wall.AddMilliseconds(15_800),
                "资格过晚时没有把全部相位整组顺延到下一墙钟周期");
            Assert(late.DeadlineUtc == wall.AddMilliseconds(30_000),
                "资格过晚时没有按最后相位整组顺延截止点");
        }

        private static void FormalStaggerHasNoCumulativeDrift()
        {
            var wall = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
            for (var cycle = 0; cycle < 500; cycle++)
            {
                var cycleWall = wall.AddMilliseconds(cycle * 15_000L);
                var qualified = cycleWall.AddMilliseconds(300 + cycle % 701);
                var window = ElectricalStaggerExecutor.CreateQualifiedPhaseWindow(
                    qualified,
                    wall,
                    15_000,
                    800);
                Assert((window.GetDueUtc(800) - window.GetDueUtc(0)).TotalMilliseconds == 800,
                    $"第{cycle + 1}圈相位差发生累积漂移");
                Assert(window.DeadlineUtc > window.GetDueUtc(800),
                    $"第{cycle + 1}圈统一截止点早于最后相位");
            }
        }

        private static void RuntimeStateDistinguishesSourceAndInterlock()
        {
            var store = new ChannelRuntimeStateStore();
            var correlation = Guid.NewGuid();
            var affected = new[] { 4, 5 };
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 5,
                State = ChannelRuntimeState.AlarmStopped,
                ReasonCode = "DaqSampleStale",
                ReasonText = "Dev1有效样本超过100ms未提交",
                SourceChannel = 5,
                AffectedChannels = affected,
                CorrelationId = correlation
            });
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 4,
                State = ChannelRuntimeState.InterlockStopped,
                ReasonCode = "DaqSampleStale",
                ReasonText = "同设备DAQ故障联锁",
                SourceChannel = 5,
                AffectedChannels = affected,
                CorrelationId = correlation
            });
            Assert(store.Get(5).State == ChannelRuntimeState.AlarmStopped, "EPB5未标记为故障源报警停机");
            Assert(store.Get(4).State == ChannelRuntimeState.InterlockStopped, "EPB4未标记为联锁停机");
            Assert(store.Get(4).SourceChannel == 5, "联锁状态未保留故障源EPB5");
            Assert(store.Get(4).CorrelationId == store.Get(5).CorrelationId, "关联故障号不一致");
        }

        private static void RuntimeStateRecoveryCannotClearAlarm()
        {
            var store = new ChannelRuntimeStateStore();
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 5,
                State = ChannelRuntimeState.AlarmStopped,
                ReasonCode = "DaqSampleStale"
            });
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 5,
                State = ChannelRuntimeState.Running,
                ReasonCode = "DaqRecovered"
            });
            Assert(store.Get(5).State == ChannelRuntimeState.AlarmStopped,
                "DAQ恢复错误解除了报警停机锁存");
        }

        private static void RuntimeStateResetsOnlyForNewRun()
        {
            var store = new ChannelRuntimeStateStore();
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 5,
                State = ChannelRuntimeState.AlarmStopped
            });
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 5,
                State = ChannelRuntimeState.Starting,
                RunId = Guid.NewGuid()
            }, allowTerminalReset: true);
            Assert(store.Get(5).State == ChannelRuntimeState.Starting,
                "新运行通过启动入口后未能复位旧锁存");
        }

        private static void ManualStopReplacesStartBlocked()
        {
            var store = new ChannelRuntimeStateStore();
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 10,
                State = ChannelRuntimeState.StartBlocked,
                ReasonCode = "StartupRollback"
            });
            store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 10,
                State = ChannelRuntimeState.ManualStopped,
                ReasonCode = "StartCanceled",
                ReasonText = "人工停止，启动/自动恢复已取消"
            }, allowTerminalReset: true);
            Assert(store.Get(10).State == ChannelRuntimeState.ManualStopped,
                "人工停止后仍锁存显示启动受阻");
        }

        private static void AlarmRecoveryWaitsForFormalCommit()
        {
            Assert(!EpbManager.CanRunStandaloneAlarmRecovery(true, false),
                "批量学习尚未提交正式阶段时错误允许单通道恢复创建Timer");
            Assert(EpbManager.CanRunStandaloneAlarmRecovery(true, true),
                "正式阶段提交后仍阻止单通道报警恢复");
            Assert(EpbManager.CanRunStandaloneAlarmRecovery(false, false),
                "无活动批次时错误阻止单通道新批次恢复");
        }

        private static void DisabledChannelStateIsNormalized()
        {
            Assert(EpbManager.NormalizeRuntimeStateForEnabled(
                       false,
                       ChannelRuntimeState.Recovering) == ChannelRuntimeState.NotEnabled,
                "禁用通道被组级自恢复状态污染");
            Assert(EpbManager.NormalizeRuntimeStateForEnabled(
                       false,
                       ChannelRuntimeState.AlarmStopped) == ChannelRuntimeState.NotEnabled,
                "禁用通道被故障源报警状态污染");
            Assert(EpbManager.NormalizeRuntimeStateForEnabled(
                       true,
                       ChannelRuntimeState.Recovering) == ChannelRuntimeState.Recovering,
                "启用通道的合法恢复状态被错误改写");
        }

        private static void TerminalRuntimeStatesRejectEnergization()
        {
            Assert(EpbManager.ChannelRuntimeAllowsEnergization(ChannelRuntimeState.Starting),
                "启动中状态未获执行许可");
            Assert(EpbManager.ChannelRuntimeAllowsEnergization(ChannelRuntimeState.Learning),
                "学习中状态未获执行许可");
            Assert(EpbManager.ChannelRuntimeAllowsEnergization(ChannelRuntimeState.Running),
                "运行状态未获执行许可");
            foreach (var state in new[]
                     {
                         ChannelRuntimeState.NotEnabled,
                         ChannelRuntimeState.StartBlocked,
                         ChannelRuntimeState.AlarmStopped,
                         ChannelRuntimeState.InterlockStopped,
                         ChannelRuntimeState.ManualStopped,
                         ChannelRuntimeState.Completed,
                         ChannelRuntimeState.SystemFault,
                         ChannelRuntimeState.Paused
                     })
                Assert(!EpbManager.ChannelRuntimeAllowsEnergization(state),
                    $"非运行终态 {state} 错误保留加电许可");
        }

        private static void TerminalStateRequiresExecutionQuiescence()
        {
            Assert(EpbManager.IsTerminalExecutionQuiescent(
                    false, false, false, false, false, true),
                "资源已清场且OFF确认时未允许终态提交");
            Assert(!EpbManager.IsTerminalExecutionQuiescent(
                    true, false, false, false, false, true),
                "活动Timer存在时错误允许StartBlocked提交");
            Assert(!EpbManager.IsTerminalExecutionQuiescent(
                    false, false, false, false, true, true),
                "仍标记加电时错误允许StartBlocked提交");
            Assert(!EpbManager.IsTerminalExecutionQuiescent(
                    false, false, false, false, false, false),
                "OFF未确认时错误允许StartBlocked提交");
        }

        private static void InfrastructureRecoveryOnlyClearsSystemFault()
        {
            var systemStore = new ChannelRuntimeStateStore();
            systemStore.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 4,
                State = ChannelRuntimeState.SystemFault,
                ReasonCode = "LegacyInfrastructureFault"
            });
            systemStore.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 4,
                State = ChannelRuntimeState.Recovering,
                ReasonCode = "InfrastructureSelfHealing"
            }, allowTerminalReset: false, allowSystemFaultReset: true);
            Assert(systemStore.Get(4).State == ChannelRuntimeState.Recovering,
                "基础设施恢复未能解除旧SystemFault锁存");

            var alarmStore = new ChannelRuntimeStateStore();
            alarmStore.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 5,
                State = ChannelRuntimeState.AlarmStopped,
                ReasonCode = "OpenCircuitOrOutputFault"
            });
            alarmStore.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 5,
                State = ChannelRuntimeState.Running,
                ReasonCode = "InfrastructureSelfHealing"
            }, allowTerminalReset: false, allowSystemFaultReset: true);
            Assert(alarmStore.Get(5).State == ChannelRuntimeState.AlarmStopped,
                "基础设施恢复越权解除了卡钳硬件报警锁存");
        }

        private static void RuntimeStateRevisionRejectsLateUiDelivery()
        {
            var store = new ChannelRuntimeStateStore();
            var alarm = store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 8,
                State = ChannelRuntimeState.AlarmStopped,
                ReasonCode = "PeakEvidenceMismatch"
            });
            var checking = store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 8,
                State = ChannelRuntimeState.ResumeChecking,
                ReasonCode = "AlarmResumeChecking"
            }, allowTerminalReset: true);
            var running = store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 8,
                State = ChannelRuntimeState.Running,
                ReasonCode = "AlarmResumed"
            }, allowTerminalReset: true);

            Assert(alarm.Revision > 0 && checking.Revision > alarm.Revision &&
                   running.Revision > checking.Revision,
                "通道运行状态未分配单调递增版本号");
            Assert(running.Clone().Revision == running.Revision,
                "状态克隆丢失版本号");

            ChannelRuntimeStateChangedEvent uiState = null;
            if (running.IsNewerThan(uiState)) uiState = running.Clone();
            // 模拟 UI 线程稍后才收到此前排队的报警消息。
            if (alarm.IsNewerThan(uiState)) uiState = alarm.Clone();
            Assert(uiState.State == ChannelRuntimeState.Running &&
                   uiState.Revision == running.Revision,
                "迟到的报警状态覆盖了已恢复运行的新状态");
        }

        private static void StopSafetyExitPolicy()
        {
            var pressureOnly = new StopSafetyResult
            {
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = false
            };
            Assert(pressureOnly.CanReleaseAcquisition && !pressureOnly.FullyConfirmed,
                "仅压力证据缺失时应允许释放DAQ但不得标记完全确认");
            Assert(!pressureOnly.CanCloseApplication,
                "持久化边界未确认时错误允许结束进程");
            Assert(!EpbManager.ShouldShutdownPersistenceForStop(
                       StopSource.ApplicationClosing,
                       persistenceBoundaryConfirmed: false),
                "窗口关闭的耐久边界未确认时错误释放写盘器");
            Assert(EpbManager.ShouldShutdownPersistenceForStop(
                       StopSource.ApplicationClosing,
                       persistenceBoundaryConfirmed: true) &&
                   EpbManager.ShouldShutdownPersistenceForStop(
                       StopSource.ProgramExit,
                       persistenceBoundaryConfirmed: true) &&
                   !EpbManager.ShouldShutdownPersistenceForStop(
                       StopSource.ManualUi,
                       persistenceBoundaryConfirmed: true),
                "写盘器关闭策略没有区分退出和普通停止");
            Assert(!EpbManager.CanReuseStopResultForSource(
                       StopSource.ManualUi,
                       StopSource.ApplicationClosing) &&
                   EpbManager.CanReuseStopResultForSource(
                       StopSource.ApplicationClosing,
                       StopSource.ApplicationClosing) &&
                   EpbManager.CanReuseStopResultForSource(
                       StopSource.ApplicationClosing,
                       StopSource.ManualUi),
                "Manual Stop的成功结果被ApplicationClosing误复用，最终persistence shutdown会被跳过");
            Assert(EpbManager.ShouldStopAcquisitionBeforeFinalPersistence(
                       StopSource.ApplicationClosing) &&
                   EpbManager.ShouldStopAcquisitionBeforeFinalPersistence(
                       StopSource.ProgramExit) &&
                   EpbManager.ShouldStopAcquisitionBeforeFinalPersistence(
                       StopSource.SystemFault) &&
                   EpbManager.ShouldStopAcquisitionBeforeFinalPersistence(
                       StopSource.ManualUi),
                "StopAll仍允许DAQ在最终Raw/SQLite边界冻结后继续接纳新批次");

            var powerMissing = new StopSafetyResult
            {
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = false,
                PressureSafeConfirmed = true
            };
            Assert(!powerMissing.CanReleaseAcquisition,
                "程控电源关闭未确认时不应允许释放DAQ");
            Assert(EpbManager.CanDiscardHistoricalStopChecksForExplicitRestart(
                       pressureOnly,
                       explicitlyStopped: true) &&
                   !EpbManager.CanDiscardHistoricalStopChecksForExplicitRestart(
                       pressureOnly,
                       explicitlyStopped: false) &&
                   !EpbManager.CanDiscardHistoricalStopChecksForExplicitRestart(
                       powerMissing,
                       explicitlyStopped: true),
                "显式停止后的重启放宽未限定为已确认电机断能和电源关闭");

            var motorMissing = new StopSafetyResult
            {
                MotorOffCommandSucceeded = false,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = true
            };
            Assert(!motorMissing.CanReleaseAcquisition,
                "电机DO关闭未确认时不应允许释放DAQ");

            var restartable = new StopSafetyResult
            {
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = true,
                PersistenceBoundaryConfirmed = true,
                LogicalQuiescenceConfirmed = true
            };
            Assert(restartable.CanRestartInProcess,
                "物理安全、持久化边界和逻辑清场全部确认后仍不可重启");
            Assert(restartable.CanCloseApplication,
                "电机/电源和持久化边界均确认后仍禁止正常退出");
            restartable.PersistenceBoundaryConfirmed = false;
            Assert(restartable.PhysicalSafetyConfirmed &&
                   !restartable.FullyConfirmed &&
                   !restartable.CanRestartInProcess &&
                   !restartable.CanCloseApplication,
                "Raw/持久化边界未闭合时错误允许同进程重启");
        }

        private static void StopPersistenceBoundaryPolicy()
        {
            Assert(EpbManager.IsStopPersistenceBoundaryClosed(100, 100, 100, 0),
                "边界、Raw发布、持久化和队列均闭合时被误拒绝");
            Assert(EpbManager.IsFrozenStopPersistenceBoundaryClosed(
                    100, 100, true, 100, 100, 0),
                "DAQ停止后同一冻结边界的Raw/SQLite前缀被误拒绝");
            Assert(!EpbManager.IsFrozenStopPersistenceBoundaryClosed(
                    100, 101, true, 101, 101, 0),
                "没有抑制终态证据时，增长的停止尾段被错误视为完整收口");
            Assert(EpbManager.IsFrozenStopPersistenceBoundaryClosed(
                    100, 105, true, 105, 100, 0,
                    suppressAfterSequence: 100,
                    suppressThroughSequence: long.MaxValue,
                    lastTerminallyHandledSequence: 105),
                "冻结前缀已落盘且截止后有限尾段已终态处理时仍未闭合");
            Assert(!EpbManager.IsFrozenStopPersistenceBoundaryClosed(
                    100, 105, true, 105, 100, 0,
                    suppressAfterSequence: 100,
                    suppressThroughSequence: 104,
                    lastTerminallyHandledSequence: 105),
                "抑制窗口没有覆盖完整尾段时错误放行");
            Assert(!EpbManager.IsFrozenStopPersistenceBoundaryClosed(
                    100, 105, true, 105, 100, 0,
                    suppressAfterSequence: 100,
                    suppressThroughSequence: long.MaxValue,
                    lastTerminallyHandledSequence: 104),
                "截止后尾段尚未全部终态处理时错误放行");
            Assert(!EpbManager.IsFrozenStopPersistenceBoundaryClosed(
                    100, 100, true, 100, 100, 0,
                    pendingHeadSequence: 0,
                    inFlightSequence: 100),
                "队列Depth为0但截止批次仍在同步写调用中时错误放行");
            Assert(!EpbManager.IsFrozenStopPersistenceBoundaryClosed(
                    100, 100, false, 100, 100, 0),
                "冻结边界的Raw链未排空却被持久化水位单独放行");
            Assert(!EpbManager.IsStopPersistenceBoundaryClosed(100, 99, 100, 0),
                "Raw发布尚未越过停止边界时错误放行");
            Assert(!EpbManager.IsStopPersistenceBoundaryClosed(100, 100, 99, 0),
                "持久化尚未越过停止边界时错误放行");
            Assert(!EpbManager.IsStopPersistenceBoundaryClosed(100, 100, 100, 1),
                "持久化队列仍有批次时错误放行");
            Assert(!EpbManager.IsStopPersistenceBoundaryClosed(
                    100, 100, 100, 0, DaqPersistenceState.Failed),
                "持久化失败状态错误放行同进程重启");
            Assert(EpbManager.IsStopPersistenceBoundaryClosed(
                    100, 100, 100, 0, DaqPersistenceState.Failed,
                    requireRecoveredState: false),
                "退出时数据已真实耐久但仅剩恢复计数锁存，被错误阻止关闭");
            Assert(!EpbManager.IsStopPersistenceBoundaryClosed(
                    100, 100, 100, 0, DaqPersistenceState.Failed,
                    requireRecoveredState: false,
                    durabilityBlocked: true),
                "退出时仍有未解决写故障却被错误放行");
            Assert(!EpbManager.IsStopPersistenceBoundaryClosed(
                    100, 100, 100, 0, DaqPersistenceState.Failed,
                    requireRecoveredState: false,
                    discardedGenerationBatchCount: 1),
                "退出时存在代次丢弃却被错误放行");
            Assert(!EpbManager.IsStopPersistenceBoundaryClosed(
                    100, 100, 100, 0, DaqPersistenceState.Failed,
                    requireRecoveredState: false,
                    overCapacityDroppedBatchCount: 1),
                "退出时存在容量丢弃却被错误放行");
            Assert(EpbManager.RequiresRecoveredPersistenceStateForStop(StopSource.ManualUi),
                "人工停止仍可能同进程重启，却错误放宽恢复门禁");
            Assert(!EpbManager.RequiresRecoveredPersistenceStateForStop(StopSource.ApplicationClosing) &&
                   !EpbManager.RequiresRecoveredPersistenceStateForStop(StopSource.ProgramExit),
                "退出路径没有采用数据耐久而非新鲜批次恢复判据");
        }

        private static void ActiveCycleBlocksInProcessRestart()
        {
            var activeCyclePending = new LogicalQuiescenceSnapshot
            {
                SoftwareRecoveryCount = 1
            };
            Assert(!activeCyclePending.IsQuiescent,
                "仍有活动圈或圈终态重试时错误判定逻辑清场完成");

            var result = new StopSafetyResult
            {
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = true,
                PersistenceBoundaryConfirmed = true,
                LogicalQuiescenceConfirmed = activeCyclePending.IsQuiescent,
                LogicalState = activeCyclePending
            };
            Assert(result.PhysicalSafetyConfirmed && result.FullyConfirmed &&
                   !result.CanRestartInProcess,
                "活动圈尚未提交唯一终态时错误允许同进程重启");

            activeCyclePending.SoftwareRecoveryCount = 0;
            result.LogicalQuiescenceConfirmed = activeCyclePending.IsQuiescent;
            Assert(activeCyclePending.IsQuiescent && result.CanRestartInProcess,
                "所有圈终态与恢复任务清场后仍拒绝同进程重启");
        }

        private static void DaqBoundedQueuesAreIndependent()
        {
            var dev1Count = 0;
            var dev2Count = 0;
            for (var i = 0; i < 64; i++)
                Assert(DaqQueueAdmission.TryEnter(ref dev1Count, 64), "Dev1队列未到容量即拒绝入队");
            Assert(!DaqQueueAdmission.TryEnter(ref dev1Count, 64), "Dev1队列满载后仍允许入队");
            Assert(dev1Count == 64, "Dev1满载拒绝后计数被破坏");
            Assert(DaqQueueAdmission.TryEnter(ref dev2Count, 64),
                "Dev1满载错误阻塞了独立的Dev2队列");
            DaqQueueAdmission.Release(ref dev2Count);
            Assert(dev2Count == 0 && dev1Count == 64, "Dev2出队错误修改了Dev1队列状态");
        }

        private static void TwelveChannelsCreateRuntimesConcurrently()
        {
            var store = new ChannelRuntimeStore<object>();
            var factoryCounts = new int[13];
            var tasks = Enumerable.Range(1, 12)
                .SelectMany(channel => Enumerable.Range(0, 16).Select(ignored => Task.Run(() =>
                    store.GetOrCreate(
                        channel,
                        () =>
                        {
                            Interlocked.Increment(ref factoryCounts[channel]);
                            return new object();
                        },
                        activate: null,
                        out var created))))
                .ToArray();

            Task.WaitAll(tasks);
            Assert(store.Active.Count == 12 && store.Cache.Count == 12,
                "12通道并发创建后运行表或缓存表数量错误");
            for (var channel = 1; channel <= 12; channel++)
                Assert(factoryCounts[channel] == 1, $"EPB{channel}被重复创建运行对象");
        }

        private static void SameChannelCreatesExactlyOneRuntime()
        {
            var store = new ChannelRuntimeStore<object>();
            var factoryCount = 0;
            var results = Enumerable.Range(0, 256)
                .Select(ignored => Task.Run(() => store.GetOrCreate(
                    11,
                    () =>
                    {
                        Interlocked.Increment(ref factoryCount);
                        return new object();
                    },
                    activate: null,
                    out var created)))
                .ToArray();

            Task.WaitAll(results);
            var first = results[0].Result;
            Assert(factoryCount == 1, "同一通道并发进入时工厂执行次数不为1");
            Assert(results.All(x => ReferenceEquals(first, x.Result)),
                "同一通道并发进入返回了不同实例");
        }

        private static void OverdueTwelveChannelReleaseCreatesSafely()
        {
            var channels = Enumerable.Range(1, 12).ToArray();
            var groups = new[]
            {
                NewElectricalGroup(1, 800, 1, 2, 3),
                NewElectricalGroup(2, 800, 4, 5, 6),
                NewElectricalGroup(3, 800, 7, 8, 9),
                NewElectricalGroup(4, 800, 10, 11, 12)
            };
            var plan = ElectricalStaggerPlanner.Build(channels, groups, 15_000);
            var store = new ChannelRuntimeStore<object>();
            var factoryCounts = new int[13];
            var sw = Stopwatch.StartNew();
            var starts = new long[13];

            ElectricalStaggerExecutor.RunAsync(
                    channels,
                    plan,
                    DateTime.UtcNow.AddSeconds(-10),
                    (channel, token) =>
                    {
                        starts[channel] = sw.ElapsedMilliseconds;
                        store.GetOrCreate(
                            channel,
                            () =>
                            {
                                Interlocked.Increment(ref factoryCounts[channel]);
                                return new object();
                            },
                            activate: null,
                            out var created);
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert(store.Active.Count == 12 && store.Cache.Count == 12,
                "错过锚点平移后未创建全部12个通道");
            Assert(Enumerable.Range(1, 12).All(channel => factoryCounts[channel] == 1),
                "错过锚点平移后造成重复创建");
            foreach (var group in groups)
            {
                var selected = group.Members.OrderBy(x => x).ToArray();
                for (var i = 1; i < selected.Length; i++)
                {
                    var delta = starts[selected[i]] - starts[selected[i - 1]];
                    Assert(delta >= 750 && delta <= 850,
                        $"过期锚点未保留组{group.Id}的800ms相位：delta={delta}ms");
                }
            }
        }

        private static void ManualCancellationIsExpected()
        {
            Assert(
                EpbManager.IsExpectedBatchCancellation(
                    new TaskCanceledException("manual stop"),
                    sessionCancellationRequested: true,
                    externalCancellationRequested: false),
                "批次令牌取消仍会按ERROR记录");
            Assert(
                EpbManager.IsExpectedBatchCancellation(
                    new OperationCanceledException("external stop"),
                    sessionCancellationRequested: false,
                    externalCancellationRequested: true),
                "外部人工取消仍会按ERROR记录");
            Assert(
                !EpbManager.IsExpectedBatchCancellation(
                    new InvalidOperationException("real fault"),
                    sessionCancellationRequested: true,
                    externalCancellationRequested: true),
                "真实启动异常被错误降级");
        }

        private static void TwelveChannelsRestartWithoutRuntimeLeaks()
        {
            var store = new ChannelRuntimeStore<object>();
            for (var cycle = 0; cycle < 50; cycle++)
            {
                var starts = Enumerable.Range(1, 12)
                    .Select(channel => Task.Run(() => store.GetOrCreate(
                        channel,
                        () => new object(),
                        activate: null,
                        out var created)))
                    .ToArray();
                Task.WaitAll(starts);
                Assert(store.Active.Count == 12 && store.Cache.Count == 12,
                    $"第{cycle + 1}轮启动后通道数量错误");

                var stops = Enumerable.Range(1, 12)
                    .Select(channel => Task.Run(() => store.Remove(channel)))
                    .ToArray();
                Task.WaitAll(stops);
                Assert(store.Active.IsEmpty && store.Cache.IsEmpty,
                    $"第{cycle + 1}轮停止后仍有运行对象残留");
            }
        }

        private static void SessionClosureRequiresAllRuntimeStoresIdle()
        {
            Assert(EpbManager.AreSessionRuntimesIdle(0, 0, 0, 0),
                "全部运行对象清空后仍拒绝会话收尾");
            Assert(!EpbManager.AreSessionRuntimesIdle(1, 0, 0, 0),
                "活动Timer存在时错误允许会话收尾");
            Assert(!EpbManager.AreSessionRuntimesIdle(0, 1, 0, 0),
                "缓存Timer存在时错误允许会话收尾");
            Assert(!EpbManager.AreSessionRuntimesIdle(0, 0, 1, 0),
                "活动Runner存在时错误允许会话收尾");
            Assert(!EpbManager.AreSessionRuntimesIdle(0, 0, 0, 1),
                "缓存Runner存在时错误允许会话收尾");
        }

        private static void RunAuthorizationRevocationIsRunBounded()
        {
            var oldRun = Guid.NewGuid();
            var newRun = Guid.NewGuid();
            Assert(EpbManager.ShouldApplyRunAuthorizationRevocation(
                    newRun.ToString("N"),
                    newRun.ToString("D")),
                "同一运行不同GUID格式未允许撤销");
            Assert(!EpbManager.ShouldApplyRunAuthorizationRevocation(
                    newRun.ToString("N"),
                    oldRun.ToString("N")),
                "迟到旧运行撤销仍会清除新运行授权");
            Assert(EpbManager.ShouldApplyRunAuthorizationRevocation(string.Empty, oldRun.ToString("N")),
                "旧格式无RunId检查点没有保持失效安全撤销");
            Assert(EpbManager.ShouldApplyRunAuthorizationRevocation(newRun.ToString("N"), null),
                "旧格式无RunId事件没有保持失效安全撤销");
        }

        private static void AutomaticRecoveryRequiresExactRunIdentity()
        {
            var oldRun = Guid.NewGuid();
            var newRun = Guid.NewGuid();
            Assert(EpbManager.AreSameNonEmptyRunIds(
                    newRun.ToString("N"),
                    newRun.ToString("D").ToUpperInvariant()),
                "同一RunId不同格式未允许登记自动恢复");
            Assert(!EpbManager.AreSameNonEmptyRunIds(
                    newRun.ToString("N"),
                    oldRun.ToString("N")),
                "迟到旧运行仍可登记到新运行检查点");
            Assert(!EpbManager.AreSameNonEmptyRunIds(string.Empty, newRun.ToString("N")) &&
                   !EpbManager.AreSameNonEmptyRunIds(newRun.ToString("N"), null) &&
                   !EpbManager.AreSameNonEmptyRunIds("not-a-guid", newRun.ToString("N")),
                "主动自动恢复错误接受了空或非法RunId");
        }

        private static void FreshRestartJoinsOldStartupCleanup()
        {
            var gate = new BatchStartLifecycleGate();
            var oldStarted = new ManualResetEventSlim(false);
            var allowOldCleanup = new ManualResetEventSlim(false);
            var oldCleanupFinished = 0;
            try
            {
                var oldStartup = gate.RunAsync(
                    async () =>
                    {
                        oldStarted.Set();
                        await Task.Run(() => allowOldCleanup.Wait()).ConfigureAwait(false);
                        Interlocked.Exchange(ref oldCleanupFinished, 1);
                        return 1;
                    },
                    CancellationToken.None);
                Assert(oldStarted.Wait(1000), "旧启动没有进入受保护生命周期");

                var restartJoin = gate.JoinAsync(CancellationToken.None);
                Assert(!restartJoin.Wait(100), "重新开始未等待旧启动的catch/finally收尾");

                allowOldCleanup.Set();
                restartJoin.GetAwaiter().GetResult();
                Assert(Volatile.Read(ref oldCleanupFinished) == 1,
                    "重新开始屏障在旧启动尾声结束前错误放行");

                var newStartup = gate.RunAsync(
                        () => Task.FromResult(Volatile.Read(ref oldCleanupFinished) == 1 ? 2 : -1),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Assert(newStartup == 2, "新启动可能被上一批次的迟到清理覆盖");
                Assert(oldStartup.GetAwaiter().GetResult() == 1, "旧启动任务未正常完成测试收尾");
            }
            finally
            {
                allowOldCleanup.Set();
                oldStarted.Dispose();
                allowOldCleanup.Dispose();
            }
        }

        private static void ConcurrentStartStopDoesNotCorruptRuntimeStore()
        {
            var store = new ChannelRuntimeStore<object>();
            var operations = Enumerable.Range(0, 2_400)
                .Select(index => Task.Run(() =>
                {
                    var channel = index % 12 + 1;
                    if ((index & 1) == 0)
                        store.GetOrCreate(channel, () => new object(), null, out var created);
                    else
                        store.Remove(channel);
                }))
                .ToArray();
            Task.WaitAll(operations);

            store.Clear();
            Assert(store.Active.IsEmpty && store.Cache.IsEmpty,
                "并发启动停止后运行表无法安全清空");

            var restart = Enumerable.Range(1, 12)
                .Select(channel => Task.Run(() => store.GetOrCreate(
                    channel, () => new object(), null, out var created)))
                .ToArray();
            Task.WaitAll(restart);
            Assert(store.Active.Count == 12 && store.Cache.Count == 12,
                "并发启动停止后无法重新启动全部12通道");
        }

        private static void DoTraceBufferFiltersRunAndAge()
        {
            var runA = Guid.NewGuid();
            var runB = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var buffer = new DoControlTraceBuffer();
            buffer.Add(new DoControlTraceEvent
            {
                Utc = now.AddSeconds(-61),
                RunId = runA,
                Channel = 8,
                Command = EpbDoCommand.Forward
            });
            buffer.Add(new DoControlTraceEvent
            {
                Utc = now,
                RunId = runB,
                Channel = 10,
                Command = EpbDoCommand.Forward
            });
            buffer.Add(new DoControlTraceEvent
            {
                Utc = now,
                RunId = runA,
                Channel = 9,
                Command = EpbDoCommand.OffHighPriority
            });

            var snapshot = buffer.Snapshot(now, runA);
            Assert(snapshot.Count == 1, "DO追踪未按60秒窗口和运行ID过滤");
            Assert(snapshot[0].Channel == 9, "DO追踪保留了错误运行或过期事件");

            var capacityBuffer = new DoControlTraceBuffer();
            for (var i = 0; i < 10001; i++)
            {
                capacityBuffer.Add(new DoControlTraceEvent
                {
                    Utc = now,
                    RunId = runA,
                    Channel = 8,
                    MonotonicTicks = i
                });
            }

            var capacitySnapshot = capacityBuffer.Snapshot(now, runA);
            Assert(capacitySnapshot.Count == 10000, "DO追踪超过10000条后未保持有界");
            Assert(capacitySnapshot[0].MonotonicTicks == 1, "DO追踪未淘汰最旧事件");
        }

        private static void AlarmControlEvidenceIsReconstructable()
        {
            var directory = CreateTempDir();
            try
            {
                var runId = Guid.NewGuid();
                var plan = ElectricalStaggerPlanner.Build(
                    new[] { 8, 9 },
                    new[] { NewElectricalGroup(3, 800, 7, 8, 9) },
                    15_000);
                var utc = DateTime.UtcNow;
                var adaptiveDecision = new AdaptiveDecisionTraceEvent
                {
                    SampleUtc = utc,
                    MonotonicTicks = 124,
                    RunId = runId,
                    CycleNumber = 42,
                    Channel = 9,
                    Direction = "Reverse",
                    Stage = EpbCurrentStage.ReleaseDecay,
                    ElapsedMs = 3500,
                    CurrentA = 1.95,
                    WindowSampleCount = 12,
                    WindowSpanMs = 187,
                    WindowMedianA = 1.91,
                    WindowMadA = 0.03,
                    WindowP10A = 1.87,
                    WindowP90A = 1.98,
                    ReleaseThresholdA = 2.44,
                    AllowedSpreadA = 1.03,
                    ReleaseCandidateElapsedMs = 187,
                    WindowQualified = true,
                    Action = "HardFault",
                    Reason = "ReverseAbsoluteOnTimeExceeded"
                };

                EpbManager.WriteAlarmMetadata(
                    Path.Combine(directory, "alarm-metadata.json"),
                    9,
                    42,
                    "堵转\"故障\n复测",
                    new AlarmCycleSnapshotEvidence
                    {
                        IsValid = true,
                        SampleCount = 7000,
                        FirstSampleUtc = utc.AddSeconds(-3.5),
                        LastSampleUtc = utc
                    },
                    utc,
                    runId,
                    plan.Get(9),
                    plan,
                    new[]
                    {
                        new DoControlTraceEvent
                        {
                            Utc = utc.AddMilliseconds(-10),
                            MonotonicTicks = 122,
                            RunId = runId,
                            Channel = 9,
                            Stage = "RunOneAdaptiveAsync",
                            Command = EpbDoCommand.Forward,
                            DoCommandResult = false,
                            BranchCurrentA = 10.25
                        },
                        new DoControlTraceEvent
                        {
                            Utc = utc,
                            MonotonicTicks = 123,
                            RunId = runId,
                            Channel = 9,
                            Stage = "RunOneAdaptiveAsync",
                            Command = EpbDoCommand.OffHighPriority,
                            DoCommandResult = true,
                            BranchCurrentA = 12.3456
                        }
                    },
                    adaptiveDecision,
                    new TerminalOffSafetyEvidence
                    {
                        CommandUtc = utc,
                        DecisionUtc = utc.AddMilliseconds(-10),
                        DoWriteStartedUtc = utc.AddMilliseconds(-8),
                        DoWriteCompletedUtc = utc.AddMilliseconds(-7),
                        CurrentClearedUtc = utc.AddMilliseconds(100),
                        Reason = "ForwardCurrentRiseStalled",
                        CommandSucceeded = true,
                        CommandElapsedMs = 1.25,
                        VerificationUtc = utc.AddMilliseconds(100),
                        VerificationCurrentA = 0.05,
                        VerificationThresholdA = 0.1,
                        VerificationWaitMs = 100,
                        ElectricalCurrentCleared = true
                    });
                EpbManager.WriteStaggerPlan(
                    Path.Combine(directory, "electrical-stagger-plan.json"),
                    runId,
                    plan);
                EpbManager.WriteDoTimeline(
                    Path.Combine(directory, "do-control-timeline.csv"),
                    new[]
                    {
                        new DoControlTraceEvent
                        {
                            Utc = utc,
                            MonotonicTicks = 123,
                            MonotonicElapsedMs = 456.789,
                            RunId = runId,
                            CycleNumber = 42,
                            ElectricalGroupId = 3,
                            Channel = 9,
                            PlannedPhaseMs = 800,
                            Stage = "RunOneAdaptiveAsync",
                            Command = EpbDoCommand.OffHighPriority,
                            DoCommandResult = true,
                            BranchCurrentA = 12.3456
                        },
                        new DoControlTraceEvent
                        {
                            Utc = utc.AddMilliseconds(-10),
                            MonotonicTicks = 122,
                            MonotonicElapsedMs = 446.789,
                            RunId = runId,
                            CycleNumber = 42,
                            ElectricalGroupId = 3,
                            Channel = 9,
                            PlannedPhaseMs = 800,
                            ElectricalPhaseDueUtc = utc.AddMilliseconds(-12),
                            ElectricalPhaseStartDeviationMs = 2,
                            Stage = "RunOneAdaptiveAsync",
                            Command = EpbDoCommand.Forward,
                            DoCommandResult = false,
                            BranchCurrentA = 10.25
                        }
                    });
                AdaptiveDecisionTraceBuffer.ExportCsv(
                    Path.Combine(directory, "adaptive-decision-timeline.csv"),
                    new[] { adaptiveDecision });

                var metadata = File.ReadAllText(Path.Combine(directory, "alarm-metadata.json"));
                var stagger = File.ReadAllText(Path.Combine(directory, "electrical-stagger-plan.json"));
                var timeline = File.ReadAllText(Path.Combine(directory, "do-control-timeline.csv"));
                var adaptiveTimeline = File.ReadAllText(
                    Path.Combine(directory, "adaptive-decision-timeline.csv"));

                Assert(metadata.Contains("\"schemaVersion\": 5") &&
                       metadata.Contains("\"terminalOffSafety\":"),
                    "报警元数据未升级到包含DO四时刻证据的schema 5");
                Assert(metadata.Contains("\"decisionUtc\":") &&
                       metadata.Contains("\"doWriteStartedUtc\":") &&
                       metadata.Contains("\"doWriteCompletedUtc\":") &&
                       metadata.Contains("\"currentClearedUtc\":"),
                    "报警元数据未显式区分decision/write-start/write-complete/current-clear");
                Assert(metadata.Contains("\"electricalCurrentCleared\": true") &&
                       metadata.Contains("\"physicalOffStatus\": \"NotMeasured\""),
                    "报警元数据未正确区分电流代理确认与物理触点状态");
                Assert(metadata.Contains("\"alarmCycleCsvAndBinComplete\": true"),
                    "报警元数据未记录CSV/BIN完整性");
                Assert(metadata.Contains("\"evidenceSampleCount\": 7000"),
                    "报警元数据未记录冻结后的证据样本数");
                Assert(metadata.Contains("\"physicalPowerState\": \"NotMeasured\""),
                    "报警元数据误将DO返回值当成物理断电确认");
                Assert(metadata.Contains("\"selectedChannelsInElectricalGroup\": [8, 9]") &&
                       metadata.Contains("\"forward\": {") &&
                       metadata.Contains("\"off\": {") &&
                       metadata.Contains("\"adaptiveDecision\": {") &&
                       metadata.Contains("\"releaseThresholdA\": 2.440000"),
                    "报警元数据缺少同组计划或最近DO命令");
                Assert(stagger.Contains("\"channel\": 9") && stagger.Contains("\"phaseMs\": 800"),
                    "错峰计划证据缺少通道相位");
                Assert(timeline.Contains("OffHighPriority") &&
                       timeline.Contains("12.345600") &&
                       timeline.Contains("NotMeasured"),
                    "DO时间线缺少命令、电流或物理状态语义");
                Assert(timeline.IndexOf("Forward", StringComparison.Ordinal) <
                       timeline.IndexOf("OffHighPriority", StringComparison.Ordinal) &&
                       timeline.Contains(",false,10.250000,NotMeasured"),
                    "DO时间线未按单调时序保存命令顺序或返回值");
                Assert(adaptiveTimeline.Contains("WindowSamples") &&
                       adaptiveTimeline.Contains("ReleaseThresholdA") &&
                       adaptiveTimeline.Contains("ReverseAbsoluteOnTimeExceeded"),
                    "自适应判定时间线缺少窗口、阈值或报警原因");

                var selected = EpbManager.SelectAlarmDecision(
                    new[]
                    {
                        adaptiveDecision,
                        new AdaptiveDecisionTraceEvent
                        {
                            SampleUtc = utc,
                            MonotonicTicks = 125,
                            Action = string.Empty,
                            Reason = string.Empty
                        }
                    },
                    utc);
                Assert(selected == adaptiveDecision,
                    "报警元数据被故障后的空判定覆盖，未保留HardFault原因");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void HardFaultDoesNotStopSiblingChannel()
        {
            var plan = ElectricalStaggerPlanner.Build(
                new[] { 8, 9 },
                new[] { NewElectricalGroup(3, 800, 7, 8, 9) },
                15_000);
            var affected = ChannelFaultIsolationPolicy.GetChannelsToStop(8);

            Assert(affected.Count == 1 && affected[0] == 8,
                "硬故障停机范围扩展到了同组其他通道");
            Assert(plan.Get(9).PhaseMs == 800,
                "同组正常通道的原计划相位被硬故障策略改变");
        }

        private static ElectricalGroup NewElectricalGroup(
            int id,
            int staggerMs,
            params int[] members)
        {
            var group = new ElectricalGroup { Id = id, StaggerMs = staggerMs };
            group.Members.AddRange(members);
            return group;
        }

        private static void AssertStaggerError(
            IEnumerable<int> selected,
            IEnumerable<ElectricalGroup> groups,
            int periodMs,
            string expected)
        {
            try
            {
                ElectricalStaggerPlanner.Build(selected, groups, periodMs);
                throw new InvalidOperationException("预期错峰配置校验失败，但计划生成成功");
            }
            catch (ElectricalStaggerPlanException ex)
            {
                Assert(ex.Message.Contains(expected), $"错误信息未包含“{expected}”：{ex.Message}");
            }
        }

        private static void HundredThousandCycleDurabilitySimulation()
        {
            const int cycleCount = 100_000;
            var random = new Random(20260731);
            var profile = StableProfile();
            var fullRateCompletions = 0;
            var maximumRetainedSamples = 0;

            for (var cycle = 0; cycle < cycleCount; cycle++)
            {
                var machine = new EpbAdaptiveCurrentStateMachine(profile);
                machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);

                EpbAdaptiveDecision terminal = null;
                for (var ms = 0; ms <= 300; ms += 20)
                    terminal = machine.OnSample(Tick(ms), 1.0 + random.NextDouble() * 0.02);
                for (var ms = 320; ms <= 500; ms += 20)
                    terminal = machine.OnSample(
                        Tick(ms),
                        1.0 + (ms - 300) * 0.045 + random.NextDouble() * 0.02);

                // 固定种子覆盖正式阶段±0.8A波动；控制通道保留在目标以下，
                // 2kHz峰值证据达到目标，验证短峰不会在长时间运行中误走硬停。
                var fullRatePeakA = 15.0 + random.NextDouble() * 0.79;
                terminal = machine.OnSample(
                    Tick(520),
                    9.5 + random.NextDouble() * 0.1,
                    fullRatePeakA);
                maximumRetainedSamples = Math.Max(
                    maximumRetainedSamples,
                    machine.RetainedWindowSampleCount);

                Assert(
                    terminal.ClampReached &&
                    !terminal.HardFault &&
                    terminal.CutoffReason == "FullRateTarget",
                    $"10万圈仿真第{cycle + 1}圈发生错误硬停");
                fullRateCompletions++;
            }

            Assert(fullRateCompletions == cycleCount, "10万圈仿真完成数不正确");
            Assert(
                maximumRetainedSamples <= 60,
                $"状态窗口样本未保持有界：max={maximumRetainedSamples}");

            // 同一套策略仍须及时拦截真实低平台、开路和DAQ断流。
            ForwardCurrentRiseStallWarnsAndCutsPower();
            OpenCircuit();
            DaqStale();
        }

        private static EpbAdaptiveCurrentStateMachine NewMachine()
        {
            return new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile { Channel = 10 });
        }

        private static EpbAdaptiveCurrentStateMachine NewReverseMachine(EpbAdaptiveProfile profile)
        {
            var machine = new EpbAdaptiveCurrentStateMachine(profile);
            machine.ArmForward(Tick(0), 100, 6000, 15.0, 1.0, 3.0);
            machine.ArmReverse(Tick(0), 100, 3500, 3.0, 3.0);
            return machine;
        }

        private static Dictionary<int, List<ReverseFixturePoint>> ReadReverseFixture(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("现场回归夹具不存在。", path);
            var result = new Dictionary<int, List<ReverseFixturePoint>>();
            using (var reader = new StreamReader(path))
            {
                reader.ReadLine();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var columns = line.Split(',');
                    var channel = int.Parse(columns[0], CultureInfo.InvariantCulture);
                    var point = new ReverseFixturePoint(
                        int.Parse(columns[1], CultureInfo.InvariantCulture),
                        double.Parse(columns[2], CultureInfo.InvariantCulture));
                    if (!result.TryGetValue(channel, out var points))
                    {
                        points = new List<ReverseFixturePoint>();
                        result[channel] = points;
                    }
                    points.Add(point);
                }
            }

            return result;
        }

        private static Dictionary<int, List<StartupFixturePoint>> ReadStartupFixture(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("启动定位现场回归夹具不存在。", path);
            var result = new Dictionary<int, List<StartupFixturePoint>>();
            using (var reader = new StreamReader(path))
            {
                reader.ReadLine();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var columns = line.Split(',');
                    var channel = int.Parse(columns[0], CultureInfo.InvariantCulture);
                    var point = new StartupFixturePoint(
                        int.Parse(columns[1], CultureInfo.InvariantCulture),
                        double.Parse(columns[2], CultureInfo.InvariantCulture));
                    if (!result.TryGetValue(channel, out var points))
                    {
                        points = new List<StartupFixturePoint>();
                        result[channel] = points;
                    }
                    points.Add(point);
                }
            }

            return result;
        }

        private static EpbAdaptiveProfile StartupFieldProfile(int channel)
        {
            switch (channel)
            {
                case 4:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ForwardEmptyCurrentA = 1.7638791730371453,
                        ForwardEmptyMadA = 0.061438496255703079,
                        ForwardClampMedianMs = 2482,
                        ForwardClampMadMs = 57,
                        ValidSampleCount = 5
                    };
                case 5:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ForwardEmptyCurrentA = 1.6306128601549541,
                        ForwardEmptyMadA = 0.017286232683002334,
                        ForwardClampMedianMs = 2867,
                        ForwardClampMadMs = 39,
                        ValidSampleCount = 5
                    };
                case 9:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ForwardEmptyCurrentA = 1.9289907016652137,
                        ForwardEmptyMadA = 0.094433085513352388,
                        ForwardClampMedianMs = 2842,
                        ForwardClampMadMs = 40,
                        ValidSampleCount = 5
                    };
                case 10:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ForwardEmptyCurrentA = 2.0077799304917923,
                        ForwardEmptyMadA = 0.048709805738773149,
                        ForwardClampMedianMs = 3346,
                        ForwardClampMadMs = 141,
                        ValidSampleCount = 5
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(channel), channel, "未知现场启动定位通道");
            }
        }

        private static EpbAdaptiveProfile StartupReverseFieldProfile(int channel)
        {
            switch (channel)
            {
                case 4:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ReverseEmptyCurrentA = 0.6225766765174765,
                        ReverseEmptyMadA = 0.010721825239599969,
                        ReverseReleaseMedianMs = 1701,
                        ReverseReleaseMadMs = 16,
                        ValidSampleCount = 9
                    };
                case 5:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ReverseEmptyCurrentA = 0.633915706264814,
                        ReverseEmptyMadA = 0.015226307956014429,
                        ReverseReleaseMedianMs = 1803,
                        ReverseReleaseMadMs = 7,
                        ValidSampleCount = 5
                    };
                case 9:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ReverseEmptyCurrentA = 0.56184642405941454,
                        ReverseEmptyMadA = 0.0098082975266891026,
                        ReverseReleaseMedianMs = 2425,
                        ReverseReleaseMadMs = 22,
                        ValidSampleCount = 5
                    };
                case 10:
                    return new EpbAdaptiveProfile
                    {
                        Channel = channel,
                        ReverseEmptyCurrentA = 0.53488574720542525,
                        ReverseEmptyMadA = 0.010892402755025343,
                        ReverseReleaseMedianMs = 2065,
                        ReverseReleaseMadMs = 16,
                        ValidSampleCount = 5
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(channel), channel,
                        "未知现场启动反向通道");
            }
        }

        private static void AssertFixtureReleases(
            int channel,
            List<ReverseFixturePoint> points,
            double reverseEmptyA,
            double reverseMadA,
            int validSampleCount)
        {
            var profile = new EpbAdaptiveProfile
            {
                Channel = channel,
                ReverseEmptyCurrentA = reverseEmptyA,
                ReverseEmptyMadA = reverseMadA,
                ValidSampleCount = validSampleCount
            };
            var machine = NewReverseMachine(profile);
            var cadenceMs = new[] { 10, 10, 20, 10, 31, 10, 10, 20, 17 };
            var elapsedMs = 0;
            var cadenceIndex = 0;
            EpbAdaptiveDecision terminal = null;
            while (elapsedMs <= points[points.Count - 1].ElapsedMs)
            {
                var decision = machine.OnSample(
                    Tick(elapsedMs),
                    InterpolateFixture(points, elapsedMs));
                if (decision.ReleaseCompleted || decision.HardFault)
                {
                    terminal = decision;
                    break;
                }

                elapsedMs += cadenceMs[cadenceIndex++ % cadenceMs.Length];
            }

            Assert(terminal != null && terminal.ReleaseCompleted && !terminal.HardFault,
                $"EPB{channel}现场反向曲线未能在绝对时限前识别释放");
        }

        private static double InterpolateFixture(
            List<ReverseFixturePoint> points,
            int elapsedMs)
        {
            if (elapsedMs <= points[0].ElapsedMs) return points[0].CurrentA;
            for (var i = 1; i < points.Count; i++)
            {
                if (elapsedMs > points[i].ElapsedMs) continue;
                var previous = points[i - 1];
                var next = points[i];
                var ratio = (elapsedMs - previous.ElapsedMs) /
                            (double)(next.ElapsedMs - previous.ElapsedMs);
                return previous.CurrentA + ratio * (next.CurrentA - previous.CurrentA);
            }

            return points[points.Count - 1].CurrentA;
        }

        private sealed class ReverseFixturePoint
        {
            public ReverseFixturePoint(int elapsedMs, double currentA)
            {
                ElapsedMs = elapsedMs;
                CurrentA = currentA;
            }

            public int ElapsedMs { get; }
            public double CurrentA { get; }
        }

        private sealed class StartupFixturePoint
        {
            public StartupFixturePoint(int elapsedMs, double currentA)
            {
                ElapsedMs = elapsedMs;
                CurrentA = currentA;
            }

            public int ElapsedMs { get; }
            public double CurrentA { get; }
        }

        private static EpbAdaptiveProfile StableProfile()
        {
            var profile = new EpbAdaptiveProfile { Channel = 10 };
            for (var i = 0; i < 5; i++)
                profile.AddSuccessfulCycle(1.0, 1.0, 3000, 1200);
            return profile;
        }

        private static EpbAdaptiveDecision Feed(
            EpbAdaptiveCurrentStateMachine machine,
            int startMs,
            int endMs,
            int stepMs,
            Func<int, double> current)
        {
            EpbAdaptiveDecision action = null;
            for (var ms = startMs; ms <= endMs; ms += stepMs)
            {
                var decision = machine.OnSample(Tick(ms), current(ms));
                if (decision.ClampReached || decision.ReleaseCompleted ||
                    decision.SoftWarning || decision.HardFault)
                    action = decision;
                if (decision.ClampReached || decision.ReleaseCompleted || decision.HardFault)
                    break;
            }

            return action ?? new EpbAdaptiveDecision();
        }

        private static long Tick(int milliseconds)
        {
            // 避免 0 被状态机视为“尚未设置”的哨兵值。
            return Stopwatch.Frequency + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        }

        private static void TwoKilohertzSampleTimestamps()
        {
            var last = new DateTime(2026, 7, 30, 18, 0, 0, DateTimeKind.Utc);
            var timestamps = HighResolutionSampleClock.BuildBatchTimestamps(last, 20, 2000);
            Assert(timestamps.Length == 20, "样本数错误");
            for (var i = 1; i < timestamps.Length; i++)
                Assert(
                    timestamps[i].Ticks - timestamps[i - 1].Ticks == 5000,
                    $"样本{i}未按5000 ticks递增");
            Assert(timestamps.Distinct().Count() == timestamps.Length, "绝对时间戳出现重复");
        }

        private static void CatchUpCallbackDoesNotOverlapBatches()
        {
            var previousEnd = new DateTime(
                2026,
                7,
                31,
                11,
                29,
                40,
                DateTimeKind.Utc);
            var catchUpHostNow = previousEnd.AddTicks(778);
            var nextEnd = HighResolutionSampleClock.AdvanceBatchEnd(
                previousEnd,
                catchUpHostNow,
                20,
                2000);
            var nextBatch = HighResolutionSampleClock.BuildBatchTimestamps(
                nextEnd,
                20,
                2000);

            Assert(nextBatch[0] > previousEnd,
                "追赶回调使用过早的主机时间，导致下一批与上一批重叠");
            Assert(nextBatch[0].Ticks - previousEnd.Ticks == 5000,
                "下一批首样本未保持2kHz连续采样间隔");

            var delayedHostNow = previousEnd.AddMilliseconds(25);
            var delayedEnd = HighResolutionSampleClock.AdvanceBatchEnd(
                previousEnd,
                delayedHostNow,
                20,
                2000);
            Assert(delayedEnd == delayedHostNow,
                "明显滞后的主机时间未用于向前纠偏");
        }

        private static void HighResolutionClockReset()
        {
            var clock = new HighResolutionSampleClock();
            var firstWall = new DateTime(2026, 7, 30, 18, 0, 0, DateTimeKind.Local);
            clock.Reset(firstWall);
            var firstOrigin = clock.StartTimestamp;
            System.Threading.Thread.SpinWait(10000);

            var secondWall = firstWall.AddMinutes(1);
            clock.Reset(secondWall);
            Assert(clock.WallTime == secondWall, "重启后墙钟原点未更新");
            Assert(clock.StartTimestamp >= firstOrigin, "重启后单调时钟起点未更新");
            Assert(Math.Abs((clock.Now() - secondWall).TotalSeconds) < 1,
                "重启后仍继承上一次采集的运行时间");
        }

        private static void OverlappingCallbacksCommitInTimestampOrder()
        {
            var origin = new DateTime(2026, 7, 31, 11, 48, 0, DateTimeKind.Utc);
            var coordinator = new DeviceBatchTimestampCoordinator();
            coordinator.Reset(origin, "Dev2");

            var firstEntered = new System.Threading.ManualResetEventSlim(false);
            var releaseFirst = new System.Threading.ManualResetEventSlim(false);
            var commits = new List<DateTime>();
            var gate = new object();

            var first = Task.Run(() =>
                coordinator.AdvanceAndCommit(
                    "Dev2",
                    origin.AddMilliseconds(10),
                    20,
                    2000,
                    (_, current, __) =>
                    {
                        firstEntered.Set();
                        releaseFirst.Wait();
                        lock (gate) commits.Add(current);
                    }));

            Assert(firstEntered.Wait(1000), "首个回调未进入提交区");
            var second = Task.Run(() =>
                coordinator.AdvanceAndCommit(
                    "Dev2",
                    origin.AddMilliseconds(20),
                    20,
                    2000,
                    (_, current, __) =>
                    {
                        lock (gate) commits.Add(current);
                    }));

            System.Threading.Thread.Sleep(30);
            lock (gate)
                Assert(commits.Count == 0, "后到回调越过了仍在提交的前一批");

            releaseFirst.Set();
            Assert(Task.WaitAll(new[] { first, second }, 2000), "重叠回调提交超时");

            lock (gate)
            {
                Assert(commits.Count == 2, "提交批次数错误");
                Assert(commits[1] > commits[0], "批次入队顺序与时间戳顺序不一致");
                var secondBatch = HighResolutionSampleClock.BuildBatchTimestamps(
                    commits[1],
                    20,
                    2000);
                Assert(secondBatch[0] > commits[0], "相邻批次仍发生时间重叠");
            }
        }

        private static void NewProjectIsIsolatedAndReset()
        {
            var root = CreateTempDir();
            try
            {
                var oldRoot = Path.Combine(root, "old");
                var oldConfigDir = Path.Combine(oldRoot, "Config");
                Directory.CreateDirectory(oldConfigDir);
                var oldConfig = Path.Combine(oldConfigDir, "TestConfig.xml");
                var xml = "<TestConfig><Basic><TestName>old</TestName><StoreDir>" +
                          root +
                          "</StoreDir></Basic></TestConfig>";
                File.WriteAllText(oldConfig, xml);
                var oldBytes = File.ReadAllBytes(oldConfig);
                var oldDb = Path.Combine(oldRoot, "index.db");
                File.WriteAllBytes(oldDb, new byte[] { 1, 2, 3, 4 });

                var source = new TestConfig
                {
                    TestName = "old",
                    StoreDir = root,
                    TestPeriod = 15,
                    TestTarget = 20,
                    LearnCycles = 10
                };
                source.EnsureEpbRecords();
                foreach (var record in source.EpbRecords)
                {
                    record.Enabled = true;
                    record.TotalCount = 20;
                    record.RunCount = 7;
                    record.Status = EpbTestStatus.Running;
                    record.RunTimeSpan = TimeSpan.FromMinutes(2);
                }

                var created = ConfigLoader.CreateNewProjectTestConfig(
                    source,
                    oldConfig,
                    root,
                    "new");

                Assert(created.EpbRecords.All(record =>
                        !record.Enabled &&
                        record.RunCount == 0 &&
                        record.Status == EpbTestStatus.NotStarted &&
                        record.RunTimeSpan == TimeSpan.Zero &&
                        record.TotalCount == 20),
                    "新项目未按未勾选、零进度初始化");
                Assert(File.ReadAllBytes(oldConfig).SequenceEqual(oldBytes), "旧项目配置被改写");
                Assert(File.ReadAllBytes(oldDb).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                    "旧项目数据库被改写");
                Assert(File.Exists(Path.Combine(root, "new", "Config", "TestConfig.xml")),
                    "新项目配置未创建");

                Directory.CreateDirectory(Path.Combine(root, "residual"));
                var residualWasBlocked = false;
                try
                {
                    ConfigLoader.CreateNewProjectTestConfig(
                        source,
                        oldConfig,
                        root,
                        "residual");
                }
                catch (IOException)
                {
                    residualWasBlocked = true;
                }
                Assert(residualWasBlocked, "缺少TestConfig.xml的残留目录未被阻止");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch
                {
                    // Test cleanup must not hide the assertion result.
                }
            }
        }

        private static void InitialSummarySelectsFirstStarted()
        {
            var records = Enumerable.Range(1, 12)
                .Select(id => EpbTestRecord.CreateDefault(id, 20))
                .ToList();
            records[7].Enabled = true;
            records[8].Enabled = true;
            records[9].Enabled = true;
            records[7].Status = EpbTestStatus.Running;
            records[8].Status = EpbTestStatus.Running;
            records[9].Status = EpbTestStatus.Running;

            Assert(EpbProjectPolicies.FindInitialSummaryChannel(records) == 8,
                "未选择编号最小的实际启动通道");
        }

        private static void SummaryAdvancesAfterCompletion()
        {
            var records = Enumerable.Range(1, 12)
                .Select(id => EpbTestRecord.CreateDefault(id, 20))
                .ToList();
            foreach (var id in new[] { 8, 9, 10 })
            {
                records[id - 1].Enabled = true;
                records[id - 1].Status = EpbTestStatus.Running;
                records[id - 1].RunCount = 1;
            }

            records[7].RunCount = 20;
            records[7].Status = EpbTestStatus.Completed;
            Assert(EpbProjectPolicies.FindSummaryChannelAfterCompletion(records, 8) == 9,
                "当前通道完成后未选择最小未完成启动通道");
            Assert(EpbProjectPolicies.FindSummaryChannelAfterCompletion(records, 10) == 10,
                "未完成的当前通道不应被其他通道抢占");

            records[8].RunCount = records[9].RunCount = 20;
            records[8].Status = records[9].Status = EpbTestStatus.Completed;
            Assert(EpbProjectPolicies.FindSummaryChannelAfterCompletion(records, 10) == 10,
                "全部完成后应保留最后显示项");
        }

        private static void UiLogFloodStaysBoundedAndBatched()
        {
            const int capacity = 512;
            const int producerCount = 8;
            const int linesPerProducer = 12500;
            var queue = new BoundedConcurrentQueue<string>(capacity);
            var clock = Stopwatch.StartNew();
            var producers = Enumerable.Range(0, producerCount)
                .Select(producer => Task.Run(() =>
                {
                    for (var line = 0; line < linesPerProducer; line++)
                        queue.Enqueue($"P{producer:D2}-{line:D5}");
                }))
                .ToArray();
            Assert(Task.WaitAll(producers, 10000), "十万条UI日志生产在10秒内未完成");
            clock.Stop();

            var totalProduced = producerCount * linesPerProducer;
            var retainedBeforeDrain = queue.Count;
            Assert(retainedBeforeDrain <= capacity,
                $"UI日志队列超过容量：Count={retainedBeforeDrain} Capacity={capacity}");
            Assert(queue.DroppedCount == totalProduced - retainedBeforeDrain,
                $"UI日志丢弃计数不一致：Produced={totalProduced} " +
                $"Retained={retainedBeforeDrain} Dropped={queue.DroppedCount}");

            var drained = 0;
            var maxBatch = 0;
            while (queue.Count > 0)
            {
                var batch = 0;
                while (batch < 50 && queue.TryDequeue(out _)) batch++;
                maxBatch = Math.Max(maxBatch, batch);
                drained += batch;
            }
            Assert(drained == retainedBeforeDrain, "UI日志批量消费发生静默丢失");
            Assert(maxBatch <= 50, $"UI单次日志消费超过50行：{maxBatch}");
            Console.WriteLine(
                $"METRIC UiLogFlood Produced={totalProduced} Retained={retainedBeforeDrain} " +
                $"Dropped={queue.DroppedCount} ProducerMs={clock.Elapsed.TotalMilliseconds:F1}");
        }

        private static void DhmsFormatting()
        {
            Assert(EpbTestRecord.FormatDHMS(TimeSpan.FromSeconds(9)) == "00D 00H 00M 09S",
                "不足一分钟格式错误");
            Assert(EpbTestRecord.FormatDHMS(new TimeSpan(0, 0, 4, 49)) == "00D 00H 04M 49S",
                "4分49秒格式错误");
            Assert(EpbTestRecord.FormatDHMS(new TimeSpan(0, 2, 3, 4)) == "00D 02H 03M 04S",
                "跨小时格式错误");
            Assert(EpbTestRecord.FormatDHMS(new TimeSpan(2, 3, 4, 5)) == "02D 03H 04M 05S",
                "跨天格式错误");
        }

        private static void EpbSelectionPropagatesOneWay()
        {
            var state = EpbProjectPolicies.ApplySettingsSelection(true);
            Assert(state.SettingsEnabled && state.PowerSelected && state.CurveSelected,
                "设置勾选未传播到电源和曲线");

            state = EpbProjectPolicies.ApplyCurveSelection(state, false);
            Assert(state.SettingsEnabled && state.PowerSelected && !state.CurveSelected,
                "曲线变化错误反写了上游");

            state = EpbProjectPolicies.ApplyPowerSelection(state, false);
            Assert(state.SettingsEnabled && !state.PowerSelected && !state.CurveSelected,
                "电源变化未驱动曲线或错误反写设置");
        }

        private static string CreateTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "EPBAdaptiveTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private sealed class TransactionalRunner : IEpbCycleRunner
        {
            private EpbAdaptiveProfile _profile;

            public TransactionalRunner(EpbAdaptiveProfile profile)
            {
                _profile = profile?.Clone() ?? new EpbAdaptiveProfile { Channel = 1 };
            }

            public void ReplaceModel(EpbAdaptiveProfile profile) => _profile = profile?.Clone();
            public Controller.Adaptive.EpbCycleOutcome LastCycleOutcome { get; } = new Controller.Adaptive.EpbCycleOutcome();
            public Controller.Adaptive.FormalCycleFaultCommitResult CommitFormalCycleFaultEvidence(Guid testRunId, int cycleNumber)
                => new Controller.Adaptive.FormalCycleFaultCommitResult();
            public Task<Controller.Adaptive.EpbCycleOutcome> RunOneAdaptiveLearningAsync(int targetPeriodMs, CancellationToken token)
                => Task.FromResult(LastCycleOutcome);
            public EpbAdaptiveProfile CaptureAdaptiveProfile() => _profile?.Clone();
            public void RestoreAdaptiveProfile(EpbAdaptiveProfile snapshot) => _profile = snapshot?.Clone();
            public bool UseNoHeadPhase { get; set; }
            public bool EnableTailCompensation { get; set; }
            public int TailMinMs { get; set; }
            public Task<bool> LearnOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, CancellationToken token)
                => Task.FromResult(false);
            public Task<bool> RunOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, DateTime deadlineUtc, CancellationToken token)
                => Task.FromResult(false);
            public void BeginLearnAggregation() { }
            public Task<EpbCycleRunner.LearnSample> LearnOneAlignedCoreAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, CancellationToken token)
                => Task.FromResult<EpbCycleRunner.LearnSample>(null);
            public void ApplyLearnSample(EpbCycleRunner.LearnSample sample) { }
            public void FinalizeLearnAggregation() { }
            public int DefaultPreReleaseDetectTimeoutMs => 3000;
            public Task<bool> PreReleaseAsync(int? keepMs, CancellationToken token) => Task.FromResult(false);
            public Task<bool> PreReleaseAsync(int? keepMs, int? detectTimeoutMs, CancellationToken token) => Task.FromResult(false);
            public void BeginSafetyMarginLearning() { }
            public void FinalizeSafetyMarginLearning() { }
        }

        private sealed class CollectingLogger : Config.IAppLogger
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public void Info(string message, string category = null) { }
            public void Warn(string message, string category = null)
            {
                Warnings.Add(message ?? string.Empty);
            }
            public void Error(string message, string category = null, Exception ex = null)
            {
                Errors.Add(message ?? string.Empty);
            }
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
