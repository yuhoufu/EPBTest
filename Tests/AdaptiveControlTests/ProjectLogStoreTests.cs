using System;
using System.IO;
using System.Linq;
using System.Text;
using Config;

namespace AdaptiveControlTests
{
    internal static class ProjectLogStoreTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("项目日志规范格式与早期缓冲补写", CanonicalFormatAndEarlyBuffer, ref passed);
            Run("项目日志中心多调用方无重复副本", SharedHubIsSingleWriteSource, ref passed);
            Run("项目日志达到容量阈值轮转", SizeRotation, ref passed);
            Run("项目日志跨日轮转", DailyRotation, ref passed);
            Run("项目日志仅清理30天前匹配归档", RetentionCleanupIsScoped, ref passed);
            Run("项目日志显式Flush", ExplicitFlush, ref passed);
            Run("项目日志写盘失败后降级并重试", WriteFailureRetriesInOrder, ref passed);
            Run("项目日志轮转失败后降级并重试", RotationFailureRetriesInOrder, ref passed);
            return passed;
        }

        private static void CanonicalFormatAndEarlyBuffer()
        {
            var dir = CreateTempDir();
            try
            {
                using (var store = new ProjectLogStore())
                {
                    store.Write(ProjectLogLevel.Info, "alpha\tbeta\r\ngamma\\delta", "类\t别");
                    Assert(store.Configure(dir), "项目加载后未能补写早期日志");
                    Assert(store.Write(ProjectLogLevel.Warning, "warning", "检查"), "警告写入失败");
                    Assert(store.Write(ProjectLogLevel.Error, "error", "检查"), "错误写入失败");
                    Assert(store.Flush(true), "耐久 Flush 失败");
                }

                var runPath = Path.Combine(dir, "log", "run.log");
                var bytes = File.ReadAllBytes(runPath);
                Assert(bytes.Length > 3 &&
                       !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
                    "项目日志不应写入 UTF-8 BOM");
                var line = File.ReadAllLines(runPath, Encoding.UTF8).Single();
                var columns = line.Split('\t');
                Assert(columns.Length == 4, "项目日志不是四列 TSV");
                Assert(columns[1] == "INFO", "日志级别不规范");
                Assert(columns[2] == "类\\t别", "分类制表符未转义");
                Assert(columns[3] == "alpha\\tbeta\\r\\ngamma\\\\delta", "消息换行或制表符未转义");
                Assert(File.Exists(Path.Combine(dir, "log", "warning.log")), "warning.log 未生成");
                Assert(File.Exists(Path.Combine(dir, "log", "error.log")), "error.log 未生成");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void SharedHubIsSingleWriteSource()
        {
            var dir = CreateTempDir();
            try
            {
                ProjectLogHub.Shutdown();
                Assert(ProjectLogHub.Configure(dir), "共享日志中心配置失败");
                ProjectLogHub.Write(ProjectLogLevel.Info, "caller-a", "A");
                ProjectLogHub.Write(ProjectLogLevel.Info, "caller-b", "B");
                ProjectLogHub.Flush(true);
                ProjectLogHub.Shutdown();
                var lines = File.ReadAllLines(Path.Combine(dir, "log", "run.log"), Encoding.UTF8);
                Assert(lines.Length == 2, "多个调用方产生了重复持久化副本");
                Assert(lines.Count(x => x.EndsWith("\tcaller-a")) == 1, "caller-a 未单写入");
                Assert(lines.Count(x => x.EndsWith("\tcaller-b")) == 1, "caller-b 未单写入");
            }
            finally
            {
                ProjectLogHub.Shutdown();
                DeleteTempDir(dir);
            }
        }

        private static void SizeRotation()
        {
            var dir = CreateTempDir();
            try
            {
                using (var store = new ProjectLogStore(new ProjectLogOptions { MaxFileBytes = 120 }))
                {
                    Assert(store.Configure(dir), "容量轮转测试配置失败");
                    store.Write(ProjectLogLevel.Info, new string('a', 70), "容量");
                    store.Write(ProjectLogLevel.Info, new string('b', 70), "容量");
                    store.Flush(true);
                }

                var archives = Directory.GetFiles(Path.Combine(dir, "log"), "run.*.001.log");
                Assert(archives.Length == 1, "达到容量阈值后未生成归档");
                Assert(File.Exists(Path.Combine(dir, "log", "run.log")), "容量轮转后活动日志缺失");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void DailyRotation()
        {
            var dir = CreateTempDir();
            var now = new DateTime(2026, 7, 30, 23, 59, 59);
            try
            {
                using (var store = new ProjectLogStore(new ProjectLogOptions
                       {
                           LocalNowProvider = () => now
                       }))
                {
                    Assert(store.Configure(dir), "跨日轮转测试配置失败");
                    store.Write(ProjectLogLevel.Info, "before-midnight", "日期");
                    now = new DateTime(2026, 7, 31, 0, 0, 1);
                    store.Write(ProjectLogLevel.Info, "after-midnight", "日期");
                    store.Flush(true);
                }

                Assert(
                    File.Exists(Path.Combine(dir, "log", "run.20260730.001.log")),
                    "跨日后未按前一日期归档");
                var active = File.ReadAllText(Path.Combine(dir, "log", "run.log"), Encoding.UTF8);
                Assert(active.Contains("after-midnight") && !active.Contains("before-midnight"),
                    "跨日轮转未隔离活动日志");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void RetentionCleanupIsScoped()
        {
            var dir = CreateTempDir();
            var logDir = Path.Combine(dir, "log");
            Directory.CreateDirectory(logDir);
            var oldArchive = Path.Combine(logDir, "run.20260601.001.log");
            var active = Path.Combine(logDir, "run.log");
            var uiInfo = Path.Combine(logDir, "ui-info.log");
            var unrelated = Path.Combine(logDir, "custom.20260601.001.log");
            File.WriteAllText(oldArchive, "old");
            File.WriteAllText(active, "active");
            File.WriteAllText(uiInfo, "ui");
            File.WriteAllText(unrelated, "custom");
            var oldTime = new DateTime(2026, 6, 1);
            foreach (var path in new[] { oldArchive, active, uiInfo, unrelated })
                File.SetLastWriteTime(path, oldTime);

            try
            {
                using (var store = new ProjectLogStore(new ProjectLogOptions
                       {
                           RetentionDays = 30,
                           LocalNowProvider = () => new DateTime(2026, 7, 30, 12, 0, 0)
                       }))
                    Assert(store.Configure(dir), "保留期测试配置失败");

                Assert(!File.Exists(oldArchive), "30天前匹配归档未清理");
                Assert(File.Exists(active), "活动日志被错误清理");
                Assert(File.Exists(uiInfo), "ui-info.log 被错误清理");
                Assert(File.Exists(unrelated), "不匹配归档规则的文件被错误清理");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void ExplicitFlush()
        {
            var dir = CreateTempDir();
            try
            {
                using (var store = new ProjectLogStore())
                {
                    store.Configure(dir);
                    store.Write(ProjectLogLevel.Info, "flush-me", "Flush");
                    Assert(store.Flush(false), "普通 Flush 返回失败");
                    Assert(store.Flush(true), "耐久 Flush 返回失败");
                }
                Assert(File.ReadAllText(Path.Combine(dir, "log", "run.log")).Contains("flush-me"),
                    "Flush 后日志不可见");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void WriteFailureRetriesInOrder()
        {
            var dir = CreateTempDir();
            var failuresRemaining = 1;
            try
            {
                using (var store = new ProjectLogStore(new ProjectLogOptions
                       {
                           AppendStreamFactory = path =>
                           {
                               if (failuresRemaining-- > 0)
                                   throw new IOException("simulated write failure");
                               return new FileStream(
                                   path,
                                   FileMode.Append,
                                   FileAccess.Write,
                                   FileShare.ReadWrite);
                           }
                       }))
                {
                    store.Configure(dir);
                    Assert(!store.Write(ProjectLogLevel.Info, "first", "失败降级"),
                        "模拟写盘失败未返回降级状态");
                    Assert(store.GetMemorySnapshot().Any(x => x.Message == "first"),
                        "写盘失败后未保留内存记录");
                    Assert(store.Write(ProjectLogLevel.Info, "second", "失败降级"),
                        "下一次写入未自动重试");
                    store.Flush(true);
                }

                var lines = File.ReadAllLines(Path.Combine(dir, "log", "run.log"), Encoding.UTF8);
                Assert(lines.Length == 2 &&
                       lines[0].EndsWith("\tfirst") &&
                       lines[1].EndsWith("\tsecond"),
                    "失败重试未保持原始顺序");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void RotationFailureRetriesInOrder()
        {
            var dir = CreateTempDir();
            var now = new DateTime(2026, 7, 30, 23, 59, 59);
            try
            {
                using (var store = new ProjectLogStore(new ProjectLogOptions
                       {
                           LocalNowProvider = () => now
                       }))
                {
                    store.Configure(dir);
                    store.Write(ProjectLogLevel.Info, "before-midnight", "轮转失败");
                    var activePath = Path.Combine(dir, "log", "run.log");
                    using (var externalReader = new FileStream(
                               activePath,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite))
                    {
                        now = new DateTime(2026, 7, 31, 0, 0, 1);
                        Assert(!store.Write(ProjectLogLevel.Info, "first-after-midnight", "轮转失败"),
                            "轮转占用冲突未进入降级状态");
                        Assert(store.GetMemorySnapshot().Any(x => x.Message == "first-after-midnight"),
                            "轮转失败后未保留内存记录");
                    }

                    Assert(store.Write(ProjectLogLevel.Info, "second-after-midnight", "轮转失败"),
                        "文件占用解除后未重建 writer 并重试");
                    store.Flush(true);
                }

                Assert(File.Exists(Path.Combine(dir, "log", "run.20260730.001.log")),
                    "轮转重试后未生成前一天归档");
                var lines = File.ReadAllLines(Path.Combine(dir, "log", "run.log"), Encoding.UTF8);
                Assert(lines.Length == 2 &&
                       lines[0].EndsWith("\tfirst-after-midnight") &&
                       lines[1].EndsWith("\tsecond-after-midnight"),
                    "轮转失败重试未保持原始顺序");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static string CreateTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "EPBProjectLogTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempDir(string path)
        {
            try { Directory.Delete(path, true); } catch { }
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
