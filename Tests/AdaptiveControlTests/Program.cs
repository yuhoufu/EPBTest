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

                Run("正常夹紧", NormalClamp);
                Run("学习尾部提前量后预测夹紧", LearnedTailLeadPredictsClamp);
                Run("低斜率不提前误触发", LowSlopeDoesNotPredictEarly);
                Run("未识别负载上升前到阈值立即停机", ThresholdBeforeLoadRiseFaults);
                Run("夹紧阈值必须连续三样本确认", ClampNeedsThreeSamples);
                Run("稳定模型后连续50圈仍记录正向空行程", StableProfileKeepsLearningForFiftyCycles);
                Run("正向未进入负载上升按模型期限硬停", ForwardLoadRiseDeadlineFaults);
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
                Run("预释放6.5A平台按15A目标不误报", PreReleaseNormalPlatformUsesForwardReference);
                Run("预释放持续超过9A触发高平台保护", PreReleaseHighPlatformStillFaults);
                Run("预释放失败阻止学习阶段", PreReleaseFailureBlocksLearning);
                Run("保持阶段不误报断流", HoldDoesNotFault);
                Run("三样本过流", ThreeSampleOverCurrent);
                Run("开路检测", OpenCircuit);
                Run("DAQ断流", DaqStale);
                Run("单点噪声不误停", NoiseSpike);
                Run("绝对上电超限", AbsoluteOnTime);
                Run("异常高电流平台", AbnormalHighPlateau);
                Run("终态断电先于阻塞诊断发布", TerminalOffPrecedesBlockingDiagnostics);
                Run("DO失败与电流未清零触发组级联锁", OffFailureEscalatesToPowerGroup);
                Run("断电电流在窗口内清零不联锁且超时只失败一次", OffCurrentPollingWindow);
                Run("断电清零阈值适配现场零偏且保持安全上限", OffCurrentThresholdTracksTrustedBaseline);
                Run("项目XML不再保存程序级安全参数", ProjectXmlIgnoresProgramSafetySettings);
                Run("EXE安全配置缺失非法时使用安全默认值", ProgramSafetySettingsValidation);
                Run("程序安全配置快照包含值与来源", ProgramSafetySnapshotIsAuditable);
                Run("报警配置加载正向低平台连续5圈", AlarmConfigLoadsForwardStallConfirmation);
                Run("旧项目100ms断电清零配置自动迁移", LegacyShortOffTimeoutIsMigrated);
                Run("旧项目液压安全节点使用默认值并在保存时补齐", LegacyHydraulicSafetyDefaultsAreCompleted);
                Run("液压目标压力默认正负5bar判定", HydraulicPressureToleranceWindow);
                Run("模型原子保存与重载", ProfilePersistence);
                Run("控流模型五圈收敛到目标带", CutoffModelConvergesWithinFiveCycles);
                Run("峰值系统偏差用于提前断电补偿", PeakBiasCorrectionIsLearned);
                Run("偶发超调不累计为连续硬故障", OvershootStreakRequiresConsecutiveCycles);
                Run("正向低平台连续5圈确认且正常圈清零", ForwardStallStreakRequiresFiveCycles);
                Run("版本1模型无损升级到版本3", VersionOneProfileMigrates);
                Run("损坏模型回退", CorruptProfileFallback);
                Run("周期超限不追赶且圈号连续", TimerDoesNotCatchUp);
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
                Run("12通道并发首次创建运行对象", TwelveChannelsCreateRuntimesConcurrently);
                Run("12通道错过锚点同时放行仍可安全创建", OverdueTwelveChannelReleaseCreatesSafely);
                Run("同通道并发只创建一个运行对象", SameChannelCreatesExactlyOneRuntime);
                Run("12通道启动停止抢占不损坏运行表", ConcurrentStartStopDoesNotCorruptRuntimeStore);
                Run("12通道连续启动停止不残留运行对象", TwelveChannelsRestartWithoutRuntimeLeaks);
                Run("DO追踪缓冲按运行过滤并限时", DoTraceBufferFiltersRunAndAge);
                Run("报警辅助证据包含计划和DO时序", AlarmControlEvidenceIsReconstructable);
                Run("同组硬故障仅停止故障通道", HardFaultDoesNotStopSiblingChannel);
                Run("2000Hz样本时间严格递增5000 ticks", TwoKilohertzSampleTimestamps);
                Run("DAQ追赶回调不造成相邻批时间重叠", CatchUpCallbackDoesNotOverlapBatches);
                Run("峰值令牌拒绝跨圈和跨运行身份", PeakCaptureTokenRejectsCrossCycleIdentity);
                Run("DAQ重叠回调保持时间分配与入队同序", OverlappingCallbacksCommitInTimestampOrder);
                Run("采集重启重建高精度时基", HighResolutionClockReset);
                Run("新项目清零且不改旧项目", NewProjectIsIsolatedAndReset);
                Run("进度摘要默认选择最小已启动通道", InitialSummarySelectsFirstStarted);
                Run("进度摘要完成后切换且全完成保持", SummaryAdvancesAfterCompletion);
                Run("EPB勾选仅按设置到电源到曲线单向传播", EpbSelectionPropagatesOneWay);
                Run("DHMS运行时间格式", DhmsFormatting);
                Run("固定随机种子10万圈耐久仿真", HundredThousandCycleDurabilitySimulation);
                _passed += HydraulicGroupCoordinatorTests.RunAll();
                _passed += PowerSupplyCoordinatorTests.RunAll();
                _passed += ProjectLogStoreTests.RunAll();
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

        private static void ThresholdBeforeLoadRiseFaults()
        {
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 6000, 15, 1, 3);
            Feed(machine, 0, 90, 10, _ => 1.0);
            var fault = machine.OnSample(Tick(100), 15.0);
            Assert(
                fault.HardFault && fault.Reason.Contains("ThresholdBeforeLoadRise"),
                "未形成完整负载上升曲线时到达阈值没有立即停机");
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
            // 首次完全释放后的学习圈尚无稳定模型，应使用配置的3000ms空行程期限。
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
            Assert(fault.HardFault && fault.Reason.Contains("OverCurrent3Samples"), "三样本过流未触发");
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
                Math.Abs(missing.PeakEvidenceMismatchToleranceA - 1.0) < 1e-9,
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
                ["EpbPeakEvidenceMismatchToleranceA"] = "0.5"
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
                Math.Abs(normalized.PeakEvidenceMismatchToleranceA - 1.0) < 1e-9,
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
                    xml.Contains("policyVersion=\"2026.08.03.1\"") &&
                    xml.Contains("key=\"EpbForwardProgressConfirmMs\" value=\"1200\" source=\"appSettings\"") &&
                    xml.Contains("key=\"EpbForwardProgressDeadlineMs\" value=\"6000\" source=\"appSettings\"") &&
                    xml.Contains("key=\"EpbReverseProgressConfirmMs\" value=\"200\" source=\"compiled-default\"") &&
                    xml.Contains("key=\"EpbPeakEvidenceMismatchToleranceA\" value=\"1\" source=\"compiled-default\""),
                    "程序安全快照未完整记录策略版本、生效值和来源");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
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
                loaded.Behavior.AdaptiveForwardStallConfirmCycles == 5 &&
                loaded.WarningSnapshots.Enabled &&
                loaded.WarningSnapshots.SaveCsv &&
                loaded.WarningSnapshots.SaveBin &&
                loaded.WarningSnapshots.HardAlarmLastNCycles == 10 &&
                loaded.WarningSnapshots.SoftWarningQuotaMb == 0 &&
                loaded.WarningSnapshots.DiskFreeWarningMb == 10240,
                "AlarmConfig.xml 未加载连续阈值或预警快照安全默认值");
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
                        Math.Abs(x.PressureToleranceBar - 5) < 1e-9 &&
                        Math.Abs(x.HoldDropToleranceBar - 5) < 1e-9 &&
                        x.HoldDropConfirmMs == 100 && x.BarrierTimeoutMs == 0),
                    "旧项目缺少液压安全节点时未使用安全默认值");
                ConfigLoader.SaveTest(target, loaded);
                var saved = File.ReadAllText(target);
                Assert(saved.Contains("<BuildTimeoutMs>5000</BuildTimeoutMs>") &&
                       saved.Contains("<PressureToleranceBar>5</PressureToleranceBar>") &&
                       saved.Contains("<PressureSampleMaxAgeMs>100</PressureSampleMaxAgeMs>") &&
                       saved.Contains("<BarrierTimeoutMs>0</BarrierTimeoutMs>"),
                    "保存旧项目时未补齐液压安全节点");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void HydraulicPressureToleranceWindow()
        {
            Assert(HydraulicController.IsPressureWithinTarget(67.922, 70, 5),
                "70bar目标下67.922bar应在默认±5bar窗口内");
            Assert(HydraulicController.IsPressureWithinTarget(65, 70, 5),
                "容差窗口下边界应合格");
            Assert(HydraulicController.IsPressureWithinTarget(75, 70, 5),
                "容差窗口上边界应合格");
            Assert(!HydraulicController.IsPressureWithinTarget(64.999, 70, 5),
                "低于容差窗口不应合格");
            Assert(!HydraulicController.IsPressureWithinTarget(75.001, 70, 5),
                "高于容差窗口不应合格");
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
                profile.TryAddCutoffObservation(15.0, 14.5, 0.05, 15.0, out _);
                profile.UpdateForwardStallStreak(true);
                profile.UpdateForwardStallStreak(true);
                store.Save(profile);

                var loaded = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(loaded.ValidSampleCount == 5, "模型样本数未持久化");
                Assert(loaded.IsStable, "五圈后模型未进入稳定状态");
                Assert(loaded.ModelVersion == 3, "控流模型未保存为版本3");
                Assert(loaded.ValidCutoffSampleCount == 1, "控流样本数未持久化");
                Assert(loaded.ConsecutiveForwardStallCount == 2, "正向低平台连续计数未持久化");
                Assert(Math.Abs(loaded.ForwardCutoffLeadMedianMs - 10.0) < 0.01,
                    "控流提前时间未持久化");
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
            Assert(profile.UpdateForwardOvershootStreak(0.812, 0.8) == 1,
                "首次边界超调未记录为1圈");
            Assert(profile.UpdateForwardOvershootStreak(0.3, 0.8) == 0,
                "回到平衡带后连续计数未清零");
            Assert(profile.UpdateForwardOvershootStreak(0.9, 0.8) == 1,
                "连续超调第1圈计数错误");
            Assert(profile.UpdateForwardOvershootStreak(0.95, 0.8) == 2,
                "连续超调第2圈计数错误");
            Assert(profile.UpdateForwardOvershootStreak(0.85, 0.8) == 3,
                "连续超调第3圈未达到确认值");
        }

        private static void ForwardStallStreakRequiresFiveCycles()
        {
            var profile = StableProfile();
            for (var cycle = 1; cycle <= 4; cycle++)
            {
                Assert(
                    profile.UpdateForwardStallStreak(true) == cycle,
                    $"正向低平台第{cycle}圈连续计数错误");
                Assert(
                    profile.ConsecutiveForwardStallCount < 5,
                    $"正向低平台第{cycle}圈被过早确认为硬故障");
                Assert(
                    !EpbCycleRunner.IsForwardStallConfirmed(
                        profile.ConsecutiveForwardStallCount,
                        5),
                    $"正向低平台第{cycle}圈被升级策略过早确认");
            }

            Assert(
                profile.UpdateForwardStallStreak(true) == 5,
                "正向低平台第5圈未达到硬故障确认值");
            Assert(
                EpbCycleRunner.IsForwardStallConfirmed(
                    profile.ConsecutiveForwardStallCount,
                    5),
                "正向低平台第5圈未被升级策略确认");
            Assert(
                profile.UpdateForwardStallStreak(false) == 0,
                "正常或近目标圈未清零正向低平台连续计数");
            Assert(
                profile.UpdateForwardStallStreak(true) == 1,
                "清零后的下一次低平台未从1重新计数");
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
                Assert(loaded.IsStable && loaded.ValidSampleCount == 5, "版本1有效样本未保留");
                Assert(Math.Abs(loaded.ForwardEmptyCurrentA - 1.1) < 0.001,
                    "版本1空行程基线未保留");
                Assert(loaded.ValidCutoffSampleCount == 0 && !loaded.HasCutoffPrediction,
                    "版本1模型错误地产生控流学习数据");

                loaded.TryAddCutoffObservation(15.0, 14.5, 0.05, 15.0, out _);
                store.Save(loaded);
                var migrated = new EpbAdaptiveProfileStore(dir).GetOrCreate(10);
                Assert(migrated.ModelVersion == 3, "版本1模型首次控流保存后未升级");
                Assert(migrated.ValidSampleCount == 5 && migrated.ValidCutoffSampleCount == 1,
                    "模型升级破坏原有样本或新增控流样本");
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

            ElectricalStaggerExecutor.RunAsync(
                    channels,
                    plan,
                    DateTime.UtcNow.AddSeconds(-10),
                    (channel, token) =>
                    {
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
                "错过锚点同时放行后未创建全部12个通道");
            Assert(Enumerable.Range(1, 12).All(channel => factoryCounts[channel] == 1),
                "错过锚点同时放行造成重复创建");
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

                Assert(metadata.Contains("\"schemaVersion\": 4") &&
                       metadata.Contains("\"terminalOffSafety\":"),
                    "报警元数据未升级到包含终态断电证据的schema 4");
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
