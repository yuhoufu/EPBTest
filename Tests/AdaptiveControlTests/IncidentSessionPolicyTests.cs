using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using Controller;
using Config;

namespace AdaptiveControlTests
{
    internal static class IncidentSessionPolicyTests
    {
        internal static void SessionKeyAndWindowMerge()
        {
            var policy = new IncidentSessionPolicy(new IncidentSessionPolicyOptions
            {
                MergeWindow = TimeSpan.FromSeconds(60),
                ActiveSessionLimitPerDevice = 4,
                DailyByteQuota = 1024 * 1024,
                MinimumFreeBytes = 0
            });
            var start = DateTime.UtcNow;
            var first = policy.Observe(new IncidentSessionRequest
            {
                Device = " dev1 ", FaultCode = " DaqCallbackStale ", RunEpoch = 7,
                OccurredUtc = start, IsTrigger = true
            });
            var merged = policy.Observe(new IncidentSessionRequest
            {
                Device = "DEV1", FaultCode = "DAQCALLBACKSTALE", RunEpoch = 7,
                OccurredUtc = start.AddSeconds(30)
            });
            var outside = policy.Observe(new IncidentSessionRequest
            {
                Device = "DEV1", FaultCode = "DAQCALLBACKSTALE", RunEpoch = 7,
                OccurredUtc = start.AddSeconds(91)
            });
            var otherCode = policy.Observe(new IncidentSessionRequest
            {
                Device = "DEV1", FaultCode = "OtherFault", RunEpoch = 7,
                OccurredUtc = start.AddSeconds(92)
            });
            var otherDevice = policy.Observe(new IncidentSessionRequest
            {
                Device = "DEV2", FaultCode = "DaqCallbackStale", RunEpoch = 7,
                OccurredUtc = start.AddSeconds(93)
            });
            Assert(first.IsNewSession && merged.Merged && first.SessionId == merged.SessionId,
                "同设备/故障/epoch 60秒窗口未合并");
            Assert(outside.IsNewSession && outside.SessionId != first.SessionId,
                "terminal前超过60秒仍复用了旧会话");
            Assert(otherCode.IsNewSession && otherDevice.IsNewSession &&
                   otherCode.SessionKey != otherDevice.SessionKey,
                "不同故障码或设备错误共享会话键");

            // The deterministic queue identity is the runtime SessionId, not
            // the changing recovery CorrelationId. One hundred observations
            // therefore produce one trigger, one terminal and all distinct
            // lightweight phases in one storage root.
            var exported = new System.Collections.Concurrent.ConcurrentQueue<DaqIncidentEvidenceBatch>();
            var queue = new DaqIncidentEvidenceQueue(batch => exported.Enqueue(batch));
            var runId = Guid.NewGuid();
            var storageId = EpbManager.DaqIncidentStorageSessionId(runId, "DaqCallbackStale", 7);
            var stableContext = EpbManager.DaqIncidentEvidenceSessionContextKey(
                "DEV1", first.SessionKey, first.SessionId);
            for (var i = 0; i < 100; i++)
            {
                var phase = i == 0 ? "00-trigger" : i == 99 ? "90-terminal" : "phase-" + i;
                Assert(queue.Submit(new DaqIncidentEvidenceSubmission
                {
                    ContextKey = stableContext,
                    SessionKey = first.SessionKey,
                    SessionId = first.SessionId,
                    StorageSessionId = storageId,
                    RunId = runId,
                    CorrelationId = Guid.NewGuid(),
                    Device = "DEV1",
                    StartedUtc = start,
                    PhaseKey = phase,
                    IncidentJson = "{}",
                    IsTrigger = i == 0,
                    IsTerminal = i == 99
                }), "不同CorrelationId同一SessionId未进入事故队列");
            }
            Assert(queue.DrainAsync(5000).GetAwaiter().GetResult(),
                "同SessionId事故队列未收口");
            var submissions = exported.SelectMany(item => item.OrderedSubmissions()).ToArray();
            Assert(submissions.Count(item => item.IsTrigger) == 1 &&
                   submissions.Count(item => item.IsTerminal) == 1 &&
                   submissions.Length == 100,
                "同SessionId未保持单trigger/单terminal或丢失phase");
            var rootA = EpbManager.DaqIncidentEvidenceDirectoryPath(
                "C:\\IncidentSnapshots", storageId, runId, Guid.NewGuid());
            var rootB = EpbManager.DaqIncidentEvidenceDirectoryPath(
                "C:\\IncidentSnapshots", storageId, runId, Guid.NewGuid());
            Assert(rootA == rootB, "同存储SessionId因CorrelationId变化生成了不同事故根");

            var ordinaryExpected = EpbManager.ResolveExpectedIncidentDevices(
                "DEV1",
                sharedCorrelation: false,
                registeredDevices: new[] { "DEV1", "DEV2" },
                correlatedDevices: new[] { "DEV2" });
            var sharedExpected = EpbManager.ResolveExpectedIncidentDevices(
                "DEV1",
                sharedCorrelation: true,
                registeredDevices: new[] { "DEV1", "DEV2" },
                correlatedDevices: new[] { "DEV2" });
            Assert(ordinaryExpected.Length == 1 && ordinaryExpected[0] == "DEV1" &&
                   sharedExpected.SequenceEqual(new[] { "DEV1", "DEV2" }),
                "普通device-scoped会话错误继承全run设备，或shared会话未冻结双方");
            var deferred = EpbManager.TryResolveExpectedIncidentDevices(
                "DEV1",
                sharedCorrelation: true,
                registeredDevices: ThrowingDevices(),
                correlatedDevices: Array.Empty<string>(),
                out var deferredExpected);
            Assert(!deferred && deferredExpected.SequenceEqual(new[] { "DEV1", "DEV2" }),
                "shared expected设备发现异常时未保留baseline并延期发布");

            AssertIncidentEvidenceWorkerIndependent();
        }

