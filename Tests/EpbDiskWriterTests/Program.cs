using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
                Console.WriteLine($"PASS {_passed}/8");
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
