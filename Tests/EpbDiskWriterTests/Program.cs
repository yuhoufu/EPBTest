using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Data.SQLite;
using System.Threading.Tasks;
using DataOperation;

namespace EpbDiskWriterTests
{
    internal static class Program
    {
        private static int _passed;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 4 &&
                    args[0].Equals("--recover", StringComparison.OrdinalIgnoreCase))
                    return RecoverCycles(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), args[3]);

                Run("重启后写指针连续", RestartRestoresWritePosition);
                Run("running 圈重启后不覆盖", RestartAfterRunningCycle);
                Run("串圈数据整圈拒绝且无半成品", MixedCycleIsRejectedAtomically);
                Run("样本序号跳变被拒绝", SampleIndexJumpIsRejected);
                Run("旧时间戳回退仍可完整导出", LegacyTimestampRollbackStillExports);
                Run("环形数据覆盖后从历史快照恢复", HistoricalSnapshotRecoversOverwrittenCycle);
                Run("归档失败不删除索引", FailedArchiveKeepsIndex);
                Run("所有 CSV 出口包含相对时间", AllCsvExportsContainRelativeTime);
                Run("2000Hz CSV保留0.5ms时间分辨率", TwoKilohertzCsvKeepsSubMillisecondTime);
                Run("报警圈原子封存与数据库边界一致", AlarmSealMatchesDatabaseBoundary);
                Run("DAQ时钟恢复圈状态独立封存", DaqClockRecoveryAbortStatusIsDurable);
                Run("软件自愈作废圈状态独立封存", SoftwareRecoveryAbortStatusIsDurable);
                Run("软件自愈作废圈可导出警告证据", SoftwareRecoveryAbortCanExportEvidence);
                Run("报警CSV和BIN不一致时校验失败", AlarmPairValidatorRejectsMismatch);
                Run("学习与资格圈终态均落盘且不改变正式计数", LearningOutcomesDoNotAffectFormalCounters);
                Run("学习负圈号跨重启连续且唯一", LearningCycleNumbersSurviveRestart);
                Run("报警与学习收尾并发只封存一次", ConcurrentSealClaimsOnce);
                Run("正式圈保留策略不删除学习索引", FormalRetentionKeepsLearningRows);
                Run("按通道圈号精确导出完整证据", ExactCompletedCycleExport);
                Run("活动圈样本硬上限阻止覆盖", ActiveCycleSampleLimitStopsWrites);
                Run("连续100次活动圈超限均原子作废且无running遗留", HundredActiveCycleLimitFaultsLeaveNoRunningRows);
                Run("批量时间窗边界与重启恢复", BatchedWindowBoundarySurvivesRestart);
                Run("设备多通道批次事务写入", DeviceBatchWritesMultipleChannels);
                Run("Latest并发导出原子且无临时残留", ConcurrentLatestExportsAreAtomic);
                Console.WriteLine($"PASS {_passed}/{_passed}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static int RecoverCycles(string indexRoot, int epbId, string outputDir)
        {
            var scratch = Path.Combine(
                Path.GetTempPath(),
                "EpbDiskWriterRecovery",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                File.Copy(
                    Path.Combine(indexRoot, "index.db"),
                    Path.Combine(scratch, "index.db"),
                    true);

                var snapshotRoot = Path.Combine(scratch, "HistoricalSnapshots");
                Directory.CreateDirectory(snapshotRoot);
                var snapshotIndex = 0;
                foreach (var source in Directory.EnumerateFiles(
                             indexRoot,
                             $"EPB{epbId}_Cycle_*.bin",
                             SearchOption.AllDirectories))
                {
                    var candidateDir = Path.Combine(
                        snapshotRoot,
                        snapshotIndex.ToString("D6", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(candidateDir);
                    File.Copy(source, Path.Combine(candidateDir, Path.GetFileName(source)), true);
                    snapshotIndex++;
                }

                var policy = new DataRetentionPolicy
                {
                    DataStorePath = scratch,
                    IndexAndExportPath = scratch,
                    IndexDbFile = "index.db",
                    FileSizeMb = 1,
                    RetainAllData = true
                };
                using (var writer = new EpbDiskWriter(policy))
                    writer.ExportLatestCyclesTo(epbId, 10, outputDir, false);

                Console.WriteLine($"RECOVERED EPB[{epbId}] -> {outputDir}");
                return 0;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                }
                catch
                {
                    // 临时环形文件不影响恢复结果。
                }
            }
        }

        private static void RestartRestoresWritePosition()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using (var writer = new EpbDiskWriter(policy))
                    WriteCompletedCycle(writer, 1, 1, 3, DateTime.UtcNow);

                using (var writer = new EpbDiskWriter(policy))
                {
                    WriteCompletedCycle(writer, 1, 2, 2, DateTime.UtcNow.AddSeconds(10));
                    var exportDir = Path.Combine(root, "export");
                    writer.ExportLatestCyclesTo(1, 10, exportDir, false);
                    AssertCsvCycle(exportDir, 1, 1, 3);
                    AssertCsvCycle(exportDir, 1, 2, 2);
                }
            });
        }

        private static void RestartAfterRunningCycle()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(1, 1, DateTime.UtcNow);
                    WriteSamples(writer, 1, 3, DateTime.UtcNow);
                }

                using (var writer = new EpbDiskWriter(policy))
                {
                    WriteCompletedCycle(writer, 1, 2, 2, DateTime.UtcNow.AddSeconds(10));
                    var exportDir = Path.Combine(root, "export");
                    writer.ExportLatestCyclesTo(1, 10, exportDir, true);
                    AssertCsvCycle(exportDir, 1, 1, 3);
                    AssertCsvCycle(exportDir, 1, 2, 2);
                }
            });
        }

        private static void MixedCycleIsRejectedAtomically()
        {
            WithCorruptedCycle(
                (stream, _) =>
                {
                    stream.Position = 8;
                    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
                    writer.Write(99);
                },
                exportDir =>
                {
                    Assert(!File.Exists(CsvPath(exportDir, 1, 1)), "校验失败后仍生成 CSV");
                    Assert(!File.Exists(BinPath(exportDir, 1, 1)), "校验失败后仍生成 BIN");
                    Assert(File.Exists(Path.Combine(exportDir, "export_errors.txt")), "未生成导出错误清单");
                });
        }

        private static void SampleIndexJumpIsRejected()
        {
            WithCorruptedCycle(
                (stream, _) =>
                {
                    stream.Position = SampleRecord.Size + 12;
                    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
                    writer.Write(42);
                },
                _ => { });
        }

        private static void LegacyTimestampRollbackStillExports()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                    WriteCompletedCycle(writer, 1, 1, 3, start);

                var datPath = Path.Combine(policy.DataStorePath, "EPB1_sliding.dat");
                using (var stream = new FileStream(datPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Position = SampleRecord.Size;
                    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
                    writer.Write(start.AddSeconds(-1).ToLocalTime().ToBinary());
                }

                using (var writer = new EpbDiskWriter(policy))
                {
                    var exportDir = Path.Combine(root, "export");
                    writer.ExportLatestCyclesTo(1, 10, exportDir, false);
                    AssertMonotonicRelativeTime(CsvPath(exportDir, 1, 1), 3);
                }
            });
        }

        private static void HistoricalSnapshotRecoversOverwrittenCycle()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    WriteCompletedCycle(writer, 1, 1, 3, start);
                    writer.ExportLatestCyclesTo(
                        1,
                        1,
                        Path.Combine(policy.IndexAndExportPath, "Historical"),
                        false);
                }

                var datPath = Path.Combine(policy.DataStorePath, "EPB1_sliding.dat");
                using (var stream = new FileStream(datPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Position = 8;
                    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
                    writer.Write(99);
                }

                using (var writer = new EpbDiskWriter(policy))
                {
                    var exportDir = Path.Combine(root, "recovered");
                    writer.ExportLatestCyclesTo(1, 1, exportDir, false);
                    AssertCsvCycle(exportDir, 1, 1, 3);
                }
            });
        }

        private static void FailedArchiveKeepsIndex()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    for (var cycle = 1; cycle <= 11; cycle++)
                        WriteCompletedCycle(writer, 1, cycle, 1, start.AddSeconds(cycle));
                }

                var datPath = Path.Combine(policy.DataStorePath, "EPB1_sliding.dat");
                using (var stream = new FileStream(datPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                using (var binary = new BinaryWriter(stream))
                {
                    stream.Position = 8;
                    binary.Write(99);
                }

                using (var writer = new EpbDiskWriter(policy))
                {
                    ExpectExportFailure(() => writer.PersistLatestCyclesNow(1, 10, "archive"));
                    Assert(writer.GetClosedCycleCount(1) == 11, "归档失败后错误删除了圈级索引");
                }
            });
        }

        private static void AllCsvExportsContainRelativeTime()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    WriteCompletedCycle(writer, 1, 1, 3, start);
                    var exportDir = Path.Combine(root, "export");
                    writer.ExportLatestCyclesTo(1, 1, exportDir, false);

                    var importedCsv = Path.Combine(root, "imported.csv");
                    writer.ImportBinToCsv(BinPath(exportDir, 1, 1), importedCsv);
                    AssertRelativeTimeCsv(importedCsv, 3);

                    writer.StartFreeRun(2);
                    WriteSamples(writer, 2, 3, start);
                    writer.StopFreeRun(2);
                    var freeRunCsv = Path.Combine(root, "free-run.csv");
                    writer.ExportFreeRunBySamples(2, 3, freeRunCsv);
                    AssertRelativeTimeCsv(freeRunCsv, 3);
                }
            });
        }

        private static void TwoKilohertzCsvKeepsSubMillisecondTime()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = new DateTime(2026, 7, 30, 18, 0, 0, DateTimeKind.Utc);
                var timestamps = Enumerable.Range(0, 20)
                    .Select(index => start.AddTicks(index * 5000L))
                    .ToArray();
                var currents = Enumerable.Range(0, 20).Select(index => (double)index).ToArray();
                var pressures = Enumerable.Repeat(70.0, 20).ToArray();

                string csvPath;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(1, 1, start);
                    writer.WriteBatch(1, timestamps, currents, pressures);
                    writer.CompleteCycle(1, 1, 20, timestamps[19]);
                    var exportDir = Path.Combine(root, "export");
                    writer.ExportLatestCyclesTo(1, 1, exportDir, false);
                    csvPath = CsvPath(exportDir, 1, 1);
                }

                var rows = File.ReadAllLines(csvPath).Skip(1).ToArray();
                Assert(rows.Length == 20, "2000Hz CSV样本数错误");
                var absolute = rows.Select(row => row.Split(',')[0]).ToArray();
                Assert(absolute.Distinct().Count() == absolute.Length, "CSV绝对时间戳重复");
                for (var i = 0; i < rows.Length; i++)
                {
                    var relative = rows[i].Split(',')[1];
                    var expected = (i * 0.0005).ToString("F7", CultureInfo.InvariantCulture);
                    Assert(relative == expected, $"CSV相对时间错误：{relative} != {expected}");
                }
            });
        }

        private static void ExactCompletedCycleExport()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using var writer = new EpbDiskWriter(policy);
                WriteCompletedCycle(writer, 3, 41, 4, start);
                WriteCompletedCycle(writer, 3, 42, 6, start.AddSeconds(2));
                var evidence = writer.ExportCompletedCycleTo(
                    3,
                    41,
                    Path.Combine(root, "exact"),
                    true,
                    true);
                Assert(evidence.IsCompleteCycle, "精确导出的圈未标记为完整圈");
                Assert(evidence.SampleCount == 4, "精确导出错误混入相邻圈样本");
                Assert(File.Exists(evidence.CsvPath) && File.Exists(evidence.BinPath),
                    "精确导出未生成 CSV/BIN");
            });
        }

        private static void ActiveCycleSampleLimitStopsWrites()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                policy.MaxActiveCycleRecords = 3;
                using var writer = new EpbDiskWriter(policy);
                writer.BeginCycle(4, 7, DateTime.UtcNow);
                WriteSamples(writer, 4, 3, DateTime.UtcNow);
                var thrown = false;
                try
                {
                    WriteSamples(writer, 4, 1, DateTime.UtcNow.AddSeconds(1));
                }
                catch (ActiveCycleDataLimitExceededException)
                {
                    thrown = true;
                }
                Assert(thrown, "达到活动圈样本硬上限后仍继续接收数据");
                Assert(writer.GetCurrentCycleSampleCount(4) == 3, "越界批次写入了半成品样本");
            });
        }

        private static void HundredActiveCycleLimitFaultsLeaveNoRunningRows()
        {
            WithRoot(root =>
            {
                const int epbId = 8;
                const int limit = 1;
                var policy = NewPolicy(root);
                policy.MaxActiveCycleRecords = limit;
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    for (var injection = 1; injection <= 100; injection++)
                    {
                        var cycleStart = start.AddSeconds(injection);
                        writer.BeginCycle(epbId, injection, cycleStart);
                        WriteSamples(writer, epbId, limit, cycleStart);
                        try
                        {
                            writer.WriteSample(
                                epbId,
                                cycleStart.AddMilliseconds(10),
                                1,
                                100);
                            throw new InvalidOperationException(
                                $"第{injection}次故障注入未触发活动圈上限");
                        }
                        catch (ActiveCycleDataLimitExceededException ex)
                        {
                            Assert(ex.EpbId == epbId &&
                                   ex.CycleNumber == injection &&
                                   ex.Limit == limit,
                                $"第{injection}次活动圈上限身份字段错误");
                        }

                        writer.AbortCycle(
                            epbId,
                            injection,
                            writer.GetCurrentCycleSampleCount(epbId),
                            cycleStart.AddMilliseconds(20),
                            "AbortedBySoftwareRecovery");
                        Assert(writer.GetCurrentCycleSampleCount(epbId) == 0,
                            $"第{injection}次清场后仍有活动圈样本");
                    }
                }

                using var connection = OpenIndex(policy);
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT " +
                    "SUM(CASE WHEN status='running' THEN 1 ELSE 0 END), " +
                    "SUM(CASE WHEN status='AbortedBySoftwareRecovery' THEN 1 ELSE 0 END) " +
                    "FROM epb_cycles WHERE epb_id=8";
                using var reader = command.ExecuteReader();
                Assert(reader.Read(), "100次活动圈故障注入没有生成索引证据");
                Assert(reader.GetInt32(0) == 0, "100次活动圈故障注入后仍有running遗留");
                Assert(reader.GetInt32(1) == 100, "100次活动圈故障未全部作废提交");
            });
        }

        private static void WithCorruptedCycle(
            Action<FileStream, DateTime> corrupt,
            Action<string> verify)
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                    WriteCompletedCycle(writer, 1, 1, 3, start);

                var datPath = Path.Combine(policy.DataStorePath, "EPB1_sliding.dat");
                using (var stream = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    corrupt(stream, start);

                using (var writer = new EpbDiskWriter(policy))
                {
                    var exportDir = Path.Combine(root, "export");
                    ExpectExportFailure(() => writer.ExportLatestCyclesTo(1, 10, exportDir, false));
                    verify(exportDir);
                }
            });
        }

        private static DataRetentionPolicy NewPolicy(string root)
        {
            return new DataRetentionPolicy
            {
                DataStorePath = Path.Combine(root, "data"),
                IndexAndExportPath = Path.Combine(root, "index"),
                IndexDbFile = "index.db",
                FileSizeMb = 1,
                RetainAllData = true
            };
        }

        private static void AlarmSealMatchesDatabaseBoundary()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                var exportDir = Path.Combine(root, "alarm");
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(9, 63, start);
                    WriteSamples(writer, 9, 7, start);
                    var evidence = writer.SealAndExportAlarmCycle(
                        9,
                        63,
                        exportDir,
                        start.AddSeconds(1));
                    Assert(evidence.IsValid, "报警圈原子封存失败：" + evidence.ValidationError);
                    Assert(evidence.SampleCount == 7, "报警证据样本数未冻结为实际导出边界。");
                    Assert(evidence.LastSampleUtc.HasValue, "报警证据缺少末样本时间。");
                }

                using (var connection = new SQLiteConnection(
                           $"Data Source={Path.Combine(policy.IndexAndExportPath, policy.IndexDbFile)}"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT sample_count,status,end_time FROM epb_cycles WHERE epb_id=9 AND cycle_number=63";
                    using var reader = command.ExecuteReader();
                    Assert(reader.Read(), "报警圈数据库记录不存在。");
                    Assert(reader.GetInt32(0) == 7, "数据库样本数与报警证据不一致。");
                    Assert(reader.GetString(1) == "alarm", "完整证据未封存为 alarm。");
                    Assert(!reader.IsDBNull(2), "报警圈数据库缺少末样本时间。");
                }
            });
        }

        private static void AlarmPairValidatorRejectsMismatch()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                var exportDir = Path.Combine(root, "alarm");
                AlarmCycleSnapshotEvidence evidence;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(10, 63, start);
                    WriteSamples(writer, 10, 6, start);
                    evidence = writer.SealAndExportAlarmCycle(
                        10,
                        63,
                        exportDir,
                        start.AddSeconds(1));
                }

                Assert(evidence.IsValid, "测试前置报警证据未通过校验。");
                var csvLines = File.ReadAllLines(evidence.CsvPath);
                File.WriteAllLines(evidence.CsvPath, csvLines.Take(csvLines.Length - 1));
                var invalid = EpbDiskWriter.ValidateAlarmCycleSnapshotPair(
                    evidence.CsvPath,
                    evidence.BinPath,
                    10,
                    63);
                Assert(!invalid.IsValid && invalid.ValidationError.Contains("样本数不一致"),
                    "CSV/BIN 数量不一致未被拒绝。");
            });
        }

        private static void DaqClockRecoveryAbortStatusIsDurable()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(4, 150, start);
                    WriteSamples(writer, 4, 5, start);
                    writer.AbortCycle(
                        4,
                        150,
                        5,
                        start.AddSeconds(1),
                        "AbortedByDaqClockRecovery");
                    Assert(writer.GetClosedCycleCount(4) == 0,
                        "DAQ时钟恢复中止圈被错误计入合格寿命圈");
                }

                using (var connection = new SQLiteConnection(
                           $"Data Source={Path.Combine(policy.IndexAndExportPath, policy.IndexDbFile)}"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT sample_count,status FROM epb_cycles WHERE epb_id=4 AND cycle_number=150";
                    using var reader = command.ExecuteReader();
                    Assert(reader.Read(), "DAQ时钟恢复中止圈数据库记录不存在");
                    Assert(reader.GetInt32(0) == 5, "DAQ时钟恢复中止圈样本边界错误");
                    Assert(reader.GetString(1) == "AbortedByDaqClockRecovery",
                        "DAQ时钟恢复中止圈状态被降级为普通failed");
                }
            });
        }

        private static void SoftwareRecoveryAbortStatusIsDurable()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(4, 151, start);
                    WriteSamples(writer, 4, 7, start);
                    writer.AbortCycle(
                        4,
                        151,
                        7,
                        start.AddSeconds(1),
                        "AbortedBySoftwareRecovery");
                    Assert(writer.GetClosedCycleCount(4) == 0,
                        "软件自愈作废圈被错误计入合格寿命圈");
                }

                using (var connection = new SQLiteConnection(
                           $"Data Source={Path.Combine(policy.IndexAndExportPath, policy.IndexDbFile)}"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT sample_count,status FROM epb_cycles WHERE epb_id=4 AND cycle_number=151";
                    using var reader = command.ExecuteReader();
                    Assert(reader.Read(), "软件自愈作废圈数据库记录不存在");
                    Assert(reader.GetInt32(0) == 7, "软件自愈作废圈样本边界错误");
                    Assert(reader.GetString(1) == "AbortedBySoftwareRecovery",
                        "软件自愈作废圈状态被降级为普通failed");
                }
            });
        }

        private static void SoftwareRecoveryAbortCanExportEvidence()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                var exportDir = Path.Combine(root, "warning-attempt");
                using var writer = new EpbDiskWriter(policy);
                writer.BeginCycle(5, 152, start);
                WriteSamples(writer, 5, 7, start);
                writer.AbortCycle(
                    5,
                    152,
                    7,
                    start.AddSeconds(1),
                    "AbortedBySoftwareRecovery");
                var evidence = writer.ExportCycleAttemptTo(5, 152, exportDir, true, true);
                Assert(
                    !evidence.IsCompleteCycle &&
                    evidence.SampleCount == 7 &&
                    File.Exists(evidence.CsvPath) &&
                    File.Exists(evidence.BinPath),
                    "软件自愈作废圈未导出完整CSV/BIN警告证据");
            });
        }

        private static void LearningOutcomesDoNotAffectFormalCounters()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                var statuses = new[]
                {
                    "learning_completed",
                    "learning_canceled",
                    "learning_failed",
                    "qualification_completed",
                    "qualification_canceled",
                    "qualification_failed"
                };
                var learningCycles = new List<int>();
                using (var writer = new EpbDiskWriter(policy))
                {
                    for (var i = 0; i < statuses.Length; i++)
                    {
                        var cycle = writer.BeginLearningCycle(8, start.AddSeconds(i));
                        learningCycles.Add(cycle);
                        if (!statuses[i].EndsWith("_canceled", StringComparison.Ordinal))
                            WriteSamples(writer, 8, 3 + i, start.AddSeconds(i));
                        var evidence = writer.SealAndExportCycle(
                            8,
                            cycle,
                            Path.Combine(root, "LearningCycles", $"Learning_{i + 1:D4}"),
                            start.AddSeconds(i + 1),
                            statuses[i]);
                        Assert(evidence.WasClaimed && evidence.IsValid,
                            $"学习圈 {statuses[i]} 未生成一致CSV/BIN：{evidence.ValidationError}");
                    }

                    var alarmCycle = writer.BeginLearningCycle(8, start.AddSeconds(10));
                    learningCycles.Add(alarmCycle);
                    WriteSamples(writer, 8, 5, start.AddSeconds(10));
                    var alarm = writer.SealAndExportAlarmCycle(
                        8,
                        alarmCycle,
                        Path.Combine(root, "AlarmSnapshots"),
                        start.AddSeconds(11));
                    Assert(alarm.WasClaimed && alarm.IsValid, "学习硬故障报警圈未原子封存");

                    WriteCompletedCycle(writer, 8, 1, 4, start.AddSeconds(20));
                    Assert(writer.GetMaxCycleNumber(8) == 1, "学习负圈号改变了正式最大圈号");
                    Assert(writer.GetClosedCycleCount(8) == 1, "学习终态改变了正式成功计数");

                    var latest = Path.Combine(root, "Latest");
                    writer.ExportLatestCyclesTo(8, 10, latest, false);
                    AssertCsvCycle(latest, 8, 1, 4);
                    Assert(
                        Directory.EnumerateFiles(latest, "*", SearchOption.AllDirectories)
                            .All(path => !Path.GetFileName(path).Contains("-00000")),
                        "最近正式圈导出混入了学习负圈号");
                }

                using var connection = OpenIndex(policy);
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT cycle_number,status FROM epb_cycles WHERE epb_id=8 ORDER BY id";
                using var reader = command.ExecuteReader();
                var rows = new List<Tuple<int, string>>();
                while (reader.Read())
                    rows.Add(Tuple.Create(reader.GetInt32(0), reader.GetString(1)));
                Assert(rows.Count == 8, "学习、资格与正式圈索引数量错误");
                Assert(rows.All(row => row.Item2 != "running"), "学习结束后仍有 running 悬挂行");
                Assert(rows.Count(row => row.Item1 < 0) == 7, "学习/资格负圈号数量错误");
                Assert(rows.Any(row => row.Item2 == "learning_completed") &&
                       rows.Any(row => row.Item2 == "learning_canceled") &&
                       rows.Any(row => row.Item2 == "learning_failed") &&
                       rows.Any(row => row.Item2 == "qualification_completed") &&
                       rows.Any(row => row.Item2 == "qualification_canceled") &&
                       rows.Any(row => row.Item2 == "qualification_failed") &&
                       rows.Any(row => row.Item2 == "alarm"),
                    "学习/资格圈终态未完整写入SQLite");
            });
        }

        private static void LearningCycleNumbersSurviveRestart()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                int first;
                using (var writer = new EpbDiskWriter(policy))
                {
                    first = writer.BeginLearningCycle(9, start);
                    WriteSamples(writer, 9, 2, start);
                    writer.SealAndExportCycle(
                        9,
                        first,
                        Path.Combine(root, "run1"),
                        start.AddSeconds(1),
                        "learning_completed");
                }

                using (var writer = new EpbDiskWriter(policy))
                {
                    var second = writer.BeginLearningCycle(9, start.AddSeconds(2));
                    Assert(first == -1 && second == -2, "学习负圈号跨重启未连续递减");
                    WriteSamples(writer, 9, 2, start.AddSeconds(2));
                    writer.SealAndExportCycle(
                        9,
                        second,
                        Path.Combine(root, "run2"),
                        start.AddSeconds(3),
                        "learning_completed");
                }
            });
        }

        private static void ConcurrentSealClaimsOnce()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var start = DateTime.UtcNow;
                var cycle = writer.BeginLearningCycle(10, start);
                WriteSamples(writer, 10, 8, start);
                var exportDir = Path.Combine(root, "concurrent");
                var tasks = new[]
                {
                    Task.Run(() => writer.SealAndExportCycle(
                        10, cycle, exportDir, DateTime.UtcNow, "learning_failed")),
                    Task.Run(() => writer.SealAndExportCycle(
                        10, cycle, exportDir, DateTime.UtcNow, "learning_failed"))
                };
                Task.WaitAll(tasks);
                Assert(tasks.Count(task => task.Result.WasClaimed) == 1,
                    "并发收尾有多个调用方取得封存权");
                Assert(tasks.Count(task => task.Result.IsValid) == 1,
                    "并发收尾生成了重复有效结果");
                Assert(File.Exists(CsvPath(exportDir, 10, cycle)) &&
                       File.Exists(BinPath(exportDir, 10, cycle)),
                    "并发收尾未保留唯一CSV/BIN");
            });
        }

        private static void FormalRetentionKeepsLearningRows()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    var learning = writer.BeginLearningCycle(11, start);
                    WriteSamples(writer, 11, 2, start);
                    writer.SealAndExportCycle(
                        11,
                        learning,
                        Path.Combine(root, "learning"),
                        start.AddSeconds(1),
                        "learning_completed");
                    for (var cycle = 1; cycle <= 11; cycle++)
                        WriteCompletedCycle(writer, 11, cycle, 1, start.AddSeconds(cycle + 1));
                    writer.PersistLatestCyclesNow(11, 10, "archive");
                }

                using var connection = OpenIndex(policy);
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM epb_cycles WHERE epb_id=11 AND cycle_number < 0";
                Assert(Convert.ToInt32(command.ExecuteScalar()) == 1,
                    "正式圈保留策略错误删除了学习索引");
            });
        }

        private static SQLiteConnection OpenIndex(DataRetentionPolicy policy)
        {
            var connection = new SQLiteConnection(
                $"Data Source={Path.Combine(policy.IndexAndExportPath, policy.IndexDbFile)}");
            connection.Open();
            return connection;
        }

        private static void BatchedWindowBoundarySurvivesRestart()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(1, 1, start);
                    writer.SealCycleWindow(1, 1, start.AddMilliseconds(1));
                    writer.WriteBatch(
                        1,
                        new[] { start.AddMilliseconds(-1), start, start.AddMilliseconds(1), start.AddMilliseconds(2) },
                        new[] { 1d, 2d, 3d, 4d },
                        new[] { 10d, 10d, 10d, 10d },
                        4);
                    writer.WriteBatch(
                        1,
                        new[] { start.AddMilliseconds(1), start.AddMilliseconds(2) },
                        new[] { 5d, 6d },
                        new[] { 10d, 10d },
                        2);
                    var final = writer.GetCurrentCycleSampleCount(1);
                    Assert(final == 3, "开始/结束时间窗未正确截取批次");
                    writer.CompleteCycle(1, 1, final, start.AddMilliseconds(1));
                }
                using (var writer = new EpbDiskWriter(policy))
                {
                    var export = Path.Combine(root, "restart-export");
                    writer.ExportLatestCyclesTo(1, 1, export, false);
                    AssertMonotonicRelativeTime(CsvPath(export, 1, 1), 3);
                    Assert(File.Exists(BinPath(export, 1, 1)), "时间窗重启导出缺少BIN");
                }
            });
        }

        private static void DeviceBatchWritesMultipleChannels()
        {
            WithRoot(root =>
            {
                var start = DateTime.UtcNow;
                using var writer = new EpbDiskWriter(NewPolicy(root));
                writer.BeginCycle(4, 1, start);
                writer.BeginCycle(5, 1, start);
                var timestamps = new[] { start, start.AddMilliseconds(1), start.AddMilliseconds(2) };
                writer.WriteDeviceBatch(
                    timestamps,
                    new[]
                    {
                        new EpbChannelDiskBatch { EpbId = 4, Currents = new[] { 1d, 2d, 3d }, Pressures = new[] { 10d, 10d, 10d } },
                        new EpbChannelDiskBatch { EpbId = 5, Currents = new[] { 4d, 5d, 6d }, Pressures = new[] { 10d, 10d, 10d } }
                    },
                    3);
                writer.CompleteCycle(4, 1, writer.GetCurrentCycleSampleCount(4), timestamps[2]);
                writer.CompleteCycle(5, 1, writer.GetCurrentCycleSampleCount(5), timestamps[2]);
                var export = Path.Combine(root, "multi");
                writer.ExportLatestCyclesTo(4, 1, export, false);
                writer.ExportLatestCyclesTo(5, 1, export, false);
                AssertCsvCycle(export, 4, 1, 3);
                AssertCsvCycle(export, 5, 1, 3);
            });
        }

        private static void ConcurrentLatestExportsAreAtomic()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                WriteCompletedCycle(writer, 1, 1, 4, DateTime.UtcNow);
                Task.WaitAll(Enumerable.Range(0, 8)
                    .Select(_ => Task.Run(() => writer.ExportLatestCyclesNow(1, 1)))
                    .ToArray());
                var latest = Path.Combine(policy.IndexAndExportPath, "Latest", "EPB1");
                var directories = Directory.GetDirectories(latest);
                Assert(directories.Length == 8, $"Latest最终目录数量错误：{directories.Length}");
                Assert(!Directory.EnumerateDirectories(latest, ".*.tmp-*", SearchOption.TopDirectoryOnly).Any(),
                    "Latest存在未清理暂存目录");
                foreach (var directory in directories)
                {
                    Assert(Directory.GetFiles(directory, "*.csv").Length == 1, "Latest缺少CSV");
                    Assert(Directory.GetFiles(directory, "*.bin").Length == 1, "Latest缺少BIN");
                }
            });
        }

        private static void WriteCompletedCycle(
            EpbDiskWriter writer,
            int epbId,
            int cycle,
            int sampleCount,
            DateTime start)
        {
            writer.BeginCycle(epbId, cycle, start);
            WriteSamples(writer, epbId, sampleCount, start);
            writer.CompleteCycle(
                epbId,
                cycle,
                writer.GetCurrentCycleSampleCount(epbId),
                start.AddMilliseconds(sampleCount));
        }

        private static void WriteSamples(
            EpbDiskWriter writer,
            int epbId,
            int sampleCount,
            DateTime start)
        {
            var timestamps = new DateTime[sampleCount];
            var currents = new double[sampleCount];
            var pressures = new double[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                timestamps[i] = start.AddMilliseconds(i);
                currents[i] = i + 0.1;
                pressures[i] = i + 10;
            }

            writer.WriteBatch(epbId, timestamps, currents, pressures);
        }

        private static void AssertCsvCycle(
            string exportDir,
            int epbId,
            int cycle,
            int expectedSamples)
        {
            var path = CsvPath(exportDir, epbId, cycle);
            Assert(File.Exists(path), "缺少 CSV：" + path);
            AssertRelativeTimeCsv(path, expectedSamples);
            var lines = File.ReadAllLines(path);
            var rows = lines.Skip(1).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                var cells = rows[i].Split(',');
                Assert(int.Parse(cells[2], CultureInfo.InvariantCulture) == cycle, $"Cycle={cycle} 混入其他圈");
                Assert(int.Parse(cells[3], CultureInfo.InvariantCulture) == i, $"Cycle={cycle} 样本序号不连续");
            }

            Assert(File.Exists(BinPath(exportDir, epbId, cycle)), "缺少 BIN");
        }

        private static void AssertRelativeTimeCsv(string path, int expectedSamples)
        {
            var lines = File.ReadAllLines(path);
            Assert(
                lines[0] == "Timestamp,RelativeTimeSeconds,Cycle,SampleIndex,EpbCurrent,GroupPressure",
                "CSV 缺少相对时间列");
            var rows = lines.Skip(1).ToList();
            Assert(rows.Count == expectedSamples, $"CSV 样本数错误：{rows.Count}");
            for (var i = 0; i < rows.Count; i++)
            {
                var cells = rows[i].Split(',');
                var relativeSeconds = double.Parse(cells[1], CultureInfo.InvariantCulture);
                Assert(
                    Math.Abs(relativeSeconds - i / 1000.0) < 0.0000005,
                    $"CSV 相对时间错误：{relativeSeconds}");
            }
        }

        private static void AssertMonotonicRelativeTime(string path, int expectedSamples)
        {
            var rows = File.ReadAllLines(path).Skip(1).ToList();
            Assert(rows.Count == expectedSamples, $"CSV 样本数错误：{rows.Count}");
            var previous = double.MinValue;
            foreach (var row in rows)
            {
                var relative = double.Parse(row.Split(',')[1], CultureInfo.InvariantCulture);
                Assert(relative >= previous, "相对时间发生倒退");
                previous = relative;
            }
        }

        private static string CsvPath(string dir, int epbId, int cycle)
        {
            return Path.Combine(dir, $"EPB{epbId}_Cycle_{cycle:D6}.csv");
        }

        private static string BinPath(string dir, int epbId, int cycle)
        {
            return Path.Combine(dir, $"EPB{epbId}_Cycle_{cycle:D6}.bin");
        }

        private static void ExpectExportFailure(Action action)
        {
            try
            {
                action();
            }
            catch (AggregateException)
            {
                return;
            }

            throw new InvalidOperationException("预期导出失败，但操作成功。");
        }

        private static void WithRoot(Action<string> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "EpbDiskWriterTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                action(root);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch
                {
                    // 测试退出时的临时目录清理失败不覆盖断言结果。
                }
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
