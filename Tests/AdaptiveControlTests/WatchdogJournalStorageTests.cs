using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class WatchdogJournalStorageTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("Watchdog Journal配置缺失非法时安全回退", PolicyDefaultsAndValidation, ref passed);
            Run("Watchdog项目Journal包含快照事件错误与终态manifest", DirectProjectJournalIsAuditable, ref passed);
            Run("Watchdog单Session轮转严格受16MiB预算约束", SessionRotationIsBounded, ref passed);
            Run("Watchdog保留器按时间数量容量清理且保护存活与未知文件", RetentionProtectsLiveAndUnknown, ref passed);
            Run("Watchdog保留器对近期会话同时执行数量与总字节上限", RetentionAppliesCountAndByteBudgets, ref passed);
            Run("Watchdog项目盘故障转入有界缓冲并按EventId回灌", EmergencySpoolIsBoundedAndReplayed, ref passed);
            Run("Watchdog本机撤权marker在项目盘故障时仍有效", LocalRevocationSurvivesProjectFailure, ref passed);
            Run("Watchdog写盘不可用不阻塞监督事件登记", FailedStorageDoesNotBlockProducer, ref passed);
            Run("独立Sidecar在主进程存活但心跳停止五秒后记录接管", SidecarRecordsLiveProcessHang, ref passed);
            return passed;
        }

        private static void PolicyDefaultsAndValidation()
        {
            var settings = new NameValueCollection
            {
                ["WatchdogJournalRetentionDays"] = "0",
                ["WatchdogJournalRetainSessionCount"] = "bad",
                ["WatchdogJournalMaxTotalBytes"] = "1",
                ["WatchdogJournalMaxSessionBytes"] = "999999999999",
                ["WatchdogJournalHeartbeatCheckpointSeconds"] = "2",
                ["WatchdogJournalEmergencySpoolMaxBytes"] = "0"
            };
            var warnings = new System.Collections.Generic.List<string>();
            var policy = WatchdogJournalPolicy.Load(settings, warnings.Add);
            Assert(policy.RetentionDays == 90 && policy.RetainSessionCount == 32,
                "非法时间/数量没有回退均衡档");
            Assert(policy.MaxTotalBytes == 128L * 1024L * 1024L &&
                   policy.MaxSessionBytes == 16L * 1024L * 1024L,
                "非法容量没有回退均衡档");
            Assert(policy.HeartbeatCheckpointSeconds == 60 &&
                   policy.EmergencySpoolMaxBytes == 8L * 1024L * 1024L,
                "非法检查点/应急预算没有回退均衡档");
            Assert(warnings.Count >= 6 && policy.ToStartupLogLine().Contains("MaxTotalBytes=134217728"),
                "配置回退没有形成可审计告警和启动快照");
            var rootRejected = false;
            try { WatchdogJournalPaths.ValidateProjectDirectory(Path.GetPathRoot(Path.GetTempPath())); }
            catch (ArgumentException) { rootRejected = true; }
            Assert(rootRejected, "卷根目录被错误接受为Watchdog Journal目录");
        }

        private static void DirectProjectJournalIsAuditable()
        {
            WithTempRoot("Direct", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(root, session, "sidecar",
                           new WatchdogJournalPolicy(), process.Id,
                           process.StartTime.ToUniversalTime().Ticks))
                {
                    store.PublishSnapshot("{\"SchemaVersion\":2,\"State\":\"Attached\"}");
                    store.Record(Event(session, "Attached", "MainProcess", 1));
                    store.Record(Event(session, "HeartbeatCheckpoint", "Periodic", 2), true);
                    store.RecordError("InjectedError");
                    store.PublishTerminal("RunCompleted", "FormalRunCompleted");
                    Assert(store.Flush(TimeSpan.FromSeconds(10)), "Journal写盘未在验收窗口内排空");
                }
                Assert(File.Exists(Path.Combine(root, "session-" + session + ".json")), "缺少原子快照");
                var eventsPath = Path.Combine(root, "session-" + session + ".sidecar-events.jsonl");
                Assert(File.ReadAllText(eventsPath, Encoding.UTF8).Contains("HeartbeatCheckpoint"),
                    "缺少状态/心跳事件");
                File.AppendAllText(eventsPath, "{\"SchemaVersion\":2,\"EventId\":\"torn-tail", Encoding.UTF8);
                Assert(WatchdogJournalStore.ReadValidEvents(eventsPath).Count == 2,
                    "崩溃残留半行破坏了此前完整JSONL事件读取");
                Assert(File.ReadAllText(Path.Combine(root, "session-" + session + ".errors.log"),
                    Encoding.UTF8).Contains("InjectedError"), "缺少分段错误日志");
                Assert(File.Exists(Path.Combine(root, "session-" + session + ".manifest.json")),
                    "缺少终态manifest");
                Assert(!File.Exists(Path.Combine(root, "session-" + session + ".lease.json")),
                    "终态Session仍残留活动租约");
            });
        }

        private static void SessionRotationIsBounded()
        {
            WithTempRoot("Rotation", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var policy = new WatchdogJournalPolicy { MaxSessionBytes = 16L * 1024L * 1024L };
                var payload = new string('X', 80 * 1024);
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(root, session, "sidecar", policy,
                           process.Id, process.StartTime.ToUniversalTime().Ticks))
                {
                    store.PublishSnapshot("{\"SchemaVersion\":2}");
                    for (var index = 0; index < 260; index++)
                    {
                        var item = Event(session, "Flood", "Rotation", index + 1);
                        item.Detail = payload;
                        store.Record(item);
                    }
                    Assert(store.Flush(TimeSpan.FromSeconds(30)), "轮转压力写入未排空");
                }
                var files = new DirectoryInfo(root).GetFiles("session-" + session + ".*",
                    SearchOption.TopDirectoryOnly);
                var total = files.Sum(file => file.Length);
                Assert(total <= 16L * 1024L * 1024L,
                    "单Session超过16MiB：" + total.ToString(CultureInfo.InvariantCulture));
                Assert(files.Count(file => file.Name.Contains("sidecar-events")) <= 4,
                    "事件日志超过当前段+3个历史段");
            });
        }

        private static void RetentionProtectsLiveAndUnknown()
        {
            WithTempRoot("Retention", root =>
            {
                var oldSessions = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
                foreach (var session in oldSessions)
                {
                    var path = Path.Combine(root, "session-" + session + ".json");
                    File.WriteAllText(path, "{\"SchemaVersion\":2}", Encoding.UTF8);
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                }
                var liveSession = Guid.NewGuid().ToString("N");
                var liveSnapshot = Path.Combine(root, "session-" + liveSession + ".json");
                File.WriteAllText(liveSnapshot, "{\"SchemaVersion\":2}", Encoding.UTF8);
                using (var process = Process.GetCurrentProcess())
                {
                    File.WriteAllText(Path.Combine(root, "session-" + liveSession + ".lease.json"),
                        "{\"SchemaVersion\":2,\"SessionId\":\"" + liveSession +
                        "\",\"ProcessId\":" + process.Id.ToString(CultureInfo.InvariantCulture) +
                        ",\"ProcessStartUtcTicks\":" +
                        process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "}",
                        Encoding.UTF8);
                }
                var unknown = Path.Combine(root, "operator-note.txt");
                File.WriteAllText(unknown, "do not delete", Encoding.UTF8);
                var current = Guid.NewGuid().ToString("N");
                var policy = new WatchdogJournalPolicy
                {
                    RetentionDays = 1,
                    RetainSessionCount = 2,
                    MaxTotalBytes = 16L * 1024L * 1024L
                };
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(root, current, "sidecar", policy,
                           process.Id, process.StartTime.ToUniversalTime().Ticks))
                {
                    store.PublishSnapshot("{\"SchemaVersion\":2}");
                    store.RequestRetention();
                    Assert(store.Flush(TimeSpan.FromSeconds(15)), "保留器未完成");
                }
                Assert(oldSessions.All(session => !File.Exists(Path.Combine(root, "session-" + session + ".json"))),
                    "超过时间的旧Session未清理");
                Assert(File.Exists(liveSnapshot), "仍存活Session被误删");
                Assert(File.Exists(unknown), "未知文件被误删");
            });
        }

        private static void EmergencySpoolIsBoundedAndReplayed()
        {
            WithTempRoot("Spool", root =>
            {
                var blocker = Path.Combine(root, "blocked");
                File.WriteAllText(blocker, "file blocks directory creation", Encoding.UTF8);
                var project = Path.Combine(blocker, "WatchdogSessions");
                var session = Guid.NewGuid().ToString("N");
                var policy = new WatchdogJournalPolicy { EmergencySpoolMaxBytes = 1024L * 1024L };
                var first = Event(session, "TakeoverRequested", "HeartbeatUnresponsive", 1);
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(project, session, "sidecar", policy,
                           process.Id, process.StartTime.ToUniversalTime().Ticks))
                {
                    store.PublishSnapshot("{\"SchemaVersion\":2,\"State\":\"TakeoverRequested\"}");
                    store.Record(first);
                    Assert(store.Flush(TimeSpan.FromSeconds(10)), "故障盘写入未转入应急缓冲");
                    var spool = WatchdogJournalPaths.LocalSpoolDirectory(project, session);
                    Assert(Directory.Exists(spool) && new DirectoryInfo(spool).GetFiles().Any(),
                        "项目盘异常时未建立本机应急缓冲");
                    Assert(new DirectoryInfo(spool).GetFiles().Sum(file => file.Length) <= policy.EmergencySpoolMaxBytes,
                        "应急缓冲超过1MiB测试预算");

                    File.Delete(blocker);
                    Directory.CreateDirectory(project);
                    store.Record(Event(session, "StorageRecovered", "Replay", 2));
                    Assert(store.Flush(TimeSpan.FromSeconds(15)), "项目盘恢复后未回灌");
                    var journal = File.ReadAllText(
                        Path.Combine(project, "session-" + session + ".sidecar-events.jsonl"), Encoding.UTF8);
                    Assert(Count(journal, first.EventId) == 1, "EventId回灌发生丢失或重复");
                    Assert(journal.Contains("StorageRecovered"), "恢复事件未写入项目Journal");
                }
            });
        }

        private static void RetentionAppliesCountAndByteBudgets()
        {
            WithTempRoot("RetentionBudgets", root =>
            {
                var sessions = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
                for (var index = 0; index < sessions.Length; index++)
                {
                    var snapshot = Path.Combine(root, "session-" + sessions[index] + ".json");
                    File.WriteAllText(snapshot, "{\"SchemaVersion\":2}", Encoding.UTF8);
                    var error = Path.Combine(root, "session-" + sessions[index] + ".errors.log");
                    using (var stream = new FileStream(error, FileMode.Create, FileAccess.Write, FileShare.Read))
                        stream.SetLength(4L * 1024L * 1024L);
                    var timestamp = DateTime.UtcNow.AddMinutes(-sessions.Length + index);
                    File.SetLastWriteTimeUtc(snapshot, timestamp);
                    File.SetLastWriteTimeUtc(error, timestamp);
                }
                var current = Guid.NewGuid().ToString("N");
                var policy = new WatchdogJournalPolicy
                {
                    RetentionDays = 90,
                    RetainSessionCount = 5,
                    MaxTotalBytes = 16L * 1024L * 1024L
                };
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(root, current, "sidecar", policy,
                           process.Id, process.StartTime.ToUniversalTime().Ticks))
                {
                    store.PublishSnapshot("{\"SchemaVersion\":2}");
                    store.RequestRetention();
                    Assert(store.Flush(TimeSpan.FromSeconds(20)), "数量/字节保留器未完成");
                }
                var retained = sessions.Count(session =>
                    File.Exists(Path.Combine(root, "session-" + session + ".json")));
                var ownedBytes = new DirectoryInfo(root).GetFiles("session-*", SearchOption.TopDirectoryOnly)
                    .Where(file => sessions.Any(session => file.Name.StartsWith(
                        "session-" + session + ".", StringComparison.OrdinalIgnoreCase)))
                    .Sum(file => file.Length);
                Assert(retained <= 3, "总字节预算未在数量预算基础上继续收敛会话");
                Assert(ownedBytes <= policy.MaxTotalBytes, "Watchdog项目总字节超过16MiB测试上限");
            });
        }

        private static void LocalRevocationSurvivesProjectFailure()
        {
            var session = Guid.NewGuid().ToString("N");
            var local = WatchdogJournalPaths.LocalRevocationPath(session);
            try
            {
                WatchdogControlMarker.WriteLocal(session, "ManualStop");
                Assert(WatchdogControlMarker.IsRevoked("Z:\\definitely-unavailable\\WatchdogSessions", session),
                    "项目盘不可用时本机撤权marker没有生效");
            }
            finally { try { if (File.Exists(local)) File.Delete(local); } catch { } }
        }

        private static void FailedStorageDoesNotBlockProducer()
        {
            WithTempRoot("NonBlocking", root =>
            {
                var blocker = Path.Combine(root, "blocked");
                File.WriteAllText(blocker, "x", Encoding.UTF8);
                var project = Path.Combine(blocker, "WatchdogSessions");
                var session = Guid.NewGuid().ToString("N");
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(project, session, "sidecar",
                           new WatchdogJournalPolicy(), process.Id,
                           process.StartTime.ToUniversalTime().Ticks))
                {
                    var stopwatch = Stopwatch.StartNew();
                    for (var index = 0; index < 1500; index++)
                        store.Record(Event(session, "HeartbeatCheckpoint", "BlockedStorage", index + 1), true);
                    stopwatch.Stop();
                    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                        "故障存储阻塞事件生产者：" + stopwatch.Elapsed);
                    // The worker may drain into the designed LocalAppData spool
                    // quickly enough that the in-memory queue never overflows.
                    // Both outcomes are valid: bounded drop accounting or
                    // successful non-blocking fallback persistence.
                    store.Flush(TimeSpan.FromSeconds(3));
                    var spool = WatchdogJournalPaths.LocalSpoolDirectory(project, session);
                    var spooled = Directory.Exists(spool) &&
                                  Directory.EnumerateFiles(
                                          spool,
                                          "*",
                                          SearchOption.AllDirectories)
                                      .Any();
                    Assert(store.DroppedEventCount > 0 || spooled,
                        "故障项目盘下既无有界丢弃计数，也无本机spool回退证据");
                }
            });
        }

        private static void SidecarRecordsLiveProcessHang()
        {
            WithTempRoot("SidecarHang", root =>
            {
                var sidecarPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MTTFTest.Watchdog.exe");
                Assert(File.Exists(sidecarPath), "测试输出缺少独立Watchdog Sidecar");
                var session = Guid.NewGuid().ToString("N");
                var pipeName = "MTTFTest.Watchdog.Test." + session;
                var command = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                Process dummy = null;
                Process sidecar = null;
                var localMarker = WatchdogJournalPaths.LocalRevocationPath(session);
                try
                {
                    dummy = Process.Start(new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    Assert(dummy != null, "无法创建主进程存活模拟器");
                    var parentStart = dummy.StartTime.ToUniversalTime().Ticks;
                    sidecar = Process.Start(new ProcessStartInfo
                    {
                        FileName = sidecarPath,
                        Arguments =
                            "--parent-pid " + dummy.Id.ToString(CultureInfo.InvariantCulture) +
                            " --parent-start-ticks " + parentStart.ToString(CultureInfo.InvariantCulture) +
                            " --session " + Quote(session) +
                            " --pipe " + Quote(pipeName) +
                            " --executable " + Quote(command) +
                            " --journal-directory " + Quote(root) +
                            " --journal-retention-days 90 --journal-retain-sessions 32" +
                            " --journal-max-total-bytes 134217728 --journal-max-session-bytes 16777216" +
                            " --journal-heartbeat-checkpoint-seconds 60 --journal-emergency-spool-max-bytes 8388608",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    Assert(sidecar != null, "无法启动独立Watchdog Sidecar");

                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                               PipeOptions.Asynchronous))
                    {
                        pipe.Connect(5000);
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)
                               { AutoFlush = true })
                        {
                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.Attach,
                                SessionId = session,
                                CorrelationId = Guid.NewGuid().ToString("N"),
                                Session = new WatchdogRunSession
                                {
                                    SessionId = session,
                                    PipeName = pipeName,
                                    ExecutablePath = command,
                                    ProcessId = dummy.Id,
                                    ProcessStartUtcTicks = parentStart,
                                    SelectedChannels = new[] { 1 }
                                }
                            }));
                            var attached = ReadMessage(reader, TimeSpan.FromSeconds(5));
                            Assert(attached?.Type == WatchdogMessageType.Attached, "Sidecar Attach握手失败");
                            Thread.Sleep(150);
                            sidecar.Refresh();
                            Assert(sidecar.MainWindowHandle == IntPtr.Zero ||
                                   !IsWindowVisible(sidecar.MainWindowHandle),
                                "普通首次Attach错误显示了Watchdog恢复过渡窗");

                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.Heartbeat,
                                SessionId = session,
                                Heartbeat = new WatchdogHeartbeat
                                {
                                    Sequence = 1,
                                    SessionId = session,
                                    ProcessId = dummy.Id,
                                    ProcessStartUtcTicks = parentStart,
                                    RunActive = true,
                                    EnabledChannels = new[] { 1 },
                                    EligibleChannels = new[] { 1 },
                                    RecoveryEligibleChannels = new[] { 1 }
                                }
                            }));

                            var takeover = false;
                            var deadline = DateTime.UtcNow.AddSeconds(8);
                            while (DateTime.UtcNow < deadline && !takeover)
                            {
                                var message = ReadMessage(reader, deadline - DateTime.UtcNow);
                                if (message == null) break;
                                takeover = message.Type == WatchdogMessageType.RequestStopAll;
                            }
                            Assert(takeover, "主进程存活但心跳停止5秒后Sidecar未请求接管");
                            WaitUntil(() =>
                            {
                                sidecar.Refresh();
                                return sidecar.MainWindowHandle != IntPtr.Zero &&
                                       IsWindowVisible(sidecar.MainWindowHandle);
                            }, TimeSpan.FromSeconds(2));

                            writer.WriteLine(WatchdogProtocol.Serialize(new WatchdogMessage
                            {
                                Type = WatchdogMessageType.MainUiReady,
                                SessionId = session,
                                Reason = "IntegrationTestMainWindowShown"
                            }));
                            WaitUntil(() =>
                            {
                                sidecar.Refresh();
                                return sidecar.MainWindowHandle == IntPtr.Zero ||
                                       !IsWindowVisible(sidecar.MainWindowHandle);
                            }, TimeSpan.FromSeconds(2));
                            WatchdogControlMarker.WriteLocal(session, "TestCompleted");
                        }
                    }

                    Assert(sidecar.WaitForExit(5000), "撤权后Sidecar未及时退出");
                    var eventsPath = Path.Combine(root, "session-" + session + ".sidecar-events.jsonl");
                    WaitUntil(() => File.Exists(eventsPath) &&
                                    File.ReadAllText(eventsPath, Encoding.UTF8).Contains("TakeoverRequested"),
                        TimeSpan.FromSeconds(5));
                    var events = File.ReadAllText(eventsPath, Encoding.UTF8);
                    Assert(events.Contains("HeartbeatSuspect") && events.Contains("HeartbeatUnresponsive"),
                        "Sidecar Journal没有完整记录3秒怀疑与5秒接管链");
                    Assert(dummy != null && !dummy.HasExited, "撤权后Sidecar仍错误终止了存活主进程");
                }
                finally
                {
                    try { if (File.Exists(localMarker)) File.Delete(localMarker); } catch { }
                    try { if (sidecar != null && !sidecar.HasExited) sidecar.Kill(); } catch { }
                    try { if (dummy != null && !dummy.HasExited) dummy.Kill(); } catch { }
                    try { sidecar?.Dispose(); } catch { }
                    try { dummy?.Dispose(); } catch { }
                }
            });
        }

        private static WatchdogMessage ReadMessage(StreamReader reader, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) return null;
            var task = Task.Run(() => reader.ReadLine());
            if (!task.Wait(timeout)) return null;
            var line = task.Result;
            return string.IsNullOrWhiteSpace(line) ? null : WatchdogProtocol.Deserialize(line);
        }

        private static void WaitUntil(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try { if (predicate()) return; } catch (IOException) { }
                Thread.Sleep(50);
            }
            Assert(predicate(), "等待条件超时");
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static WatchdogJournalEvent Event(string session, string type, string reason, long sequence) =>
            new WatchdogJournalEvent
            {
                SessionId = session,
                EventType = type,
                State = type,
                Reason = reason,
                EventSequence = sequence,
                ProcessId = Process.GetCurrentProcess().Id,
                HeartbeatSequence = sequence,
                AckSequence = sequence
            };

        private static int Count(string value, string token)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }

        private static void WithTempRoot(string name, Action<string> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBTest-WatchdogJournal-" + name + "-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void Run(string name, Action action, ref int passed)
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