        internal static void HeavyGatesPreserveSummary()
        {
            var policy = new IncidentSessionPolicy(new IncidentSessionPolicyOptions
            {
                ActiveSessionLimitPerDevice = 1,
                DailyByteQuota = 10,
                MinimumFreeBytes = 100
            });
            var decision = policy.Observe(new IncidentSessionRequest
            {
                Device = "Dev1", FaultCode = "Fault", RunEpoch = 1,
                IsTrigger = true, IsHeavyEvidence = true, EstimatedHeavyBytes = 100,
                AvailableFreeBytes = 10
            });
            Assert(!decision.HeavyEvidenceAllowed && decision.SummaryMustBeWritten,
                "活动/余量门禁未抑制重证据或错误抑制摘要");
            var unknown = policy.Observe(new IncidentSessionRequest
            {
                Device = "Dev1", FaultCode = "UnknownFree", RunEpoch = 1,
                IsTrigger = true, IsHeavyEvidence = true, EstimatedHeavyBytes = 1,
                AvailableFreeBytes = -1
            });
            Assert(!unknown.HeavyEvidenceAllowed && unknown.SummaryMustBeWritten,
                "余量探测unknown时未抑制重证据或错误抑制摘要");
            var probedPath = "\\\\server\\share\\IncidentSnapshots";
            var probeSeen = string.Empty;
            Assert(EpbManager.TryGetIncidentAvailableFreeBytesForTest(
                       probedPath,
                       path => { probeSeen = path; return 123L; }) == 123L &&
                   probeSeen.StartsWith("\\\\", StringComparison.Ordinal),
                "Incident UNC-safe余量探测未复用注入探针");
            Assert(EpbManager.TryGetIncidentAvailableFreeBytesForTest(
                       "\\\\unknown-server\\missing-share\\IncidentSnapshots",
                       path => null) < 0,
                "Incident余量探测失败未返回unknown");
            policy.RecordPersistedBytes("Dev1", 20);
            Assert(policy.GetDailyPersistedBytes("dev1") == 20,
                "每日字节累计未按设备归一化");
        }

