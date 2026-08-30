using System;
using System.IO;
using System.Xml;
using Config;
using Controller;
using Controller.Adaptive;

namespace AdaptiveControlTests
{
    internal static class NonRecoverableAlarmPolicyTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("16.9A不计永久过冲且17.0A计数", OvershootBoundaryIsInclusive, ref passed);
            Run("低平台第8个完整提交圈才锁存", LowPlateauLatchesAfterEighthCommit, ref passed);
            Run("过冲第8个完整提交圈才锁存", OvershootLatchesAfterEighthCommit, ref passed);
            Run("已完成永久报警使用不可变触发圈号", CompletedAlarmKeepsConfirmedTerminalCycle, ref passed);
            Run("反向或落盘失败不得提交异常圈", IncompleteCycleCannotCommitEvidence, ref passed);
            Run("近零电流仅当前运行隔离且其余客户报警持久禁用",
                CustomerFaultsDisableWithoutRecovery, ref passed);
            Run("高负载停滞只允许人工完整重学习", HighLoadStallRequiresOperatorRelearning, ref passed);
            Run("持久禁用只改Enabled且保留已落盘圈数", PersistentDisablePreservesDiskProgress, ref passed);
            Run("共享故障组一次原子禁用且失败不部分提交", PersistentDisableGroupIsAtomic, ref passed);
            Run("重复XML通道记录在锁存与清除后保持一致",
                DuplicateAlarmRecordsUpdateTogether, ref passed);
            return passed;
        }

        private static void OvershootBoundaryIsInclusive()
        {
            Assert(!FormalCycleFaultPolicy.IsPermanentOvershoot(16.9, 15.0, 2.0),
                "16.9A被错误计入目标+2A永久报警");
            Assert(FormalCycleFaultPolicy.IsPermanentOvershoot(17.0, 15.0, 2.0),
                "17.0A边界未计入目标+2A永久报警");
            Assert(FormalCycleFaultPolicy.IsPermanentOvershoot(18.2, 15.0, 2.0),
                "超过目标3A的完整峰值未按一次目标+2A事件累计");
        }

        private static void LowPlateauLatchesAfterEighthCommit()
        {
            var profile = new EpbAdaptiveProfile { Channel = 3 };
            var runId = Guid.NewGuid();
            for (var cycle = 1; cycle <= 7; cycle++)
            {
                var result = FormalCycleFaultPolicy.Commit(
                    profile,
                    SuccessfulOutcome(lowPlateau: true),
                    runId,
                    cycle,
                    8,
                    8);
                Assert(!result.ShouldLatchAlarm && result.ForwardLowPlateauStreak == cycle,
                    $"低平台第{cycle}圈被过早锁存或计数错误");
            }

            var eighth = FormalCycleFaultPolicy.Commit(
                profile,
                SuccessfulOutcome(lowPlateau: true),
                runId,
                8,
                8,
                8);
            Assert(eighth.ShouldLatchAlarm &&
                   eighth.AlarmCode == "ForwardLowPlateauConfirmed" &&
                   eighth.AlarmReason.Contains("Streak=8/8"),
                "低平台第8个完整提交圈未锁存稳定报警码");

            var resetProfile = new EpbAdaptiveProfile { Channel = 3 };
            FormalCycleFaultPolicy.Commit(resetProfile, SuccessfulOutcome(true), runId, 1, 8, 8);
            var normal = FormalCycleFaultPolicy.Commit(resetProfile, SuccessfulOutcome(false), runId, 2, 8, 8);
            Assert(normal.ForwardLowPlateauStreak == 0, "正常完整圈未清零低平台连续数");
        }

        private static void OvershootLatchesAfterEighthCommit()
        {
            var profile = new EpbAdaptiveProfile { Channel = 4 };
            var runId = Guid.NewGuid();
            FormalCycleFaultCommitResult result = null;
            for (var cycle = 1; cycle <= 8; cycle++)
            {
                result = FormalCycleFaultPolicy.Commit(
                    profile,
                    SuccessfulOutcome(overshoot: true),
                    runId,
                    cycle,
                    8,
                    8);
                Assert(result.ShouldLatchAlarm == (cycle == 8),
                    $"永久过冲第{cycle}圈锁存时机错误");
            }
            Assert(result != null &&
                   result.AlarmCode == "ForwardPeakOvershoot2AConfirmed" &&
                   result.AlarmReason.Contains("Peak=17.000A"),
                "永久过冲第8圈缺少完整峰值证据");
        }

        private static void IncompleteCycleCannotCommitEvidence()
        {
            var profile = new EpbAdaptiveProfile { Channel = 5 };
            var incomplete = SuccessfulOutcome(lowPlateau: true);
            incomplete.Kind = EpbCycleOutcomeKind.HardFault;
            var threw = false;
            try
            {
                FormalCycleFaultPolicy.Commit(profile, incomplete, Guid.NewGuid(), 8, 8, 8);
            }
            catch (InvalidOperationException) { threw = true; }
            Assert(threw && profile.ConsecutiveForwardStallCount == 0,
                "不完整圈被伪计为正式低平台圈");
        }

        private static void CompletedAlarmKeepsConfirmedTerminalCycle()
        {
            Assert(EpbManager.ResolveAlarmSnapshotCycle(33552, 0, 0) == 33552,
                "正式圈提交后活动圈已清除时丢失永久报警触发圈号");
            Assert(EpbManager.ResolveAlarmSnapshotCycle(33552, 33553, 33551) == 33552,
                "明确触发圈被迟到活动圈或旧冻结圈覆盖");
            Assert(EpbManager.ResolveAlarmSnapshotCycle(0, 42, 41) == 42,
                "运行中报警未优先冻结当前活动圈");
            Assert(EpbManager.ResolveAlarmSnapshotCycle(0, 0, 41) == 41,
                "异步报警未保留已冻结圈号");
        }

        private static void CustomerFaultsDisableWithoutRecovery()
        {
            foreach (var code in new[]
                     {
                         "ForwardLoadRiseNotStarted",
                         "ForwardLowPlateauConfirmed",
                         "ForwardPeakOvershoot2AConfirmed"
                     })
                Assert(
                    EpbManager.ResolveChannelFaultRecoveryPolicy(code, false) ==
                    FaultRecoveryPolicy.NonRecoverableDisableChannel,
                    $"{code} 未使用不可恢复持久禁用策略");
            Assert(
                EpbManager.ResolveChannelFaultRecoveryPolicy(
                    "OpenCircuitOrOutputFault",
                    false) == FaultRecoveryPolicy.CurrentRunDisableChannel,
                "近零电流未使用当前进程隔离策略");
            Assert(
                EpbManager.ResolveChannelFaultRecoveryPolicy("DaqSampleStale", false) ==
                FaultRecoveryPolicy.Recoverable,
                "无关软件故障被错误改为不可恢复");
            Assert(
                EpbManager.ResolveChannelFaultRecoveryPolicy("OutputCommandFailed", true) ==
                FaultRecoveryPolicy.Recoverable,
                "输出控制链故障被错误锁存为卡钳硬件故障");
        }

        private static void HighLoadStallRequiresOperatorRelearning()
        {
            Assert(EpbManager.RequiresOperatorFullRelearning(
                    "ForwardUnderTargetHighLoadStall",
                    "Peak=10.8A"),
                "稳定故障码未进入人工完整重学习门禁");
            Assert(EpbManager.RequiresOperatorFullRelearning(
                    "ChannelFault",
                    "Reason=ForwardUnderTargetHighLoadStall"),
                "嵌套故障原因未进入人工完整重学习门禁");
            Assert(!EpbManager.RequiresOperatorFullRelearning(
                    "DaqSampleStale",
                    "software transient"),
                "纯软件瞬态被错误升级为人工完整重学习");
        }

        private static void PersistentDisablePreservesDiskProgress()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "epb-disable-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var source = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
                File.Copy(source, path);
                var config = ConfigLoader.LoadTest(path, null);
                config.EnsureEpbRecords(12);
                config.GetEpbRecord(6).Enabled = true;
                config.GetEpbRecord(6).RunCount = 77;
                ConfigLoader.SaveTest(path, config);

                // 模拟报警线程持有尚未完成UI圈数同步的旧内存值。
                config.GetEpbRecord(6).RunCount = 0;
                ConfigLoader.UpdateTestEpbEnabled(path, 6, false);
                var reloaded = ConfigLoader.LoadTest(path, null);
                Assert(!reloaded.GetEpbRecord(6).Enabled &&
                       reloaded.GetEpbRecord(6).RunCount == 77,
                    "单通道禁用覆盖了磁盘上已提交的正式圈数");
                Assert(Directory.GetFiles(directory, "*.tmp").Length == 0,
                    "单通道原子禁用遗留临时文件");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void PersistentDisableGroupIsAtomic()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "epb-group-disable-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var source = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
                File.Copy(source, path);
                var config = ConfigLoader.LoadTest(path, null);
                config.EnsureEpbRecords(12);
                foreach (var channel in new[] { 4, 9, 10, 11 })
                    config.GetEpbRecord(channel).Enabled = true;
                config.GetEpbRecord(9).RunCount = 901;
                config.GetEpbRecord(10).RunCount = 1001;
                config.GetEpbRecord(11).RunCount = 1101;
                ConfigLoader.SaveTest(path, config);

                ConfigLoader.UpdateTestEpbEnabled(path, new[] { 9, 10, 11 }, false);
                var reloaded = ConfigLoader.LoadTest(path, null);
                Assert(reloaded.GetEpbRecord(4).Enabled &&
                       !reloaded.GetEpbRecord(9).Enabled &&
                       !reloaded.GetEpbRecord(10).Enabled &&
                       !reloaded.GetEpbRecord(11).Enabled &&
                       reloaded.GetEpbRecord(9).RunCount == 901 &&
                       reloaded.GetEpbRecord(10).RunCount == 1001 &&
                       reloaded.GetEpbRecord(11).RunCount == 1101,
                    "共享故障组禁用牵连健康通道或覆盖耐久圈数");

                foreach (var channel in new[] { 9, 10, 11 })
                    reloaded.GetEpbRecord(channel).Enabled = true;
                ConfigLoader.SaveTest(path, reloaded);
                var xml = new XmlDocument();
                xml.Load(path);
                var missing = xml.SelectSingleNode(
                    "/TestConfig/EpbRecords/Record[Id='11']");
                missing?.ParentNode?.RemoveChild(missing);
                xml.Save(path);
                var before = File.ReadAllText(path);
                var threw = false;
                try
                {
                    ConfigLoader.UpdateTestEpbEnabled(path, new[] { 9, 11 }, false);
                }
                catch (InvalidOperationException) { threw = true; }
                Assert(threw && string.Equals(before, File.ReadAllText(path), StringComparison.Ordinal),
                    "组内记录缺失时仍部分禁用了其他通道");
                Assert(Directory.GetFiles(directory, "*.tmp").Length == 0,
                    "故障组原子禁用遗留临时文件");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void DuplicateAlarmRecordsUpdateTogether()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "epb-duplicate-alarm-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "TestConfig.xml");
            try
            {
                var source = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
                File.Copy(source, path);
                var xml = new XmlDocument();
                xml.Load(path);
                var original = xml.SelectSingleNode(
                    "/TestConfig/EpbRecords/Record[Id='4']") as XmlElement;
                Assert(original != null, "基准配置缺少EPB4");
                original.ParentNode.AppendChild(original.CloneNode(true));
                xml.Save(path);

                ConfigLoader.UpdateTestEpbAlarmState(path, new[]
                {
                    new EpbAlarmPersistenceUpdate
                    {
                        Channel = 4,
                        Enabled = false,
                        PermanentAlarmLatched = true,
                        PermanentAlarmCode = "DuplicateLatch",
                        PermanentAlarmReason = "test",
                        PermanentAlarmUtc = DateTime.UtcNow,
                        PermanentAlarmCorrelationId = Guid.NewGuid(),
                        ConsecutivePeriodOverrunCount = 8,
                        LastPeriodOverrunUtc = DateTime.UtcNow
                    }
                });
                AssertDuplicateAlarmState(path, true, false, 8, "DuplicateLatch");

                ConfigLoader.UpdateTestEpbAlarmState(path, new[]
                {
                    new EpbAlarmPersistenceUpdate
                    {
                        Channel = 4,
                        Enabled = true,
                        PermanentAlarmLatched = false,
                        ConsecutivePeriodOverrunCount = 0
                    }
                });
                AssertDuplicateAlarmState(path, false, true, 0, string.Empty);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void AssertDuplicateAlarmState(
            string path,
            bool latched,
            bool enabled,
            int streak,
            string code)
        {
            var xml = new XmlDocument();
            xml.Load(path);
            var nodes = xml.SelectNodes("/TestConfig/EpbRecords/Record[Id='4']");
            Assert(nodes != null && nodes.Count == 2, "重复EPB4测试记录丢失");
            foreach (XmlNode node in nodes)
                Assert(
                    string.Equals(node.SelectSingleNode("PermanentAlarmLatched")?.InnerText,
                        latched ? "True" : "False", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(node.SelectSingleNode("Enabled")?.InnerText,
                        enabled ? "True" : "False", StringComparison.OrdinalIgnoreCase) &&
                    node.SelectSingleNode("ConsecutivePeriodOverrunCount")?.InnerText ==
                    streak.ToString() &&
                    (node.SelectSingleNode("PermanentAlarmCode")?.InnerText ?? string.Empty) == code,
                    "同一通道的重复XML记录状态不一致");
        }

        private static EpbCycleOutcome SuccessfulOutcome(
            bool lowPlateau = false,
            bool overshoot = false)
        {
            return new EpbCycleOutcome
            {
                Kind = EpbCycleOutcomeKind.SuccessWithWarning,
                Stage = EpbCurrentStage.Released,
                PeakCurrentA = overshoot ? 17.0 : 14.1,
                TargetCurrentA = 15.0,
                PeakErrorA = overshoot ? 2.0 : -0.9,
                EstimatedSlopeAperMs = 0.0009,
                ForwardAcceptableFloorA = 14.2,
                PermanentOvershootDeltaA = 2.0,
                ForwardLowPlateauCandidate = lowPlateau,
                ForwardPermanentOvershootCandidate = overshoot
            };
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
