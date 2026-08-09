using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Config;
using Controller;
using Controller.Alarm;

namespace AdaptiveControlTests
{
    internal static class ProjectLogStoreTests
    {
        public static int RunRealtimeIsolationRegression()
        {
            var passed = 0;
            Run("项目日志后台队列不反压控制线程", AsyncQueueDoesNotBackpressureCaller, ref passed);
            Run("后台约1秒合并普通Flush", BackgroundSoftFlushIsPeriodicAndCoalesced, ref passed);
            Run("Flush版本水位不吞并发刷新请求", FlushWatermarkPreservesConcurrentRequest, ref passed);
            Run("Shutdown准入屏障保全已接受日志", ShutdownAdmissionBarrierPreservesAcceptedRecords, ref passed);
            Run("Shutdown耐久失败保留旧Store并禁止换代", ShutdownFlushFailureKeepsOldStore, ref passed);
            Run("十万日志与刷新风暴保持有界且合并", AsyncLogAndFlushStormIsBounded, ref passed);
            return passed;
        }

        public static int RunAll()
        {
            var passed = 0;
            Run("项目日志规范格式与早期缓冲补写", CanonicalFormatAndEarlyBuffer, ref passed);
            Run("项目日志中心多调用方无重复副本", SharedHubIsSingleWriteSource, ref passed);
            Run("项目日志达到容量阈值轮转", SizeRotation, ref passed);
            Run("项目日志跨日轮转", DailyRotation, ref passed);
            Run("项目日志仅清理30天前匹配归档", RetentionCleanupIsScoped, ref passed);
            Run("run保留7天而告警错误保留30天", RetentionCleanupUsesPerLogPolicy, ref passed);
            Run("ui-info按日大小轮转并仅读取尾部", UiInfoRotationAndTailRead, ref passed);
            Run("ui-info文件队列有界且退出前可排空", UiInfoQueueIsBoundedAndDrained, ref passed);
            Run("清空项目隔离数据轮转日志并删除学习模型", ProjectRestartCleanupIsIsolated, ref passed);
            Run("项目日志显式Flush", ExplicitFlush, ref passed);
            Run("项目日志后台队列不反压控制线程", AsyncQueueDoesNotBackpressureCaller, ref passed);
            Run("后台约1秒合并普通Flush", BackgroundSoftFlushIsPeriodicAndCoalesced, ref passed);
            Run("Flush版本水位不吞并发刷新请求", FlushWatermarkPreservesConcurrentRequest, ref passed);
            Run("Shutdown准入屏障保全已接受日志", ShutdownAdmissionBarrierPreservesAcceptedRecords, ref passed);
            Run("Shutdown耐久失败保留旧Store并禁止换代", ShutdownFlushFailureKeepsOldStore, ref passed);
            Run("十万日志与刷新风暴保持有界且合并", AsyncLogAndFlushStormIsBounded, ref passed);
            Run("软预警完整证据限频按Run隔离", WarningSnapshotRateLimitIsRunScoped, ref passed);
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
                Assert(lines.Length == 3, "多个调用方产生了重复持久化副本或缺少退出审计");
                Assert(lines.Count(x => x.EndsWith("\tcaller-a")) == 1, "caller-a 未单写入");
                Assert(lines.Count(x => x.EndsWith("\tcaller-b")) == 1, "caller-b 未单写入");
                Assert(lines.Last().Contains("ProjectLogShutdown AsyncLogDroppedTotal="),
                    "退出前最后记录未包含日志丢弃累计审计");
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

        private static void RetentionCleanupUsesPerLogPolicy()
        {
            var dir = CreateTempDir();
            var logDir = Path.Combine(dir, "log");
            Directory.CreateDirectory(logDir);
            var run = Path.Combine(logDir, "run.20260722.001.log");
            var warning = Path.Combine(logDir, "warning.20260722.001.log");
            var error = Path.Combine(logDir, "error.20260722.001.log");
            foreach (var path in new[] { run, warning, error })
            {
                File.WriteAllText(path, "old");
                File.SetLastWriteTime(path, new DateTime(2026, 7, 22));
            }
            try
            {
                using (var store = new ProjectLogStore(new ProjectLogOptions
                       {
                           RunRetentionDays = 7,
                           WarningRetentionDays = 30,
                           ErrorRetentionDays = 30,
                           LocalNowProvider = () => new DateTime(2026, 7, 30, 12, 0, 0)
                       }))
                    Assert(store.Configure(dir), "分级日志保留配置失败");
                Assert(!File.Exists(run), "8天前run归档未按7天策略清理");
                Assert(File.Exists(warning), "8天前warning归档被30天策略误删");
                Assert(File.Exists(error), "8天前error归档被30天策略误删");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void UiInfoRotationAndTailRead()
        {
            var dir = CreateTempDir();
            var now = new DateTime(2026, 7, 30, 23, 59, 59);
            try
            {
                using (var store = new UiInfoLogStore(new UiInfoLogOptions
                       {
                           MaxFileBytes = 100,
                           RetentionDays = 30,
                           MaximumRecentLines = 5,
                           LocalNowProvider = () => now
                       }))
                {
                    Assert(store.Initialize(dir), "ui-info初始化失败");
                    for (var i = 1; i <= 6; i++)
                        store.AppendAsync("line-" + i + "-" + new string('x', 18)).GetAwaiter().GetResult();
                    now = new DateTime(2026, 7, 31, 0, 0, 1);
                    store.AppendAsync("next-day").GetAwaiter().GetResult();
                    var recent = store.ReadRecentLines(5);
                    Assert(recent.Count == 5 && recent.Last() == "next-day",
                        "ui-info未从活动文件和归档合并读取最近5行");
                }
                var logDir = Path.Combine(dir, "log");
                Assert(Directory.GetFiles(logDir, "ui-info.20260730.*.log").Length >= 2,
                    "ui-info未按大小和跨日产生归档");
                Assert(File.Exists(Path.Combine(logDir, "ui-info.log")), "ui-info活动文件缺失");
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void UiInfoQueueIsBoundedAndDrained()
        {
            var dir = CreateTempDir();
            try
            {
                using (var store = new UiInfoLogStore(new UiInfoLogOptions
                       {
                           MaximumPendingLines = 2,
                           MaximumRecentLines = 10
                       }))
                {
                    Assert(store.Initialize(dir), "ui-info有界队列初始化失败");
                    var gateField = typeof(UiInfoLogStore).GetField(
                        "_fileGate",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var fileGate = gateField?.GetValue(store) as SemaphoreSlim;
                    Assert(fileGate != null, "无法建立ui-info阻塞写入测试门禁");
                    fileGate.Wait();
                    var first = store.AppendAsync("blocked-first");
                    Assert(SpinWait.SpinUntil(() => store.PendingCount == 0, 1000),
                        "ui-info首条记录未进入阻塞写入状态");

                    var attempts = Enumerable.Range(0, 10)
                        .Select(i => store.AppendAsync("queued-" + i))
                        .ToArray();
                    Assert(store.PendingCount == 2 && store.DroppedLines == 8,
                        $"ui-info队列容量未严格限制：Pending={store.PendingCount} " +
                        $"Dropped={store.DroppedLines}");
                    fileGate.Release();

                    Assert(store.FlushAsync().Wait(TimeSpan.FromSeconds(2)),
                        "ui-info解除阻塞后未在2秒内排空");
                    Task.WhenAll(attempts.Concat(new[] { first })).GetAwaiter().GetResult();
                    Assert(store.PendingCount == 0,
                        "ui-info排空后仍残留待写记录");
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static void ProjectRestartCleanupIsIsolated()
        {
            var root = CreateTempDir();
            try
            {
                var configDir = Path.Combine(root, "Config");
                var logDir = Path.Combine(root, "log");
                Directory.CreateDirectory(configDir);
                Directory.CreateDirectory(logDir);
                var configPath = Path.Combine(configDir, "TestConfig.xml");
                File.WriteAllText(
                    configPath,
                    "<TestConfig><Basic><TestName>reset</TestName><TestTarget>1</TestTarget>" +
                    "<IsSameCycleForAllEpb>true</IsSameCycleForAllEpb><TestCycle>15</TestCycle>" +
                    "<LearnCycle>5</LearnCycle><StoreDir>" + EscapeXml(Path.GetDirectoryName(root)) +
                    "</StoreDir><Owner>x</Owner><Description>x</Description></Basic></TestConfig>");
                var config = ConfigLoader.LoadTest(configPath, NullLogger.Instance);
                config.TestName = Path.GetFileName(root);
                config.StoreDir = Path.GetDirectoryName(root);
                config.EnsureEpbRecords();
                config.EpbRecords[0].Enabled = true;
                config.EpbRecords[0].RunCount = 123;
                config.EpbRecords[0].TotalCount = 10000;
                Directory.CreateDirectory(Path.Combine(root, "Latest"));
                File.WriteAllText(Path.Combine(root, "Latest", "payload.bin"), "payload");
                Directory.CreateDirectory(Path.Combine(root, "UnknownData"));
                File.WriteAllText(Path.Combine(root, "UnknownData", "keep.bin"), "keep");
                File.WriteAllText(Path.Combine(root, "index.db"), "db");
                File.WriteAllText(Path.Combine(configDir, "EpbAdaptiveProfiles.xml"), "profile");
                var runLogPath = Path.Combine(logDir, "run.log");
                var uiInfoLogPath = Path.Combine(logDir, "ui-info.log");
                File.WriteAllText(runLogPath, "run");
                File.WriteAllText(uiInfoLogPath, "ui");
                var expectedLogDate = new DateTime(2026, 8, 7, 12, 0, 0);
                File.SetLastWriteTime(runLogPath, expectedLogDate);
                File.SetLastWriteTime(uiInfoLogPath, expectedLogDate);
                var checkpointCleared = false;

                var result = ProjectRestartCleanupService.ResetForFreshLearning(
                    root,
                    config,
                    NullLogger.Instance,
                    () => checkpointCleared = true,
                    expectedLogDate);

                Assert(result.Succeeded && checkpointCleared, "项目清空未成功或恢复检查点未清除");
                Assert(!Directory.Exists(Path.Combine(root, "Latest")) &&
                       !File.Exists(Path.Combine(root, "index.db")), "旧运行数据或索引仍在活动路径");
                Assert(!File.Exists(Path.Combine(configDir, "EpbAdaptiveProfiles.xml")),
                    "自适应模型未删除");
                Assert(Directory.Exists(Path.Combine(root, "UnknownData")), "未知目录被错误删除");
                var reloaded = ConfigLoader.LoadTest(configPath, NullLogger.Instance);
                var record = reloaded.GetEpbRecord(1);
                Assert(record.Enabled && record.RunCount == 0 && record.TotalCount == 10000 &&
                       record.Status == EpbTestStatus.NotStarted,
                    "清空后未保留通道选择/目标次数或进度未归零");
                Assert(Directory.GetFiles(logDir, "run.20260807.*.log").Length == 1 &&
                       Directory.GetFiles(logDir, "ui-info.20260807.*.log").Length == 1,
                    "活动日志未在清空前轮转");
                Assert(File.Exists(result.AuditPath) && File.Exists(result.ConfigBackupPath),
                    "清空审计或配置备份缺失");
            }
            finally
            {
                DeleteTempDir(root);
            }
        }

        private static string EscapeXml(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
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

        private static void AsyncQueueDoesNotBackpressureCaller()
        {
            var dir = CreateTempDir();
            try
            {
                ProjectLogHub.Shutdown();
                Assert(ProjectLogHub.Configure(dir), "后台日志测试配置失败");
                var storeField = typeof(ProjectLogHub).GetField(
                    "_store",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert(storeField != null, "未找到项目日志中心 Store");
                var store = storeField.GetValue(null);
                var gateField = typeof(ProjectLogStore).GetField(
                    "_gate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(gateField != null, "未找到项目日志 Store 锁");
                var gate = gateField.GetValue(store);
                using (var entered = new ManualResetEventSlim(false))
                {
                    var holder = new Thread(() =>
                    {
                        lock (gate)
                        {
                            entered.Set();
                            Thread.Sleep(500);
                        }
                    });
                    holder.Start();
                    Assert(entered.Wait(1000), "未能注入日志写盘锁阻塞");

                    var clock = Stopwatch.StartNew();
                    Assert(ProjectLogHub.Enqueue(
                            ProjectLogLevel.Warning,
                            "control-path",
                            "实时隔离"),
                        "后台日志未能入队");
                    Assert(ProjectLogHub.RequestFlush(true), "后台耐久刷新请求未能入队");
                    clock.Stop();
                    Assert(clock.ElapsedMilliseconds < 100,
                        $"日志锁阻塞反压调用线程 {clock.ElapsedMilliseconds}ms");
                    Assert(holder.Join(2000), "日志锁阻塞注入线程未退出");
                }

                Assert(ProjectLogHub.Flush(true), "后台日志排空失败");
                ProjectLogHub.Shutdown();
                var warningPath = Path.Combine(dir, "log", "warning.log");
                Assert(File.ReadAllText(warningPath, Encoding.UTF8).Contains("control-path"),
                    "后台日志排空后记录缺失");
            }
            finally
            {
                ProjectLogHub.Shutdown();
                DeleteTempDir(dir);
            }
        }

        private static void AsyncLogAndFlushStormIsBounded()
        {
            var dir = CreateTempDir();
            try
            {
                ProjectLogHub.Shutdown();
                Assert(ProjectLogHub.Configure(dir), "日志风暴测试配置失败");
                var droppedBefore = ProjectLogHub.DroppedAsyncRecords;
                var coalescedBefore = ProjectLogHub.CoalescedFlushRequestCount;
                var durableFlushBefore = ProjectLogHub.DurableFlushExecutionCount;
                var storeField = typeof(ProjectLogHub).GetField(
                    "_store",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var gateField = typeof(ProjectLogStore).GetField(
                    "_gate",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(storeField != null && gateField != null, "无法建立日志风暴写盘阻塞门禁");
                var gate = gateField.GetValue(storeField.GetValue(null));
                using (var entered = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    var holder = new Thread(() =>
                    {
                        lock (gate)
                        {
                            entered.Set();
                            release.Wait();
                        }
                    });
                    holder.Start();
                    Assert(entered.Wait(1000), "日志风暴写盘阻塞门禁未就绪");

                    long elapsedMs;
                    int queueDepth;
                    int pendingKinds;
                    int durableSignals;
                    long coalescedDelta;
                    long droppedDelta;
                    try
                    {
                        var clock = Stopwatch.StartNew();
                        for (var i = 0; i < 100000; i++)
                        {
                            ProjectLogHub.Enqueue(
                                ProjectLogLevel.Info,
                                "ordinary-log-" + i,
                                "压力测试");
                            Assert(ProjectLogHub.RequestFlush(false), "普通刷新请求登记失败");
                            Assert(ProjectLogHub.RequestFlush(true), "耐久刷新请求登记失败");
                        }
                        clock.Stop();
                        elapsedMs = clock.ElapsedMilliseconds;
                        queueDepth = ProjectLogHub.PendingAsyncRecords;
                        pendingKinds = ProjectLogHub.PendingFlushRequestKinds;
                        durableSignals = ProjectLogHub.PendingDurableFlushSignals;
                        coalescedDelta = ProjectLogHub.CoalescedFlushRequestCount - coalescedBefore;
                        droppedDelta = ProjectLogHub.DroppedAsyncRecords - droppedBefore;
                    }
                    finally
                    {
                        release.Set();
                    }

                    Assert(holder.Join(2000), "日志风暴写盘阻塞门禁未退出");
                    Assert(elapsedMs < 15000,
                        $"十万日志/刷新请求耗时异常 {elapsedMs}ms（平均应低于0.15ms/组）");
                    Assert(queueDepth <= ProjectLogHub.MaximumPendingAsyncRecords,
                        $"日志后台队列越界：Depth={queueDepth} " +
                        $"Capacity={ProjectLogHub.MaximumPendingAsyncRecords}");
                    Assert(pendingKinds <= 2,
                        $"刷新风暴产生多个独立请求类别：PendingKinds=" +
                        pendingKinds);
                    Assert(durableSignals <= 1,
                        "耐久刷新风暴生成了多个独立队列信号");
                    Assert(coalescedDelta >= 99990,
                        "刷新请求未被充分合并");
                    Assert(droppedDelta > 0,
                        "阻塞写盘下十万日志未触发有界队列丢弃审计");
                }

                Assert(ProjectLogHub.Flush(true), "日志风暴解除后后台队列未排空");
                Assert(ProjectLogHub.DurableFlushExecutionCount - durableFlushBefore <= 4,
                    $"十万耐久请求执行了过多物理 Flush(true)：" +
                    (ProjectLogHub.DurableFlushExecutionCount - durableFlushBefore));
                var droppedAfterDrain = ProjectLogHub.DroppedAsyncRecords;
                ProjectLogHub.Shutdown();
                Assert(ProjectLogHub.DroppedAsyncRecords == droppedAfterDrain,
                    "Shutdown 静默清零了累计 DroppedAsyncRecords");
                var runPath = Path.Combine(dir, "log", "run.log");
                var lastLine = File.ReadLines(runPath, Encoding.UTF8).Last();
                Assert(lastLine.Contains(
                        "ProjectLogShutdown AsyncLogDroppedTotal=" + droppedAfterDrain),
                    "Shutdown 前最后一条日志未审计累计丢弃数");
            }
            finally
            {
                ProjectLogHub.Shutdown();
                DeleteTempDir(dir);
            }
        }

        private static void BackgroundSoftFlushIsPeriodicAndCoalesced()
        {
            var dir = CreateTempDir();
            try
            {
                ProjectLogHub.Shutdown();
                Assert(ProjectLogHub.Configure(dir), "后台普通刷新测试配置失败");
                var softBefore = ProjectLogHub.SoftFlushExecutionCount;
                var coalescedBefore = ProjectLogHub.CoalescedFlushRequestCount;
                Assert(ProjectLogHub.Enqueue(
                        ProjectLogLevel.Info,
                        "periodic-soft-flush",
                        "合并刷新"),
                    "后台普通刷新测试日志未入队");
                for (var i = 0; i < 10000; i++)
                    Assert(ProjectLogHub.RequestFlush(false), "普通刷新合并请求失败");

                var runPath = Path.Combine(dir, "log", "run.log");
                var becameVisible = SpinWait.SpinUntil(() =>
                {
                    try
                    {
                        return File.Exists(runPath) &&
                               ReadSharedText(runPath)
                                   .Contains("periodic-soft-flush");
                    }
                    catch
                    {
                        return false;
                    }
                }, 3000);
                if (!becameVisible)
                {
                    var length = File.Exists(runPath) ? new FileInfo(runPath).Length : -1;
                    var content = File.Exists(runPath)
                        ? ReadSharedText(runPath)
                            .Replace("\r", "\\r")
                            .Replace("\n", "\\n")
                        : "missing";
                    var failure = ProjectLogHub.LastFailure?.ToString() ?? "none";
                    throw new InvalidOperationException(
                        "后台未在约1秒内执行 Flush(false)：" +
                        $"SoftFlushes={ProjectLogHub.SoftFlushExecutionCount - softBefore} " +
                        $"QueueDepth={ProjectLogHub.PendingAsyncRecords} " +
                        $"PendingKinds={ProjectLogHub.PendingFlushRequestKinds} " +
                        $"FileLength={length} Content={content} Failure={failure}");
                }
                var softExecutions = ProjectLogHub.SoftFlushExecutionCount - softBefore;
                Assert(softExecutions >= 1 && softExecutions <= 3,
                    $"一万普通刷新请求未合并：PhysicalFlushes={softExecutions}");
                Assert(ProjectLogHub.CoalescedFlushRequestCount - coalescedBefore >= 9990,
                    "一万普通刷新请求未记录合并审计");
            }
            finally
            {
                ProjectLogHub.Shutdown();
                DeleteTempDir(dir);
            }
        }

        private static void FlushWatermarkPreservesConcurrentRequest()
        {
            var dir = CreateTempDir();
            var flushGate = new FlushWatermarkGate();
            try
            {
                ProjectLogHub.Shutdown();
                Assert(SpinWait.SpinUntil(
                        () => ProjectLogHub.PendingAsyncRecords == 0 &&
                              ProjectLogHub.PendingDurableFlushSignals == 0,
                        3000),
                    "安装竞态 Store 前日志后台队列未清空");
                InstallHubStoreForTest(new ProjectLogStore(new ProjectLogOptions
                {
                    BeforeFlush = flushGate.BeforeFlush
                }));
                Assert(ProjectLogHub.Configure(dir), "Flush版本水位测试配置失败");
                Assert(ProjectLogHub.Write(ProjectLogLevel.Info, "watermark-seed", "Flush竞态"),
                    "Flush版本水位测试首条日志未进入单写者");

                var firstFlush = Task.Run(() => ProjectLogHub.Flush(true));
                Assert(flushGate.FirstEntered.Wait(3000), "未进入第一轮受控 Flush");

                // 该请求发生在物理 Flush 已观察旧版本、但尚未返回的窗口。旧 bool 实现
                // 会在 Flush 返回后把这里设置的 dirty 标记清零，之后再也没有物理刷新。
                Assert(ProjectLogHub.RequestFlush(false), "并发普通刷新请求未被接受");
                Assert(ProjectLogHub.PendingFlushRequestKinds >= 1,
                    "并发请求登记后版本水位未保持待刷新");
                flushGate.ReleaseFirst.Set();
                Assert(firstFlush.Wait(3000) && firstFlush.Result, "第一轮受控 Flush 未完成");

                Assert(flushGate.SecondEntered.Wait(3000),
                    "第一轮 Flush 错误吞掉并发请求，后台未执行第二轮 Flush");
                Assert(ProjectLogHub.PendingFlushRequestKinds >= 1,
                    "第二轮 Flush 尚未完成却提前确认了并发请求版本");
                flushGate.ReleaseSecond.Set();
                Assert(SpinWait.SpinUntil(() => ProjectLogHub.PendingFlushRequestKinds == 0, 3000),
                    "第二轮 Flush 返回后请求版本未被确认");
            }
            finally
            {
                flushGate.ReleaseFirst.Set();
                flushGate.ReleaseSecond.Set();
                ProjectLogHub.Shutdown();
                DeleteTempDir(dir);
            }
        }

        private static void ShutdownAdmissionBarrierPreservesAcceptedRecords()
        {
            var oldDir = CreateTempDir();
            var newDir = CreateTempDir();
            try
            {
                ProjectLogHub.Shutdown();
                Assert(ProjectLogHub.Configure(oldDir), "Shutdown屏障旧目录配置失败");
                var store = GetHubStore();
                var storeGate = GetStoreGate(store);
                using (var holderEntered = new ManualResetEventSlim(false))
                using (var holderRelease = new ManualResetEventSlim(false))
                {
                    var holder = new Thread(() =>
                    {
                        lock (storeGate)
                        {
                            holderEntered.Set();
                            holderRelease.Wait();
                        }
                    });
                    holder.Start();
                    Assert(holderEntered.Wait(1000), "Shutdown屏障写盘阻塞门禁未就绪");
                    Assert(ProjectLogHub.Enqueue(
                            ProjectLogLevel.Info,
                            "accepted-before-shutdown",
                            "Shutdown竞态"),
                        "关闭前记录未被接受");

                    var shutdown = Task.Run(() => ProjectLogHub.Shutdown());
                    Assert(SpinWait.SpinUntil(IsHubShutdownClosing, 3000),
                        "Shutdown未关闭日志准入");
                    Assert(!ProjectLogHub.Enqueue(
                            ProjectLogLevel.Info,
                            "rejected-during-shutdown",
                            "Shutdown竞态"),
                        "关闭屏障之后仍错误接受新记录");
                    Assert(!ProjectLogHub.Write(
                            ProjectLogLevel.Info,
                            "sync-rejected-during-shutdown",
                            "Shutdown竞态"),
                        "关闭屏障之后同步写仍落入旧/未配置 Store");
                    holderRelease.Set();
                    Assert(shutdown.Wait(5000), "Shutdown屏障未在解除写盘阻塞后完成");
                    Assert(holder.Join(2000), "Shutdown屏障写盘阻塞线程未退出");
                }

                var oldRunPath = Path.Combine(oldDir, "log", "run.log");
                var oldText = ReadSharedText(oldRunPath);
                Assert(oldText.Contains("accepted-before-shutdown"),
                    "Shutdown屏障丢失了关闭前已接受记录");
                Assert(oldText.Contains("ProjectLogShutdown AsyncLogDroppedTotal="),
                    "Shutdown最终耐久屏障缺少退出审计");
                Assert(!oldText.Contains("rejected-during-shutdown"),
                    "关闭后拒绝记录错误写入旧 Store");

                Assert(!ProjectLogHub.Enqueue(
                        ProjectLogLevel.Info,
                        "rejected-before-reconfigure",
                        "Shutdown竞态"),
                    "Shutdown完成后、重新配置前错误开放了未配置 Store");
                Assert(ProjectLogHub.Configure(newDir), "Shutdown屏障新目录配置失败");
                Assert(ProjectLogHub.Write(
                        ProjectLogLevel.Info,
                        "accepted-after-reconfigure",
                        "Shutdown竞态"),
                    "重新配置后未恢复日志准入");
                Assert(ProjectLogHub.Flush(true), "重新配置后的日志未耐久刷新");

                var newText = ReadSharedText(Path.Combine(newDir, "log", "run.log"));
                Assert(newText.Contains("accepted-after-reconfigure"),
                    "新一代 Store 缺少重新配置后记录");
                Assert(!newText.Contains("accepted-before-shutdown") &&
                       !newText.Contains("rejected-during-shutdown") &&
                       !newText.Contains("rejected-before-reconfigure"),
                    "Shutdown边界两侧记录发生跨 Store 串写");
            }
            finally
            {
                ProjectLogHub.Shutdown();
                DeleteTempDir(oldDir);
                DeleteTempDir(newDir);
            }
        }

        private static void ShutdownFlushFailureKeepsOldStore()
        {
            var oldDir = CreateTempDir();
            var newDir = CreateTempDir();
            var failDurableFlush = 0;
            try
            {
                ProjectLogHub.Shutdown();
                Assert(SpinWait.SpinUntil(
                        () => ProjectLogHub.PendingAsyncRecords == 0 &&
                              ProjectLogHub.PendingDurableFlushSignals == 0,
                        3000),
                    "安装Shutdown失败Store前后台队列未清空");
                InstallHubStoreForTest(new ProjectLogStore(new ProjectLogOptions
                {
                    BeforeFlush = durable =>
                    {
                        if (durable && Volatile.Read(ref failDurableFlush) != 0)
                            throw new IOException("simulated durable shutdown failure");
                    }
                }));
                Assert(ProjectLogHub.Configure(oldDir), "Shutdown失败测试旧目录配置失败");
                Assert(ProjectLogHub.Write(
                        ProjectLogLevel.Info,
                        "accepted-before-failed-shutdown",
                        "Shutdown失败"),
                    "Shutdown失败测试记录未进入单写者");
                var oldStore = GetHubStore();

                Volatile.Write(ref failDurableFlush, 1);
                ProjectLogHub.Shutdown();
                Assert(ReferenceEquals(oldStore, GetHubStore()),
                    "耐久Flush失败后仍交换了Store，已接受记录可能丢失");
                Assert(!ProjectLogHub.Configure(newDir),
                    "关闭屏障失败后仍允许切换目录，可能跨项目写入旧记录");

                Volatile.Write(ref failDurableFlush, 0);
                ProjectLogHub.Shutdown();
                Assert(!ReferenceEquals(oldStore, GetHubStore()),
                    "耐久Flush恢复后未完成安全Store换代");
                var oldText = ReadSharedText(Path.Combine(oldDir, "log", "run.log"));
                Assert(oldText.Contains("accepted-before-failed-shutdown"),
                    "关闭屏障重试成功后仍丢失了原已接受记录");

                Assert(ProjectLogHub.Configure(newDir), "关闭屏障恢复后新目录配置失败");
            }
            finally
            {
                Volatile.Write(ref failDurableFlush, 0);
                ProjectLogHub.Shutdown();
                DeleteTempDir(oldDir);
                DeleteTempDir(newDir);
            }
        }

        private static void WarningSnapshotRateLimitIsRunScoped()
        {
            var gate = new WarningSnapshotWorkGate();
            var now = new DateTime(2026, 8, 10, 1, 2, 3, DateTimeKind.Utc);
            var firstRun = Guid.NewGuid();
            var secondRun = Guid.NewGuid();
            var firstKey = EpbManager.GetWarningSnapshotRateLimitKey(
                firstRun,
                4,
                "PeakEvidenceLagWarning");
            var secondKey = EpbManager.GetWarningSnapshotRateLimitKey(
                secondRun,
                4,
                "PeakEvidenceLagWarning");
            Assert(!string.Equals(firstKey, secondKey, StringComparison.OrdinalIgnoreCase),
                "不同 Run 生成了相同的完整证据限频键");

            Assert(gate.TryQueue("run-a:first", firstKey, now, 600, 1),
                "首个 Run 的首次事故未准入");
            Assert(gate.TryStart("run-a:first"), "首个 Run 的任务未启动");
            gate.Complete("run-a:first");
            Assert(!gate.TryQueue("run-a:second", firstKey, now.AddSeconds(1), 600, 1),
                "同一 Run 的 10 分钟限频失效");
            Assert(gate.TryQueue("run-b:first", secondKey, now.AddSeconds(1), 600, 1),
                "新 Run 的首次事故被上一 Run 的限频窗口吞掉");
            Assert(gate.TryStart("run-b:first"), "新 Run 的首次事故任务未启动");
            gate.Complete("run-b:first");
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

        private static ProjectLogStore GetHubStore()
        {
            var field = typeof(ProjectLogHub).GetField(
                "_store",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(field != null, "未找到项目日志中心 Store 字段");
            return (ProjectLogStore)field.GetValue(null);
        }

        private static object GetStoreGate(ProjectLogStore store)
        {
            var field = typeof(ProjectLogStore).GetField(
                "_gate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "未找到项目日志 Store 锁");
            return field.GetValue(store);
        }

        private static void InstallHubStoreForTest(ProjectLogStore store)
        {
            var field = typeof(ProjectLogHub).GetField(
                "_store",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(field != null, "未找到项目日志中心 Store 字段");
            field.SetValue(null, store);
        }

        private static bool IsHubShutdownClosing()
        {
            var field = typeof(ProjectLogHub).GetField(
                "_shutdownClosing",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(field != null, "未找到项目日志关闭准入状态");
            return (bool)field.GetValue(null);
        }

        private sealed class FlushWatermarkGate
        {
            private int _durableFlushBlocked;
            private int _softFlushBlocked;
            public readonly ManualResetEventSlim FirstEntered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim ReleaseFirst = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim SecondEntered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim ReleaseSecond = new ManualResetEventSlim(false);

            public void BeforeFlush(bool durable)
            {
                if (durable && Interlocked.CompareExchange(ref _durableFlushBlocked, 1, 0) == 0)
                {
                    FirstEntered.Set();
                    ReleaseFirst.Wait();
                }
                else if (!durable && Volatile.Read(ref _durableFlushBlocked) != 0 &&
                         Interlocked.CompareExchange(ref _softFlushBlocked, 1, 0) == 0)
                {
                    SecondEntered.Set();
                    ReleaseSecond.Wait();
                }
            }
        }

        private static string CreateTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "EPBProjectLogTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string ReadSharedText(string path)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                return reader.ReadToEnd();
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