        internal static void TerminalManifestAndRetention()
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBTest-Incident-" + Guid.NewGuid().ToString("N"));
            var session = Path.Combine(root, "Run-1-Incident-1");
            Directory.CreateDirectory(session);
            try
            {
                var receipt = IncidentSessionManifestStore.WritePhase(
                    Path.Combine(session, "00-trigger-DEV1"),
                    "00-trigger", "trigger", "Dev1", "DEV1|FAULT|1", "{}\n", null, true,
                    out var heavyError);
                Assert(heavyError == null && receipt.JsonCommitted &&
                       File.Exists(Path.Combine(session, "00-trigger-DEV1", "phase.receipt.json")),
                    "phase receipt 未在 JSON 提交后写入");
                IncidentSessionManifestStore.RecordPhase(session, Guid.NewGuid(), Guid.NewGuid(),
                    DateTime.UtcNow, receipt, true, false);
                var terminalReceipt = IncidentSessionManifestStore.WritePhase(
                    Path.Combine(session, "90-terminal-DEV1"),
                    "90-terminal", "terminal", "Dev1", "DEV1|FAULT|1", "{}\n", null, true,
                    out var terminalHeavyError);
                Assert(terminalHeavyError == null && terminalReceipt.JsonCommitted,
                    "terminal phase receipt 未提交");
                terminalReceipt.PhaseDirectory = "90-terminal-DEV1";
                terminalReceipt.JsonPath = "90-terminal-DEV1/incident.json";
                terminalReceipt.ReceiptPath = "90-terminal-DEV1/phase.receipt.json";
                IncidentSessionManifestStore.RewriteReceipt(
                    Path.Combine(session, "90-terminal-DEV1"), terminalReceipt);
                IncidentSessionManifestStore.RecordPhase(session, Guid.NewGuid(), Guid.NewGuid(),
                    DateTime.UtcNow, terminalReceipt, false, true);
                Assert(IncidentSessionManifestStore.PublishTerminalAtomic(session),
                    "终态 manifest 未原子发布");
                Assert(IncidentSessionManifestStore.TryReadValidated(session, out var manifest, out _),
                    "终态 manifest 校验失败");
                Assert(manifest.IsRetentionEligible, "完整终态会话未进入保留候选");

                // A shared correlation freezes the participating device set at
                // trigger/export time.  Dev1 may reach terminal first, but its
                // root must remain unpublished until the delayed Dev2 trigger
                // and terminal phases have committed to the same manifest.
                var shared = Path.Combine(root, "shared-devices");
                Directory.CreateDirectory(shared);
                IncidentSessionManifestStore.RegisterExpectedDevices(shared, new[] { "DEV1", "DEV2" });
                var sharedRun = Guid.NewGuid();
                var sharedCorrelation = Guid.NewGuid();
                var sharedKey = "DEV1|FAULT|1";
                var dev1Trigger = CommitPhase(shared, "00-trigger-DEV1", "trigger", "DEV1", sharedKey);
                IncidentSessionManifestStore.RecordPhase(
                    shared, sharedRun, sharedCorrelation, DateTime.UtcNow,
                    dev1Trigger, true, false);
                var dev1Terminal = CommitPhase(shared, "90-terminal-DEV1", "terminal", "DEV1", sharedKey);
                IncidentSessionManifestStore.RecordPhase(
                    shared, sharedRun, sharedCorrelation, DateTime.UtcNow,
                    dev1Terminal, false, true);
                Assert(!IncidentSessionManifestStore.PublishTerminalAtomic(shared),
                    "共享根在Dev2尚未到达时错误发布终态");

                var dev2Trigger = CommitPhase(shared, "00-trigger-DEV2", "trigger", "DEV2", "DEV2|FAULT|1");
                IncidentSessionManifestStore.RecordPhase(
                    shared, sharedRun, sharedCorrelation, DateTime.UtcNow,
                    dev2Trigger, true, false);
                var dev2Terminal = CommitPhase(shared, "90-terminal-DEV2", "terminal", "DEV2", "DEV2|FAULT|1");
                IncidentSessionManifestStore.RecordPhase(
                    shared, sharedRun, sharedCorrelation, DateTime.UtcNow,
                    dev2Terminal, false, true);
                Assert(IncidentSessionManifestStore.PublishTerminalAtomic(shared),
                    "共享根在全部设备terminal后未发布");
                Assert(IncidentSessionManifestStore.TryReadValidated(shared, out var sharedManifest, out _) &&
                       sharedManifest.ExpectedDevices.Count == 2 &&
                       sharedManifest.Devices.Count == 2,
                    "共享根expected device集合未冻结或未完整收口");

                var ordinary = Path.Combine(root, "ordinary-dev1");
                Directory.CreateDirectory(ordinary);
                IncidentSessionManifestStore.RegisterExpectedDevices(
                    ordinary,
                    EpbManager.ResolveExpectedIncidentDevices(
                        "DEV1", false, new[] { "DEV1", "DEV2" }, Array.Empty<string>()));
                var ordinaryRun = Guid.NewGuid();
                var ordinaryCorrelation = Guid.NewGuid();
                var ordinaryTrigger = CommitPhase(
                    ordinary, "00-trigger-DEV1", "trigger", "DEV1", "DEV1|FAULT|1");
                IncidentSessionManifestStore.RecordPhase(
                    ordinary, ordinaryRun, ordinaryCorrelation, DateTime.UtcNow,
                    ordinaryTrigger, true, false);
                var ordinaryTerminal = CommitPhase(
                    ordinary, "90-terminal-DEV1", "terminal", "DEV1", "DEV1|FAULT|1");
                IncidentSessionManifestStore.RecordPhase(
                    ordinary, ordinaryRun, ordinaryCorrelation, DateTime.UtcNow,
                    ordinaryTerminal, false, true);
                Assert(IncidentSessionManifestStore.PublishTerminalAtomic(ordinary),
                    "普通Dev1 device-scoped根因错误等待未发生故障的Dev2");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        internal static void ConfigDefaultsAndStormLevels()
        {
            var values = new NameValueCollection
            {
                ["DaqIncidentMergeWindowSeconds"] = "60",
                ["DaqIncidentStormWarnPerHour"] = "2",
                ["DaqIncidentStormErrorPerHour"] = "5",
                ["DaqIncidentCopyRecentCycles"] = "false"
            };
            var policy = IncidentSessionPolicy.FromAppSettings(values);
            var first = policy.Observe(new IncidentSessionRequest
            {
                Device = "Dev1", FaultCode = "Fault", RunEpoch = 1,
                IsTrigger = true, OccurredUtc = DateTime.UtcNow
            });
            Assert(first.StormLevel == IncidentStormLevel.Normal && !policy.Options.CopyRecentCycles,
                "Incident 严格配置默认值解析错误");
            for (var i = 0; i < 5; i++)
                first = policy.Observe(new IncidentSessionRequest
                {
                    Device = "Dev1", FaultCode = "Fault-" + i, RunEpoch = 1,
                    IsTrigger = true, OccurredUtc = DateTime.UtcNow.AddSeconds(i)
                });
            Assert(first.StormLevel == IncidentStormLevel.Error,
                "storm >5/h 未提升为 error");
        }

        internal static void RetentionKeepsLatestTenAndSkipsUnsafe()
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBTest-IncidentRetention-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                for (var i = 0; i < 11; i++)
                    CreateCompletedSession(Path.Combine(root, "session-" + i), i);
                var active = Path.Combine(root, "active");
                Directory.CreateDirectory(active);
                File.WriteAllText(Path.Combine(active, IncidentSessionManifest.FileName), "{}");
                var legacy = Path.Combine(root, "legacy");
                Directory.CreateDirectory(legacy);
                File.WriteAllText(Path.Combine(legacy, IncidentSessionManifest.FileName), "{\"Schema\":99}");
                var corrupt = Path.Combine(root, "corrupt");
                CreateCompletedSession(corrupt, 99);
                File.WriteAllText(Path.Combine(corrupt, "phase-DEV1", "incident.json"), "corrupt");

                var candidates = IncidentSessionRetention.FindCandidates(
                    root,
                    new IncidentSessionRetentionOptions
                    {
                        RetainCompleteSessionsPerDevice = 10,
                        BatchInterval = TimeSpan.Zero
                    },
                    out var skipped);
                Assert(candidates.Count == 1 && Path.GetFileName(candidates[0]) == "session-10",
                    "完整会话保留10个后未只选最旧第11个");
                var enforced = IncidentSessionRetention.Enforce(
                    root,
                    new IncidentSessionRetentionOptions
                    {
                        RetainCompleteSessionsPerDevice = 10,
                        BatchInterval = TimeSpan.Zero,
                        WarningSink = message => Console.WriteLine("WARN " + message)
                    });
                Assert(enforced.DeletedDirectories == 1 &&
                       Directory.GetDirectories(root, "session-*", SearchOption.TopDirectoryOnly).Length == 10,
                    $"Enforce未实际删除第11个完整会话并收敛到10个：Deleted={enforced.DeletedDirectories}; Failed={enforced.FailedDirectories}; Remaining={Directory.GetDirectories(root, "session-*", SearchOption.TopDirectoryOnly).Length}");
                Assert(skipped >= 3 && Directory.Exists(active) && Directory.Exists(legacy) &&
                       Directory.Exists(corrupt),
                    "active/legacy/corrupt 会话被错误列入删除");

                // A crash after Directory.Move but before sidecar promotion
                // leaves the durable pending marker beside the old source.
                // Startup resume must promote it, remove only the marked tree,
                // and leave an unmarked staging tree for inspection.
                var pendingSource = Path.Combine(root, "pending-source");
                var pendingStaging = Path.Combine(root, ".incident-retention-staging-pending");
                Directory.CreateDirectory(pendingStaging);
                File.WriteAllText(Path.Combine(pendingStaging, "payload.bin"), "x");
                var pendingPath = pendingSource + ".incident-retention.pending.json";
                File.WriteAllText(
                    pendingPath,
                    "{\"Schema\":1,\"Source\":\"" + JsonEscape(pendingSource) +
                    "\",\"Staging\":\"" + JsonEscape(pendingStaging) + "\"}");
                var unknownStaging = Path.Combine(root, ".incident-retention-staging-unknown");
                Directory.CreateDirectory(unknownStaging);
                File.WriteAllText(Path.Combine(unknownStaging, "keep.bin"), "keep");
                IncidentSessionRetention.ResumeStaging(root, System.Threading.CancellationToken.None, null);
                Assert(!Directory.Exists(pendingStaging) && !File.Exists(pendingPath) &&
                       Directory.Exists(unknownStaging) &&
                       File.Exists(Path.Combine(unknownStaging, "keep.bin")),
                    "pending sidecar续扫或未知staging保护失败");

                // Resume keeps the production five-second cadence, while the
                // injected waiter makes the batch boundary deterministic here.
                var throttledStaging = Path.Combine(root, ".incident-retention-staging-throttle");
                Directory.CreateDirectory(throttledStaging);
                for (var i = 0; i < 17; i++)
                    File.WriteAllText(Path.Combine(throttledStaging, "part-" + i + ".bin"), "x");
                var throttledSidecar = throttledStaging + ".incident-retention.json";
                File.WriteAllText(
                    throttledSidecar,
                    "{\"Schema\":1,\"Source\":\"" + JsonEscape(Path.Combine(root, "throttled-source")) +
                    "\",\"Staging\":\"" + JsonEscape(throttledStaging) + "\"}");
                var observedInterval = TimeSpan.Zero;
                IncidentSessionRetention.ResumeStaging(
                    root,
                    System.Threading.CancellationToken.None,
                    null,
                    (interval, token) => { observedInterval = interval; return false; });
                Assert(observedInterval >= TimeSpan.FromSeconds(5) &&
                       !Directory.Exists(throttledStaging) && !File.Exists(throttledSidecar),
                    "Resume staging未按5秒可取消节流或批次未收口");

                // A single file larger than the 32 MiB batch budget is never
                // treated as a reason to exceed the cap: it remains staged for
                // a later retry and emits only a warning.
                var oversizedStaging = Path.Combine(root, ".incident-retention-staging-oversized");
                Directory.CreateDirectory(oversizedStaging);
                var oversizedFile = Path.Combine(oversizedStaging, "oversized.bin");
                using (var stream = new FileStream(oversizedFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    stream.SetLength(32L * 1024L * 1024L + 1L);
                var oversizedSidecar = oversizedStaging + ".incident-retention.json";
                File.WriteAllText(
                    oversizedSidecar,
                    "{\"Schema\":1,\"Source\":\"" + JsonEscape(Path.Combine(root, "oversized-source")) +
                    "\",\"Staging\":\"" + JsonEscape(oversizedStaging) + "\"}");
                var oversizedWarning = false;
                IncidentSessionRetention.ResumeStaging(
                    root,
                    System.Threading.CancellationToken.None,
                    message =>
                    {
                        oversizedWarning |= message.IndexOf("超过批次预算", StringComparison.OrdinalIgnoreCase) >= 0;
                    },
                    (interval, token) => false);
                Assert(File.Exists(oversizedFile) && File.Exists(oversizedSidecar) && oversizedWarning,
                    "超过32MiB单文件未保留并Warn");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void CreateCompletedSession(string root, int index)
        {
            Directory.CreateDirectory(root);
            var run = Guid.NewGuid();
            var correlation = Guid.NewGuid();
            var phase = Path.Combine(root, "phase-DEV1");
            var receipt = IncidentSessionManifestStore.WritePhase(
                phase, "00-trigger", "trigger", "Dev1", "DEV1|FAULT|1", "{}\n", null, true,
                out var ignored);
            receipt.PhaseDirectory = "phase-DEV1";
            receipt.JsonPath = "phase-DEV1/incident.json";
            receipt.ReceiptPath = "phase-DEV1/phase.receipt.json";
            IncidentSessionManifestStore.RewriteReceipt(phase, receipt);
            IncidentSessionManifestStore.RecordPhase(root, run, correlation,
                DateTime.UtcNow.AddMinutes(-index), receipt, true, false);
            var terminal = IncidentSessionManifestStore.WritePhase(
                Path.Combine(root, "terminal-DEV1"), "90-terminal", "terminal", "Dev1",
                "DEV1|FAULT|1", "{}\n", null, true, out ignored);
            terminal.PhaseDirectory = "terminal-DEV1";
            terminal.JsonPath = "terminal-DEV1/incident.json";
            terminal.ReceiptPath = "terminal-DEV1/phase.receipt.json";
            IncidentSessionManifestStore.RewriteReceipt(Path.Combine(root, "terminal-DEV1"), terminal);
            IncidentSessionManifestStore.RecordPhase(root, run, correlation,
                DateTime.UtcNow.AddMinutes(-index), terminal, false, true);
            IncidentSessionManifestStore.PublishTerminalAtomic(
                root,
                quiet: true,
                completedUtc: DateTime.UtcNow.AddMinutes(-index));
        }

        private static IncidentPhaseReceipt CommitPhase(
            string root,
            string directoryName,
            string kind,
            string device,
            string sessionKey)
        {
            var phaseDirectory = Path.Combine(root, directoryName);
            var receipt = IncidentSessionManifestStore.WritePhase(
                phaseDirectory,
                directoryName,
                kind,
                device,
                sessionKey,
                "{}\n",
                null,
                true,
                out var heavyError);
            Assert(heavyError == null && receipt.JsonCommitted,
                "共享设备phase JSON/receipt未提交");
            receipt.PhaseDirectory = directoryName;
            receipt.JsonPath = directoryName + "/incident.json";
            receipt.ReceiptPath = directoryName + "/phase.receipt.json";
            IncidentSessionManifestStore.RewriteReceipt(phaseDirectory, receipt);
            return receipt;
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        private static IEnumerable<string> ThrowingDevices()
        {
            throw new InvalidOperationException("injected expected-device discovery failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        private static void AssertIncidentEvidenceWorkerIndependent()
        {
            // Use an uninitialized shell only to exercise the drain boundary;
            // the evidence queue itself is injected below and never registered
            // with TaskSupervisor.
            var manager = (EpbManager)FormatterServices.GetUninitializedObject(typeof(EpbManager));
            var supervisor = new TaskSupervisor(NullLogger.Instance);
            typeof(EpbManager).GetField(
                    "_taskSupervisor",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(manager, supervisor);

            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var attempts = 0;
            var queue = new DaqIncidentEvidenceQueue(
                batch =>
                {
                    entered.Set();
                    if (Interlocked.Increment(ref attempts) == 1)
                        throw new IOException("injected incident export failure");
                    if (!release.Wait(5000))
                        throw new TimeoutException("incident retry was not released");
                });
            typeof(EpbManager).GetField(
                    "_daqIncidentEvidenceQueue",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(manager, queue);

            var runId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var contextKey = "DEV1:independent:" + Guid.NewGuid().ToString("N");
            Assert(queue.Submit(new DaqIncidentEvidenceSubmission
            {
                ContextKey = contextKey,
                RunId = runId,
                CorrelationId = correlationId,
                Device = "DEV1",
                StartedUtc = DateTime.UtcNow,
                PhaseKey = "00-trigger",
                IncidentJson = "{}",
                IsTrigger = true
            }), "独立Incident worker trigger未入队");
            Assert(entered.Wait(2000), "独立Incident worker未启动");
            Assert(manager.DrainBackgroundTasksAsync(50).GetAwaiter().GetResult() &&
                   supervisor.ActiveCount == 0,
                "Incident worker异常进入TaskSupervisor或StopAll drain边界");
            Assert(queue.Submit(new DaqIncidentEvidenceSubmission
            {
                ContextKey = contextKey,
                RunId = runId,
                CorrelationId = correlationId,
                Device = "DEV1",
                StartedUtc = DateTime.UtcNow,
                PhaseKey = "90-terminal",
                IncidentJson = "{}",
                IsTerminal = true
            }), "独立Incident worker retry期间terminal未保留");
            release.Set();
            Assert(queue.DrainAsync(5000).GetAwaiter().GetResult() && attempts >= 2,
                "Incident worker失败后未持久重试并收口");
        }
    }
}
