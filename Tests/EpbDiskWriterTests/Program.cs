using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Data.SQLite;
using System.Threading.Tasks;
using System.Threading;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using Config;
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
                if (args.Length == 6 &&
                    args[0].Equals("--recover-alarm-bins", StringComparison.OrdinalIgnoreCase))
                    return RecoverAlarmBins(
                        args[1],
                        int.Parse(args[2], CultureInfo.InvariantCulture),
                        int.Parse(args[3], CultureInfo.InvariantCulture),
                        int.Parse(args[4], CultureInfo.InvariantCulture),
                        args[5]);
                if (args.Length == 2 &&
                    args[0].Equals("--inspect-index", StringComparison.OrdinalIgnoreCase))
                    return InspectIndex(args[1]);
                if (args.Length == 1 &&
                    args[0].Equals("--sequence-boundary", StringComparison.OrdinalIgnoreCase))
                {
                    Run("DAQ序号圈边界抵抗1.5秒墙钟偏移且保留预触发", SequencedBoundaryKeepsPreTriggerAcrossWallClockSkew);
                    Console.WriteLine($"PASS {_passed}/{_passed}");
                    return 0;
                }

                Run("重启后写指针连续", RestartRestoresWritePosition);
                Run("running 圈重启后不覆盖", RestartAfterRunningCycle);
                Run("Watchdog强制接管只作废旧running圈", WatchdogTakeoverAbortsInterruptedCycles);
                Run("启动修复纠正旧running圈的倒置终止时间", StartupRepairClampsExistingEndBeforeStart);
                Run("圈进度和终态时间不早于圈头且状态单向迁移", CycleTimeAndStatusRemainMonotonic);
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
                Run("圈终态操作拒绝修改非当前圈", TerminalMutationRejectsWrongCycle);
                Run("软件自愈作废圈可导出警告证据", SoftwareRecoveryAbortCanExportEvidence);
                Run("学习负圈索引样本数竞态可从封存BIN恢复", LearningSnapshotRecoversWhenIndexCountIsZero);
                Run("报警CSV和BIN不一致时校验失败", AlarmPairValidatorRejectsMismatch);
                Run("学习与资格圈终态均落盘且不改变正式计数", LearningOutcomesDoNotAffectFormalCounters);
                Run("机械完成事实跨终态和旧库迁移均可恢复", MechanicalCompletionSurvivesStatusAndMigration);
                Run("学习负圈号跨重启连续且唯一", LearningCycleNumbersSurviveRestart);
                Run("报警与学习收尾并发只封存一次", ConcurrentSealClaimsOnce);
                Run("已完成报警触发圈可回读并导出最近10圈", CompletedAlarmTriggerCycleCanBeRecovered);
                Run("正式圈保留策略不删除学习索引", FormalRetentionKeepsLearningRows);
                Run("按通道圈号精确导出完整证据", ExactCompletedCycleExport);
                Run("活动圈样本硬上限阻止覆盖", ActiveCycleSampleLimitStopsWrites);
                Run("连续100次活动圈超限均原子作废且无running遗留", HundredActiveCycleLimitFaultsLeaveNoRunningRows);
                Run("批量时间窗边界与重启恢复", BatchedWindowBoundarySurvivesRestart);
                Run("设备多通道批次事务写入", DeviceBatchWritesMultipleChannels);
                Run("DAQ序号圈边界抵抗1.5秒墙钟偏移且保留预触发", SequencedBoundaryKeepsPreTriggerAcrossWallClockSkew);
                Run("圈封口后仍接纳冻结Accepted边界内的延迟尾批", SequencedBoundaryIncludesDelayedAcceptedTail);
                Run("不足200ms预触发不得标记语义完整", InsufficientPreTriggerIsEvidenceIncomplete);
                Run("Latest并发导出原子且无临时残留", ConcurrentLatestExportsAreAtomic);
                Run("Latest每通道计数收敛且Unlimited不删除", LatestPackageRetentionModes);
                Run("暂停最近10圈重复请求只生成一个证据包", PauseLatestExportIsIdempotent);
                Run("暂停后停止相同10圈不重复导出", PauseThenStopDoesNotDuplicateLatestPackage);
                Run("仅停止包含最后未完整圈及零样本圈", StopLatestExportIncludesInterruptedCycle);
                Run("不同数据根目录的写盘器可同时映射且保持隔离", DifferentRootsUseIndependentMappingScopes);
                Run("Raw首次Flush不再主动丢弃缓存", RawFirstFlushPersistsBufferedData);
                Run("Raw硬容量背压后全部批次落盘", RawCapacityBackpressurePreservesAllBatches);
                Run("Raw写入失败保留FIFO并可重试", RawWriteFailureRetainsFifoForRetry);
                Run("程序级三种存储格式均可导出", ProgramStorageLevelsExportSingleOrPair);
                Run("非法程序级存储配置回退并告警", InvalidProgramStorageConfigFallsBack);
                Run("旧CSV+BIN包在单格式策略下仍可校验", LegacyPairRemainsCompatibleWithSinglePolicy);
                Run("报警终态回读按CsvOnly请求单格式", AlarmFinalizedRecoveryHonorsSingleFormat);
                Run("Learning保留配置严格解析", LearningRetentionPolicyParsing);
                Run("Learning运行链manifest原子发布与哈希校验", LearningRunManifestAtomicAndHash);
                Run("Learning模型保存失败不推进模型且manifest不可清理", LearningModelSaveFailureIsNotEligible);
                Run("Learning 3+3保留规划", LearningRetentionPlannerConverges3Plus3);
                Run("Learning失败链受protected root保护", FailedLearningChainIsSkippedWhenProtected);
                Run("Housekeeping大目录扫描不在调用线程", HousekeepingScanRunsOffCallerThread);
                Run("SaveWithReceipt读回失败磁盘与内存均回滚", SaveWithReceiptReadbackFailureRollsBackDiskAndMemory);
                Run("SaveWithReceipt回滚失败抛致命并保留备份", SaveWithReceiptRollbackFailureIsFatalAndKeepsBackup);
                Run("Housekeeping实际删除收敛且保护未知/当前/临时", HousekeepingProcessConvergesAndProtects);
                Run("Unlimited重启后4+4学习链仍全部保留", UnlimitedRetentionNeverDeletesAfterRestart);
                Run("旧staging对应Root重新激活后不得删除", ReactivatedRootPreservesOwnedStaging);
                Console.WriteLine($"PASS {_passed}/{_passed}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static void CycleTimeAndStatusRemainMonotonic()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                using var writer = new EpbDiskWriter(policy);
                writer.BeginCycle(1, 42, start);
                writer.WriteSample(1, start.AddSeconds(-2), 1, 10);
                writer.CompleteCycle(1, 42, 1, start.AddSeconds(-1));

                var update = typeof(EpbDiskWriter).GetMethod(
                    "ExecuteCycleUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(update != null, "未找到圈状态迁移入口");
                update.Invoke(writer, new object[] { 1, 42, 99, start.AddSeconds(2), "running" });

                using var connection = OpenIndex(policy);
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT start_time,end_time,status,sample_count FROM epb_cycles " +
                    "WHERE epb_id=1 AND cycle_number=42";
                using var reader = command.ExecuteReader();
                Assert(reader.Read(), "单调圈记录不存在");
                var persistedStart = DateTime.Parse(
                    reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var persistedEnd = DateTime.Parse(
                    reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Assert(persistedEnd >= persistedStart, "圈终态时间早于圈头");
                Assert(reader.GetString(2) == "completed", "迟到进度把终态回退为running");
                Assert(reader.GetInt32(3) == 1, "迟到进度修改了终态样本数");
            });
        }

        private static int InspectIndex(string databasePath)
        {
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                ReadOnly = true,
                FailIfMissing = true
            };
            using (var connection = new SQLiteConnection(builder.ConnectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA integrity_check;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            Console.WriteLine("INTEGRITY " + reader.GetString(0));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT status, COUNT(*) FROM epb_cycles GROUP BY status ORDER BY COUNT(*) DESC;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            Console.WriteLine($"STATUS {reader.GetString(0)} {reader.GetInt64(1)}");
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT epb_id,cycle_number,status,sample_count,start_time,end_time " +
                        "FROM epb_cycles WHERE lower(status) IN ('running','started','active') " +
                        "ORDER BY epb_id,cycle_number;";
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            Console.WriteLine(
                                $"UNFINISHED EPB={reader.GetInt32(0)} Cycle={reader.GetInt32(1)} " +
                                $"Status={reader.GetString(2)} Samples={reader.GetInt32(3)} " +
                                $"Start={reader[4]} End={reader[5]}");
                }
                foreach (var channel in new[] { 4, 5, 9, 10, 11 })
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT epb_id,cycle_number,status,sample_count,start_time,end_time " +
                            "FROM epb_cycles WHERE epb_id=@epb AND cycle_number<0 " +
                            "ORDER BY id DESC LIMIT 8;";
                        command.Parameters.AddWithValue("@epb", channel);
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                                Console.WriteLine(
                                    $"NEGATIVE EPB={reader.GetInt32(0)} Cycle={reader.GetInt32(1)} " +
                                    $"Status={reader.GetString(2)} Samples={reader.GetInt32(3)} " +
                                    $"Start={reader[4]} End={reader[5]}");
                    }
                }
            }
            return 0;
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

        private static int RecoverAlarmBins(
            string projectRoot,
            int epbId,
            int alarmCycle,
            int requestedCycles,
            string outputDir)
        {
            if (epbId < 1 || epbId > 12) throw new ArgumentOutOfRangeException(nameof(epbId));
            if (alarmCycle < 1) throw new ArgumentOutOfRangeException(nameof(alarmCycle));
            requestedCycles = Math.Max(1, requestedCycles);
            var history = Path.Combine(
                projectRoot,
                "HistoricalSnapshots",
                $"EPB{epbId:D2}");
            if (!Directory.Exists(history))
                throw new DirectoryNotFoundException(history);
            var selected = Directory.EnumerateFiles(history, $"EPB{epbId}_Cycle_*.bin", SearchOption.AllDirectories)
                .Select(path => new
                {
                    Path = path,
                    Cycle = ParseCycleNumber(path)
                })
                .Where(item => item.Cycle > 0 && item.Cycle <= alarmCycle)
                .GroupBy(item => item.Cycle)
                .Select(group => group.OrderByDescending(item => File.GetLastWriteTimeUtc(item.Path)).First())
                .OrderByDescending(item => item.Cycle)
                .Take(requestedCycles)
                .OrderBy(item => item.Cycle)
                .ToArray();
            if (selected.Length != requestedCycles)
                throw new InvalidDataException(
                    $"历史BIN不足：EPB={epbId}, AlarmCycle={alarmCycle}, " +
                    $"Requested={requestedCycles}, Found={selected.Length}");
            if (Directory.Exists(outputDir))
                throw new IOException("恢复目标已存在，拒绝覆盖：" + outputDir);

            var fullOutput = Path.GetFullPath(outputDir);
            var parent = Path.GetDirectoryName(fullOutput) ?? throw new InvalidDataException("恢复目标无父目录");
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, "." + Path.GetFileName(fullOutput) + ".tmp-" + Guid.NewGuid().ToString("N"));
            var scratch = Path.Combine(Path.GetTempPath(), "EpbAlarmBinRecovery", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(scratch);
            try
            {
                var policy = NewPolicy(scratch);
                using (var writer = new EpbDiskWriter(policy))
                {
                    foreach (var item in selected)
                    {
                        var stem = $"EPB{epbId}_Cycle_{item.Cycle:D6}";
                        var bin = Path.Combine(staging, stem + ".bin");
                        var csv = Path.Combine(staging, stem + ".csv");
                        File.Copy(item.Path, bin, true);
                        writer.ImportBinToCsv(bin, csv);
                        var evidence = EpbDiskWriter.ValidateAlarmCycleSnapshotPair(
                            csv,
                            bin,
                            epbId,
                            item.Cycle);
                        if (!evidence.IsValid)
                            throw new InvalidDataException(evidence.ValidationError);
                    }
                }

                var checksums = Directory.EnumerateFiles(staging, "*.*", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Select(path => $"{Sha256(path)}  {Path.GetFileName(path)}")
                    .ToArray();
                File.WriteAllLines(Path.Combine(staging, "SHA256SUMS.txt"), checksums, Encoding.ASCII);
                File.WriteAllLines(
                    Path.Combine(staging, "RECOVERY.txt"),
                    new[]
                    {
                        "Purpose=Recover missing hard-alarm latest-cycle evidence from immutable HistoricalSnapshots BIN files",
                        $"SourceProject={Path.GetFullPath(projectRoot)}",
                        $"EpbId={epbId}",
                        $"AlarmCycle={alarmCycle}",
                        $"RequestedCycles={requestedCycles}",
                        $"RecoveredCycles={string.Join(",", selected.Select(item => item.Cycle))}",
                        $"RecoveredUtc={DateTime.UtcNow:O}",
                        "OriginalAlarmSnapshotModified=false"
                    },
                    new UTF8Encoding(false));
                Directory.Move(staging, fullOutput);
                Console.WriteLine($"RECOVERED ALARM EPB[{epbId}] CYCLES={string.Join(",", selected.Select(item => item.Cycle))} -> {fullOutput}");
                return 0;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }
            }
        }

        private static int ParseCycleNumber(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var marker = stem.LastIndexOf("_Cycle_", StringComparison.OrdinalIgnoreCase);
            return marker >= 0 && int.TryParse(
                stem.Substring(marker + 7),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var cycle)
                ? cycle
                : 0;
        }

        private static string Sha256(string path)
        {
            using var algorithm = SHA256.Create();
            using var stream = File.OpenRead(path);
            return string.Concat(algorithm.ComputeHash(stream).Select(value => value.ToString("x2")));
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
                    writer.ExportCycleAttemptTo(1, 1, exportDir, true, true);
                    AssertCsvCycle(exportDir, 1, 1, 3);
                    AssertCsvCycle(exportDir, 1, 2, 2);
                }
            });
        }

        private static void WatchdogTakeoverAbortsInterruptedCycles()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(1, 7, DateTime.UtcNow);
                    WriteSamples(writer, 1, 3, DateTime.UtcNow);
                }

                using (var writer = new EpbDiskWriter(policy))
                {
                    var affected = writer.AbortInterruptedCyclesForSoftwareRecovery(DateTime.UtcNow);
                    Assert(affected == 0, "启动构造已原子作废事故圈后Watchdog不应重复修改");
                    Assert(writer.AbortInterruptedCyclesForSoftwareRecovery(DateTime.UtcNow) == 0,
                        "Watchdog 接管重复调用再次修改事故圈");
                    using var connection = new SQLiteConnection(
                        "Data Source=" + Path.Combine(root, "index", "index.db") + ";Version=3;");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT status FROM epb_cycles WHERE epb_id=1 AND cycle_number=7;";
                    Assert(string.Equals(Convert.ToString(command.ExecuteScalar()),
                            "aborted_on_startup", StringComparison.OrdinalIgnoreCase),
                        "启动恢复事故圈终态不正确");
                }
            });
        }

        private static void StartupRepairClampsExistingEndBeforeStart()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var started = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                    writer.BeginCycle(4, 5557, started);

                using (var connection = OpenIndex(policy))
                using (var corrupt = connection.CreateCommand())
                {
                    corrupt.CommandText =
                        "UPDATE epb_cycles SET end_time=@bad WHERE epb_id=4 AND cycle_number=5557;";
                    corrupt.Parameters.AddWithValue(
                        "@bad",
                        started.AddMilliseconds(-27.681).ToLocalTime().ToString("o"));
                    Assert(corrupt.ExecuteNonQuery() == 1, "未构造事故形态的倒置终止时间");
                }

                using (var writer = new EpbDiskWriter(policy))
                using (var connection = OpenIndex(policy))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT start_time,end_time,status FROM epb_cycles " +
                        "WHERE epb_id=4 AND cycle_number=5557;";
                    using var reader = command.ExecuteReader();
                    Assert(reader.Read(), "启动修复后事故圈不存在");
                    var persistedStart = DateTime.Parse(
                        reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    var persistedEnd = DateTime.Parse(
                        reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    Assert(persistedEnd >= persistedStart, "启动修复仍保留end_time早于start_time");
                    Assert(string.Equals(
                            reader.GetString(2),
                            "aborted_on_startup",
                            StringComparison.OrdinalIgnoreCase),
                        "启动修复未把旧running圈闭合为作废终态");
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

        private static void DifferentRootsUseIndependentMappingScopes()
        {
            WithRoot(root =>
            {
                var firstRoot = Path.Combine(root, "first");
                var secondRoot = Path.Combine(root, "second");
                var started = DateTime.UtcNow;

                using var first = new EpbDiskWriter(NewPolicy(firstRoot));
                using var second = new EpbDiskWriter(NewPolicy(secondRoot));
                first.BeginCycle(1, 1, started);
                second.BeginCycle(1, 1, started);
                WriteSamples(first, 1, 3, started);
                WriteSamples(second, 1, 5, started);
                Assert(first.GetCurrentCycleSampleCount(1) == 3,
                    "第一数据根目录的映射样本数错误");
                Assert(second.GetCurrentCycleSampleCount(1) == 5,
                    "第二数据根目录的映射样本数错误");
                first.CompleteCycle(1, 1, 3, started.AddMilliseconds(3));
                second.CompleteCycle(1, 1, 5, started.AddMilliseconds(5));
            });
        }

        private static void CompletedAlarmTriggerCycleCanBeRecovered()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                var exportDir = Path.Combine(root, "alarm");
                using var writer = new EpbDiskWriter(policy);
                var recorder = new DiskWriterRecorderAdapter(writer);
                for (var cycle = 1; cycle <= 10; cycle++)
                    WriteCompletedCycle(writer, 8, cycle, 4, start.AddSeconds(cycle));

                var first = recorder.SealAndExportAlarmCycle(
                    8,
                    10,
                    exportDir,
                    start.AddSeconds(11));
                Assert(!first.IsValid && !first.WasClaimed,
                    "已完成圈不应再次取得当前圈封存权");

                var recovered = AlarmCycleSnapshotRecovery.TryExportFinalizedCycle(
                    recorder,
                    8,
                    10,
                    exportDir,
                    first);
                Assert(recovered.IsValid,
                    "已完成报警触发圈未能从持久化索引回读：" + recovered.ValidationError);
                Assert(!recovered.WasClaimed && recovered.FinalStatus == "CompletedTriggerCycle",
                    "回读证据未保留‘已完成触发圈’语义");

                recorder.FlushRecentTo(8, 10, exportDir, false);
                var csv = Directory.GetFiles(exportDir, "*.csv", SearchOption.TopDirectoryOnly);
                var bin = Directory.GetFiles(exportDir, "*.bin", SearchOption.TopDirectoryOnly);
                Assert(csv.Length == 10 && bin.Length == 10,
                    $"报警最近10圈不完整：CSV={csv.Length}, BIN={bin.Length}");
                for (var cycle = 1; cycle <= 10; cycle++)
                    AssertCsvCycle(exportDir, 8, cycle, 4);

                // 真实报警停机链在 AlarmSnapshots 成功后，还必须生成一个明确包含
                // 触发终态圈的 Latest 包，不能只依赖无约束的普通最近圈导出。
                var stopExporter = (IStopRecentCycleEvidenceExporter)recorder;
                stopExporter.FlushRecentForStop(8, 10, 10);
                var latestRoot = Path.Combine(policy.IndexAndExportPath, "Latest", "EPB8");
                var latestPackage = Directory.GetDirectories(latestRoot).Single();
                Assert(Directory.GetFiles(latestPackage, "*.csv").Length == 10 &&
                       Directory.GetFiles(latestPackage, "*.bin").Length == 10,
                    "报警停机后的Latest最近10圈不完整");
                AssertCsvCycle(latestPackage, 8, 10, 4);
            });
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

        private static void TerminalMutationRejectsWrongCycle()
        {
            WithRoot(root =>
            {
                var start = DateTime.UtcNow;
                using var writer = new EpbDiskWriter(NewPolicy(root));
                writer.BeginCycle(4, 152, start);
                WriteSamples(writer, 4, 3, start);

                var rejected = false;
                try
                {
                    writer.AbortCycle(
                        4,
                        151,
                        3,
                        start.AddSeconds(1),
                        "AbortedBySoftwareRecovery");
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }

                Assert(rejected, "非当前圈终态修改未被拒绝");
                Assert(writer.GetCurrentCycleSampleCount(4) == 3,
                    "拒绝错误圈后破坏了真实活动圈");
                writer.AbortCycle(
                    4,
                    152,
                    3,
                    start.AddSeconds(1),
                    "AbortedBySoftwareRecovery");
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

        private static void LearningSnapshotRecoversWhenIndexCountIsZero()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var start = DateTime.UtcNow;
                var learningDir = Path.Combine(
                    policy.IndexAndExportPath,
                    "LearningCycles",
                    Guid.NewGuid().ToString("N"),
                    "EPB05",
                    "Learning_0005",
                    "Attempt_0002");
                int cycle;
                using (var writer = new EpbDiskWriter(policy))
                {
                    cycle = writer.BeginLearningCycle(5, start);
                    WriteSamples(writer, 5, 7, start);
                    var sealedEvidence = writer.SealAndExportCycle(
                        5,
                        cycle,
                        learningDir,
                        start.AddSeconds(1),
                        "learning_failed");
                    Assert(sealedEvidence.IsValid && sealedEvidence.SampleCount == 7,
                        "测试前置学习负圈未成功封存");

                    using (var connection = OpenIndex(policy))
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "UPDATE epb_cycles SET sample_count=0 WHERE epb_id=5 AND cycle_number=@cycle";
                        command.Parameters.AddWithValue("@cycle", cycle);
                        command.ExecuteNonQuery();
                    }

                    var warningDir = Path.Combine(root, "warning-negative-cycle");
                    var recovered = writer.ExportCycleAttemptTo(
                        5,
                        cycle,
                        warningDir,
                        true,
                        true);
                    Assert(recovered.SampleCount == 7 &&
                           File.Exists(recovered.CsvPath) &&
                           File.Exists(recovered.BinPath),
                        "索引样本数为0时未从LearningCycles/Attempt封存BIN恢复预警证据");
                }
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

        private static void MechanicalCompletionSurvivesStatusAndMigration()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                Directory.CreateDirectory(policy.IndexAndExportPath);
                var dbPath = Path.Combine(policy.IndexAndExportPath, "index.db");
                using (var legacy = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
                {
                    legacy.Open();
                    using var create = legacy.CreateCommand();
                    create.CommandText = @"
CREATE TABLE epb_cycles(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 epb_id INTEGER NOT NULL,
 cycle_number INTEGER NOT NULL,
 start_time TEXT NOT NULL,
 end_time TEXT,
 start_position INTEGER NOT NULL,
 sample_count INTEGER DEFAULT 0,
 status TEXT DEFAULT 'running',
 created_at TEXT DEFAULT (datetime('now')),
 UNIQUE(epb_id, cycle_number));";
                    create.ExecuteNonQuery();
                    create.CommandText = @"
INSERT INTO epb_cycles(
 epb_id,cycle_number,start_time,end_time,start_position,sample_count,status)
VALUES(4,-1,'2026-08-16T12:00:00','2026-08-16T12:00:01',0,3,'learning_completed');";
                    create.ExecuteNonQuery();
                }

                var start = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    var learning = writer.BeginLearningCycle(4, start);
                    WriteSamples(writer, 4, 3, start);
                    writer.MarkMechanicalCycleCompleted(4, learning, start.AddMilliseconds(500));
                    writer.SealAndExportCycle(
                        4,
                        learning,
                        Path.Combine(root, "Learning"),
                        start.AddSeconds(1),
                        "learning_failed");

                    writer.BeginCycle(4, 1, start.AddSeconds(2));
                    WriteSamples(writer, 4, 3, start.AddSeconds(2));
                    writer.MarkMechanicalCycleCompleted(4, 1, start.AddSeconds(3));
                    writer.AbortCycle(4, 1, 3, start.AddSeconds(3), "AbortedBySoftwareRecovery");
                    Assert(writer.GetMechanicalCycleCompletedCount(4) == 3,
                        "历史学习完成圈、学习失败圈或软件作废正式圈没有计入机械完成事实");
                    var lastCompletedUtc = writer.GetLastMechanicalCycleCompletedUtc(4);
                    Assert(lastCompletedUtc.HasValue &&
                           Math.Abs((lastCompletedUtc.Value - start.AddSeconds(3)).TotalMilliseconds) < 2,
                        "逐通道最后机械完成时间未从真实SQLite机械事实恢复");
                }

                using (var restarted = new EpbDiskWriter(policy))
                    Assert(restarted.GetMechanicalCycleCompletedCount(4) == 3,
                        "重启后机械完成事实计数丢失");

                using var verify = OpenIndex(policy);
                using var columns = verify.CreateCommand();
                columns.CommandText = "PRAGMA table_info(epb_cycles)";
                using var reader = columns.ExecuteReader();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read()) names.Add(reader["name"].ToString());
                Assert(names.Contains("mechanical_completed") &&
                       names.Contains("mechanical_completed_at"),
                    "旧 index.db 未原位迁移机械完成字段");
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
                    writer.SealCycleWindow(1, 1, start.AddMilliseconds(10));
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
                    Assert(final == 3, "开始/结束时间窗被迟到恢复请求扩大或未正确截取批次");
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

        private static void SequencedBoundaryKeepsPreTriggerAcrossWallClockSkew()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                policy.AlarmStorageLevel = StorageFormatLevel.CsvOnly;
                using var writer = new EpbDiskWriter(policy);
                var sampleUtc = new DateTime(2026, 8, 21, 9, 33, 16, DateTimeKind.Utc);
                var timestamps = Enumerable.Range(0, 400)
                    .Select(index => sampleUtc.AddTicks(index * 5000L))
                    .ToArray();
                var baseline = Enumerable.Repeat(0.05, 400).ToArray();
                var pressure = Enumerable.Repeat(100.0, 400).ToArray();
                var channels = new[]
                {
                    new EpbChannelDiskBatch(10, baseline, pressure)
                };
                writer.WriteDeviceBatch("Dev2", 7, 100, timestamps, channels, 1, 400);

                var wallClockStart = sampleUtc.AddMilliseconds(1500);
                writer.BeginCycleAtDaqBoundary(10, 85993, wallClockStart, "Dev2", 7, 100);
                var liveTimestamps = Enumerable.Range(0, 20)
                    .Select(index => sampleUtc.AddMilliseconds(200).AddTicks(index * 5000L))
                    .ToArray();
                var liveCurrent = Enumerable.Range(0, 20)
                    .Select(index => 2.0 + index * 0.5)
                    .ToArray();
                writer.WriteDeviceBatch(
                    "Dev2",
                    7,
                    101,
                    liveTimestamps,
                    new[] { new EpbChannelDiskBatch(10, liveCurrent, pressure) },
                    1,
                    20);
                writer.SealCycleWindowAtDaqBoundary(
                    10,
                    85993,
                    wallClockStart.AddSeconds(1),
                    "Dev2",
                    7,
                    101);
                var evidence = writer.SealAndExportAlarmCycle(
                    10,
                    85993,
                    Path.Combine(root, "alarm"),
                    wallClockStart.AddSeconds(1));

                Assert(evidence.IsValid && evidence.SemanticEvidenceComplete,
                    "序号边界圈未通过预触发语义完整性校验：" + evidence.ValidationError);
                Assert(evidence.SampleCount == 420 && evidence.PreTriggerSampleCount == 400 &&
                       evidence.RequiredPreTriggerSampleCount == 400,
                    $"序号边界未保留200ms预触发或错误截样：" +
                    $"Total={evidence.SampleCount} Pre={evidence.PreTriggerSampleCount} " +
                    $"Required={evidence.RequiredPreTriggerSampleCount}");
                Assert(evidence.FirstSampleUtc.HasValue &&
                       evidence.FirstSampleUtc.Value < wallClockStart.AddSeconds(-1),
                    "样本仍按跳变后的墙钟开始时间被截断");
                Assert(evidence.CycleStartAfterSequence == 100 && evidence.CycleEndSequence == 101 &&
                       evidence.LastWrittenSequence == 101,
                    "报警证据未记录权威DAQ序号边界");
            });
        }

        private static void SequencedBoundaryIncludesDelayedAcceptedTail()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                policy.AlarmStorageLevel = StorageFormatLevel.CsvOnly;
                using var writer = new EpbDiskWriter(policy);
                var utc = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
                var preTs = Enumerable.Range(0, 400)
                    .Select(index => utc.AddTicks(index * 5000L))
                    .ToArray();
                var preCurrent = Enumerable.Repeat(0.04, 400).ToArray();
                var prePressure = Enumerable.Repeat(100.0, 400).ToArray();
                writer.WriteDeviceBatch(
                    "Dev2",
                    9,
                    100,
                    preTs,
                    new[] { new EpbChannelDiskBatch(10, preCurrent, prePressure) },
                    1,
                    400);
                writer.BeginCycleAtDaqBoundary(10, 90001, utc.AddSeconds(1), "Dev2", 9, 100);

                void WriteLive(long sequence, double current)
                {
                    var ts = Enumerable.Range(0, 20)
                        .Select(index => utc.AddSeconds(1).AddMilliseconds((sequence - 101) * 10)
                            .AddTicks(index * 5000L))
                        .ToArray();
                    writer.WriteDeviceBatch(
                        "Dev2",
                        9,
                        sequence,
                        ts,
                        new[]
                        {
                            new EpbChannelDiskBatch(
                                10,
                                Enumerable.Repeat(current, 20).ToArray(),
                                Enumerable.Repeat(100.0, 20).ToArray())
                        },
                        1,
                        20);
                }

                WriteLive(101, 2.0);
                // 模拟控制链已接纳到103，但落盘链当前只发布到101：先冻结103，
                // 102/103随后到达仍必须归入本圈；104属于截止后批次，必须排除。
                writer.SealCycleWindowAtDaqBoundary(
                    10,
                    90001,
                    utc.AddSeconds(2),
                    "Dev2",
                    9,
                    103);
                WriteLive(102, 8.0);
                WriteLive(103, 14.5);
                WriteLive(104, 0.0);

                var evidence = writer.SealAndExportAlarmCycle(
                    10,
                    90001,
                    Path.Combine(root, "delayed-tail"),
                    utc.AddSeconds(2));
                Assert(evidence.IsValid && evidence.SemanticEvidenceComplete,
                    "冻结Accepted边界内的延迟尾批未形成完整证据：" + evidence.ValidationError);
                Assert(evidence.SampleCount == 460,
                    $"延迟尾批未完整纳入或截止后批次越界：SampleCount={evidence.SampleCount}");
                Assert(evidence.CycleEndSequence == 103 && evidence.LastWrittenSequence == 103,
                    $"圈尾水位不一致：End={evidence.CycleEndSequence} " +
                    $"LastWritten={evidence.LastWrittenSequence}");
            });
        }

        private static void InsufficientPreTriggerIsEvidenceIncomplete()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                policy.AlarmStorageLevel = StorageFormatLevel.CsvOnly;
                using var writer = new EpbDiskWriter(policy);
                var utc = DateTime.UtcNow;
                var timestamps = Enumerable.Range(0, 20)
                    .Select(index => utc.AddTicks(index * 5000L))
                    .ToArray();
                var pressure = Enumerable.Repeat(100.0, 20).ToArray();
                writer.WriteDeviceBatch(
                    "Dev2",
                    11,
                    200,
                    timestamps,
                    new[]
                    {
                        new EpbChannelDiskBatch(10, Enumerable.Repeat(0.04, 20).ToArray(), pressure)
                    },
                    1,
                    20);
                writer.BeginCycleAtDaqBoundary(10, 90002, utc.AddSeconds(1), "Dev2", 11, 200);
                writer.WriteDeviceBatch(
                    "Dev2",
                    11,
                    201,
                    timestamps.Select(value => value.AddSeconds(1)).ToArray(),
                    new[]
                    {
                        new EpbChannelDiskBatch(10, Enumerable.Repeat(10.0, 20).ToArray(), pressure)
                    },
                    1,
                    20);
                writer.SealCycleWindowAtDaqBoundary(
                    10,
                    90002,
                    utc.AddSeconds(2),
                    "Dev2",
                    11,
                    201);
                var evidence = writer.SealAndExportAlarmCycle(
                    10,
                    90002,
                    Path.Combine(root, "short-pretrigger"),
                    utc.AddSeconds(2));

                Assert(evidence.WasClaimed && !evidence.IsValid &&
                       !evidence.SemanticEvidenceComplete,
                    "不足200ms预触发仍被标记为完整报警证据");
                Assert(evidence.PreTriggerSampleCount == 20 &&
                       evidence.RequiredPreTriggerSampleCount == 400 &&
                       evidence.ValidationError.Contains("预触发样本不足"),
                    "不完整证据未记录实际/要求预触发数量和明确原因");
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

        private static void LatestPackageRetentionModes()
        {
            WithRoot(root =>
            {
                var countRoot = Path.Combine(root, "count");
                var policy = NewPolicy(countRoot);
                policy.RetainLatestStopPackagesPerChannel = 3;
                using (var writer = new EpbDiskWriter(policy))
                {
                    WriteCompletedCycle(writer, 1, 1, 4, DateTime.UtcNow);
                    for (var i = 0; i < 8; i++) writer.ExportLatestCyclesNow(1, 1);
                    var channel = Path.Combine(policy.IndexAndExportPath, "Latest", "EPB1");
                    Directory.CreateDirectory(Path.Combine(channel, "unknown-package"));
                    AssertEventually(
                        () => Directory.GetDirectories(channel)
                                  .Count(path => Path.GetFileName(path) != "unknown-package") == 3,
                        "Latest Count模式未最终收敛到3包");
                    Assert(Directory.Exists(Path.Combine(channel, "unknown-package")),
                        "Latest未知目录被错误删除");
                }

                var unlimitedRoot = Path.Combine(root, "unlimited");
                var unlimitedPolicy = NewPolicy(unlimitedRoot);
                unlimitedPolicy.RetainLatestStopPackagesPerChannel = 1;
                unlimitedPolicy.RetainAllLatestStopPackages = true;
                using (var writer = new EpbDiskWriter(unlimitedPolicy))
                {
                    WriteCompletedCycle(writer, 2, 1, 4, DateTime.UtcNow);
                    for (var i = 0; i < 4; i++) writer.ExportLatestCyclesNow(2, 1);
                    var channel = Path.Combine(unlimitedPolicy.IndexAndExportPath, "Latest", "EPB2");
                    Thread.Sleep(100);
                    Assert(Directory.GetDirectories(channel).Length == 4,
                        "Latest Unlimited模式错误删除停止包");
                }
            });
        }

        private static void PauseLatestExportIsIdempotent()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var start = DateTime.UtcNow;
                for (var cycle = 1; cycle <= 12; cycle++)
                    WriteCompletedCycle(writer, 1, cycle, 2, start.AddSeconds(cycle));

                writer.ExportLatestCyclesOnceNow(1, 10);
                writer.ExportLatestCyclesOnceNow(1, 10);

                var packages = Directory.GetDirectories(
                    Path.Combine(policy.IndexAndExportPath, "Latest", "EPB1"));
                Assert(packages.Length == 1, $"暂停重复导出生成了{packages.Length}个包");
                Assert(Directory.GetFiles(packages[0], "*.csv").Length == 10, "暂停包不是最近10圈");
                Assert(!File.Exists(CsvPath(packages[0], 1, 2)), "暂停包错误包含第2圈");
                Assert(File.Exists(CsvPath(packages[0], 1, 3)), "暂停包缺少第3圈");
                Assert(File.Exists(CsvPath(packages[0], 1, 12)), "暂停包缺少第12圈");
            });
        }

        private static void PauseThenStopDoesNotDuplicateLatestPackage()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var start = DateTime.UtcNow;
                for (var cycle = 1; cycle <= 10; cycle++)
                    WriteCompletedCycle(writer, 2, cycle, 2, start.AddSeconds(cycle));

                writer.ExportLatestCyclesOnceNow(2, 10);
                writer.ExportLatestCyclesForStopNow(2, 10, 10);

                var packages = Directory.GetDirectories(
                    Path.Combine(policy.IndexAndExportPath, "Latest", "EPB2"));
                Assert(packages.Length == 1, $"暂停后停止重复生成了{packages.Length}个相同包");
                var manifest = Path.Combine(packages[0], "latest-manifest.json");
                Assert(File.Exists(manifest) &&
                       File.ReadAllText(manifest).Contains("\"snapshotState\": \"Final\""),
                    "最终Latest缺少可审计manifest或Final语义");
            });
        }

        private static void StopLatestExportIncludesInterruptedCycle()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var start = DateTime.UtcNow;
                for (var cycle = 1; cycle <= 10; cycle++)
                    WriteCompletedCycle(writer, 3, cycle, 2, start.AddSeconds(cycle));

                writer.BeginCycle(3, 11, start.AddSeconds(11));
                WriteSamples(writer, 3, 1, start.AddSeconds(11));
                writer.AbortCycle(3, 11, writer.GetCurrentCycleSampleCount(3), start.AddSeconds(12), "canceled");
                writer.ExportLatestCyclesForStopNow(3, 10, 11);

                var package = Directory.GetDirectories(
                    Path.Combine(policy.IndexAndExportPath, "Latest", "EPB3")).Single();
                Assert(Directory.GetFiles(package, "*.csv").Length == 10, "停止包不是最近10圈");
                Assert(!File.Exists(CsvPath(package, 3, 1)), "停止包未淘汰最早完整圈");
                AssertCsvCycle(package, 3, 11, 1);

                writer.BeginCycle(4, 1, start);
                writer.AbortCycle(4, 1, 0, start.AddMilliseconds(1), "canceled");
                writer.ExportLatestCyclesForStopNow(4, 10, 1);
                var emptyPackage = Directory.GetDirectories(
                    Path.Combine(policy.IndexAndExportPath, "Latest", "EPB4")).Single();
                Assert(File.ReadAllLines(CsvPath(emptyPackage, 4, 1)).Length == 1,
                    "零样本停止圈CSV应保留表头");
                Assert(new FileInfo(BinPath(emptyPackage, 4, 1)).Length == 0,
                    "零样本停止圈BIN应为空但必须存在");
            });
        }

        private static void AssertEventually(Func<bool> predicate, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return;
                Thread.Sleep(20);
            }
            throw new InvalidOperationException(message);
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

        private static void RawFirstFlushPersistsBufferedData()
        {
            WithRoot(root =>
            {
                var context = new DaqAIContext("Dev1", 256, 60, 1, 1, 1, root);
                context.EnqueueRawData(new[,] { { 12.5 } }, DateTime.Now, DateTime.Now.AddMilliseconds(-1));
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();
                var path = Path.Combine(root, "DAQ_Dev1_Raw_1.bin");
                Assert(File.Exists(path) && new FileInfo(path).Length == 20,
                    "首次Flush仍丢弃缓存或Raw帧长度错误");
                Assert(context.RawQueueDepth == 0, "首次Flush成功后Raw队列未清零");
            });
        }

        private static void RawCapacityBackpressurePreservesAllBatches()
        {
            WithRoot(root =>
            {
                var context = new DaqAIContext("Dev1", 256, 60, 1, 1, 1, root);
                var queueFullEvents = 0;
                context.QueueFull += (_, __, ___) => Interlocked.Increment(ref queueFullEvents);
                var now = DateTime.Now;
                for (var i = 0; i < context.RawQueueCapacity; i++)
                    context.EnqueueRawData(new[,] { { (double)i } }, now.AddMilliseconds(i + 1), now.AddMilliseconds(i));

                var admissionStarted = Stopwatch.StartNew();
                var rejected = false;
                try
                {
                    context.EnqueueRawData(
                        new[,] { { 999.0 } },
                        now.AddSeconds(1),
                        now.AddSeconds(1).AddMilliseconds(-1));
                }
                catch (TimeoutException)
                {
                    rejected = true;
                }
                admissionStarted.Stop();
                Assert(rejected && admissionStarted.ElapsedMilliseconds < 2000,
                    $"Raw容量满时没有在有界时间拒绝并保留上游重试权：{admissionStarted.ElapsedMilliseconds}ms");
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();
                // 生产实现会保留同一 Owned 批并按原序重试；这里复现同一语义。
                context.EnqueueRawData(
                    new[,] { { 999.0 } },
                    now.AddSeconds(1),
                    now.AddSeconds(1).AddMilliseconds(-1));
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();

                var path = Path.Combine(root, "DAQ_Dev1_Raw_1.bin");
                Assert(new FileInfo(path).Length == (context.RawQueueCapacity + 1L) * 20L,
                    "Raw容量背压期间仍发生批次丢失");
                Assert(queueFullEvents == 1 && context.RawQueueDepth == 0,
                    "同一次Raw容量事故重复发布或队列未排空");
            });
        }

        private static void RawWriteFailureRetainsFifoForRetry()
        {
            WithRoot(root =>
            {
                var missing = Path.Combine(root, "not-created", "raw");
                var context = new DaqAIContext("Dev1", 256, 60, 1, 1, 1, missing);
                var now = DateTime.Now;
                context.EnqueueRawData(new[,] { { 7.0 } }, now, now.AddMilliseconds(-1));
                var failed = false;
                try { context.FlushRawToDiskAsync().GetAwaiter().GetResult(); }
                catch { failed = true; }
                Assert(failed && context.RawQueueDepth == 1,
                    "Raw写入失败被吞掉或FIFO所有权已释放");

                Directory.CreateDirectory(missing);
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();
                var path = Path.Combine(missing, "DAQ_Dev1_Raw_1.bin");
                Assert(File.Exists(path) && new FileInfo(path).Length == 20 && context.RawQueueDepth == 0,
                    "存储恢复后Raw保留批次未成功补写");
            });
        }

        private static void ProgramStorageLevelsExportSingleOrPair()
        {
            foreach (var level in new[]
                     {
                         StorageFormatLevel.CsvOnly,
                         StorageFormatLevel.BinOnly,
                         StorageFormatLevel.CsvAndBin
                     })
            {
                WithRoot(root =>
                {
                    var data = Path.Combine(root, "Data");
                    var index = Path.Combine(root, "Index");
                    using (var writer = new EpbDiskWriter(new DataRetentionPolicy
                    {
                        DataStorePath = data,
                        IndexAndExportPath = index,
                        FileSizeMb = 1,
                        LatestStorageLevel = level
                    }))
                    {
                        WriteCompletedCycle(writer, 1, 1, 3, DateTime.UtcNow);
                        writer.ExportLatestCyclesNow(1, 1);
                    }

                    var package = Directory.EnumerateDirectories(
                            Path.Combine(index, "Latest", "EPB1"), "*", SearchOption.TopDirectoryOnly)
                        .Single();
                    var csv = File.Exists(Path.Combine(package, "EPB1_Cycle_000001.csv"));
                    var bin = File.Exists(Path.Combine(package, "EPB1_Cycle_000001.bin"));
                    Assert(csv == (level == StorageFormatLevel.CsvOnly || level == StorageFormatLevel.CsvAndBin),
                        $"Latest CSV 格式错误：{level}");
                    Assert(bin == (level == StorageFormatLevel.BinOnly || level == StorageFormatLevel.CsvAndBin),
                        $"Latest BIN 格式错误：{level}");
                    var evidence = EpbDiskWriter.ValidateAlarmCycleSnapshotFiles(
                        csv ? Path.Combine(package, "EPB1_Cycle_000001.csv") : null,
                        bin ? Path.Combine(package, "EPB1_Cycle_000001.bin") : null,
                        1,
                        1,
                        false,
                        level);
                    Assert(evidence.IsValid && evidence.SampleCount == 3,
                        $"单格式校验失败：{level} {evidence.ValidationError}");
                });
            }

            WithRoot(root =>
            {
                var alarmDir = Path.Combine(root, "Alarm");
                using var writer = new EpbDiskWriter(new DataRetentionPolicy
                {
                    DataStorePath = Path.Combine(root, "Data"),
                    IndexAndExportPath = Path.Combine(root, "Index"),
                    FileSizeMb = 1,
                    AlarmStorageLevel = StorageFormatLevel.CsvOnly
                });
                writer.BeginCycle(2, 1, DateTime.UtcNow);
                WriteSamples(writer, 2, 2, DateTime.UtcNow);
                var evidence = writer.SealAndExportAlarmCycle(2, 1, alarmDir, DateTime.UtcNow);
                Assert(evidence.IsValid && evidence.StorageFormat == "CsvOnly" &&
                       File.Exists(evidence.CsvPath) && string.IsNullOrEmpty(evidence.BinPath),
                    "Alarm CsvOnly 封存未生成单格式有效证据");
                var alarmAdapter = new DiskWriterRecorderAdapter(writer);
                alarmAdapter.FlushRecentForAlarm(2, 1, alarmDir, includeRunningCycle: false);
                Assert(!Directory.EnumerateFiles(alarmDir, "*.bin", SearchOption.TopDirectoryOnly).Any(),
                    "Alarm=CsvOnly 时最近圈报警目录仍生成 BIN");
            });

            WithRoot(root =>
            {
                var learningDir = Path.Combine(root, "Learning");
                using var writer = new EpbDiskWriter(new DataRetentionPolicy
                {
                    DataStorePath = Path.Combine(root, "Data"),
                    IndexAndExportPath = Path.Combine(root, "Index"),
                    FileSizeMb = 1,
                    LearningStorageLevel = StorageFormatLevel.BinOnly
                });
                var learning = writer.BeginLearningCycle(3, DateTime.UtcNow);
                WriteSamples(writer, 3, 2, DateTime.UtcNow);
                var evidence = writer.SealAndExportCycle(
                    3, learning, learningDir, DateTime.UtcNow, "learning_completed");
                Assert(evidence.IsValid && evidence.StorageFormat == "BinOnly" &&
                       File.Exists(evidence.BinPath) && string.IsNullOrEmpty(evidence.CsvPath),
                    "Learning BinOnly 封存未生成 BIN 证据");
            });
        }

        private static void InvalidProgramStorageConfigFallsBack()
        {
            var warnings = new List<string>();
            var settings = new NameValueCollection
            {
                ["LatestStorageLevel"] = "None",
                ["AlarmStorageLevel"] = "bogus",
                ["LearningStorageLevel"] = "",
                ["HistoricalEnabled"] = "maybe",
                ["HistoricalRetainCyclesPerChannel"] = "0"
            };
            var policy = ProgramStoragePolicy.Load(warnings.Add, settings);
            Assert(policy.Latest == StorageFormatLevel.CsvOnly &&
                   policy.Alarm == StorageFormatLevel.CsvOnly &&
                   policy.Learning == StorageFormatLevel.BinOnly &&
                   policy.HistoricalEnabled && policy.HistoricalRetainCyclesPerChannel == 12,
                "非法程序级配置未回退到安全默认");
            Assert(warnings.Count >= 4, "非法程序级配置未记录足够告警");
        }

        private static void LegacyPairRemainsCompatibleWithSinglePolicy()
        {
            WithRoot(root =>
            {
                var csv = Path.Combine(root, "EPB1_Cycle_000001.csv");
                var bin = Path.Combine(root, "EPB1_Cycle_000001.bin");
                var ts = DateTime.UtcNow.ToLocalTime().ToBinary();
                File.WriteAllText(csv,
                    "Timestamp,RelativeTimeSeconds,Cycle,SampleIndex,EpbCurrent,GroupPressure\n" +
                    $"{DateTime.FromBinary(ts):yyyy-MM-dd HH:mm:ss.fffffff},0.0000000,1,0,1.000,2.0\n",
                    Encoding.UTF8);
                using (var stream = File.Create(bin))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(ts);
                    writer.Write(1);
                    writer.Write(0);
                    writer.Write(1.0);
                    writer.Write(2.0);
                }
                var evidence = EpbDiskWriter.ValidateAlarmCycleSnapshotFiles(
                    csv, bin, 1, 1, false, StorageFormatLevel.CsvOnly);
                Assert(evidence.IsValid && evidence.SampleCount == 1 &&
                       evidence.StorageFormat == "CsvAndBin",
                    "旧CSV+BIN包未在单格式策略下保持兼容");
            });
        }

        private static void AlarmFinalizedRecoveryHonorsSingleFormat()
        {
            WithRoot(root =>
            {
                var csv = Path.Combine(root, "EPB1_Cycle_000007.csv");
                var ts = DateTime.UtcNow.ToLocalTime().ToBinary();
                File.WriteAllText(csv,
                    "Timestamp,RelativeTimeSeconds,Cycle,SampleIndex,EpbCurrent,GroupPressure\n" +
                    $"{DateTime.FromBinary(ts):yyyy-MM-dd HH:mm:ss.fffffff},0.0000000,7,0,1.000,2.0\n",
                    Encoding.UTF8);
                var recorder = new FakeAlarmAttemptRecorder(csv);
                var original = new AlarmCycleSnapshotEvidence
                {
                    StorageFormat = StorageFormatLevel.CsvOnly.ToString(),
                    ValidationError = "not claimed"
                };
                var recovered = AlarmCycleSnapshotRecovery.TryExportFinalizedCycle(
                    recorder,
                    1,
                    7,
                    root,
                    original);
                Assert(recorder.LastSaveCsv && !recorder.LastSaveBin,
                    "报警终态回读没有按CsvOnly请求单格式");
                Assert(recovered.IsValid && recovered.SampleCount == 1 &&
                       recovered.StorageFormat == StorageFormatLevel.CsvOnly.ToString(),
                    "报警终态CSV-only回读校验失败");
            });

            WithRoot(root =>
            {
                var data = Path.Combine(root, "Data");
                var index = Path.Combine(root, "Index");
                using (var writer = new EpbDiskWriter(new DataRetentionPolicy
                {
                    DataStorePath = data,
                    IndexAndExportPath = index,
                    FileSizeMb = 1,
                    LatestStorageLevel = StorageFormatLevel.BinOnly
                }))
                {
                    WriteCompletedCycle(writer, 1, 1, 2, DateTime.UtcNow);
                    var generic = Path.Combine(root, "Generic");
                    writer.ExportLatestCyclesTo(1, 1, generic, false);
                    Assert(File.Exists(Path.Combine(generic, "EPB1_Cycle_000001.csv")) &&
                           File.Exists(Path.Combine(generic, "EPB1_Cycle_000001.bin")),
                        "Latest=BinOnly 错误影响通用 FlushRecentTo 双格式导出");
                }
            });
        }

        private static void LearningRetentionPolicyParsing()
        {
            var warnings = new List<string>();
            var invalid = new NameValueCollection
            {
                ["LearningRetentionMode"] = "None",
                ["LearningSuccessfulRunRetainCount"] = "0",
                ["LearningFailedRunRetainCount"] = "-1"
            };
            var policy = ProgramStoragePolicy.Load(warnings.Add, invalid);
            Assert(policy.LearningRetentionMode == LearningRetentionMode.Count &&
                   policy.LearningSuccessfulRunRetainCount == 3 &&
                   policy.LearningFailedRunRetainCount == 3 && warnings.Count >= 3,
                "Learning非法/0配置未严格回退");
            var unlimited = ProgramStoragePolicy.Load(null, new NameValueCollection
            {
                ["LearningRetentionMode"] = "Unlimited",
                ["LearningSuccessfulRunRetainCount"] = "100",
                ["LearningFailedRunRetainCount"] = "1"
            });
            Assert(unlimited.LearningRetentionMode == LearningRetentionMode.Unlimited &&
                   unlimited.LearningSuccessfulRunRetainCount == 100 &&
                   unlimited.LearningFailedRunRetainCount == 1,
                "Unlimited/有效计数未解析");
        }

        private static void LearningRunManifestAtomicAndHash()
        {
            WithRoot(root =>
            {
                var chain = Path.Combine(root, "LearningCycles", Guid.NewGuid().ToString("N"));
                var executionId = Guid.NewGuid();
                var evidence = Path.Combine(chain, "Executions", executionId.ToString("N"), "EPB01", "Learning_0001");
                Directory.CreateDirectory(evidence);
                var evidenceFile = Path.Combine(evidence, "EPB01.csv");
                File.WriteAllText(evidenceFile, "h\nrow\n");
                WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, "Successful");
                var identity = new RunChainIdentity(executionId, Guid.Parse(Path.GetFileName(chain)), Guid.Empty, 1, 7);
                var manifest = LearningRunManifestStore.BuildFromDirectory(
                    chain, identity, "2.13.0.3", "cfg", new[] { 1 }, 1, 0,
                    "Successful", string.Empty, "modelhash", "receipt");
                manifest.ModelCommitReceipts = new List<string> { "EPB01:sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                Assert(LearningRunManifestStore.PublishAtomic(chain, manifest, out var path) && File.Exists(path),
                    "manifest未原子发布");
                Assert(LearningRunManifestStore.TryReadValidated(path, out var loaded, out var reason),
                    "manifest校验失败：" + reason);
                var receiptPath = LearningRunManifestStore.AttemptReceiptPath(evidence);
                File.AppendAllText(receiptPath, "tamper\n");
                Assert(!LearningRunManifestStore.TryReadValidated(path, out _, out reason) &&
                       reason == "ManifestArtifactHashMismatch", "manifest未发现证据篡改");
            });
        }

        private static void LearningModelSaveFailureIsNotEligible()
        {
            WithRoot(root =>
            {
                // A file occupying the would-be model directory forces the
                // atomic SaveWithReceipt path to fail before replacing any
                // model file.  The original marker remains untouched.
                var blockedRoot = Path.Combine(root, "blocked-model-root");
                File.WriteAllText(blockedRoot, "marker");
                var store = new EpbAdaptiveProfileStore(blockedRoot);
                var profile = new EpbAdaptiveProfile
                {
                    Channel = 1,
                    ValidSampleCount = 5,
                    UpdatedUtc = DateTime.UtcNow,
                    ForwardClampMedianMs = 10,
                    ReverseReleaseMedianMs = 10
                };
                var threw = false;
                try { store.SaveWithReceipt(profile); }
                catch { threw = true; }
                Assert(threw && File.ReadAllText(blockedRoot) == "marker" &&
                       !File.Exists(Path.Combine(blockedRoot, "EpbAdaptiveProfiles.xml")),
                    "模型保存故障未保持旧文件/未显式失败");

                var chain = Path.Combine(root, "LearningCycles", Guid.NewGuid().ToString("N"));
                var executionId = Guid.NewGuid();
                var evidence = Path.Combine(chain, "Executions", executionId.ToString("N"),
                    "EPB01", "Learning_0001");
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "e.csv"), "h\nrow\n");
                WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, "Successful");
                var id = Guid.Parse(Path.GetFileName(chain));
                var manifest = LearningRunManifestStore.BuildFromDirectory(
                    chain, new RunChainIdentity(executionId, id), "2.13.0.3", "",
                    new[] { 1 }, 1, 0, "Successful", string.Empty,
                    "sha256:model", "sha256:receipt");
                // Deliberately omit the per-channel receipt: a successful
                // learning chain must not become an eligible retention target.
                LearningRunManifestStore.PublishAtomic(chain, manifest, out var path);
                Assert(!LearningRunManifestStore.TryReadValidated(path, out _, out var reason) &&
                       reason == "ManifestSuccessEvidenceIncomplete",
                    "缺少模型commit receipt仍被判为Successful/可清理");
            });
        }

        private static void LearningRetentionPlannerConverges3Plus3()
        {
            WithRoot(root =>
            {
                var now = DateTime.UtcNow;
                for (var i = 0; i < 4; i++)
                {
                    var chain = Path.Combine(root, i.ToString("x32"));
                    var evidence = Path.Combine(chain, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, "e.csv"), "h\nrow\n");
                    WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, "Successful");
                    var id = Guid.Parse(Path.GetFileName(chain));
                    var m = LearningRunManifestStore.BuildFromDirectory(chain,
                        new RunChainIdentity(id, id), "2.13.0.3", "", new[] { 1 }, 1, 0,
                        "Successful", string.Empty, "model", "receipt");
                    m.ModelCommitReceipts = new List<string> { "EPB01:sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                    LearningRunManifestStore.PublishAtomic(chain, m, out _);
                    File.SetLastWriteTimeUtc(LearningRunManifestStore.ManifestPath(chain), now.AddMinutes(i));
                }
                for (var i = 4; i < 8; i++)
                {
                    var chain = Path.Combine(root, i.ToString("x32"));
                    var evidence = Path.Combine(chain, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, "e.csv"), "h\nrow\n");
                    WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, "Failed");
                    var id = Guid.Parse(Path.GetFileName(chain));
                    var m = LearningRunManifestStore.BuildFromDirectory(chain,
                        new RunChainIdentity(id, id), "2.13.0.3", "", new[] { 1 }, 1, 0,
                        "Failed", "fault");
                    LearningRunManifestStore.PublishAtomic(chain, m, out _);
                    File.SetLastWriteTimeUtc(LearningRunManifestStore.ManifestPath(chain), now.AddMinutes(i));
                }
                var candidates = LearningRetentionPlanner.FindCandidates(root, 3, 3, null);
                Assert(candidates.Count == 2, "4成功+4失败未收敛为各保留3条");
            });
        }

        private static void FailedLearningChainIsSkippedWhenProtected()
        {
            WithRoot(root =>
            {
                var chain = Path.Combine(root, Guid.NewGuid().ToString("N"));
                var evidence = Path.Combine(chain, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "e.csv"), "h\nrow\n");
                WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, "Failed");
                var id = Guid.Parse(Path.GetFileName(chain));
                var manifest = LearningRunManifestStore.BuildFromDirectory(
                    chain, new RunChainIdentity(id, id, Guid.Empty, 0, 3), "2.13.0.3", "",
                    new[] { 1 }, 1, 0, "Failed", "fault");
                LearningRunManifestStore.PublishAtomic(chain, manifest, out _);
                var candidates = Controller.LearningRetentionPlanner.FindCandidates(
                    root, 0, 0, null, null,
                    path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(chain),
                        StringComparison.OrdinalIgnoreCase));
                Assert(candidates.Count == 0 && Directory.Exists(chain),
                    "受保护失败Learning链仍被扫描为可删除候选");
            });
        }

        private static void HousekeepingScanRunsOffCallerThread()
        {
            WithRoot(root =>
            {
                var callerThread = Thread.CurrentThread.ManagedThreadId;
                var callbackSeen = new ManualResetEventSlim(false);
                var sameThread = 0;
                for (var i = 0; i < 256; i++)
                    Directory.CreateDirectory(Path.Combine(root, i.ToString("x32", CultureInfo.InvariantCulture)));
                try
                {
                    using (var service = new Controller.DataHousekeepingService(
                        root,
                        new Controller.DataHousekeepingOptions
                        {
                            IsCurrentPath = path =>
                            {
                                if (Thread.CurrentThread.ManagedThreadId == callerThread)
                                    Interlocked.Exchange(ref sameThread, 1);
                                callbackSeen.Set();
                                return false;
                            },
                            BatchInterval = TimeSpan.Zero
                        }))
                    {
                        Assert(callbackSeen.Wait(TimeSpan.FromSeconds(5)),
                            "Housekeeping worker未执行目录扫描");
                        Assert(Volatile.Read(ref sameThread) == 0,
                            "Housekeeping目录扫描仍发生在调用线程");
                    }
                }
                finally { callbackSeen.Dispose(); }
            });
        }

        private static void SaveWithReceiptReadbackFailureRollsBackDiskAndMemory()
        {
            WithRoot(root =>
            {
                var store = new EpbAdaptiveProfileStore(root);
                var original = new EpbAdaptiveProfile
                {
                    Channel = 1,
                    ValidSampleCount = 5,
                    UpdatedUtc = DateTime.UtcNow,
                    ForwardClampMedianMs = 10,
                    ReverseReleaseMedianMs = 11
                };
                store.Save(original);
                var modelPath = store.FilePath;
                var beforeBytes = File.ReadAllBytes(modelPath);
                var changed = original.Clone();
                changed.ForwardClampMedianMs = 99;
                var injected = false;
                EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = stage =>
                {
                    if (stage == "AfterReplaceBeforeReadback" && !injected)
                    {
                        injected = true;
                        throw new IOException("injected readback failure");
                    }
                };
                try
                {
                    var threw = false;
                    try { store.SaveWithReceipt(changed); }
                    catch (IOException) { threw = true; }
                    Assert(threw && beforeBytes.SequenceEqual(File.ReadAllBytes(modelPath)),
                        "SaveWithReceipt读回故障未回滚原始磁盘字节");
                    var inMemory = store.GetOrCreate(1);
                    Assert(Math.Abs(inMemory.ForwardClampMedianMs - original.ForwardClampMedianMs) < 1e-9,
                        "SaveWithReceipt读回故障未回滚内存模型");
                }
                finally { EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = null; }
            });
        }

        private static void SaveWithReceiptRollbackFailureIsFatalAndKeepsBackup()
        {
            WithRoot(root =>
            {
                var store = new EpbAdaptiveProfileStore(root);
                var original = new EpbAdaptiveProfile
                {
                    Channel = 1,
                    ValidSampleCount = 7,
                    UpdatedUtc = DateTime.UtcNow,
                    ForwardClampMedianMs = 12,
                    ReverseReleaseMedianMs = 13
                };
                store.Save(original);
                var beforeBytes = File.ReadAllBytes(store.FilePath);
                var changed = original.Clone();
                changed.ForwardClampMedianMs = 99;

                EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = stage =>
                {
                    if (stage == "AfterReplaceBeforeReadback" || stage == "BeforeRollback")
                        throw new IOException("injected " + stage + " failure");
                };
                try
                {
                    EpbAdaptiveProfilePersistenceFatalException fatal = null;
                    try { store.SaveWithReceipt(changed); }
                    catch (EpbAdaptiveProfilePersistenceFatalException ex) { fatal = ex; }
                    Assert(fatal != null && fatal.IsFatal,
                        "SaveWithReceipt回滚失败未抛专用致命异常");
                    Assert(fatal is OperationCanceledException,
                        "磁盘回滚致命异常未使用不可重试的owner-stop语义");
                    Assert(!string.IsNullOrWhiteSpace(fatal.BackupPath) &&
                           File.Exists(fatal.BackupPath),
                        "回滚失败后未保留原始模型备份");
                    Assert(fatal.WriteFailure is IOException &&
                           fatal.RollbackFailure is IOException,
                        "致命异常未保留写入/回滚两段故障原因");
                    Assert(store.GetOrCreate(1).ForwardClampMedianMs == original.ForwardClampMedianMs,
                        "回滚失败后内存模型仍推进");
                    Assert(beforeBytes.SequenceEqual(File.ReadAllBytes(fatal.BackupPath)),
                        "故障注入后保留备份未保持原始模型字节");
                    // The active file may still contain the replaced bytes when
                    // rollback itself is fault-injected; only the preserved
                    // backup is authoritative for operator recovery.
                    Assert(File.Exists(store.FilePath), "回滚失败后当前模型文件意外丢失");
                }
                finally { EpbAdaptiveProfileStore.SaveWithReceiptFailureInjection = null; }
            });
        }

        private static void HousekeepingProcessConvergesAndProtects()
        {
            WithRoot(root =>
            {
                var now = DateTime.UtcNow;
                var learningRoot = Path.Combine(root, "LearningCycles");
                Directory.CreateDirectory(learningRoot);
                var chains = new List<string>();
                for (var i = 0; i < 8; i++)
                {
                    var chain = Path.Combine(learningRoot, i.ToString("x32"));
                    var evidence = Path.Combine(chain, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, "e.csv"), "h\nrow\n");
                    var id = Guid.Parse(Path.GetFileName(chain));
                    var status = i < 4 ? "Successful" : "Failed";
                    WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, status);
                    var manifest = LearningRunManifestStore.BuildFromDirectory(
                        chain, new RunChainIdentity(id, id), "2.13.0.3", "", new[] { 1 }, 1, 0,
                        status, status == "Failed" ? "fault" : string.Empty,
                        i < 4 ? "model" : string.Empty, i < 4 ? "receipt" : string.Empty);
                    if (i < 4)
                        manifest.ModelCommitReceipts = new List<string> { "EPB01:sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                    LearningRunManifestStore.PublishAtomic(chain, manifest, out _);
                    File.SetLastWriteTimeUtc(LearningRunManifestStore.ManifestPath(chain), now.AddMinutes(i));
                    chains.Add(chain);
                }
                var unknown = Path.Combine(learningRoot, "f0000000000000000000000000000000");
                Directory.CreateDirectory(unknown);
                File.WriteAllText(Path.Combine(unknown, "legacy.bin"), "legacy");
                var current = chains[3];
                var tmp = Path.Combine(learningRoot, "e0000000000000000000000000000000");
                var tmpEvidence = Path.Combine(tmp, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                Directory.CreateDirectory(tmpEvidence);
                File.WriteAllText(Path.Combine(tmpEvidence, "x.tmp"), "tmp");
                var tmpManifest = LearningRunManifestStore.BuildFromDirectory(
                    tmp, new RunChainIdentity(Guid.NewGuid(), Guid.Parse(Path.GetFileName(tmp))),
                    "2.13.0.3", "", new[] { 1 }, 1, 0, "Failed", "tmp");
                LearningRunManifestStore.PublishAtomic(tmp, tmpManifest, out _);
                var badHash = Path.Combine(learningRoot, "d0000000000000000000000000000000");
                var badEvidence = Path.Combine(badHash, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                Directory.CreateDirectory(badEvidence);
                var badFile = Path.Combine(badEvidence, "bad.csv");
                File.WriteAllText(badFile, "h\nrow\n");
                var badManifest = LearningRunManifestStore.BuildFromDirectory(
                    badHash, new RunChainIdentity(Guid.NewGuid(), Guid.Parse(Path.GetFileName(badHash))),
                    "2.13.0.3", "", new[] { 1 }, 1, 0, "Failed", "bad");
                LearningRunManifestStore.PublishAtomic(badHash, badManifest, out _);
                File.AppendAllText(badFile, "tampered\n");

                using (var service = new Controller.DataHousekeepingService(
                    learningRoot,
                    new Controller.DataHousekeepingOptions
                    {
                        DryRun = false,
                        AutoScanOnStart = false,
                        BatchInterval = TimeSpan.Zero,
                        IsCurrentPath = path => string.Equals(
                            Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)
                    }))
                {
                    var candidates = LearningRetentionPlanner.FindCandidates(learningRoot, 3, 3,
                                 path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    using (var busyService = new Controller.DataHousekeepingService(
                        learningRoot,
                        new Controller.DataHousekeepingOptions
                        {
                            BatchInterval = TimeSpan.Zero,
                            AutoScanOnStart = false,
                            BusyStateProvider = () => new Controller.HousekeepingBusyState { StopBusy = true }
                        }))
                    {
                        Assert(!busyService.ProcessOne(candidates[0]) && Directory.Exists(candidates[0]),
                            "Housekeeping忙碌窗口未延期候选");
                    }
                    foreach (var candidate in candidates)
                        Assert(service.ProcessOne(candidate), "Housekeeping未处理候选：" + candidate);
                }
                Assert(Directory.Exists(badHash), "Housekeeping误删哈希损坏目录");
                var remaining = Directory.EnumerateDirectories(learningRoot, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith(".retention-staging-", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var terminalRemaining = remaining.Where(path =>
                    !string.Equals(Path.GetFileName(path), Path.GetFileName(unknown), StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileName(path), Path.GetFileName(tmp), StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileName(path), Path.GetFileName(badHash), StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert(terminalRemaining.Length == 6,
                    "Housekeeping实际删除后未保留3成功+3失败：" + terminalRemaining.Length +
                    " [" + string.Join(",", terminalRemaining.Select(Path.GetFileName)) + "]");
                Assert(Directory.Exists(unknown) && Directory.Exists(current) && Directory.Exists(tmp),
                    "Housekeeping误删Unknown/current/tmp目录");
            });
        }

        private static void UnlimitedRetentionNeverDeletesAfterRestart()
        {
            WithRoot(root =>
            {
                var now = DateTime.UtcNow;
                for (var i = 0; i < 8; i++)
                {
                    var chain = Path.Combine(root, i.ToString("x32", CultureInfo.InvariantCulture));
                    var evidence = Path.Combine(
                        chain, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, "e.csv"), "h\nrow\n");
                    var status = i < 4 ? "Successful" : "Failed";
                    WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, status);
                    var id = Guid.Parse(Path.GetFileName(chain));
                    var manifest = LearningRunManifestStore.BuildFromDirectory(
                        chain, new RunChainIdentity(id, id), "2.13.0.3", "",
                        new[] { 1 }, 1, 0, status,
                        status == "Failed" ? "fault" : string.Empty,
                        status == "Successful" ? "model" : string.Empty,
                        status == "Successful" ? "receipt" : string.Empty);
                    if (status == "Successful")
                        manifest.ModelCommitReceipts = new List<string>
                        {
                            "EPB01:sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                        };
                    Assert(LearningRunManifestStore.PublishAtomic(chain, manifest, out _),
                        "Unlimited测试链manifest发布失败");
                    File.SetLastWriteTimeUtc(
                        LearningRunManifestStore.ManifestPath(chain), now.AddMinutes(i));
                }

                var options = new Controller.DataHousekeepingOptions
                {
                    AutoScanOnStart = false,
                    RetentionEnabled = false,
                    BatchInterval = TimeSpan.Zero
                };
                using (var initial = new Controller.DataHousekeepingService(root, options))
                {
                    Assert(initial.EnqueueNewManifestChains() == 0 &&
                           !initial.Enqueue(Path.Combine(root, "00000000000000000000000000000000")) &&
                           !initial.ProcessOne(Path.Combine(root, "00000000000000000000000000000000")),
                        "Unlimited服务仍接受扫描/候选删除命令");
                }

                // Simulate a process restart.  AutoScanOnStart is deliberately
                // true here; RetentionEnabled must still suppress the worker's
                // startup scan and any deletion.
                using (var restarted = new Controller.DataHousekeepingService(
                    root,
                    new Controller.DataHousekeepingOptions
                    {
                        AutoScanOnStart = true,
                        RetentionEnabled = false,
                        BatchInterval = TimeSpan.Zero
                    }))
                {
                    Thread.Sleep(250);
                    Assert(restarted.Statistics.Queued == 0,
                        "Unlimited重启构造错误投递startup扫描");
                }

                var remaining = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => !Path.GetFileName(path).StartsWith(".retention-staging-", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert(remaining.Length == 8,
                    "Unlimited重启后4+4链数量变化：" + remaining.Length);
            });
        }

        private static void ReactivatedRootPreservesOwnedStaging()
        {
            WithRoot(root =>
            {
                var rootId = Guid.NewGuid();
                var rootIdText = rootId.ToString("N");
                var chain = Path.Combine(root, rootIdText);
                var evidence = Path.Combine(
                    chain, "Executions", Guid.NewGuid().ToString("N"), "EPB01", "Learning_0001");
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "a.csv"), "h\nrow-a\n");
                File.WriteAllText(Path.Combine(evidence, "b.csv"), "h\nrow-b\n");
                WriteTestAttemptReceipt(chain, evidence, "Learning", 1, 1, 1, "Failed");
                var manifest = LearningRunManifestStore.BuildFromDirectory(
                    chain,
                    new RunChainIdentity(rootId, rootId),
                    "2.13.0.3",
                    string.Empty,
                    new[] { 1 },
                    1,
                    0,
                    "Failed",
                    "fault");
                Assert(LearningRunManifestStore.PublishAtomic(chain, manifest, out _),
                    "staging身份测试manifest发布失败");

                var reactivated = false;
                Func<string, bool> currentPath = path =>
                {
                    var full = Path.GetFullPath(path);
                    if (!string.Equals(Path.GetFileName(full), rootIdText,
                            StringComparison.OrdinalIgnoreCase))
                        return false;

                    // Once the first one-file batch has completed, the next
                    // batch callback sees fewer source artifacts.  Recreate the
                    // original RootRunId at that boundary and mark it current.
                    if (!reactivated)
                    {
                        var activeStaging = Directory.EnumerateDirectories(
                                root, ".retention-staging-*", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
                        var remaining = activeStaging == null
                            ? int.MaxValue
                            : Directory.EnumerateFiles(activeStaging, "*", SearchOption.AllDirectories)
                                .Count(file => !file.EndsWith(".retention.json", StringComparison.OrdinalIgnoreCase));
                        if (remaining <= 2)
                        {
                            Directory.CreateDirectory(full);
                            reactivated = true;
                        }
                    }
                    return reactivated;
                };

                string staging;
                using (var service = new Controller.DataHousekeepingService(
                    root,
                    new Controller.DataHousekeepingOptions
                    {
                        AutoScanOnStart = false,
                        BatchInterval = TimeSpan.Zero,
                        MaxFilesPerBatch = 1,
                        IsCurrentPath = currentPath
                    }))
                {
                    Assert(!service.ProcessOne(chain),
                        "Root重新激活后staging仍被报告为已完成删除");
                    staging = Directory.EnumerateDirectories(
                            root, ".retention-staging-*", SearchOption.TopDirectoryOnly)
                        .SingleOrDefault();
                    Assert(reactivated && staging != null,
                        "第一删除批次后未重建Root或未保留staging");
                    Assert(Directory.Exists(chain),
                        "Root重新激活目录未保留");
                }

                // Simulate restart: startup continuation must re-check the
                // sidecar/source identity before enqueueing old staging.
                using (var restarted = new Controller.DataHousekeepingService(
                    root,
                    new Controller.DataHousekeepingOptions
                    {
                        AutoScanOnStart = true,
                        BatchInterval = TimeSpan.Zero,
                        MaxFilesPerBatch = 1,
                        // Recovery/checkpoint identity is loaded before the
                        // startup scan command is allowed to run.
                        ProtectedRootIds = new[] { rootId },
                        IsCurrentPath = path => false
                    }))
                {
                    Thread.Sleep(250);
                    Assert(Directory.Exists(staging),
                        "重启auto scan误删已重新激活Root对应的旧staging");
                    Assert(restarted.Statistics.Skipped > 0,
                        "重启续扫未记录受保护/当前staging跳过");
                }
            });
        }

        private sealed class FakeAlarmAttemptRecorder : IEpbCycleRecorder, ICycleAttemptEvidenceExporter
        {
            private readonly string _csvPath;
            public bool LastSaveCsv { get; private set; }
            public bool LastSaveBin { get; private set; }

            public FakeAlarmAttemptRecorder(string csvPath) { _csvPath = csvPath; }
            public void BeginCycle(int epbId, int cycleNumber, DateTime utcNow) { }
            public int BeginLearningCycle(int epbId, DateTime utcNow) => -1;
            public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures) { }
            public int GetCurrentCycleSampleCount(int epbId) => 1;
            public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(
                int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc) => null;
            public AlarmCycleSnapshotEvidence SealAndExportCycle(
                int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc, string status) => null;
            public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status) { }
            public void FlushRecent(int epbId, int lastNCycles) { }
            public int GetLastCycleNumber(int ch) => 0;
            public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle) { }

            public CycleSnapshotEvidence ExportCycleAttemptTo(
                int epbId,
                int cycleNumber,
                string exportDir,
                bool saveCsv,
                bool saveBin)
            {
                LastSaveCsv = saveCsv;
                LastSaveBin = saveBin;
                return new CycleSnapshotEvidence
                {
                    EpbId = epbId,
                    CycleNumber = cycleNumber,
                    SampleCount = 1,
                    CsvPath = saveCsv ? _csvPath : null,
                    BinPath = saveBin ? Path.Combine(exportDir, "missing.bin") : null
                };
            }
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

        private static void WriteTestAttemptReceipt(
            string chain,
            string evidence,
            string phase,
            int ordinal,
            int attempt,
            int internalCycle,
            string status)
        {
            var artifacts = Directory.EnumerateFiles(evidence, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFileName(path),
                    LearningRunManifestStore.AttemptReceiptFileName,
                    StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    var relative = Path.GetFullPath(path)
                        .Substring(Path.GetFullPath(chain).TrimEnd(Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar).Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    return new LearningArtifact
                    {
                        RelativePath = relative,
                        Format = info.Extension.TrimStart('.').ToUpperInvariant(),
                        Sha256 = LearningRunManifestStore.ComputeSha256(path),
                        Bytes = info.Length,
                        SampleCount = 1,
                        StartedUtc = info.CreationTimeUtc,
                        CompletedUtc = info.LastWriteTimeUtc
                    };
                }).ToList();
            LearningRunManifestStore.WriteAttemptReceiptAtomic(
                evidence,
                new LearningAttemptReceipt
                {
                    Phase = phase,
                    LogicalOrdinal = ordinal,
                    Attempt = attempt,
                    InternalCycle = internalCycle,
                    Status = status,
                    SampleCount = 1,
                    StartedUtc = DateTime.UtcNow.AddSeconds(-1),
                    CompletedUtc = DateTime.UtcNow,
                    Artifacts = artifacts
                });
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
