using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using MTTFTest.SafetyAgent;
using MTTFTest.SafetyHardware;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class V213043RecoveryTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("SafetyAgent使用快照参数并从最后阶段幂等续跑",
                SafetyAgentUsesSnapshotAndResumesStages, ref passed);
            Run("快照篡改在创建硬件前被证据门禁拒绝",
                SnapshotTamperFailsBeforeHardwareOpen, ref passed);
            Run("正式SafetyAgent新进程拒绝篡改快照",
                ProductionAgentRejectsTamperInFreshProcess, ref passed);
            Run("唯一恢复事务并发观察只推进一次",
                ReplacementTransactionIsSingleConsumption, ref passed);
            Run("DAQ恢复重入门禁在失联后重新累计稳定窗口",
                DaqRejoinGateRollsBackAndRecovers, ref passed);
            Run("DAQ恢复重入门禁对持续接管许可保持关闭",
                DaqRejoinGateFailsClosedOnPendingTakeover, ref passed);
            Run("自动半开只接受非永久主程序启动失败域",
                AutomaticHalfOpenIsFailureDomainSpecific, ref passed);
            Run("CircuitOpen在未附着状态仍推进半开且重复观察按30秒汇总",
                CircuitOpenIsAttachmentIndependentAndSummarized, ref passed);
            Run("最小安全DAQ边界首次写OFF、字面零电压并保持2000/20",
                MinimalSafetyDaqUsesFailSafeFirstWrites, ref passed);
            Run("SafetyAgent二进制依赖闭包拒绝生产采集和业务程序集",
                SafetyAgentDependencyClosureIsMinimal, ref passed);
            Run("Webhook关闭为LocalOnly且不创建远程积压或网络线程",
                WebhookDisabledIsHealthyLocalOnly, ref passed);
            Run("Webhook告警HMAC签名、压缩、重放和退避重试",
                UnattendedAlarmIsSignedDurableAndRetryable, ref passed);
            Run("不可变DAQ参数拒绝零值和非法批大小",
                DaqRuntimeSettingsRejectInvalidValues, ref passed);
            Run("生产与校正采集入口显式保持不可变DAQ参数",
                AcquisitionEntrypointsKeepImmutableSettings, ref passed);
            return passed;
        }

        private static void SafetyAgentUsesSnapshotAndResumesStages()
        {
            using (var fixture = SafetyFixture.Create())
            {
                MtEmbTest.ClsGlobal.DaqFrequency = 0;
                MtEmbTest.ClsGlobal.SamplesPerChannel = 0;
                var factory = new RecordingHardwareFactory(failFirstPowerConfirmation: true);

                var first = SafetyAgentRunner.Run(fixture.Arguments, factory);
                Assert(first == 20, "首次电源确认失败未归入可重试硬件失败。");
                var firstReceipt = fixture.ReadReceipt();
                Assert(firstReceipt.Stage == WatchdogSafetyStage.AoZeroConfirmed &&
                       firstReceipt.State == WatchdogSafetyHandoffState.WorkerStarted &&
                       firstReceipt.FailureDomain == RecoveryFailureDomain.HardwareUnavailable,
                    "阶段回执未停留在最后一个已确认安全阶段。");

                var second = SafetyAgentRunner.Run(fixture.Arguments, factory);
                Assert(second == 0, "相同Permit未能从最后有效阶段继续完成。");
                var completed = fixture.ReadReceipt();
                Assert(completed.IsSafetyCompleted &&
                       completed.Stage == WatchdogSafetyStage.Completed &&
                       completed.FailureDomain == RecoveryFailureDomain.None,
                    "续跑后未形成schema v3完整安全证据。");
                Assert(factory.LastRuntime != null &&
                       Math.Abs(factory.LastRuntime.SampleRateHz - 2000) < 0.001 &&
                       factory.LastRuntime.SamplesPerChannel == 20,
                    "SafetyAgent读取了全局0/0，而不是快照中的2000/20。");
                Assert(factory.DoCalls == 1 && factory.AoCalls == 1 &&
                       factory.PowerCalls == 2 && factory.PressureCalls == 1,
                    "阶段续跑重复执行了已确认动作，或漏掉未确认动作。");

                var createCount = factory.CreateCount;
                Assert(SafetyAgentRunner.Run(fixture.Arguments, factory) == 0 &&
                       factory.CreateCount == createCount,
                    "已完成回执被重复消费并再次打开硬件。");
            }
        }

        private static void SnapshotTamperFailsBeforeHardwareOpen()
        {
            using (var fixture = SafetyFixture.Create())
            {
                File.AppendAllText(
                    Path.Combine(fixture.Snapshot.ConfigDirectory, "AIConfig.xml"),
                    "<!--tampered-->");
                var factory = new RecordingHardwareFactory(false);
                Assert(SafetyAgentRunner.Run(fixture.Arguments, factory) == 10,
                    "篡改快照未被配置/证据门禁拒绝。");
                var receipt = fixture.ReadReceipt();
                Assert(factory.CreateCount == 0 &&
                       receipt.State == WatchdogSafetyHandoffState.Failed &&
                       receipt.FailureDomain == RecoveryFailureDomain.EvidenceBinding &&
                       receipt.FailureCode == "SafetyAgentConfigInvalid",
                    "篡改快照在硬件打开前未失败，或污染了错误失败域。");
            }
        }

        private static void ProductionAgentRejectsTamperInFreshProcess()
        {
            using (var fixture = SafetyFixture.Create())
            {
                File.AppendAllText(
                    Path.Combine(fixture.Snapshot.ConfigDirectory, "DOConfig.xml"),
                    "<!--tampered-->");
                var executable = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "MTTFTest.SafetyAgent.exe");
                Assert(File.Exists(executable), "测试输出缺少正式SafetyAgent.exe。");
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--session-id " + Quote(fixture.SessionId) +
                                " --handoff-id " + Quote(fixture.HandoffId) +
                                " --handoff-nonce " + Quote(fixture.Nonce) +
                                " --journal-directory " + Quote(fixture.JournalDirectory),
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    Assert(process != null && process.WaitForExit(10000),
                        "正式SafetyAgent无效配置测试超时。");
                    Assert(process.ExitCode == 10,
                        "正式SafetyAgent未以配置无效退出码拒绝篡改快照。");
                }
                var receipt = fixture.ReadReceipt();
                Assert(receipt.State == WatchdogSafetyHandoffState.Failed &&
                       receipt.Stage == WatchdogSafetyStage.None &&
                       receipt.FailureDomain == RecoveryFailureDomain.EvidenceBinding,
                    "fresh-process拦截发生在硬件动作之后或失败域错误。");
            }
        }

        private static void ReplacementTransactionIsSingleConsumption()
        {
            var journal = NewTemporaryDirectory("ReplacementTransaction");
            var session = Guid.NewGuid().ToString("N");
            var permit = Guid.NewGuid().ToString("N");
            try
            {
                var applied = 0;
                var replayed = 0;
                Parallel.For(0, 32, _ =>
                {
                    var result = RecoveryReplacementTransactionStore.Advance(
                        journal, session, 7, permit,
                        RecoveryReplacementState.Approved, "concurrent observer");
                    Assert(result.Succeeded, "并发恢复事务提交失败：" + result.Reason);
                    if (result.AlreadyApplied) Interlocked.Increment(ref replayed);
                    else Interlocked.Increment(ref applied);
                });
                Assert(applied == 1 && replayed == 31,
                    "同一(Session,Generation,Permit)被多次消费。");

                for (var state = RecoveryReplacementState.OldProcessExitProven;
                     state <= RecoveryReplacementState.CheckpointCommitted;
                     state++)
                {
                    var result = RecoveryReplacementTransactionStore.Advance(
                        journal, session, 7, permit, state, state.ToString());
                    Assert(result.Succeeded && !result.AlreadyApplied,
                        "恢复事务未按固定状态机单向推进：" + result.Reason);
                }
                var replay = RecoveryReplacementTransactionStore.Advance(
                    journal, session, 7, permit,
                    RecoveryReplacementState.MainStarted, "late observer");
                Assert(replay.Succeeded && replay.AlreadyApplied,
                    "迟到观察者未返回AlreadyApplied。");

                RecoveryReplacementTransaction read;
                Assert(RecoveryReplacementTransactionStore.TryRead(
                           journal, session, 7, permit, out read) &&
                       read.State == RecoveryReplacementState.CheckpointCommitted,
                    "唯一恢复事务终态未耐久提交。");
                var skipped = RecoveryReplacementTransactionStore.Advance(
                    journal, session, 8, Guid.NewGuid().ToString("N"),
                    RecoveryReplacementState.SafetyAgentRunning, "skip");
                Assert(!skipped.Succeeded && skipped.Reason.Contains("ApprovalMissing"),
                    "新Permit允许跳过Approved直接启动SafetyAgent。");
            }
            finally
            {
                TryDeleteDirectory(journal);
            }
        }

        private static void DaqRejoinGateRollsBackAndRecovers()
        {
            var calls = 0;
            long monotonicMilliseconds = 1;
            DaqRecoveryRejoinGate.WaitAsync(
                TimeSpan.FromMilliseconds(70),
                TimeSpan.FromMilliseconds(500),
                () =>
                {
                    var current = Interlocked.Increment(ref calls);
                    var watchdogHealthy = current <= 3 || current >= 7;
                    return Observation(current, watchdogHealthy, false);
                },
                () => monotonicMilliseconds,
                1000,
                _ =>
                {
                    monotonicMilliseconds += 20;
                    return Task.CompletedTask;
                },
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(calls >= 10 && monotonicMilliseconds >= 180,
                "Watchdog空窗后未回滚稳定窗口，过早允许DAQ重入。");
        }

        private static void DaqRejoinGateFailsClosedOnPendingTakeover()
        {
            AssertThrows<TimeoutException>(() =>
                DaqRecoveryRejoinGate.WaitAsync(
                    TimeSpan.FromMilliseconds(60),
                    TimeSpan.FromMilliseconds(140),
                    () => Observation(1, true, true),
                    CancellationToken.None).GetAwaiter().GetResult(),
                "存在待处理接管许可时DAQ重入门禁未保持关闭。");
        }

        private static void AutomaticHalfOpenIsFailureDomainSpecific()
        {
            Assert(WatchdogHost.IsAutomaticHalfOpenEligible(
                       "RecoveryLaunchFailed:Process.Start", false) &&
                   WatchdogHost.IsAutomaticHalfOpenEligible(
                       "RecoveryAttachFailed:Timeout", false) &&
                   !WatchdogHost.IsAutomaticHalfOpenEligible(
                       "SafetyAgentConfigInvalid", false) &&
                   !WatchdogHost.IsAutomaticHalfOpenEligible(
                       "RecoveryLaunchFailed:IdentityMismatch", true),
                "Circuit half-open混入安全证据失败或永久身份失败。");
        }

        private static void CircuitOpenIsAttachmentIndependentAndSummarized()
        {
            Assert(WatchdogHost.ShouldProbeCircuitHalfOpen(true, false) &&
                   WatchdogHost.ShouldProbeCircuitHalfOpen(true, true) &&
                   !WatchdogHost.ShouldProbeCircuitHalfOpen(false, false),
                "CircuitOpen半开仍错误依赖旧主程序_attached状态。");

            var summarizer = new RepeatedEventSummarizer(30000);
            var first = 0;
            var summaries = 0;
            var suppressed = 0;
            for (long timestamp = 0; timestamp <= 300000; timestamp += 250)
            {
                var decision = summarizer.Observe(
                    "ApplicationExitIdentityMismatch|session|7|permit|fingerprint",
                    timestamp);
                if (decision.EmitFirst) first++;
                if (decision.EmitSummary)
                {
                    summaries++;
                    suppressed += decision.SuppressedCount;
                }
            }
            var changedIdentity = summarizer.Observe(
                "ApplicationExitIdentityMismatch|session|7|permit|changed",
                300000);
            Assert(first == 1 && summaries == 10 && suppressed == 1200 &&
                   changedIdentity.EmitFirst,
                "5分钟250ms观察未压缩为首次事件和最多10条30秒汇总，或身份变化未单独记录。" +
                " First=" + first + ";Summary=" + summaries +
                ";Suppressed=" + suppressed);
        }

        private static void MinimalSafetyDaqUsesFailSafeFirstWrites()
        {
            var configDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Config");
            var configuration = SafetyHardwareConfiguration.Load(
                configDirectory,
                new[] { "Pressure_1" });
            var backend = new RecordingSafetyDaqBackend();

            using (var digital = new SafetyDigitalOutputController(backend))
                Assert(digital.ConfirmAllOff(configuration.DoPhysicalLines),
                    "最小安全DO未确认全OFF。");
            Assert(backend.DigitalWrites.Count > 0 &&
                   backend.DigitalWrites.All(values => values.All(value => !value)),
                "DO任务创建后的首次硬件写入不是全false。");

            using (var analog = new SafetyAnalogOutputController(backend))
                Assert(analog.ConfirmLiteralZero(configuration.AoPhysicalChannels),
                    "最小安全AO未确认字面0V。");
            Assert(backend.AnalogWrites.Count == configuration.AoPhysicalChannels.Length &&
                   backend.AnalogWrites.All(value => Math.Abs(value) < double.Epsilon),
                "AO安全动作经过了压力标定换算或未写入字面0.0V。");

            using (var pressure = new SafetyPressureProbe(
                       configuration.PressureChannels,
                       2000,
                       20,
                       backend))
            {
                var sample = pressure.Read(configuration.PressureChannels[0].HydraulicId);
                Assert(sample.IsFinite, "fake NI压力探针未生成有效最小样本。");
            }
            Assert(backend.PressureOpenRates.SequenceEqual(new[] { 2000.0 }) &&
                   backend.PressureOpenBatchSizes.SequenceEqual(new[] { 20 }) &&
                   backend.PressureReadBatchSizes.SequenceEqual(new[] { 20 }),
                "最小压力探针未使用snapshot v2显式2000/20参数。");

            var invalidDirectory = NewTemporaryDirectory("InvalidSafetyHardwareConfig");
            try
            {
                foreach (var name in new[]
                {
                    "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml"
                })
                    File.Copy(Path.Combine(configDirectory, name),
                        Path.Combine(invalidDirectory, name));
                var aoPath = Path.Combine(invalidDirectory, "AOConfig.xml");
                File.WriteAllText(
                    aoPath,
                    File.ReadAllText(aoPath, Encoding.UTF8)
                        .Replace("<MaxVoltage>10</MaxVoltage>",
                            "<MaxVoltage>-1</MaxVoltage>"),
                    new UTF8Encoding(false));
                AssertThrows<SafetyHardwareConfigurationException>(
                    () => SafetyHardwareConfiguration.Load(
                        invalidDirectory,
                        new[] { "Pressure_1" }),
                    "不包含0V的AO范围未在创建NI任务前被配置门禁拒绝。");
            }
            finally
            {
                TryDeleteDirectory(invalidDirectory);
            }
        }

        private static void SafetyAgentDependencyClosureIsMinimal()
        {
            var output = AppDomain.CurrentDomain.BaseDirectory;
            var agentPath = Path.Combine(output, "MTTFTest.SafetyAgent.exe");
            var hardwarePath = Path.Combine(output, "MTTFTest.SafetyHardware.dll");
            Assert(File.Exists(agentPath) && File.Exists(hardwarePath),
                "测试输出缺少SafetyAgent或SafetyHardware正式二进制。");

            var closure = ReadLocalDependencyClosure(output, agentPath);
            Assert(closure.Contains("MTTFTest.SafetyHardware") &&
                   closure.All(IsAllowedSafetyDependency),
                "SafetyAgent递归依赖闭包仍包含生产采集、持久化、UI或未知业务程序集：" +
                string.Join(",", closure.OrderBy(value => value)));
        }

        private static HashSet<string> ReadLocalDependencyClosure(
            string outputDirectory,
            string rootAssemblyPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(rootAssemblyPath);
            while (queue.Count > 0)
            {
                var path = Path.GetFullPath(queue.Dequeue());
                if (!visitedPaths.Add(path)) continue;
                var assembly = System.Reflection.Assembly.ReflectionOnlyLoadFrom(path);
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    result.Add(reference.Name);
                    var candidate = Path.Combine(outputDirectory, reference.Name + ".dll");
                    if (File.Exists(candidate) && IsAllowedSafetyDependency(reference.Name))
                        queue.Enqueue(candidate);
                }
            }
            return result;
        }

        private static bool IsAllowedSafetyDependency(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Microsoft.Win32", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("MTTFTest.SafetyHardware", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("MTTFTest.Watchdog.Protocol", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("PowerSupply.Core", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("NationalInstruments.", StringComparison.OrdinalIgnoreCase);
        }

        private static void WebhookDisabledIsHealthyLocalOnly()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "epb-local-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var executableDirectory = Path.Combine(root, "application");
            var configDirectory = Path.Combine(executableDirectory, "Config");
            var journal = Path.Combine(root, "journal");
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(journal);
            File.WriteAllText(
                Path.Combine(configDirectory, "UnattendedAlarmConfig.xml"),
                "<UnattendedAlarm Enabled=\"false\" />",
                new UTF8Encoding(false));
            var handler = new CaptureWebhookHandler();
            try
            {
                using (var sink = new UnattendedAlarmSink(
                           executableDirectory,
                           journal,
                           handler,
                           new[] { 5, 10 }))
                {
                    Assert(sink.DeliveryState == UnattendedAlarmDeliveryState.LocalOnly &&
                           !sink.DeliveryDegraded,
                        "Webhook关闭未进入健康LocalOnly状态。");
                    sink.Publish("P0", "LocalOnlyEvent", "same local evidence",
                        RecoveryFailureDomain.HardwareUnavailable);
                    sink.Publish("P0", "LocalOnlyEvent", "same local evidence",
                        RecoveryFailureDomain.HardwareUnavailable);
                    Thread.Sleep(50);
                }

                var localDirectory = Path.Combine(journal, "UnattendedAlarmJournal");
                var localFiles = Directory.GetFiles(localDirectory, "*.json");
                var localBody = localFiles.Length == 1
                    ? File.ReadAllText(localFiles[0], Encoding.UTF8)
                    : string.Empty;
                Assert(handler.AttemptCount == 0 &&
                       !Directory.Exists(Path.Combine(journal, "UnattendedAlarmSpool")) &&
                       !File.Exists(Path.Combine(journal, "unattended-alarm-degraded.log")) &&
                       localFiles.Length == 1 &&
                       localBody.Contains("\"OccurrenceCount\":2"),
                    "LocalOnly仍发起网络、创建远程积压/降级文件，或未保留本地耐久告警。" +
                    " Attempts=" + handler.AttemptCount +
                    ";RemoteDirectory=" + Directory.Exists(
                        Path.Combine(journal, "UnattendedAlarmSpool")) +
                    ";DegradedFile=" + File.Exists(
                        Path.Combine(journal, "unattended-alarm-degraded.log")) +
                    ";LocalCount=" + localFiles.Length + ";Body=" + localBody);

                var secretVariable = "EPB_LOCAL_ONLY_TEST_" +
                                     Guid.NewGuid().ToString("N").ToUpperInvariant();
                Environment.SetEnvironmentVariable(secretVariable, "enabled-secret");
                try
                {
                    File.WriteAllText(
                        Path.Combine(configDirectory, "UnattendedAlarmConfig.xml"),
                        "<UnattendedAlarm Enabled=\"true\" " +
                        "Endpoint=\"https://localhost/epb-alarm\" " +
                        "SecretEnvironmentVariable=\"" + secretVariable + "\" />",
                        new UTF8Encoding(false));
                    var enabledHandler = new CaptureWebhookHandler();
                    using (var enabled = new UnattendedAlarmSink(
                               executableDirectory,
                               journal,
                               enabledHandler,
                               new[] { 5, 10 }))
                    {
                        enabled.Publish("P0", "EnabledOnlyEvent", "new remote evidence",
                            RecoveryFailureDomain.MainLaunch);
                        WaitUntil(() => enabledHandler.AttemptCount >= 1, 3000,
                            "Webhook启用后的新事件未发送。");
                        Assert(enabledHandler.Bodies.Count == 1 &&
                               enabledHandler.Bodies[0].Contains("EnabledOnlyEvent") &&
                               !enabledHandler.Bodies[0].Contains("LocalOnlyEvent"),
                            "Webhook启用后错误补发LocalOnly历史或产生突发积压。");
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable(secretVariable, null);
                }
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private static void DaqRuntimeSettingsRejectInvalidValues()
        {
            AssertThrows<ArgumentOutOfRangeException>(
                () => new DaqRuntimeSettings(0, 20), "采样率0未被拒绝。");
            AssertThrows<ArgumentOutOfRangeException>(
                () => new DaqRuntimeSettings(2000, 0), "批大小0未被拒绝。");
            var valid = new DaqRuntimeSettings(2000, 20);
            Assert(Math.Abs(valid.BatchPeriodMs - 10) < 0.001,
                "合法DAQ运行参数计算错误。");
        }

        private static void AcquisitionEntrypointsKeepImmutableSettings()
        {
            var settings = new DaqRuntimeSettings(2000, 20);
            MtEmbTest.ClsGlobal.DaqFrequency = 0;
            MtEmbTest.ClsGlobal.SamplesPerChannel = 0;
            using (var monitor = new MTEmbTest.FrmEpbMainMonitor(settings))
            using (var calibration = new MTEmbTest.FrmDAQCalibrate(settings))
            {
                var monitorField = typeof(MTEmbTest.FrmEpbMainMonitor).GetField(
                    "_daqRuntimeSettings",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                var calibrationField = typeof(MTEmbTest.FrmDAQCalibrate).GetField(
                    "_daqRuntimeSettings",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                Assert(ReferenceEquals(monitorField?.GetValue(monitor), settings) &&
                       ReferenceEquals(calibrationField?.GetValue(calibration), settings),
                    "生产或校正采集入口重新读取了可变全局DAQ默认值。");
            }
        }

        private static void UnattendedAlarmIsSignedDurableAndRetryable()
        {
            // DurableJsonFileStore intentionally uses long owner-hash temp names; keep this
            // fixture below legacy .NET Framework MAX_PATH so the test exercises alarm logic.
            var root = Path.Combine(Path.GetTempPath(),
                "epb-alarm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var executableDirectory = Path.Combine(root, "application");
            var configDirectory = Path.Combine(executableDirectory, "Config");
            var journal = Path.Combine(root, "journal");
            var secretVariable = "EPB_TEST_WEBHOOK_SECRET_" +
                                 Guid.NewGuid().ToString("N").ToUpperInvariant();
            const string secret = "unit-test-secret";
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(journal);
            File.WriteAllText(
                Path.Combine(configDirectory, "UnattendedAlarmConfig.xml"),
                "<UnattendedAlarm Enabled=\"true\" FailurePolicy=\"DegradeAndContinue\" " +
                "Endpoint=\"https://localhost/epb-alarm\" " +
                "SecretEnvironmentVariable=\"" + secretVariable + "\" " +
                "RequestTimeoutMs=\"1000\" RetrySeconds=\"1,5,15,30,60,300\" " +
                "SpoolMaxBytes=\"16777216\" />",
                new UTF8Encoding(false));
            Environment.SetEnvironmentVariable(secretVariable, null);
            try
            {
                using (var degraded = new UnattendedAlarmSink(
                           executableDirectory, journal, new CaptureWebhookHandler(),
                           new[] { 5, 10 }))
                {
                    Assert(degraded.DeliveryState ==
                               UnattendedAlarmDeliveryState.WebhookDegraded &&
                           degraded.DeliveryDegraded,
                        "Webhook密钥缺失时未进入AlarmDeliveryDegraded。");
                    degraded.Publish("P0", "RepeatedIdentityMismatch", "same evidence",
                        RecoveryFailureDomain.EvidenceBinding);
                    degraded.Publish("P0", "RepeatedIdentityMismatch", "same evidence",
                        RecoveryFailureDomain.EvidenceBinding);
                }
                var spool = Path.Combine(journal, "UnattendedAlarmSpool");
                var compressed = Directory.GetFiles(spool, "*.json");
                var compressedBody = compressed.Length == 1
                    ? File.ReadAllText(compressed[0], Encoding.UTF8)
                    : string.Empty;
                var degradedLogPath = Path.Combine(journal, "unattended-alarm-degraded.log");
                var degradedLog = File.Exists(degradedLogPath)
                    ? File.ReadAllText(degradedLogPath, Encoding.UTF8)
                    : string.Empty;
                Assert(compressed.Length == 1 &&
                       compressedBody.Contains("\"OccurrenceCount\":2"),
                    "重复告警未持久化去重压缩。Count=" + compressed.Length +
                    ";Body=" + compressedBody + ";Degraded=" + degradedLog);

                Environment.SetEnvironmentVariable(secretVariable, secret);
                var handler = new CaptureWebhookHandler();
                using (var replay = new UnattendedAlarmSink(
                           executableDirectory, journal, handler, new[] { 5, 10 }))
                {
                    WaitUntil(() => handler.AttemptCount >= 1 &&
                                    Directory.GetFiles(spool, "*.json").Length == 0,
                        3000, "服务重启后未重放耐久告警。");
                    Assert(replay.DeliveryState ==
                               UnattendedAlarmDeliveryState.WebhookHealthy &&
                           !replay.DeliveryDegraded,
                        "Webhook恢复成功后降级状态未清除。");
                    VerifyLastSignature(handler, secret);
                    Assert(handler.Bodies.Last().Contains("\"OccurrenceCount\":2"),
                        "Webhook重放丢失压缩次数。");

                    handler.FailuresRemaining = 1;
                    replay.Publish("P0", "RetryableAlarm", "timeout then retry",
                        RecoveryFailureDomain.MainLaunch);
                    WaitUntil(() => handler.AttemptCount >= 3 &&
                                    Directory.GetFiles(spool, "*.json").Length == 0,
                        3000, "Webhook超时后未按退避策略重试并清空队列。");
                    Assert(handler.Bodies[handler.Bodies.Count - 1] ==
                           handler.Bodies[handler.Bodies.Count - 2],
                        "重试改变了事件ID或Webhook JSON，破坏幂等性。");
                    VerifyLastSignature(handler, secret);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(secretVariable, null);
                TryDeleteDirectory(root);
            }
        }

        private static void VerifyLastSignature(
            CaptureWebhookHandler handler,
            string secret)
        {
            var body = handler.Bodies.Last();
            var timestamp = handler.Timestamps.Last();
            string expected;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                expected = "sha256=" + BitConverter.ToString(hmac.ComputeHash(
                        Encoding.UTF8.GetBytes(timestamp + "\n" + body)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            Assert(string.Equals(expected, handler.Signatures.Last(),
                       StringComparison.Ordinal),
                "Webhook HMAC-SHA256签名不正确。");
        }

        private static void WaitUntil(Func<bool> predicate, int timeoutMs, string message)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate()) return;
                Thread.Sleep(10);
            }
            throw new InvalidOperationException(message);
        }

        private static DaqRecoveryRejoinObservation Observation(
            long sequence,
            bool watchdogHealthy,
            bool takeoverPending)
        {
            return new DaqRecoveryRejoinObservation(
                true, 1, sequence, 0, 0,
                true, true, watchdogHealthy, takeoverPending);
        }

        private static string Quote(string value) =>
            "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static string NewTemporaryDirectory(string name)
        {
            var path = Path.Combine(Path.GetTempPath(),
                "MTTFTest.V213043." + name + "." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
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

        private sealed class RecordingHardwareFactory : ISafetyHardwareFactory
        {
            private readonly bool _failFirstPowerConfirmation;

            internal RecordingHardwareFactory(bool failFirstPowerConfirmation)
            {
                _failFirstPowerConfirmation = failFirstPowerConfirmation;
            }

            internal int CreateCount;
            internal int DoCalls;
            internal int AoCalls;
            internal int PowerCalls;
            internal int PressureCalls;
            internal SafetyRuntimeSnapshot LastRuntime;

            public ISafetyHardware Create(string configDirectory, SafetyRuntimeSnapshot runtime)
            {
                CreateCount++;
                LastRuntime = runtime;
                return new RecordingHardware(this,
                    _failFirstPowerConfirmation && CreateCount == 1);
            }

            private sealed class RecordingHardware : ISafetyHardware
            {
                private readonly RecordingHardwareFactory _owner;
                private readonly bool _failPower;

                internal RecordingHardware(RecordingHardwareFactory owner, bool failPower)
                {
                    _owner = owner;
                    _failPower = failPower;
                }

                public bool ConfirmDoOff() { _owner.DoCalls++; return true; }
                public bool ConfirmAoZero() { _owner.AoCalls++; return true; }
                public bool ConfirmPowerOff() { _owner.PowerCalls++; return !_failPower; }
                public bool ConfirmPressureSafe() { _owner.PressureCalls++; return true; }
                public void Dispose() { }
            }
        }

        private sealed class RecordingSafetyDaqBackend : ISafetyDaqBackend
        {
            internal readonly List<bool[]> DigitalWrites = new List<bool[]>();
            internal readonly List<double> AnalogWrites = new List<double>();
            internal readonly List<double> PressureOpenRates = new List<double>();
            internal readonly List<int> PressureOpenBatchSizes = new List<int>();
            internal readonly List<int> PressureReadBatchSizes = new List<int>();

            public ISafetyDigitalOutput OpenDigitalOutput(
                string taskName,
                string[] physicalLines)
            {
                return new Digital(this);
            }

            public ISafetyAnalogOutput OpenAnalogOutput(
                string taskName,
                string physicalChannel)
            {
                return new Analog(this);
            }

            public ISafetyPressureInput OpenPressureInput(
                string taskName,
                string physicalChannel,
                double sampleRateHz,
                int samplesPerChannel)
            {
                PressureOpenRates.Add(sampleRateHz);
                PressureOpenBatchSizes.Add(samplesPerChannel);
                return new Pressure(this);
            }

            private sealed class Digital : ISafetyDigitalOutput
            {
                private readonly RecordingSafetyDaqBackend _owner;
                internal Digital(RecordingSafetyDaqBackend owner) { _owner = owner; }
                public void Write(bool[] values)
                {
                    _owner.DigitalWrites.Add((values ?? Array.Empty<bool>()).ToArray());
                }
                public void Dispose() { }
            }

            private sealed class Analog : ISafetyAnalogOutput
            {
                private readonly RecordingSafetyDaqBackend _owner;
                internal Analog(RecordingSafetyDaqBackend owner) { _owner = owner; }
                public void WriteVoltage(double value) { _owner.AnalogWrites.Add(value); }
                public void Dispose() { }
            }

            private sealed class Pressure : ISafetyPressureInput
            {
                private readonly RecordingSafetyDaqBackend _owner;
                internal Pressure(RecordingSafetyDaqBackend owner) { _owner = owner; }
                public double[] ReadSamples(int samplesPerChannel)
                {
                    _owner.PressureReadBatchSizes.Add(samplesPerChannel);
                    return Enumerable.Repeat(1.0, samplesPerChannel).ToArray();
                }
                public void Dispose() { }
            }
        }

        private sealed class CaptureWebhookHandler : HttpMessageHandler
        {
            private int _attemptCount;
            internal readonly System.Collections.Generic.List<string> Bodies =
                new System.Collections.Generic.List<string>();
            internal readonly System.Collections.Generic.List<string> Timestamps =
                new System.Collections.Generic.List<string>();
            internal readonly System.Collections.Generic.List<string> Signatures =
                new System.Collections.Generic.List<string>();
            internal int FailuresRemaining;
            internal int AttemptCount => Volatile.Read(ref _attemptCount);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var timestamp = request.Headers.GetValues("X-EPB-Timestamp").Single();
                var signature = request.Headers.GetValues("X-EPB-Signature").Single();
                lock (Bodies)
                {
                    Bodies.Add(body);
                    Timestamps.Add(timestamp);
                    Signatures.Add(signature);
                }
                Interlocked.Increment(ref _attemptCount);
                if (Interlocked.CompareExchange(ref FailuresRemaining, 0, 0) > 0)
                {
                    Interlocked.Decrement(ref FailuresRemaining);
                    return Task.FromException<HttpResponseMessage>(
                        new TaskCanceledException("mock timeout"));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        private sealed class SafetyFixture : IDisposable
        {
            private SafetyFixture() { }

            internal string RootDirectory;
            internal string JournalDirectory;
            internal string SessionId;
            internal string HandoffId;
            internal string Nonce;
            internal WatchdogSafetyConfigSnapshotResult Snapshot;
            internal SafetyAgentArguments Arguments;

            internal static SafetyFixture Create()
            {
                var fixture = new SafetyFixture
                {
                    RootDirectory = NewTemporaryDirectory("SafetyAgent"),
                    SessionId = Guid.NewGuid().ToString("N"),
                    HandoffId = Guid.NewGuid().ToString("N"),
                    Nonce = Guid.NewGuid().ToString("N")
                };
                fixture.JournalDirectory = Path.Combine(fixture.RootDirectory, "journal");
                var appConfig = Path.Combine(fixture.RootDirectory, "app", "Config");
                var projectConfig = Path.Combine(fixture.RootDirectory, "project", "Config");
                Directory.CreateDirectory(fixture.JournalDirectory);
                Directory.CreateDirectory(appConfig);
                Directory.CreateDirectory(projectConfig);
                foreach (var name in new[]
                {
                    "AIConfig.xml", "AOConfig.xml", "DOConfig.xml",
                    "PowerSupplyConfig.xml", "TestConfig.xml"
                })
                    File.WriteAllText(Path.Combine(appConfig, name),
                        "<Config Name=\"" + name + "\" />");
                File.WriteAllText(Path.Combine(projectConfig, "TestConfig.xml"),
                    "<Config Name=\"ProjectTestConfig.xml\" />");

                var permitId = Guid.NewGuid().ToString("N");
                var permitNonce = Guid.NewGuid().ToString("N");
                fixture.Snapshot = WatchdogSafetyConfigSnapshotStore.Create(
                    fixture.JournalDirectory,
                    fixture.HandoffId,
                    appConfig,
                    projectConfig,
                    "2.13.0.43-test",
                    new SafetyRuntimeSnapshot
                    {
                        SampleRateHz = 2000,
                        SamplesPerChannel = 20,
                        PressureChannels = new[] { "Pressure_1" },
                        ReleaseSafePressureBar = new[] { 5.0 },
                        PressureSampleMaxAgeMs = 250,
                        ReleaseStableMs = 100,
                        ReleaseTimeoutMs = 2000
                    },
                    fixture.SessionId,
                    3,
                    5,
                    7,
                    permitId,
                    new string('a', 64),
                    new string('b', 64));
                Assert(fixture.Snapshot.Succeeded,
                    "测试安全快照创建失败：" + fixture.Snapshot.Error);

                var receipt = new WatchdogSafetyHandoffReceipt
                {
                    SessionId = fixture.SessionId,
                    SessionGeneration = 3,
                    SessionLease = 5,
                    HandoffId = fixture.HandoffId,
                    Nonce = fixture.Nonce,
                    StopSafetyTransactionId = Guid.NewGuid().ToString("N"),
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 1,
                    Revision = 1,
                    State = WatchdogSafetyHandoffState.Accepted,
                    Stage = WatchdogSafetyStage.None,
                    ProjectDirectory = fixture.JournalDirectory,
                    MainExecutablePath = Path.Combine(fixture.RootDirectory, "MTTFTest.exe"),
                    MainExecutableSha256 = new string('a', 64),
                    SafetyAgentExecutablePath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "MTTFTest.SafetyAgent.exe"),
                    SafetyAgentExecutableSha256 = new string('b', 64),
                    ConfigSnapshotPath = fixture.Snapshot.ConfigDirectory,
                    ConfigSnapshotManifestPath = fixture.Snapshot.ManifestPath,
                    ConfigSnapshotManifestSha256 = fixture.Snapshot.ManifestSha256,
                    ConfigSnapshotSchemaVersion = 2,
                    RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                    RelaunchPermitGeneration = 7,
                    RelaunchPermitId = permitId,
                    RelaunchPermitNonceSha256 =
                        WatchdogTakeoverPermitBindingPolicy.HashNonce(permitNonce),
                    PersistenceDrained = true,
                    LogicalQuiescent = true,
                    HardwareResourcesReleased = true,
                    ExecutionAuthorizationRevoked = true,
                    CallbacksIsolated = true
                };
                WatchdogSafetyHandoffReceiptStore.WriteThrough(
                    fixture.JournalDirectory, receipt);
                Assert(SafetyAgentArguments.TryParse(new[]
                {
                    "--session-id", fixture.SessionId,
                    "--handoff-id", fixture.HandoffId,
                    "--handoff-nonce", fixture.Nonce,
                    "--journal-directory", fixture.JournalDirectory
                }, out fixture.Arguments), "SafetyAgent测试参数无效。");
                return fixture;
            }

            internal WatchdogSafetyHandoffReceipt ReadReceipt()
            {
                WatchdogSafetyHandoffReceipt receipt;
                Assert(WatchdogSafetyHandoffReceiptStore.TryRead(
                           JournalDirectory, SessionId, out receipt),
                    "无法读取安全阶段回执。");
                return receipt;
            }

            public void Dispose()
            {
                TryDeleteFile(WatchdogJournalPaths.LocalSafetyHandoffPath(SessionId));
                TryDeleteFile(WatchdogJournalPaths.LocalSafetyHandoffPath(SessionId) + ".bak");
                TryDeleteDirectory(RootDirectory);
            }
        }
    }
}
