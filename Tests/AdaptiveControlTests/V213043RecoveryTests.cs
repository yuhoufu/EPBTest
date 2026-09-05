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
using System.Web.Script.Serialization;
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
            Run("V216真实权威存储保持耗时精度并拒绝哈希篡改", AuthorityStorePreservesNumericIdentity, ref passed);
            Run("SafetyAgent使用快照参数并从最后阶段幂等续跑",
                SafetyAgentUsesSnapshotAndResumesStages, ref passed);
            Run("强杀恢复使用schema6权威回执并由独立代理完成物理确认",
                CrashRecoverySafetyAgentCompletesPhysicalProof, ref passed);
            Run("快照篡改在创建硬件前被证据门禁拒绝",
                SnapshotTamperFailsBeforeHardwareOpen, ref passed);
            Run("正式SafetyAgent新进程拒绝篡改快照",
                ProductionAgentRejectsTamperInFreshProcess, ref passed);
            Run("唯一恢复事务并发观察只推进一次",
                ReplacementTransactionIsSingleConsumption, ref passed);
            Run("Supervisor SafetyAgent请求绑定schema6权威token与挑战nonce",
                SupervisorSafetyAgentProtocolBindsAuthority, ref passed);
            Run("Supervisor权威终态读取绑定固定token并精确往返",
                SupervisorSafetyAuthorityReadProtocolBindsFixedToken,
                ref passed);
            Run("正式重启门禁只接受Supervisor权威且开发态保留证据镜像",
                FormalRecoveryRequiresSupervisorSafetyAuthority,
                ref passed);
            Run("主程序只接受Supervisor单次capability并拒绝直启旧schema、PID复用和错误EXE哈希",
                MainLaunchCapabilityGateRejectsInvalidBindings, ref passed);
            Run("Supervisor恢复拉起协议完整绑定原intent、Session、permit和authority",
                SupervisorRecoveryMainLaunchProtocolBindsDurableAuthority,
                ref passed);
            Run("schema6权威回执拒绝镜像竞争、revision漂移和单字段篡改",
                SupervisorSafetyAuthorityRejectsCompetingEvidenceAndTamper,
                ref passed);
            Run("Supervisor P0告警请求绑定schema6身份且静音不等于清除锁存",
                SupervisorP0AlarmProtocolPreservesLatchSemantics, ref passed);
            Run("Supervisor正式安装读取ProgramData而开发运行读取相邻Config",
                WatchdogRuntimeConfigPathMatchesApplicationPolicy, ref passed);
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

        private static void WatchdogRuntimeConfigPathMatchesApplicationPolicy()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "epb-watchdog-config-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var application = Path.Combine(root, "application");
            var common = Path.Combine(root, "common");
            Directory.CreateDirectory(application);
            Directory.CreateDirectory(common);
            try
            {
                Assert(WatchdogRuntimeConfigPaths.ResolveConfigDirectory(application, common) ==
                       Path.Combine(application, "Config"),
                    "开发运行没有读取EXE相邻Config");
                File.WriteAllText(
                    Path.Combine(application, WatchdogRuntimeConfigPaths.FormalModeMarkerName),
                    string.Empty);
                Assert(WatchdogRuntimeConfigPaths.ResolveConfigDirectory(application, common) ==
                       Path.Combine(common, "MTTFTest", "Config"),
                    "正式Supervisor没有切换到ProgramData配置");
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private static void FormalRecoveryRequiresSupervisorSafetyAuthority()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "epb-safety-authority-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var formal = Path.Combine(root, "formal");
            var development = Path.Combine(root, "development");
            Directory.CreateDirectory(formal);
            Directory.CreateDirectory(development);
            try
            {
                var formalExecutable = Path.Combine(formal, "MTTFTest.exe");
                var developmentExecutable = Path.Combine(development, "MTTFTest.exe");
                Assert(!WatchdogHost.RequiresSupervisorSafetyAuthority(
                        formalExecutable),
                    "没有正式模式标记时不应强制Supervisor权威读取");
                File.WriteAllText(
                    Path.Combine(
                        formal,
                        WatchdogRuntimeConfigPaths.FormalModeMarkerName),
                    string.Empty);
                Assert(WatchdogHost.RequiresSupervisorSafetyAuthority(
                        formalExecutable),
                    "正式模式标记没有切换到Supervisor唯一授权源");
                Assert(!WatchdogHost.RequiresSupervisorSafetyAuthority(
                        developmentExecutable),
                    "开发目录被相邻正式目录的标记错误污染");
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private static void SafetyAgentUsesSnapshotAndResumesStages()
        {
            using (var fixture = SafetyFixture.Create())
            {
                MtEmbTest.ClsGlobal.DaqFrequency = 0;
                MtEmbTest.ClsGlobal.SamplesPerChannel = 0;
                var factory = new RecordingHardwareFactory(failFirstPowerConfirmation: true);

                var first = SafetyAgentRunner.Run(fixture.Arguments, factory);
                Assert(first == 20,
                    "首次电源确认失败未归入可重试硬件失败。ExitCode=" + first);
                var firstReceipt = fixture.ReadReceipt();
                Assert(firstReceipt.Stage == WatchdogSafetyStage.AoZeroConfirmed &&
                       firstReceipt.State == WatchdogSafetyHandoffState.WorkerStarted &&
                       firstReceipt.FailureDomain == RecoveryFailureDomain.HardwareUnavailable,
                    "阶段回执未停留在最后一个已确认安全阶段。");

                var second = SafetyAgentRunner.Run(fixture.Arguments, factory);
                Assert(second == 0,
                    "相同Permit未能从最后有效阶段继续完成。ExitCode=" + second);
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

        private static void AuthorityStorePreservesNumericIdentity()
        {
            using (var fixture = SafetyFixture.Create())
            {
                var receipt = fixture.ReadReceipt();
                receipt.Revision++;
                receipt.StageMonotonicElapsedMs = 77.679864632748;
                var stored = SupervisorSafetyAuthorityStore.Advance(fixture.AuthorityDirectory,
                    fixture.AuthorityId, fixture.AuthorityRevision, fixture.AuthorityCanonicalSha256, receipt);
                Assert(stored.IsValid() && BitConverter.DoubleToInt64Bits(stored.Receipt.StageMonotonicElapsedMs) ==
                    BitConverter.DoubleToInt64Bits(receipt.StageMonotonicElapsedMs), "真实Store写入/读回漂移");
                var json = new JavaScriptSerializer();
                var tampered = json.Deserialize<SupervisorSafetyAuthorityRecord>(File.ReadAllText(fixture.AuthorityReceiptPath));
                tampered.ReceiptCanonicalSha256 = new string('e', 64);
                File.WriteAllText(fixture.AuthorityReceiptPath, json.Serialize(tampered));
                Assert(!SupervisorSafetyAuthorityStore.TryRead(fixture.AuthorityDirectory, fixture.AuthorityId,
                    out _, out var failure) && failure.Contains("ReceiptCanonicalSha256Mismatch"), "篡改被放过或诊断没有指出哈希字段");
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

        private static void CrashRecoverySafetyAgentCompletesPhysicalProof()
        {
            using (var fixture = SafetyFixture.Create())
            {
                var receipt = fixture.ReadReceipt();
                receipt.SchemaVersion = SupervisorProtocol.SchemaVersion;
                receipt.CrashRecovery = true;
                receipt.OldProcessExitProven = true;
                receipt.OldProcessId = 43210;
                receipt.OldProcessStartUtcTicks = DateTime.UtcNow.AddMinutes(-1).Ticks;
                receipt.OldProcessObservation =
                    DurableRelaunchProcessObservation.Dead;
                receipt.OldProcessExitObservedUtcTicks = DateTime.UtcNow.Ticks;
                receipt.OldProcessExitEvidenceOwner =
                    WatchdogSafetyEvidenceOwner.SupervisorService;
                receipt.OldProcessExitEvidenceSource = "TestExactProcessProbe";
                receipt.DataAuditState =
                    WatchdogDataAuditState.CrashRepairRequired;
                receipt.PersistenceDrained = false;
                receipt.Revision++;
                fixture.WriteReceipt(receipt);

                var factory = new RecordingHardwareFactory(
                    failFirstPowerConfirmation: false);
                var exitCode = SafetyAgentRunner.Run(fixture.Arguments, factory);
                var afterRun = fixture.ReadReceipt();
                Assert(exitCode == 0,
                    "独立SafetyAgent拒绝了强杀恢复schema6权威回执：Exit=" +
                    exitCode + ";State=" + afterRun.State + ";Stage=" +
                    afterRun.Stage + ";Code=" + afterRun.FailureCode +
                    ";Detail=" + afterRun.Detail);
                var completed = fixture.ReadReceipt();
                Assert(completed.SchemaVersion == SupervisorProtocol.SchemaVersion &&
                       completed.CrashRecovery &&
                       completed.OldProcessExitProven &&
                       !completed.PersistenceDrained &&
                       WatchdogRecoveryReadinessPolicy
                           .IsCompleteSafetyHandoffProof(completed),
                    "强杀后物理安全证据未形成可恢复闭环");
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
                                " --journal-directory " + Quote(fixture.JournalDirectory) +
                                " --authority-id " + Quote(fixture.AuthorityId) +
                                " --authority-receipt " +
                                Quote(fixture.AuthorityReceiptPath) +
                                " --authority-revision " +
                                fixture.AuthorityRevision +
                                " --authority-sha256 " +
                                Quote(fixture.AuthorityCanonicalSha256),
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    Assert(process != null && process.WaitForExit(10000),
                        "正式SafetyAgent无效配置测试超时。");
                    Assert(process.ExitCode == 10,
                        "正式SafetyAgent未以配置无效退出码拒绝篡改快照：Exit=" +
                        process.ExitCode + "。");
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
                Assert(read.SchemaVersion == WatchdogJournalPolicy.CurrentSchemaVersion,
                    "恢复事务必须统一写入当前schema 5。实际=" + read.SchemaVersion);
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

        private static void SupervisorSafetyAgentProtocolBindsAuthority()
        {
            var arguments = "--session-id " + Guid.NewGuid().ToString("N") +
                            " --handoff-id " + Guid.NewGuid().ToString("N") +
                            " --handoff-nonce " + Guid.NewGuid().ToString("N") +
                            " --journal-directory C:\\EPB-Test";
            SupervisorSafetyAgentLaunchRequest source;
            using (var current = Process.GetCurrentProcess())
            {
                source = new SupervisorSafetyAgentLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    SessionId = Guid.NewGuid().ToString("N"),
                    PermitGeneration = 9,
                    PermitId = Guid.NewGuid().ToString("N"),
                    HandoffId = Guid.NewGuid().ToString("N"),
                    HandoffNonceSha256 = new string('A', 64),
                    AuthorityId = Guid.NewGuid().ToString("N"),
                    AuthorityReceiptRevision = 17,
                    AuthorityReceiptCanonicalSha256 = new string('C', 64),
                    ExecutablePath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "MTTFTest.SafetyAgent.exe"),
                    ExecutableSha256 = new string('B', 64),
                    Arguments = arguments,
                    ArgumentsSha256 =
                        SupervisorProtocol.ComputeTextSha256(arguments),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
            }
            Assert(source.IsStructurallyValid(),
                "合法schema6 SafetyAgent监督请求被拒绝。");

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    source.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    var magic = SupervisorProtocol.ReadRequestMagic(reader);
                    var roundTrip =
                        SupervisorSafetyAgentLaunchRequest.ReadBodyFrom(reader, magic);
                    Assert(roundTrip.IsStructurallyValid() &&
                           roundTrip.SchemaVersion ==
                               WatchdogJournalPolicy.CurrentSchemaVersion &&
                           roundTrip.PermitGeneration == source.PermitGeneration &&
                           string.Equals(roundTrip.PermitId, source.PermitId,
                               StringComparison.Ordinal) &&
                           string.Equals(roundTrip.ChallengeNonce,
                               source.ChallengeNonce, StringComparison.Ordinal) &&
                           string.Equals(roundTrip.AuthorityId,
                               source.AuthorityId, StringComparison.Ordinal) &&
                           roundTrip.AuthorityReceiptRevision ==
                               source.AuthorityReceiptRevision &&
                           string.Equals(roundTrip.AuthorityReceiptCanonicalSha256,
                               source.AuthorityReceiptCanonicalSha256,
                               StringComparison.Ordinal),
                        "SafetyAgent监督协议往返丢失schema/permit/challenge身份。");
                }
            }

            source.Arguments += " --tampered true";
            Assert(!source.IsStructurallyValid(),
                "参数被篡改但未更新哈希的SafetyAgent请求仍被接受。");
        }

        private static void
            SupervisorSafetyAuthorityReadProtocolBindsFixedToken()
        {
            SupervisorSafetyAuthorityReadRequest source;
            using (var current = Process.GetCurrentProcess())
            {
                source = new SupervisorSafetyAuthorityReadRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    AuthorityId = Guid.NewGuid().ToString("N"),
                    SessionId = Guid.NewGuid().ToString("N"),
                    HandoffId = Guid.NewGuid().ToString("N"),
                    PermitGeneration = 19,
                    PermitId = Guid.NewGuid().ToString("N"),
                    InitialReceiptRevision = 7,
                    InitialReceiptCanonicalSha256 = new string('A', 64)
                };
            }
            Assert(source.IsStructurallyValid(),
                "合法Supervisor权威读取请求被结构门禁拒绝。");

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    source.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    var magic = SupervisorProtocol.ReadRequestMagic(reader);
                    var roundTrip =
                        SupervisorSafetyAuthorityReadRequest.ReadBodyFrom(
                            reader,
                            magic);
                    Assert(roundTrip.IsStructurallyValid() &&
                           roundTrip.AuthorityId == source.AuthorityId &&
                           roundTrip.SessionId == source.SessionId &&
                           roundTrip.HandoffId == source.HandoffId &&
                           roundTrip.PermitGeneration ==
                               source.PermitGeneration &&
                           roundTrip.PermitId == source.PermitId &&
                           roundTrip.InitialReceiptRevision ==
                               source.InitialReceiptRevision &&
                           roundTrip.InitialReceiptCanonicalSha256 ==
                               source.InitialReceiptCanonicalSha256,
                        "权威读取请求往返丢失固定token字段。");
                }
            }

            var receiptJson = "{\"State\":\"Completed\",\"Revision\":12}";
            var response = new SupervisorSafetyAuthorityReadResponse
            {
                RequestId = source.RequestId,
                ChallengeNonce = source.ChallengeNonce,
                Accepted = true,
                AuthorityId = source.AuthorityId,
                SessionId = source.SessionId,
                HandoffId = source.HandoffId,
                ReceiptRevision = 12,
                ReceiptCanonicalSha256 =
                    SupervisorProtocol.ComputeTextSha256(receiptJson),
                ReceiptJson = receiptJson,
                Detail = "SupervisorSafetyAuthorityRead"
            };
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    response.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    var roundTrip =
                        SupervisorSafetyAuthorityReadResponse.ReadFrom(reader);
                    Assert(roundTrip.SchemaVersion ==
                               SupervisorProtocol.SchemaVersion &&
                           roundTrip.Accepted &&
                           roundTrip.RequestId == source.RequestId &&
                           roundTrip.ChallengeNonce == source.ChallengeNonce &&
                           roundTrip.AuthorityId == source.AuthorityId &&
                           roundTrip.SessionId == source.SessionId &&
                           roundTrip.HandoffId == source.HandoffId &&
                           roundTrip.ReceiptRevision == 12 &&
                           roundTrip.ReceiptJson == receiptJson &&
                           roundTrip.ReceiptCanonicalSha256 ==
                               SupervisorProtocol.ComputeTextSha256(receiptJson),
                        "权威终态响应往返丢失revision或canonical hash。");
                }
            }

            source.InitialReceiptCanonicalSha256 = "tampered";
            Assert(!source.IsStructurallyValid(),
                "被篡改的固定authority token仍通过权威读取门禁。");
        }

        private static void MainLaunchCapabilityGateRejectsInvalidBindings()
        {
            string failure;
            Assert(!MtEmbTest.LaunchCapabilityGate.TryValidate(
                       Array.Empty<string>(), out failure) &&
                   failure == "LaunchCapabilitySchemaMismatch",
                "主程序直接运行未被明确拒绝：" + failure);

            var capabilityId = Guid.NewGuid().ToString("N");
            var sessionId = Guid.NewGuid().ToString("N");
            var permitId = Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N");
            Assert(!MtEmbTest.LaunchCapabilityGate.TryValidate(new[]
                   {
                       SessionAgentProtocol.CapabilityArgument, capabilityId,
                       SessionAgentProtocol.NonceArgument, nonce,
                       SessionAgentProtocol.SessionArgument, sessionId,
                       SessionAgentProtocol.SchemaArgument, "5"
                   }, out failure) &&
                   failure == "LaunchCapabilitySchemaMismatch",
                "旧schema capability未被明确拒绝：" + failure);

            using (var process = Process.GetCurrentProcess())
            {
                var executable = Path.GetFullPath(process.MainModule.FileName);
                var startTicks = process.StartTime.ToUniversalTime().Ticks;
                var now = DateTime.UtcNow.Ticks;
                var canonical = new SessionLaunchCapability
                {
                    CapabilityId = capabilityId,
                    SessionId = sessionId,
                    PermitGeneration = 7,
                    PermitId = permitId,
                    DesktopSessionId = process.SessionId,
                    ExecutablePath = executable,
                    ExecutableSha256 = SupervisorProtocol.ComputeSha256(executable),
                    Arguments = "--test-launch-capability",
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    LaunchNonce = nonce,
                    IssuedUtcTicks = now - TimeSpan.FromSeconds(1).Ticks,
                    ExpiresUtcTicks = now + TimeSpan.FromMinutes(1).Ticks,
                    IssuerProcessId = process.Id,
                    IssuerProcessStartUtcTicks = startTicks
                };
                canonical.ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                    canonical.Arguments);
                var record = CreateConsumptionRecord(
                    canonical, process.Id, startTicks);

                Assert(MtEmbTest.LaunchCapabilityGate.ValidateBoundCapability(
                           canonical, record, capabilityId, nonce, sessionId,
                           process.Id, startTicks, process.SessionId, executable,
                           now, out failure),
                    "合法Supervisor capability未通过字段级门禁：" + failure);

                Assert(!MtEmbTest.LaunchCapabilityGate.ValidateBoundCapability(
                           canonical, record, capabilityId, nonce, sessionId,
                           process.Id, startTicks + 1, process.SessionId, executable,
                           now, out failure) &&
                       failure == "LaunchCapabilityProcessIdentityMismatch",
                    "PID复用StartTicks未被拒绝：" + failure);

                var correctHash = canonical.ExecutableSha256;
                canonical.ExecutableSha256 = new string('a', 64);
                Assert(!MtEmbTest.LaunchCapabilityGate.ValidateBoundCapability(
                           canonical, record, capabilityId, nonce, sessionId,
                           process.Id, startTicks, process.SessionId, executable,
                           now, out failure) &&
                       failure == "LaunchCapabilityExecutableMismatch",
                    "错误EXE哈希未被拒绝：" + failure);
                canonical.ExecutableSha256 = correctHash;

                record.PermitId = Guid.NewGuid().ToString("N");
                Assert(!MtEmbTest.LaunchCapabilityGate.ValidateBoundCapability(
                           canonical, record, capabilityId, nonce, sessionId,
                           process.Id, startTicks, process.SessionId, executable,
                           now, out failure) &&
                       failure == "LaunchCapabilityBindingMismatch",
                    "consumption permit字段篡改未被拒绝：" + failure);
            }
        }

        private static SessionLaunchConsumptionRecord CreateConsumptionRecord(
            SessionLaunchCapability capability,
            int processId,
            long processStartUtcTicks)
        {
            return new SessionLaunchConsumptionRecord
            {
                SchemaVersion = SessionAgentProtocol.SchemaVersion,
                CapabilityId = capability.CapabilityId,
                SessionId = capability.SessionId,
                PermitGeneration = capability.PermitGeneration,
                PermitId = capability.PermitId,
                LaunchNonce = capability.LaunchNonce,
                ExecutablePath = capability.ExecutablePath,
                ExecutableSha256 = capability.ExecutableSha256,
                ArgumentsSha256 = capability.ArgumentsSha256,
                State = "Started",
                ProcessId = processId,
                ProcessStartUtcTicks = processStartUtcTicks
            };
        }

        private static void
            SupervisorRecoveryMainLaunchProtocolBindsDurableAuthority()
        {
            SupervisorMainLaunchRequest source;
            using (var process = Process.GetCurrentProcess())
            {
                var executable = Path.GetFullPath(process.MainModule.FileName);
                source = new SupervisorMainLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = process.Id,
                    RequesterProcessStartUtcTicks =
                        process.StartTime.ToUniversalTime().Ticks,
                    DesktopSessionId =
                        SessionAgentProtocol.RegisteredDesktopSessionId,
                    ExecutablePath = executable,
                    ExecutableSha256 =
                        SupervisorProtocol.ComputeSha256(executable),
                    Arguments = "--recovery-protocol-test",
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    IsRecoveryLaunch = true,
                    RecoveryCapabilityId = Guid.NewGuid().ToString("N"),
                    RecoverySessionId = Guid.NewGuid().ToString("N"),
                    RecoveryPermitGeneration = 17,
                    RecoveryPermitId = Guid.NewGuid().ToString("N"),
                    RecoveryAuthorityRevision = 23,
                    RecoveryAuthoritySha256 = new string('A', 64)
                };
            }
            source.ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                source.Arguments);
            Assert(source.IsStructurallyValid(),
                "完整恢复拉起绑定被结构门禁拒绝。");

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    source.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    var magic = reader.ReadString();
                    var roundTrip = SupervisorMainLaunchRequest.ReadBodyFrom(
                        reader, magic);
                    Assert(roundTrip.IsStructurallyValid() &&
                           roundTrip.IsRecoveryLaunch &&
                           roundTrip.DesktopSessionId ==
                               SessionAgentProtocol.RegisteredDesktopSessionId &&
                           roundTrip.RecoveryCapabilityId ==
                               source.RecoveryCapabilityId &&
                           roundTrip.RecoverySessionId ==
                               source.RecoverySessionId &&
                           roundTrip.RecoveryPermitGeneration ==
                               source.RecoveryPermitGeneration &&
                           roundTrip.RecoveryPermitId ==
                               source.RecoveryPermitId &&
                           roundTrip.RecoveryAuthorityRevision ==
                               source.RecoveryAuthorityRevision &&
                           roundTrip.RecoveryAuthoritySha256 ==
                               source.RecoveryAuthoritySha256,
                        "Supervisor恢复拉起协议往返丢失权威绑定字段。");
                }
            }

            using (var process = Process.GetCurrentProcess())
                source.DesktopSessionId = process.SessionId;
            Assert(!source.IsStructurallyValid(),
                "恢复请求不得重新猜测当前控制台桌面会话。");
            source.DesktopSessionId =
                SessionAgentProtocol.RegisteredDesktopSessionId;
            source.RecoveryPermitId = string.Empty;
            Assert(!source.IsStructurallyValid(),
                "缺失permit的恢复拉起请求仍被接受。");
            source.IsRecoveryLaunch = false;
            Assert(!source.IsStructurallyValid(),
                "初始启动请求夹带恢复authority字段仍被接受。");
        }

        private static void SupervisorP0AlarmProtocolPreservesLatchSemantics()
        {
            SupervisorP0AlarmRequest source;
            using (var current = Process.GetCurrentProcess())
            {
                source = new SupervisorP0AlarmRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    Action = SupervisorP0AlarmAction.MuteBuzzer,
                    EventId = Guid.NewGuid().ToString("N"),
                    Code = "ConfirmedHardwareFault",
                    Detail = "FaultLightAndLatchPreserved=True"
                };
            }
            Assert(source.IsStructurallyValid(),
                "合法schema5 P0静音请求被结构门禁拒绝。");

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    source.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    var magic = SupervisorProtocol.ReadRequestMagic(reader);
                    var roundTrip = SupervisorP0AlarmRequest.ReadBodyFrom(reader, magic);
                    Assert(roundTrip.IsStructurallyValid() &&
                           roundTrip.SchemaVersion ==
                               WatchdogJournalPolicy.CurrentSchemaVersion &&
                           roundTrip.Action == SupervisorP0AlarmAction.MuteBuzzer &&
                           roundTrip.Action != SupervisorP0AlarmAction.ClearTransient &&
                           string.Equals(roundTrip.EventId, source.EventId,
                               StringComparison.Ordinal) &&
                           string.Equals(roundTrip.ChallengeNonce,
                               source.ChallengeNonce, StringComparison.Ordinal),
                        "P0告警协议往返丢失schema/动作/事件/challenge身份，或把静音降级为清锁存。");
                }
            }

            source.Action = (SupervisorP0AlarmAction)999;
            Assert(!source.IsStructurallyValid(),
                "未定义的P0告警动作仍被结构门禁接受。");
        }

        private static void SupervisorSafetyAuthorityRejectsCompetingEvidenceAndTamper()
        {
            Assert(SupervisorOriginalProcessIdentityPolicy.Matches(
                       1201, 638924256000000000, 1201, 638924256000000000) &&
                   !SupervisorOriginalProcessIdentityPolicy.Matches(
                       1201, 638924256000000000, 1202, 638924256000000000) &&
                   !SupervisorOriginalProcessIdentityPolicy.Matches(
                       1201, 638924256000000000, 1201, 638924256000000001),
                "Supervisor未把恢复回执中的旧PID/StartTicks精确绑定到原主进程身份。");
            using (var fixture = SafetyFixture.Create())
            {
                var serializer = new JavaScriptSerializer();
                var forgedMirror = fixture.ReadReceipt();
                forgedMirror.Revision += 1000;
                forgedMirror.HandoffId = Guid.NewGuid().ToString("N");
                var mirrorPath = WatchdogJournalPaths.ProjectSafetyHandoffPath(
                    fixture.JournalDirectory,
                    fixture.SessionId);
                File.WriteAllText(
                    mirrorPath,
                    serializer.Serialize(forgedMirror),
                    new UTF8Encoding(false));

                var authoritative = fixture.ReadReceipt();
                Assert(authoritative.HandoffId == fixture.HandoffId &&
                       authoritative.Revision < forgedMirror.Revision,
                    "项目证据镜像竞争覆盖了Supervisor唯一权威回执。");

                var next = authoritative;
                next.Detail = "revision-mismatch-probe";
                next.Revision++;
                var revisionRejected = false;
                try
                {
                    SupervisorSafetyAuthorityStore.Advance(
                        fixture.AuthorityDirectory,
                        fixture.AuthorityId,
                        fixture.AuthorityRevision + 1,
                        fixture.AuthorityCanonicalSha256,
                        next);
                }
                catch (InvalidDataException ex)
                {
                    revisionRejected = ex.Message.Contains(
                        "InitialRevisionMismatch");
                }
                Assert(revisionRejected,
                    "固定authority token的revision漂移未被明确拒绝。");

                var authorityJson = File.ReadAllText(
                    fixture.AuthorityReceiptPath,
                    Encoding.UTF8);
                authorityJson = authorityJson.Replace(
                    fixture.HandoffId,
                    Guid.NewGuid().ToString("N"));
                File.WriteAllText(
                    fixture.AuthorityReceiptPath,
                    authorityJson,
                    new UTF8Encoding(false));
                SupervisorSafetyAuthorityRecord ignored;
                string failure;
                Assert(!SupervisorSafetyAuthorityStore.TryRead(
                           fixture.AuthorityDirectory,
                           fixture.AuthorityId,
                           out ignored,
                           out failure),
                    "权威回执单字段篡改仍被接受。");
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
            Assert(WatchdogHost.CalculateCircuitHalfOpenDelaySeconds(0, false) == 30 &&
                   WatchdogHost.CalculateCircuitHalfOpenDelaySeconds(2, false) == 300 &&
                   WatchdogHost.CalculateCircuitHalfOpenDelaySeconds(0, true) == 1800 &&
                   WatchdogHost.CalculateCircuitHalfOpenDelaySeconds(99, true) == 1800,
                "LastKnownGood失败后未固定30分钟半开探测，或普通退避被破坏。");

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
            internal string AuthorityDirectory;
            internal string AuthorityId;
            internal string AuthorityReceiptPath;
            internal long AuthorityRevision;
            internal string AuthorityCanonicalSha256;
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
                fixture.AuthorityDirectory = Path.Combine(
                    fixture.RootDirectory,
                    "supervisor-authority");
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
                var authority = SupervisorSafetyAuthorityStore.CreateOrRead(
                    fixture.AuthorityDirectory,
                    fixture.JournalDirectory,
                    receipt);
                fixture.AuthorityId = authority.AuthorityId;
                fixture.AuthorityReceiptPath =
                    SupervisorSafetyAuthorityStore.GetPath(
                        fixture.AuthorityDirectory,
                        authority.AuthorityId);
                fixture.AuthorityRevision = authority.InitialReceiptRevision;
                fixture.AuthorityCanonicalSha256 =
                    authority.InitialReceiptCanonicalSha256;
                Assert(SafetyAgentArguments.TryParse(new[]
                {
                    "--session-id", fixture.SessionId,
                    "--handoff-id", fixture.HandoffId,
                    "--handoff-nonce", fixture.Nonce,
                    "--journal-directory", fixture.JournalDirectory,
                    "--authority-id", fixture.AuthorityId,
                    "--authority-receipt", fixture.AuthorityReceiptPath,
                    "--authority-revision", fixture.AuthorityRevision.ToString(),
                    "--authority-sha256", fixture.AuthorityCanonicalSha256
                }, out fixture.Arguments), "SafetyAgent测试参数无效。");
                return fixture;
            }

            internal WatchdogSafetyHandoffReceipt ReadReceipt()
            {
                SupervisorSafetyAuthorityRecord authority;
                string failure;
                Assert(SupervisorSafetyAuthorityStore.TryRead(
                           AuthorityDirectory,
                           AuthorityId,
                           out authority,
                           out failure),
                    "无法读取Supervisor权威安全回执：" + failure);
                return authority.Receipt;
            }

            internal void WriteReceipt(WatchdogSafetyHandoffReceipt receipt)
            {
                SupervisorSafetyAuthorityStore.Advance(
                    AuthorityDirectory,
                    AuthorityId,
                    AuthorityRevision,
                    AuthorityCanonicalSha256,
                    receipt);
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
