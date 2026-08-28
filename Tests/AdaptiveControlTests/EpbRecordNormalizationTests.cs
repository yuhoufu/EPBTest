using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Config;

namespace AdaptiveControlTests
{
    internal static class EpbRecordNormalizationTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("重复EPB记录合并单调证据与启用授权", DuplicateRecordsMergeMonotonicEvidence, ref passed);
            Run("加载重复EPB配置时归一化并记录诊断", LoadTestNormalizesDuplicateRecords, ref passed);
            Run("保存边界禁止重复EPB记录回写", SaveTestWritesCanonicalRecords, ref passed);
            Run("并发Ensure仍保持十二通道唯一", ConcurrentEnsureIsIdempotent, ref passed);
            Run("归一化后恶意重复插入仍不能污染启动计划",
                DuplicateReinsertionCannotCorruptStartPlan, ref passed);
            Run("永久报警与连续超限次数保存后可恢复", PermanentAlarmRoundTrips, ref passed);
            Run("完整重学习门禁跨进程往返并可原子清除",
                OperatorFullRelearningGateRoundTrips, ref passed);
            Run("零正式圈永久报警初始化后仍保持报警", ZeroRunPermanentAlarmSurvivesInitialization, ref passed);
            Run("旧项目仅禁用报警状态迁移为永久报警", LegacyDisabledAlarmMigrationIsNarrow, ref passed);
            return passed;
        }

        private static void DuplicateRecordsMergeMonotonicEvidence()
        {
            var config = new TestConfig { TestTarget = 100000 };
            var historical = EpbTestRecord.CreateDefault(4, 100000);
            historical.Enabled = true;
            historical.RunCount = 73195;
            historical.RunTimeSpan = TimeSpan.FromDays(12);
            historical.StartTime = new DateTime(2026, 8, 4, 14, 24, 11);
            historical.LatestStartTime = new DateTime(2026, 8, 19, 11, 51, 12);

            var generatedDefault = EpbTestRecord.CreateDefault(4, 100000);
            generatedDefault.Enabled = false;
            generatedDefault.RunCount = 73195;
            generatedDefault.StartTime = new DateTime(2026, 8, 19, 11, 55, 57);
            generatedDefault.LatestStartTime = generatedDefault.StartTime;

            config.EpbRecords.Add(historical);
            config.EpbRecords.Add(generatedDefault);
            config.EnsureEpbRecords(12);

            var merged = config.GetEpbRecord(4);
            Assert(config.EpbRecords.Count == 12 &&
                   config.EpbRecords.Select(record => record.Id).Distinct().Count() == 12,
                "归一化后不是1..12每通道唯一");
            Assert(merged.Enabled, "重复记录合并时丢失了已启用授权");
            Assert(merged.RunCount == 73195 && merged.MechanicalCycleCount == 73195,
                "重复记录合并时破坏了完成计数");
            Assert(merged.RunTimeSpan == TimeSpan.FromDays(12),
                "重复记录合并时被默认副本清空了累计时长");
            Assert(merged.StartTime == historical.StartTime &&
                   merged.LatestStartTime == historical.LatestStartTime,
                "重复记录合并时未保留较强历史记录的时间证据");
        }

        private static void LoadTestNormalizesDuplicateRecords()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "TestConfig.xml");
                File.WriteAllText(path, BuildDuplicateConfigXml());
                var logger = new CollectingLogger();

                var config = ConfigLoader.LoadTest(path, logger);

                Assert(config.EpbRecords.Count == 12 &&
                       config.EpbRecords.Select(record => record.Id).SequenceEqual(
                           Enumerable.Range(1, 12)),
                    "加载层未在控制器初始化前归一化重复记录");
                Assert(config.GetEpbRecord(4).Enabled && config.GetEpbRecord(9).Enabled,
                    "加载归一化丢失了分布在不同副本中的启用授权");
                Assert(logger.Warnings.Any(message =>
                        message.Contains("DuplicateIds=[4,9]") &&
                        message.Contains("InvalidCount=1")),
                    "加载归一化没有输出可定位的重复Id诊断");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void SaveTestWritesCanonicalRecords()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "TestConfig.xml");
                File.WriteAllText(path, BuildDuplicateConfigXml());
                var config = new TestConfig { TestTarget = 100000 };
                foreach (var id in Enumerable.Range(1, 12))
                {
                    config.EpbRecords.Add(EpbTestRecord.CreateDefault(id, 100000));
                    config.EpbRecords.Add(EpbTestRecord.CreateDefault(id, 100000));
                }

                ConfigLoader.SaveTest(path, config);

                var doc = new XmlDocument();
                doc.Load(path);
                var ids = doc.SelectNodes("/TestConfig/EpbRecords/Record/Id")
                    .Cast<XmlNode>()
                    .Select(node => int.Parse(node.InnerText))
                    .ToArray();
                Assert(ids.SequenceEqual(Enumerable.Range(1, 12)),
                    "保存边界仍把重复Id写入TestConfig");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void ConcurrentEnsureIsIdempotent()
        {
            var config = new TestConfig();
            Parallel.For(0, 64, _ => config.EnsureEpbRecords(12));
            Assert(config.EpbRecords.Count == 12 &&
                   config.EpbRecords.Select(record => record.Id).Distinct().Count() == 12,
                "并发Ensure生成了重复通道记录");
        }

        private static void DuplicateReinsertionCannotCorruptStartPlan()
        {
            var config = new TestConfig { TestTarget = 100000 };
            config.EnsureEpbRecords(12);
            var duplicate = EpbTestRecord.CreateDefault(4, 100000);
            duplicate.Enabled = true;
            duplicate.RunCount = 321;
            duplicate.MechanicalCycleCount = 321;

            Parallel.For(0, 128, _ => config.EpbRecords.Add(duplicate));
            var plan = config.CreateEpbStartPlan(config.TestTarget, 12);

            Assert(config.EpbRecords.Count == 12 &&
                   config.EpbRecords.Select(record => record.Id).Distinct().Count() == 12,
                "唯一集合仍允许归一化后的并发重复插入");
            Assert(plan.Count == 12 && plan.Keys.OrderBy(id => id)
                       .SequenceEqual(Enumerable.Range(1, 12)) &&
                   plan[4] == 99679,
                "原子StartPlan存在重复/缺失键或没有保留单调完成证据");
        }

        private static void PermanentAlarmRoundTrips()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "TestConfig.xml");
                File.WriteAllText(path, "<TestConfig />");
                var config = new TestConfig
                {
                    TestName = "alarm-roundtrip",
                    StoreDir = dir,
                    TestTarget = 100
                };
                config.EnsureEpbRecords(12);
                var correlationId = Guid.NewGuid();
                var record = config.GetEpbRecord(3);
                record.ConsecutivePeriodOverrunCount = 7;
                record.LastPeriodOverrunUtc = new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc);
                record.LatchPermanentAlarm(
                    "ConsecutivePeriodOverrun",
                    "连续8次超限",
                    new DateTime(2026, 8, 28, 1, 3, 4, DateTimeKind.Utc),
                    correlationId);

                ConfigLoader.SaveTest(path, config);
                var reloaded = ConfigLoader.LoadTest(path, NullLogger.Instance);
                var actual = reloaded.GetEpbRecord(3);
                Assert(actual.PermanentAlarmLatched && !actual.Enabled &&
                       actual.PermanentAlarmCode == "ConsecutivePeriodOverrun" &&
                       actual.PermanentAlarmCorrelationId == correlationId &&
                       actual.ConsecutivePeriodOverrunCount == 7 &&
                       actual.LastPeriodOverrunUtc.HasValue,
                    "永久报警或连续超限字段未完整往返");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void OperatorFullRelearningGateRoundTrips()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "TestConfig.xml");
                File.WriteAllText(path, "<TestConfig />");
                var config = new TestConfig
                {
                    TestName = "relearning-roundtrip",
                    StoreDir = dir,
                    TestTarget = 100
                };
                config.EnsureEpbRecords(12);
                var correlationId = Guid.NewGuid();
                var utc = new DateTime(2026, 8, 28, 2, 3, 4, DateTimeKind.Utc);
                config.GetEpbRecord(12).RequireOperatorFullRelearning(
                    "ForwardUnderTargetHighLoadStall",
                    utc,
                    correlationId);

                ConfigLoader.SaveTest(path, config);
                var loaded = ConfigLoader.LoadTest(path, NullLogger.Instance);
                var actual = loaded.GetEpbRecord(12);
                Assert(actual.OperatorFullRelearningRequired &&
                       actual.OperatorFullRelearningReason.Contains(
                           "ForwardUnderTargetHighLoadStall") &&
                       actual.OperatorFullRelearningCorrelationId == correlationId,
                    "完整重学习门禁未跨保存/加载恢复");

                ConfigLoader.UpdateTestEpbAlarmState(path, new[]
                {
                    new EpbAlarmPersistenceUpdate
                    {
                        Channel = 12,
                        PermanentAlarmLatched = actual.PermanentAlarmLatched,
                        PermanentAlarmCode = actual.PermanentAlarmCode,
                        PermanentAlarmReason = actual.PermanentAlarmReason,
                        PermanentAlarmUtc = actual.PermanentAlarmUtc,
                        PermanentAlarmCorrelationId = actual.PermanentAlarmCorrelationId,
                        OperatorFullRelearningRequired = false,
                        ConsecutivePeriodOverrunCount =
                            actual.ConsecutivePeriodOverrunCount,
                        LastPeriodOverrunUtc = actual.LastPeriodOverrunUtc
                    }
                });
                var cleared = ConfigLoader.LoadTest(path, NullLogger.Instance)
                    .GetEpbRecord(12);
                Assert(!cleared.OperatorFullRelearningRequired &&
                       string.IsNullOrEmpty(cleared.OperatorFullRelearningReason) &&
                       cleared.OperatorFullRelearningCorrelationId == Guid.Empty,
                    "资格通过后的原子更新未清除完整重学习门禁");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void LegacyDisabledAlarmMigrationIsNarrow()
        {
            var dir = CreateTempDir();
            try
            {
                var path = Path.Combine(dir, "TestConfig.xml");
                File.WriteAllText(
                    path,
                    "<TestConfig><Basic><TestName>legacy</TestName><TestTarget>10</TestTarget>" +
                    "<IsSameCycleForAllEpb>true</IsSameCycleForAllEpb><TestCycle>15</TestCycle>" +
                    "<LearnCycle>10</LearnCycle><StoreDir>C:\\Temp</StoreDir></Basic>" +
                    "<EpbRecords>" +
                    RecordXmlWithStatus(1, false, "Alarm") +
                    RecordXmlWithStatus(2, false, "NotStarted") +
                    RecordXmlWithStatus(3, true, "Alarm") +
                    "</EpbRecords></TestConfig>");

                var loaded = ConfigLoader.LoadTest(path, NullLogger.Instance);
                Assert(loaded.GetEpbRecord(1).PermanentAlarmLatched &&
                       loaded.GetEpbRecord(1).PermanentAlarmCode == "LegacyDisabledAlarm",
                    "Enabled=false + Alarm 未迁移");
                Assert(!loaded.GetEpbRecord(2).PermanentAlarmLatched,
                    "普通未启用通道被错误反推为永久报警");
                Assert(!loaded.GetEpbRecord(3).PermanentAlarmLatched,
                    "仍启用的旧报警状态不应迁移为禁用永久报警");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void ZeroRunPermanentAlarmSurvivesInitialization()
        {
            var record = EpbTestRecord.CreateDefault(1, 100);
            record.Enabled = true;
            record.LatchPermanentAlarm(
                "StartupPositioningFault",
                "启动定位硬件故障",
                DateTime.UtcNow,
                Guid.NewGuid());

            record.InitializeOnLoad(DateTime.Now);

            Assert(record.RunCount == 0 && record.PermanentAlarmLatched &&
                   !record.Enabled && record.Status == EpbTestStatus.Alarm,
                "零正式圈永久报警被加载初始化降级为未开始");
        }

        private static string RecordXmlWithStatus(int id, bool enabled, string status)
        {
            return $"<Record><Id>{id}</Id><Enabled>{enabled}</Enabled>" +
                   "<RunTime>0.00:00:00</RunTime><TotalCount>10</TotalCount>" +
                   $"<RunCount>0</RunCount><Status>{status}</Status></Record>";
        }

        private static string BuildDuplicateConfigXml()
        {
            return
                "<TestConfig><Basic><TestName>duplicate</TestName><TestTarget>100000</TestTarget>" +
                "<IsSameCycleForAllEpb>true</IsSameCycleForAllEpb><TestCycle>15</TestCycle>" +
                "<LearnCycle>10</LearnCycle><StoreDir>C:\\Temp</StoreDir></Basic>" +
                // 现场类故障既可能是一个容器内重复，也可能是多个同名容器被旧保存逻辑遗留。
                "<EpbRecords>" +
                RecordXml(4, true, 73195, "12.07:53:15", "2026-08-04 14:24:11") +
                RecordXml(9, false, 73286, "0.00:00:00", "2026-08-19 11:55:57") +
                "</EpbRecords><EpbRecords>" +
                RecordXml(4, false, 73195, "0.00:00:00", "2026-08-19 11:55:57") +
                RecordXml(9, true, 73286, "12.08:15:42", "2026-08-04 14:24:11") +
                RecordXml(99, true, 1, "0.00:00:15", "2026-08-19 11:55:57") +
                "</EpbRecords></TestConfig>";
        }

        private static string RecordXml(
            int id,
            bool enabled,
            int runCount,
            string runTime,
            string start)
        {
            return $"<Record><Id>{id}</Id><Enabled>{enabled}</Enabled>" +
                   $"<StartTime>{start}</StartTime><LatestStartTime>{start}</LatestStartTime>" +
                   $"<RunTime>{runTime}</RunTime><TotalCount>100000</TotalCount>" +
                   $"<RunCount>{runCount}</RunCount><Status>NotStarted</Status></Record>";
        }

        private static string CreateTempDir()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "EpbRecordNormalizationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempDir(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
                // Cleanup must not hide the assertion result.
            }
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

        private sealed class CollectingLogger : IAppLogger
        {
            public readonly List<string> Warnings = new List<string>();

            public void Info(string message, string category = null) { }

            public void Warn(string message, string category = null)
            {
                Warnings.Add(message ?? string.Empty);
            }

            public void Error(string message, string category = null, Exception ex = null) { }
        }
    }
}
