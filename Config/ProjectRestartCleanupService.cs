using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Config
{
    public sealed class ProjectRestartResult
    {
        public bool Succeeded { get; internal set; }
        public string AuditPath { get; internal set; }
        public string ConfigBackupPath { get; internal set; }
        public long BytesIsolated { get; internal set; }
        public long BytesDeleted { get; internal set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// 仅供监控关闭后的显式“清空进度并重新开始”使用。先隔离旧运行数据，
    /// 再原子保存清零配置；删除失败不会把旧数据放回活动路径。
    /// </summary>
    public static class ProjectRestartCleanupService
    {
        private static readonly string[] RuntimeDirectories =
        {
            "Latest",
            "HistoricalSnapshots",
            "AlarmSnapshots",
            "WarningSnapshots",
            "IncidentSnapshots",
            "LearningCycles",
            "PowerSupplyTelemetry"
        };

        private static readonly string[] RuntimeFiles =
        {
            "index.db",
            "index.db-wal",
            "index.db-shm"
        };

        public static ProjectRestartResult ResetForFreshLearning(
            string projectRoot,
            TestConfig config,
            IAppLogger log = null,
            Action clearMatchingRecoveryCheckpoint = null,
            DateTime? localNow = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var root = ValidateProjectRoot(projectRoot);
            var now = localNow ?? DateTime.Now;
            var stamp = now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var logDirectory = Path.Combine(root, "log");
            Directory.CreateDirectory(logDirectory);
            var result = new ProjectRestartResult
            {
                AuditPath = Path.Combine(logDirectory, $"reset-audit-{stamp}.log"),
                ConfigBackupPath = Path.Combine(logDirectory, $"reset-config-{stamp}.xml")
            };
            var audit = new List<string>
            {
                $"Started={now:O}",
                "Mode=ClearProgressAndFreshLearning",
                $"ProjectRoot={root}"
            };

            var projectConfigPath = Path.Combine(root, "Config", "TestConfig.xml");
            var originalConfig = File.ReadAllBytes(projectConfigPath);
            File.WriteAllBytes(result.ConfigBackupPath, originalConfig);

            var staging = Path.Combine(root, $".reset-staging-{stamp}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var moved = new List<Tuple<string, string, long>>();
            var committed = false;
            try
            {
                RotateActiveLogs(logDirectory, now, audit, result);
                foreach (var source in EnumerateResetTargets(root))
                {
                    var bytes = MeasureBytes(source);
                    var destination = Path.Combine(staging, Path.GetFileName(source));
                    destination = EnsureUniquePath(destination);
                    if (Directory.Exists(source)) Directory.Move(source, destination);
                    else File.Move(source, destination);
                    moved.Add(Tuple.Create(source, destination, bytes));
                    result.BytesIsolated += bytes;
                    audit.Add($"ISOLATED\t{bytes}\t{source}");
                }

                ResetProgressKeepProjectSettings(config);
                ConfigLoader.SaveTest(projectConfigPath, config);
                committed = true;

                try { clearMatchingRecoveryCheckpoint?.Invoke(); }
                catch (Exception ex)
                {
                    AddWarning(result, audit, log, $"无人值守恢复检查点清理失败：{ex.Message}");
                }

                try
                {
                    Directory.Delete(staging, true);
                    result.BytesDeleted = result.BytesIsolated;
                    audit.Add($"DELETED_STAGING\t{result.BytesDeleted}\t{staging}");
                }
                catch (Exception ex)
                {
                    AddWarning(
                        result,
                        audit,
                        log,
                        $"旧数据已与活动路径隔离，但暂存目录删除未完成：{staging} Error={ex.Message}");
                }

                result.Succeeded = true;
                audit.Add("Result=SUCCESS");
                audit.Add($"BytesIsolated={result.BytesIsolated}");
                audit.Add($"BytesDeleted={result.BytesDeleted}");
                audit.Add($"Finished={DateTime.Now:O}");
                File.WriteAllLines(result.AuditPath, audit, new UTF8Encoding(false));
                log?.Info(
                    $"项目进度和旧运行数据已清空，将重新执行自适应学习。Audit={result.AuditPath}",
                    "配置");
                return result;
            }
            catch (Exception ex)
            {
                audit.Add($"FAILED\t{ex.GetType().Name}\t{ex.Message}");
                if (!committed)
                {
                    foreach (var item in moved.AsEnumerable().Reverse())
                    {
                        try
                        {
                            if (Directory.Exists(item.Item2)) Directory.Move(item.Item2, item.Item1);
                            else if (File.Exists(item.Item2)) File.Move(item.Item2, item.Item1);
                            audit.Add($"ROLLED_BACK\t{item.Item1}");
                        }
                        catch (Exception rollbackException)
                        {
                            audit.Add($"ROLLBACK_FAILED\t{item.Item1}\t{rollbackException.Message}");
                        }
                    }
                    RestoreConfigAtomic(projectConfigPath, originalConfig);
                }
                audit.Add("Result=FAILED");
                audit.Add($"Finished={DateTime.Now:O}");
                try { File.WriteAllLines(result.AuditPath, audit, new UTF8Encoding(false)); } catch { }
                log?.Error("项目清空失败，未允许切换到半重置状态。", "配置", ex);
                throw;
            }
        }

        private static IEnumerable<string> EnumerateResetTargets(string root)
        {
            foreach (var name in RuntimeDirectories)
            {
                var path = EnsureChild(root, Path.Combine(root, name));
                if (!Directory.Exists(path)) continue;
                if (ContainsReparsePoint(path))
                    throw new InvalidOperationException($"运行数据目录包含重解析点或无法安全枚举：{path}");
                yield return path;
            }
            foreach (var name in RuntimeFiles)
            {
                var path = EnsureChild(root, Path.Combine(root, name));
                if (!File.Exists(path)) continue;
                if (ContainsReparsePoint(path))
                    throw new InvalidOperationException($"运行数据文件是重解析点或无法安全访问：{path}");
                yield return path;
            }

            var configDirectory = Path.Combine(root, "Config");
            if (!Directory.Exists(configDirectory)) yield break;
            foreach (var path in Directory.EnumerateFiles(
                         configDirectory,
                         "EpbAdaptiveProfiles.xml*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, "EpbAdaptiveProfiles.xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "EpbAdaptiveProfiles.xml.bak", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("EpbAdaptiveProfiles.xml.corrupt.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("EpbAdaptiveProfiles.xml.tmp-", StringComparison.OrdinalIgnoreCase))
                {
                    if (ContainsReparsePoint(path))
                        throw new InvalidOperationException($"自适应模型文件是重解析点或无法安全访问：{path}");
                    yield return EnsureChild(root, path);
                }
            }
        }

        private static void ResetProgressKeepProjectSettings(TestConfig config)
        {
            config.EnsureEpbRecords(12);
            foreach (var record in config.EpbRecords)
            {
                var total = Math.Max(0, record.TotalCount);
                record.RunCount = 0;
                record.Status = EpbTestStatus.NotStarted;
                record.StartTime = null;
                record.LatestStartTime = null;
                record.RunTime = "0.00:00:00";
                record.TotalCount = total;
            }
        }

        private static void RotateActiveLogs(
            string logDirectory,
            DateTime now,
            ICollection<string> audit,
            ProjectRestartResult result)
        {
            foreach (var stem in new[] { "run", "warning", "error", "ui-info" })
            {
                var active = Path.Combine(logDirectory, stem + ".log");
                if (!File.Exists(active) || new FileInfo(active).Length == 0) continue;
                try
                {
                    var archiveDate = File.GetLastWriteTime(active).Date;
                    if (archiveDate == DateTime.MinValue.Date) archiveDate = now.Date;
                    var sequence = 1;
                    string archive;
                    do
                    {
                        archive = Path.Combine(
                            logDirectory,
                            $"{stem}.{archiveDate:yyyyMMdd}.{sequence:000}.log");
                        sequence++;
                    } while (File.Exists(archive));
                    File.Move(active, archive);
                    audit.Add($"ROTATED\t{active}\t{archive}");
                }
                catch (Exception ex)
                {
                    var message = $"活动日志轮转失败：{active} Error={ex.Message}";
                    AddWarning(result, audit, null, message);
                    throw new IOException(message, ex);
                }
            }
        }

        private static string ValidateProjectRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("项目根目录不能为空。", nameof(projectRoot));
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var volumeRoot = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("禁止在磁盘根目录执行项目清空。");
            if (!File.Exists(Path.Combine(root, "Config", "TestConfig.xml")))
                throw new InvalidOperationException("目标目录缺少 Config\\TestConfig.xml，不是合法项目根目录。");
            return root;
        }

        private static string EnsureChild(string root, string path)
        {
            var full = Path.GetFullPath(path);
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"目标越出项目根目录：{full}");
            return full;
        }

        private static bool ContainsReparsePoint(string path)
        {
            try
            {
                var pending = new Stack<string>();
                pending.Push(path);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                    if ((attributes & FileAttributes.Directory) == 0) continue;
                    foreach (var child in Directory.EnumerateFileSystemEntries(
                                 current,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        var childAttributes = File.GetAttributes(child);
                        if ((childAttributes & FileAttributes.ReparsePoint) != 0) return true;
                        if ((childAttributes & FileAttributes.Directory) != 0) pending.Push(child);
                    }
                }
                return false;
            }
            catch
            {
                // 无法完整证明安全时，不把目标加入删除白名单。
                return true;
            }
        }

        private static long MeasureBytes(string path)
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }

        private static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            return path + "-" + Guid.NewGuid().ToString("N");
        }

        private static void RestoreConfigAtomic(string path, byte[] bytes)
        {
            var temporary = path + ".reset-rollback-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
        }

        private static void AddWarning(
            ProjectRestartResult result,
            ICollection<string> audit,
            IAppLogger log,
            string message)
        {
            result.Warnings.Add(message);
            audit.Add("WARNING\t" + message);
            log?.Warn(message, "配置");
        }
    }
}
