using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            RunOpenExistingRegression(ref passed);
            Run("Watchdog Journal配置缺失非法时安全回退", PolicyDefaultsAndValidation, ref passed);
            Run("Watchdog项目Journal包含快照事件错误与终态manifest", DirectProjectJournalIsAuditable, ref passed);
            Run("Watchdog恢复ClientAuditOnly不接管快照租约终态或pending spool",
                ClientAuditOnlyCannotMutateAuthorityArtifacts, ref passed);
            Run("Watchdog ClientAuditOnly使用隔离有界audit spool并可恢复回放",
                ClientAuditSpoolIsIndependentAndBounded, ref passed);
            Run("Watchdog单Session轮转严格受16MiB预算约束", SessionRotationIsBounded, ref passed);
            Run("Watchdog保留器按时间数量容量清理且保护存活与未知文件", RetentionProtectsLiveAndUnknown, ref passed);
            Run("Watchdog authority proof与relaunch文件按Session成对保留清理",
                RetentionKeepsAuthorityFilesPaired, ref passed);
            Run("Watchdog保留器对近期会话同时执行数量与总字节上限", RetentionAppliesCountAndByteBudgets, ref passed);
            Run("Watchdog项目盘故障转入有界缓冲并按EventId回灌", EmergencySpoolIsBoundedAndReplayed, ref passed);
            Run("Watchdog本机撤权marker在项目盘故障时仍有效", LocalRevocationSurvivesProjectFailure, ref passed);
            Run("恢复批次提交marker在管道失效时仍可确认", RecoveryCommitMarkerSurvivesTransportFailure, ref passed);
            Run("Watchdog写盘不可用不阻塞监督事件登记", FailedStorageDoesNotBlockProducer, ref passed);
            Run("独立Sidecar在主进程存活但心跳停止五秒后记录接管", SidecarRecordsLiveProcessHang, ref passed);
            Run("V2 Journal安全迁移到V3并保留阻断状态", Schema2MigrationPreservesBlockedState, ref passed);
            return passed;
        }

        internal static int RunOpenExistingRegression()
        {
            var passed = 0;
            RunOpenExistingRegression(ref passed);
            return passed;
        }

        internal static int RunClientAuditSpoolRegression()
        {
            var passed = 0;
            Run("Watchdog ClientAuditOnly使用隔离有界audit spool并可恢复回放",
                ClientAuditSpoolIsIndependentAndBounded, ref passed);
            return passed;
        }

        private static void RunOpenExistingRegression(ref int passed)
        {
            Run("Watchdog schema4快照只读开启并保留摘要", OpenExistingAcceptsBootstrapAndHostSnapshots,
                ref passed);
            Run("Watchdog schema4快照非法输入返回类型化错误", OpenExistingRejectsInvalidSnapshots, ref passed);
            Run("Watchdog开启严格校验N格式Session与长度并支持大于默认上限的合法快照",
                OpenExistingRejectsInvalidSessionAndLimits, ref passed);
            Run("Watchdog文件锁重试耗尽后释放可再次只读开启",
                OpenExistingFileLockRetriesThenSucceeds, ref passed);
            Run("Watchdog File.Replace并发开启只看到完整旧/新快照", OpenExistingSurvivesAtomicReplace, ref passed);
            Run("Watchdog开启忽略permit事件租约与spool状态", OpenExistingIgnoresAuthorityArtifacts, ref passed);
            Run("Watchdog schema4快照64并发开启不改变文件", OpenExistingConcurrentReadsAreStable, ref passed);
        }

        private static void OpenExistingAcceptsBootstrapAndHostSnapshots()
        {
            WithTempRoot("OpenExistingBootstrap", root =>
            {
                var bootstrapSession = Guid.NewGuid().ToString("N");
                using (var process = Process.GetCurrentProcess())
                {
                    Assert(WatchdogJournalBootstrap.TryCreateNew(
                               root,
                               bootstrapSession,
                               process.Id,
                               process.StartTime.ToUniversalTime().Ticks,
                               out _,
                               out var createError),
                        "Bootstrap快照创建失败：" + createError);
                }
                AssertOpenSnapshot(root, bootstrapSession, 1024 * 1024);

                var hostSession = Guid.NewGuid().ToString("N");
                var hostJson = CompleteHostSnapshot(hostSession, "Attached");
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(
                           root,
                           hostSession,
                           "host",
                           new WatchdogJournalPolicy(),
                           process.Id,
                           process.StartTime.ToUniversalTime().Ticks))
                {
                    Assert(store.TryPublishSnapshotSynchronously(hostJson),
                        "Host完整snapshot未同步落盘");
                }

                var path = SessionSnapshotPath(root, hostSession);
                var beforeBytes = File.ReadAllBytes(path);
                var beforeTime = File.GetLastWriteTimeUtc(path);
                Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                           root,
                           hostSession.ToUpperInvariant(),
                           1024 * 1024,
                           out var receipt,
                           out var error) && error == WatchdogJournalOpenExistingError.None,
                    "Host完整snapshot开启失败：" + error);
                Assert(receipt.CanonicalDirectory == Path.GetFullPath(root).TrimEnd(
                           Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) &&
                       receipt.Path == path &&
                       receipt.SessionId == hostSession.ToLowerInvariant() &&
                       receipt.SchemaVersion == 4 &&
                       receipt.Length == beforeBytes.LongLength &&
                       receipt.SHA256 == Sha256(beforeBytes),
                    "schema4开启回执字段不完整或摘要不匹配");
                Assert(beforeBytes.SequenceEqual(File.ReadAllBytes(path)) &&
                       beforeTime == File.GetLastWriteTimeUtc(path),
                    "只读开启改变了Host快照内容或mtime");
            });
        }

        private static void OpenExistingRejectsInvalidSnapshots()
        {
            WithTempRoot("OpenExistingInvalid", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var path = SessionSnapshotPath(root, session);
                WatchdogJournalOpenExistingReceipt receipt;
                WatchdogJournalOpenExistingError error;

                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 1024, out receipt, out error) &&
                       error == WatchdogJournalOpenExistingError.Missing && receipt == null,
                    "缺失快照未返回Missing：" + error);

                File.WriteAllText(path, string.Empty, Encoding.UTF8);
                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 1024, out receipt, out error) &&
                       error == WatchdogJournalOpenExistingError.Empty && receipt == null,
                    "空快照未返回Empty：" + error);

                File.WriteAllText(path, "{\"SchemaVersion\":4,\"SessionId\":\"", Encoding.UTF8);
                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 1024, out receipt, out error) &&
                       error == WatchdogJournalOpenExistingError.InvalidJson && receipt == null,
                    "截断快照未返回InvalidJson：" + error);

                File.WriteAllText(path, "not-json", Encoding.UTF8);
                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 1024, out receipt, out error) &&
                       error == WatchdogJournalOpenExistingError.InvalidJson && receipt == null,
                    "损坏快照未返回InvalidJson：" + error);

                File.WriteAllText(path, "[]", Encoding.UTF8);
                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 1024, out receipt, out error) &&
                       error == WatchdogJournalOpenExistingError.InvalidJsonRoot && receipt == null,
                    "非对象JSON根未返回InvalidJsonRoot：" + error);

                File.WriteAllText(path, CompleteHostSnapshot(session, "Attached"), Encoding.UTF8);
                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 8, out receipt, out error) &&
                       error == WatchdogJournalOpenExistingError.Oversize && receipt == null,
                    "超限快照未返回Oversize：" + error);

                File.WriteAllText(path, CompleteHostSnapshot(Guid.NewGuid().ToString("N"), "Attached"),
                    Encoding.UTF8);
                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 1024 * 1024, out receipt, out error) &&
                       error == WatchdogJournalOpenExistingError.SessionMismatch && receipt == null,
                    "错误SessionId未返回SessionMismatch：" + error);

                foreach (var schema in new[] { 0, 2, 3, 5 })
                {
                    File.WriteAllText(path,
                        "{\"SchemaVersion\":" + schema.ToString(CultureInfo.InvariantCulture) +
                        ",\"SessionId\":\"" + session + "\"}", Encoding.UTF8);
                    Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                               root, session, 1024 * 1024, out receipt, out error) &&
                           error == WatchdogJournalOpenExistingError.SchemaMismatch && receipt == null,
                        "Schema=" + schema + "未返回SchemaMismatch：" + error);
                }

                var inaccessibleRoot = Path.Combine(root, "not-a-directory");
                File.WriteAllText(inaccessibleRoot, "blocking-file", Encoding.UTF8);
                    Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           Path.Combine(inaccessibleRoot, "child"),
                           session,
                           1024,
                           out receipt,
                           out error) &&
                       (error == WatchdogJournalOpenExistingError.Missing ||
                        error == WatchdogJournalOpenExistingError.AccessDenied ||
                        error == WatchdogJournalOpenExistingError.ReadFailed ||
                        error == WatchdogJournalOpenExistingError.RetryExhausted) &&
                       receipt == null,
                    "不可访问路径未返回有界类型化错误：" + error);
            });
        }

        private static void OpenExistingRejectsInvalidSessionAndLimits()
        {
            WithTempRoot("OpenExistingIdentityAndLimits", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var path = SessionSnapshotPath(root, session);
                var validJson = CompleteHostSnapshot(session, "Attached");
                File.WriteAllText(path, validJson, new UTF8Encoding(false));

                foreach (var invalidSession in new[]
                {
                    string.Empty,
                    "   ",
                    "not-a-guid",
                    Guid.NewGuid().ToString("D"),
                    "{" + session + "}"
                })
                {
                    Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                               root,
                               invalidSession,
                               1024 * 1024,
                               out var receipt,
                               out var error) &&
                           error == WatchdogJournalOpenExistingError.InvalidSessionId &&
                           receipt == null,
                        "非N格式Session未拒绝：" + (invalidSession ?? "<null>") + ";" + error);
                }

                foreach (var maximumBytes in new long[] { 0, -1, (long)int.MaxValue + 1 })
                {
                    Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                               root,
                               session,
                               maximumBytes,
                               out var receipt,
                               out var error) &&
                           error == WatchdogJournalOpenExistingError.InvalidMaximumSnapshotBytes &&
                           receipt == null,
                        "非法maximumSnapshotBytes未拒绝：" + maximumBytes + ";" + error);
                }

                File.WriteAllBytes(path, new byte[] { 0x7B, 0x22, 0xC3, 0x28, 0x22, 0x3A, 0x31, 0x7D });
                var invalidUtf8Bytes = File.ReadAllBytes(path);
                var invalidUtf8Time = File.GetLastWriteTimeUtc(path);
                Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                           root,
                           session,
                           1024 * 1024,
                           out var invalidUtf8Receipt,
                           out var invalidUtf8Error) &&
                       invalidUtf8Error == WatchdogJournalOpenExistingError.InvalidJson &&
                           invalidUtf8Receipt == null,
                    "非法UTF8未返回InvalidJson：" + invalidUtf8Error);
                Assert(invalidUtf8Bytes.SequenceEqual(File.ReadAllBytes(path)) &&
                       invalidUtf8Time == File.GetLastWriteTimeUtc(path),
                    "非法UTF8失败开启改变了snapshot内容或mtime");

                // Opening is read-only; restore a valid snapshot before the
                // large-file check.  The failure receipt was null and the
                // API itself did not mutate bytes/mtime.
                File.WriteAllText(path, validJson, new UTF8Encoding(false));

                const int twoMiB = 2 * 1024 * 1024;
                var largeJson = "{\"SchemaVersion\":4,\"SessionId\":\"" +
                                 session + "\",\"RelaunchState\":\"Attached\",\"Detail\":\"" +
                                 new string('x', twoMiB) + "\"}";
                var largeBytes = Encoding.UTF8.GetBytes(largeJson);
                Assert(largeBytes.LongLength > twoMiB && largeBytes.LongLength < 4L * 1024 * 1024,
                    "大于默认2MiB的合法fixture大小不在预期范围：" + largeBytes.LongLength);
                File.WriteAllBytes(path, largeBytes);
                Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                           root,
                           session.ToUpperInvariant(),
                           4L * 1024 * 1024,
                           out var largeReceipt,
                           out var largeError) &&
                       largeError == WatchdogJournalOpenExistingError.None &&
                       largeReceipt != null &&
                       largeReceipt.Length == largeBytes.LongLength &&
                       largeReceipt.SHA256 == Sha256(largeBytes),
                    "大于默认2MiB但小于maximumSnapshotBytes的合法schema4未开启：" + largeError);
            });
        }

        private static void OpenExistingFileLockRetriesThenSucceeds()
        {
            WithTempRoot("OpenExistingFileLock", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var path = SessionSnapshotPath(root, session);
                File.WriteAllText(path, CompleteHostSnapshot(session, "Attached"),
                    new UTF8Encoding(false));

                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert(!WatchdogJournalBootstrap.TryOpenExistingSession(
                               root,
                               session,
                               1024 * 1024,
                               out var lockedReceipt,
                               out var lockedError) &&
                           lockedError == WatchdogJournalOpenExistingError.RetryExhausted &&
                           lockedReceipt == null,
                        "FileShare.None锁未在有界重试后返回RetryExhausted：" + lockedError);
                }

                Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                           root,
                           session,
                           1024 * 1024,
                           out var receipt,
                           out var error) &&
                       error == WatchdogJournalOpenExistingError.None &&
                       receipt != null,
                    "释放FileShare.None锁后未能重新开启：" + error);
            });
        }

        private static void OpenExistingSurvivesAtomicReplace()
        {
            WithTempRoot("OpenExistingReplace", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var path = SessionSnapshotPath(root, session);
                var oldJson = CompleteHostSnapshot(session, "Old");
                var newJson = CompleteHostSnapshot(session, "New");
                File.WriteAllText(path, oldJson, new UTF8Encoding(false));
                var oldHash = Sha256(Encoding.UTF8.GetBytes(oldJson));
                var newHash = Sha256(Encoding.UTF8.GetBytes(newJson));
                var failures = new ConcurrentQueue<Exception>();
                using var start = new Barrier(66);

                var replacer = new Thread(() =>
                {
                    try
                    {
                        start.SignalAndWait(5000);
                        for (var index = 0; index < 100; index++)
                        {
                            var temporary = Path.Combine(root, "replace-" + index + ".tmp");
                            File.WriteAllText(temporary, (index & 1) == 0 ? oldJson : newJson,
                                new UTF8Encoding(false));
                            File.Replace(temporary, path, null);
                        }
                    }
                    catch (Exception exception) { failures.Enqueue(exception); }
                }) { IsBackground = true, Name = "WatchdogSnapshotReplacer" };

                var readers = Enumerable.Range(0, 64).Select(_ => new Thread(() =>
                {
                    try
                    {
                        start.SignalAndWait(5000);
                        for (var iteration = 0; iteration < 8; iteration++)
                        {
                            Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                                       root,
                                       session,
                                       1024 * 1024,
                                       out var receipt,
                                       out var error) &&
                                   error == WatchdogJournalOpenExistingError.None,
                                "File.Replace并发开启失败：" + error);
                            Assert(receipt.SHA256 == oldHash || receipt.SHA256 == newHash,
                                "File.Replace并发开启观察到非旧/新完整摘要");
                        }
                    }
                    catch (Exception exception) { failures.Enqueue(exception); }
                })).ToArray();

                replacer.Start();
                foreach (var reader in readers) reader.Start();
                Assert(start.SignalAndWait(5000), "File.Replace并发起跑栅栏未建立");
                replacer.Join(15000);
                foreach (var reader in readers) reader.Join(15000);
                Assert(!replacer.IsAlive && readers.All(reader => !reader.IsAlive),
                    "File.Replace并发开启线程未在有界时间内退出");
                if (failures.TryPeek(out var failure))
                    throw new InvalidOperationException("File.Replace并发开启失败：" + failure.Message, failure);
            });
        }

        private static void OpenExistingIgnoresAuthorityArtifacts()
        {
            WithTempRoot("OpenExistingArtifacts", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var path = SessionSnapshotPath(root, session);
                foreach (var state in new[]
                {
                    "None", "Approved", "LaunchIntent", "Started", "Attached", "Failed",
                    "Blocked", "Committed", "Revoked"
                })
                {
                    var snapshot = CompleteHostSnapshot(session, state);
                    Assert(snapshot.Contains("\"RelaunchState\":\"" + state + "\""),
                        "CompleteHostSnapshot未写入RelaunchState=" + state);
                    File.WriteAllText(path, snapshot, new UTF8Encoding(false));
                    Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                               root, session, 1024 * 1024, out var receipt, out var error) &&
                           error == WatchdogJournalOpenExistingError.None &&
                           receipt.SessionId == session,
                        "permit状态=" + state + "不应改变只读开启结果：" + error);
                }

                File.WriteAllText(Path.Combine(root, "session-" + session + ".sidecar-events.jsonl"),
                    "event", Encoding.UTF8);
                File.WriteAllText(Path.Combine(root, "session-" + session + ".lease.json"),
                    "lease", Encoding.UTF8);
                var spool = Path.Combine(root, "spool");
                Directory.CreateDirectory(spool);
                File.WriteAllText(Path.Combine(spool, "permit-state"), "blocked", Encoding.UTF8);
                Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                           root, session, 1024 * 1024, out var finalReceipt, out var finalError) &&
                       finalError == WatchdogJournalOpenExistingError.None &&
                       finalReceipt.Path == path,
                    "event/lease/spool不应成为snapshot authority：" + finalError);
            });
        }

        private static void OpenExistingConcurrentReadsAreStable()
        {
            WithTempRoot("OpenExistingConcurrent", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var path = SessionSnapshotPath(root, session);
                var json = CompleteHostSnapshot(session, "Attached");
                File.WriteAllText(path, json, new UTF8Encoding(false));
                var beforeBytes = File.ReadAllBytes(path);
                var beforeHash = Sha256(beforeBytes);
                var beforeTime = File.GetLastWriteTimeUtc(path);
                var failures = new ConcurrentQueue<Exception>();
                var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
                {
                    try
                    {
                        Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                                   root,
                                   session,
                                   1024 * 1024,
                                   out var receipt,
                                   out var error) &&
                               error == WatchdogJournalOpenExistingError.None &&
                               receipt.SHA256 == beforeHash &&
                               receipt.Length == beforeBytes.LongLength,
                            "64并发snapshot开启结果不一致：" + error);
                    }
                    catch (Exception exception) { failures.Enqueue(exception); }
                })).ToArray();
                Assert(Task.WaitAll(tasks, 15000), "64并发snapshot开启未完成");
                if (failures.TryPeek(out var failure))
                    throw new InvalidOperationException("64并发snapshot开启失败：" + failure.Message, failure);
                Assert(beforeBytes.SequenceEqual(File.ReadAllBytes(path)) &&
                       beforeTime == File.GetLastWriteTimeUtc(path),
                    "64并发只读开启改变了snapshot内容或mtime");
            });
        }

        private static string CompleteHostSnapshot(string sessionId, string state)
        {
            return "{\"SchemaVersion\":4,\"SessionId\":\"" + sessionId.ToLowerInvariant() +
                   "\",\"State\":\"" + state +
                   "\",\"RelaunchState\":\"" + state + "\",\"RelaunchGeneration\":41," +
                   "\"RelaunchPermitId\":\"permit-ignored\",\"RelaunchPermitNonce\":\"nonce-ignored\"," +
                   "\"RecoveryBlocked\":true,\"CurrentPid\":1234," +
                   "\"CurrentProcessStartUtcTicks\":987654321," +
                   "\"UpdatedUtc\":\"2026-08-24T00:00:00.0000000Z\"}";
        }

        private static string SessionSnapshotPath(string root, string sessionId) =>
            Path.Combine(root, "session-" + sessionId.ToLowerInvariant() + ".json");

        private static void AssertOpenSnapshot(string root, string sessionId, long maximumBytes)
        {
            Assert(WatchdogJournalBootstrap.TryOpenExistingSession(
                       root,
                       sessionId,
                       maximumBytes,
                       out var receipt,
                       out var error) &&
                   error == WatchdogJournalOpenExistingError.None &&
                   receipt.SessionId == sessionId.ToLowerInvariant(),
                "schema4快照开启失败：" + error);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        private static void Schema2MigrationPreservesBlockedState()
        {
            var legacyJournal =
                "{\"SchemaVersion\":2,\"SessionId\":\"legacy\",\"RecoveryBlocked\":true," +
                "\"RecoveryFailureCode\":\"ConfigDuplicateEpbId\",\"ConsecutiveStartupFailures\":3}";
            var migratedJournal = WatchdogJournalMigration.MigrateJournalJson(legacyJournal);
            Assert(!string.IsNullOrWhiteSpace(migratedJournal) &&
                   migratedJournal.Contains("\"SchemaVersion\":4") &&
                   migratedJournal.Contains("\"RecoveryBlocked\":true") &&
                   migratedJournal.Contains("ConfigDuplicateEpbId"),
                "V2 Journal迁移丢失RecoveryBlocked或失败证据");

            WithTempRoot("Schema2Migration", root =>
            {
                var path = Path.Combine(root, "legacy-events.jsonl");
                File.WriteAllText(path,
                    "{\"SchemaVersion\":2,\"EventId\":\"legacy-event\",\"EventType\":\"SafeIdleRecoveryBlocked\"," +
                    "\"RootCode\":\"ConfigDuplicateEpbId\",\"RunId\":\"run-1\"}" +
                    Environment.NewLine,
                    Encoding.UTF8);
                var events = WatchdogJournalStore.ReadValidEvents(path);
                Assert(events.Count == 1 &&
                       events[0].SchemaVersion == WatchdogJournalPolicy.CurrentSchemaVersion &&
                       events[0].EventType == "SafeIdleRecoveryBlocked" &&
                       events[0].RootCode == "ConfigDuplicateEpbId" &&
                       events[0].RunId == "run-1",
                    "V2事件未安全升级为V3或结构化失败字段丢失");
            });
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

        private static void ClientAuditOnlyCannotMutateAuthorityArtifacts()
        {
            WithTempRoot("ClientAuditOnly", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var snapshotPath = SessionSnapshotPath(root, session);
                var originalSnapshot = CompleteHostSnapshot(session, "Attached");
                File.WriteAllText(snapshotPath, originalSnapshot, new UTF8Encoding(false));
                var snapshotBytes = File.ReadAllBytes(snapshotPath);
                var snapshotTime = File.GetLastWriteTimeUtc(snapshotPath);

                var leasePath = Path.Combine(root, "session-" + session + ".lease.json");
                var manifestPath = Path.Combine(root, "session-" + session + ".manifest.json");
                File.WriteAllText(leasePath, "existing-lease", new UTF8Encoding(false));
                File.WriteAllText(manifestPath, "existing-manifest", new UTF8Encoding(false));
                var leaseBytes = File.ReadAllBytes(leasePath);
                var manifestBytes = File.ReadAllBytes(manifestPath);
                var leaseTime = File.GetLastWriteTimeUtc(leasePath);
                var manifestTime = File.GetLastWriteTimeUtc(manifestPath);

                var spool = WatchdogJournalPaths.LocalSpoolDirectory(root, session);
                Directory.CreateDirectory(spool);
                var pendingSnapshotPath = Path.Combine(spool, "session.snapshot.pending.json");
                var pendingSnapshot = "pending-authority-snapshot";
                File.WriteAllText(pendingSnapshotPath, pendingSnapshot, new UTF8Encoding(false));
                var pendingBytes = File.ReadAllBytes(pendingSnapshotPath);
                var pendingTime = File.GetLastWriteTimeUtc(pendingSnapshotPath);

                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(
                           root,
                           session,
                           "client",
                           new WatchdogJournalPolicy(),
                           process.Id,
                           process.StartTime.ToUniversalTime().Ticks,
                           WatchdogJournalStoreMode.ClientAuditOnly))
                {
                    Assert(store.Mode == WatchdogJournalStoreMode.ClientAuditOnly,
                        "AuditOnly模式没有冻结到store实例");
                    Assert(!store.PublishSnapshot("{\"SchemaVersion\":4,\"SessionId\":\"" +
                                                  session + "\",\"State\":\"Mutated\"}"),
                        "AuditOnly PublishSnapshot未拒绝");
                    Assert(!store.TryPublishSnapshotSynchronously("{\"authority\":true}"),
                        "AuditOnly TryPublishSnapshotSynchronously未拒绝");
                    Assert(!store.PublishTerminal("ShouldNotPublish", "AuditOnly"),
                        "AuditOnly PublishTerminal未拒绝");
                    Assert(!store.RequestRetention(), "AuditOnly RequestRetention未拒绝");
                    Assert(store.Record(Event(session, "ClientAudit", "Recovery", 1)),
                        "AuditOnly client event未接受");
                    store.RecordError("AuditOnlyError");
                    Assert(store.Flush(TimeSpan.FromSeconds(10)),
                        "AuditOnly client event/error未排空");
                }

                Assert(snapshotBytes.SequenceEqual(File.ReadAllBytes(snapshotPath)) &&
                       snapshotTime == File.GetLastWriteTimeUtc(snapshotPath),
                    "AuditOnly改变了主snapshot内容或mtime");
                Assert(leaseBytes.SequenceEqual(File.ReadAllBytes(leasePath)) &&
                       leaseTime == File.GetLastWriteTimeUtc(leasePath),
                    "AuditOnly改变了lease内容或mtime");
                Assert(manifestBytes.SequenceEqual(File.ReadAllBytes(manifestPath)) &&
                       manifestTime == File.GetLastWriteTimeUtc(manifestPath),
                    "AuditOnly改变了terminal manifest内容或mtime");
                Assert(File.Exists(pendingSnapshotPath) &&
                       pendingBytes.SequenceEqual(File.ReadAllBytes(pendingSnapshotPath)) &&
                       pendingTime == File.GetLastWriteTimeUtc(pendingSnapshotPath),
                    "AuditOnly消费或覆盖了snapshot pending spool");

                var clientEventsPath = Path.Combine(root, "session-" + session + ".client-events.jsonl");
                var errorsPath = Path.Combine(root, "session-" + session + ".errors.log");
                Assert(File.Exists(clientEventsPath) &&
                       File.ReadAllText(clientEventsPath, Encoding.UTF8).Contains("ClientAudit"),
                    "AuditOnly未写入client event audit");
                Assert(File.Exists(errorsPath) &&
                       File.ReadAllText(errorsPath, Encoding.UTF8).Contains("AuditOnlyError"),
                    "AuditOnly未写入error audit");

                // The default mode remains the historical authority writer.
                using (var process = Process.GetCurrentProcess())
                using (var full = new WatchdogJournalStore(
                           root,
                           session,
                           "sidecar",
                           new WatchdogJournalPolicy(),
                           process.Id,
                           process.StartTime.ToUniversalTime().Ticks))
                {
                    Assert(full.Mode == WatchdogJournalStoreMode.FullAuthority,
                        "默认store模式未保持FullAuthority");
                    Assert(full.TryPublishSnapshotSynchronously(
                               "{\"SchemaVersion\":4,\"SessionId\":\"" + session +
                               "\",\"State\":\"FullAuthority\"}"),
                        "FullAuthority未恢复snapshot写入能力");
                }
                Assert(File.ReadAllText(snapshotPath, Encoding.UTF8).Contains("FullAuthority"),
                    "FullAuthority snapshot未落盘");
            });
        }

        private static void ClientAuditSpoolIsIndependentAndBounded()
        {
            WithTempRoot("ClientAuditSpool", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var otherSession = Guid.NewGuid().ToString("N");
                var policy = new WatchdogJournalPolicy
                {
                    EmergencySpoolMaxBytes = 1024L * 1024L
                };
                var authoritySpool = WatchdogJournalPaths.LocalSpoolDirectory(root, session);
                var auditSpool = WatchdogJournalPaths.ClientAuditSpoolDirectory(root, session);
                var otherAuditSpool = WatchdogJournalPaths.ClientAuditSpoolDirectory(root, otherSession);
                Assert(Path.GetFullPath(auditSpool).EndsWith(
                           Path.Combine("session-" + session, "client-audit"),
                           StringComparison.OrdinalIgnoreCase),
                    "ClientAuditOnly spool未落在session-<safe>/client-audit目录");
                Assert(!string.Equals(auditSpool, otherAuditSpool, StringComparison.OrdinalIgnoreCase),
                    "不同Session共享了ClientAuditOnly spool路径");

                Directory.CreateDirectory(authoritySpool);
                Directory.CreateDirectory(otherAuditSpool);
                var otherSentinel = Path.Combine(otherAuditSpool, "error-other.pending.log");
                File.WriteAllText(otherSentinel, "other-session-sentinel", new UTF8Encoding(false));

                // These are authority-owned pending artifacts.  The audit
                // store must not enumerate, replay, or delete them.
                var authorityPending = new[]
                {
                    Path.Combine(authoritySpool, "session.snapshot.pending.json"),
                    Path.Combine(authoritySpool, "session.lease.pending.json"),
                    Path.Combine(authoritySpool, "session.terminal.pending.json")
                };
                foreach (var path in authorityPending)
                    File.WriteAllText(path, "authority-pending-" + Path.GetFileName(path),
                        new UTF8Encoding(false));
                var authorityBytes = authorityPending.ToDictionary(path => path,
                    path => File.ReadAllBytes(path));
                var authorityTimes = authorityPending.ToDictionary(path => path,
                    path => File.GetLastWriteTimeUtc(path));

                var eventsPath = Path.Combine(root, "session-" + session + ".client-events.jsonl");
                var errorsPath = Path.Combine(root, "session-" + session + ".errors.log");
                var revocationPath = WatchdogJournalPaths.ProjectRevocationPath(root, session);
                File.WriteAllText(eventsPath, "existing-events", new UTF8Encoding(false));
                File.WriteAllText(errorsPath, "existing-errors", new UTF8Encoding(false));
                File.WriteAllText(revocationPath, "existing-revocation", new UTF8Encoding(false));
                var eventBytes = File.ReadAllBytes(eventsPath);
                var errorBytes = File.ReadAllBytes(errorsPath);
                var revocationBytes = File.ReadAllBytes(revocationPath);
                var eventTime = File.GetLastWriteTimeUtc(eventsPath);
                var errorTime = File.GetLastWriteTimeUtc(errorsPath);
                var revocationTime = File.GetLastWriteTimeUtc(revocationPath);

                Directory.CreateDirectory(auditSpool);
                var oldItem = Path.Combine(auditSpool, "error-old.pending.log");
                File.WriteAllText(oldItem, new string('O', 700 * 1024), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(oldItem, DateTime.UtcNow.AddHours(-1));

                using (var process = Process.GetCurrentProcess())
                using (var eventLock = new FileStream(eventsPath, FileMode.Open, FileAccess.ReadWrite,
                           FileShare.None))
                using (var errorLock = new FileStream(errorsPath, FileMode.Open, FileAccess.ReadWrite,
                           FileShare.None))
                using (var revocationLock = new FileStream(revocationPath, FileMode.Open,
                           FileAccess.ReadWrite, FileShare.None))
                using (var store = new WatchdogJournalStore(
                           root,
                           session,
                           "client",
                           policy,
                           process.Id,
                           process.StartTime.ToUniversalTime().Ticks,
                           WatchdogJournalStoreMode.ClientAuditOnly))
                {
                    // Keep the project files locked while the producer emits a
                    // sustained stream.  Every fallback must land in this
                    // session's audit child, and the oldest item must be
                    // evicted when the independent budget is exceeded.
                    for (var index = 0; index < 28; index++)
                    {
                        var item = Event(session, "ClientAuditFlood", "ProjectUnavailable", index + 1);
                        item.Detail = new string('E', 48 * 1024);
                        Assert(store.Record(item), "Audit flood event未接受");
                        store.RecordError("AuditError-" + index.ToString(CultureInfo.InvariantCulture) + "-" +
                                          new string('R', 48 * 1024));
                        if ((index & 3) == 3)
                            Assert(store.Flush(TimeSpan.FromSeconds(10)), "Audit spool批次未排空");
                    }
                    Assert(store.PublishRevocation("AuditRevocationWhileUnavailable"),
                        "AuditOnly revocation未接受");
                    Assert(store.Flush(TimeSpan.FromSeconds(20)), "Audit spool最终批次未排空");

                    var auditFiles = new DirectoryInfo(auditSpool).GetFiles();
                    Assert(auditFiles.Sum(file => file.Length) <= policy.EmergencySpoolMaxBytes,
                        "ClientAuditOnly spool超过独立EmergencySpoolMaxBytes预算");
                    Assert(!File.Exists(oldItem), "ClientAuditOnly未按时间顺序裁剪最老audit spool项");
                    Assert(File.Exists(otherSentinel), "ClientAuditOnly误删了其他Session的audit spool");

                    foreach (var path in authorityPending)
                        Assert(authorityBytes[path].SequenceEqual(File.ReadAllBytes(path)) &&
                               authorityTimes[path] == File.GetLastWriteTimeUtc(path),
                            "ClientAuditOnly扫描或改变了authority pending：" + path);
                }

                Assert(eventBytes.SequenceEqual(File.ReadAllBytes(eventsPath)) &&
                       eventTime == File.GetLastWriteTimeUtc(eventsPath),
                    "项目盘不可写期间client event目标被改变");
                Assert(errorBytes.SequenceEqual(File.ReadAllBytes(errorsPath)) &&
                       errorTime == File.GetLastWriteTimeUtc(errorsPath),
                    "项目盘不可写期间error目标被改变");
                Assert(revocationBytes.SequenceEqual(File.ReadAllBytes(revocationPath)) &&
                       revocationTime == File.GetLastWriteTimeUtc(revocationPath),
                    "项目盘不可写期间revocation目标被改变");

                // Release the locks and drive the same production store through
                // its replay path.  A new queued item makes Flush wait for the
                // worker rather than observing the pre-existing idle signal.
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(
                           root,
                           session,
                           "client",
                           policy,
                           process.Id,
                           process.StartTime.ToUniversalTime().Ticks,
                           WatchdogJournalStoreMode.ClientAuditOnly))
                {
                    Assert(store.Record(Event(session, "AuditReplayCompleted", "StorageRecovered", 9001)),
                        "Audit恢复事件未接受");
                    store.RecordError("AuditReplayError");
                    Assert(store.PublishRevocation("AuditReplayRevocation"),
                        "Audit恢复撤权未接受");
                    Assert(store.Flush(TimeSpan.FromSeconds(20)), "Audit spool未完成回放");
                }

                Assert(File.ReadAllText(eventsPath, Encoding.UTF8).Contains("AuditReplayCompleted"),
                    "Audit spool event未回放到client event日志");
                Assert(File.ReadAllText(errorsPath, Encoding.UTF8).Contains("AuditReplayError"),
                    "Audit spool error未回放到error日志");
                Assert(File.ReadAllText(revocationPath, Encoding.UTF8).Contains("AuditReplayRevocation"),
                    "Audit spool revocation未回放到安全marker");
                Assert(!Directory.Exists(auditSpool) ||
                       !new DirectoryInfo(auditSpool).GetFiles("*.pending.*").Any(),
                    "项目盘恢复后audit spool pending未清空");
                Assert(File.Exists(otherSentinel), "回放期间跨Session audit spool被误处理");
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

        private static void RetentionKeepsAuthorityFilesPaired()
        {
            WithTempRoot("RetentionAuthorityPair", root =>
            {
                var oldSession = Guid.NewGuid().ToString("N");
                var files = new[]
                {
                    Path.Combine(root, "session-" + oldSession + ".json"),
                    Path.Combine(root, "session-" + oldSession + ".bootstrap.json"),
                    Path.Combine(root, "session-" + oldSession + ".relaunch.json")
                };
                foreach (var path in files)
                {
                    File.WriteAllText(path, "{\"SchemaVersion\":2}", new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                }

                var current = Guid.NewGuid().ToString("N");
                var policy = new WatchdogJournalPolicy
                {
                    RetentionDays = 1,
                    RetainSessionCount = 1,
                    MaxTotalBytes = 16L * 1024L * 1024L
                };
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(root, current, "sidecar", policy,
                           process.Id, process.StartTime.ToUniversalTime().Ticks))
                {
                    store.PublishSnapshot("{\"SchemaVersion\":2}");
                    store.RequestRetention();
                    Assert(store.Flush(TimeSpan.FromSeconds(15)), "authority pair retention未完成");
                }

                Assert(files.All(path => !File.Exists(path)),
                    "authority主快照、bootstrap proof与relaunch记录未按Session成对清理");
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

        private static void RecoveryCommitMarkerSurvivesTransportFailure()
        {
            WithTempRoot("RecoveryCommitMarker", root =>
            {
                var session = Guid.NewGuid().ToString("N");
                var local = WatchdogJournalPaths.LocalRecoveryCommitPath(session);
                try
                {
                    WatchdogRecoveryCommitMarker.WriteLocal(session, 41, "LocalCommit");
                    WatchdogRecoveryCommitMarker.WriteProject(root, session, 42, "ProjectCommit");
                    long generation;
                    Assert(WatchdogRecoveryCommitMarker.TryRead(root, session, out generation) &&
                           generation == 42,
                        "恢复提交没有从本机/项目双marker选择最大代次");
                    File.Delete(WatchdogJournalPaths.ProjectRecoveryCommitPath(root, session));
                    Assert(WatchdogRecoveryCommitMarker.TryRead(
                               "Z:\\definitely-unavailable\\WatchdogSessions",
                               session,
                               out generation) && generation == 41,
                        "项目盘或命名管道不可用时本机恢复提交marker没有生效");
                    var runId = Guid.NewGuid().ToString("N");
                    WatchdogRecoveryCommitMarker.WriteLocal(
                        session, 43, "Committed", runId, 9, "PersistenceClosed");
                    Assert(WatchdogRecoveryCommitMarker.TryRead(
                               root,
                               session,
                               out WatchdogRecoveryCommitEvidence evidence) &&
                           evidence.SchemaVersion == 2 && !evidence.Legacy &&
                           evidence.Generation == 43 && evidence.RunId == runId &&
                           evidence.RunEpoch == 9 && evidence.Stage == "PersistenceClosed" &&
                           evidence.ContentSha256.Length == 64,
                        "新marker未携带Run/Epoch/Stage/生成代次/内容哈希");
                    File.AppendAllText(local, "tamper", Encoding.UTF8);
                    Assert(!WatchdogRecoveryCommitMarker.TryRead(root, session, out evidence),
                        "内容哈希损坏的新marker仍被接受");
                    WatchdogRecoveryCommitMarker.WriteLocal(
                        session, 43, "Committed", runId, 9, "PersistenceClosed");
                    WatchdogRecoveryCommitMarker.Archive(root, session, 43, "Accepted");
                    Assert(!File.Exists(local) &&
                           File.Exists(local + ".43.Accepted.processed"),
                        "成功marker没有原子归档并停止重复消费");
                }
                finally
                {
                    try { if (File.Exists(local)) File.Delete(local); } catch { }
                    try
                    {
                        var archive = local + ".43.Accepted.processed";
                        if (File.Exists(archive)) File.Delete(archive);
                    }
                    catch { }
                }
            });
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
                var launchNonce = Guid.NewGuid().ToString("N");
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
                    var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                        root,
                        session,
                        dummy.Id,
                        parentStart,
                        RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit);
                    Assert(bootstrap.Succeeded,
                        "Sidecar hang strict authority初始化失败：" + bootstrap.Reason);
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
                            " --sidecar-instance-nonce " + launchNonce +
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
                            Assert(attached?.Type == WatchdogMessageType.Attached &&
                                   attached.SidecarInstanceNonce == launchNonce &&
                                   attached.InstanceNonce == launchNonce,
                                "Sidecar Attach握手失败或nonce未原样回显");
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
                            var deadline = DateTime.UtcNow.AddSeconds(22);
                            while (DateTime.UtcNow < deadline && !takeover)
                            {
                                var message = ReadMessage(reader, deadline - DateTime.UtcNow);
                                if (message == null) break;
                                takeover = message.Type == WatchdogMessageType.RequestStopAll;
                            }
                            Assert(takeover,
                                "无窗口主进程越过15秒启动宽限并连续三次探测失败后Sidecar未请求接管");
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
                    Assert(events.Contains("HeartbeatSuspect") &&
                           events.Contains("HeartbeatUnresponsiveConfirmed") &&
                           events.Contains("HeartbeatUnresponsive"),
                        "Sidecar Journal没有完整记录心跳怀疑、三次UI探测确认与接管链");
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
