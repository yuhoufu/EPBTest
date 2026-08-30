using System;
using System.IO;
using System.Linq;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class WatchdogSafetyClosureV39Tests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("schema v1终态只代表停止意图且不得终止Sidecar", LegacyClosingTerminalIsNotSafetyProof, ref passed);
            Run("schema v2完整安全证明才允许Sidecar终止", VersionTwoTerminalRequiresAllSafetyProof, ref passed);
            Run("handoff耐久状态拒绝revision和状态倒退并从损坏副本回退", HandoffStoreIsMonotonicAndRecoversCorruption, ref passed);
            Run("Watchdog v4安全交接结构化消息精确往返", SafetyHandoffWireRoundTrips, ref passed);
            Run("应用退出意图绑定精确进程且状态单调", ApplicationExitReceiptIsExactAndMonotonic, ref passed);
            Run("强类型接管退出精确绑定Permit并拒绝ABA",
                TypedTakeoverExitRejectsPermitAba, ref passed);
            Run("安全接管配置快照完整且篡改后拒绝使用",
                SafetyConfigSnapshotIsImmutableAndVerified, ref passed);
            return passed;
        }

        private static void LegacyClosingTerminalIsNotSafetyProof()
        {
            var closing = CreateClosing(1, WatchdogClosingTombstoneState.Terminal);
            closing.SafetyStage = 0;
            closing.MotorsOff = true;
            closing.PowerOff = true;
            closing.PressureSafe = true;
            closing.PersistenceDrained = true;
            closing.LogicalQuiescent = true;
            Assert(closing.IsValidFor(closing.SessionId), "schema v1未被兼容读取");
            Assert(!closing.IsSafetyTerminal &&
                   closing.EffectiveSafetyStage == WatchdogClosingSafetyStage.ClosingIntent &&
                   WatchdogHost.EvaluateCloseFenceAction(false, closing, false, false) ==
                   WatchdogCloseFenceAction.SuppressRelaunch,
                "schema v1被错误解释为安全完成");
        }

        private static void VersionTwoTerminalRequiresAllSafetyProof()
        {
            var closing = CreateClosing(2, WatchdogClosingTombstoneState.Terminal);
            closing.SafetyStage = WatchdogClosingSafetyStage.Terminal;
            closing.MotorsOff = true;
            closing.PowerOff = true;
            closing.PressureSafe = true;
            closing.PersistenceDrained = true;
            closing.LogicalQuiescent = false;
            Assert(!closing.IsSafetyTerminal &&
                   WatchdogHost.EvaluateCloseFenceAction(false, closing, false, false) ==
                   WatchdogCloseFenceAction.SuppressRelaunch,
                "缺失逻辑静默证明的v2围栏被错误终止");
            closing.LogicalQuiescent = true;
            Assert(closing.IsSafetyTerminal &&
                   WatchdogHost.EvaluateCloseFenceAction(false, closing, false, false) ==
                   WatchdogCloseFenceAction.TerminateSession,
                "完整v2安全终态未获准终止Sidecar");
        }

        private static void HandoffStoreIsMonotonicAndRecoversCorruption()
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "MTTFTest.SafetyClosureV39." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var session = Guid.NewGuid().ToString("N");
            try
            {
                var receipt = new WatchdogSafetyHandoffReceipt
                {
                    SchemaVersion = 1,
                    SessionId = session,
                    SessionGeneration = 7,
                    SessionLease = 11,
                    HandoffId = Guid.NewGuid().ToString("N"),
                    Nonce = Guid.NewGuid().ToString("N"),
                    StopSafetyTransactionId = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    State = WatchdogSafetyHandoffState.Requested,
                    PersistenceDrained = true,
                    HardwareResourcesReleased = true,
                    ExecutionAuthorizationRevoked = true,
                    CallbacksIsolated = true
                };
                WatchdogSafetyHandoffReceiptStore.WriteThrough(directory, receipt);
                receipt.Revision = 2;
                receipt.State = WatchdogSafetyHandoffState.Accepted;
                WatchdogSafetyHandoffReceiptStore.WriteThrough(directory, receipt);
                Assert(receipt.CanExitApplication, "Accepted交接未形成UI退出证明");

                var regressed = new WatchdogSafetyHandoffReceipt
                {
                    SchemaVersion = 1,
                    SessionId = receipt.SessionId,
                    SessionGeneration = receipt.SessionGeneration,
                    SessionLease = receipt.SessionLease,
                    HandoffId = receipt.HandoffId,
                    Nonce = receipt.Nonce,
                    StopSafetyTransactionId = receipt.StopSafetyTransactionId,
                    Revision = 3,
                    State = WatchdogSafetyHandoffState.Requested,
                    PersistenceDrained = true,
                    HardwareResourcesReleased = true,
                    ExecutionAuthorizationRevoked = true,
                    CallbacksIsolated = true
                };
                AssertThrows(() => WatchdogSafetyHandoffReceiptStore.WriteThrough(directory, regressed),
                    "交接状态倒退未被拒绝");

                File.WriteAllText(WatchdogJournalPaths.LocalSafetyHandoffPath(session), "{");
                WatchdogSafetyHandoffReceipt recovered;
                Assert(WatchdogSafetyHandoffReceiptStore.TryRead(directory, session, out recovered) &&
                       recovered.Revision == 2 &&
                       recovered.State == WatchdogSafetyHandoffState.Accepted,
                    "本机副本损坏后未从项目副本恢复最新有效revision");
            }
            finally
            {
                DeleteFile(WatchdogJournalPaths.LocalSafetyHandoffPath(session));
                DeleteFile(WatchdogJournalPaths.LocalSafetyHandoffPath(session) + ".bak");
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void SafetyHandoffWireRoundTrips()
        {
            var session = Guid.NewGuid().ToString("N");
            var handoff = Guid.NewGuid().ToString("N");
            var message = new WatchdogMessage
            {
                ProtocolVersion = WatchdogProtocol.Version,
                Type = WatchdogMessageType.SafetyHandoffAccepted,
                SessionId = session,
                CorrelationId = handoff,
                SafetyHandoff = new WatchdogSafetyHandoff
                {
                    SessionId = session,
                    SessionGeneration = 3,
                    SessionLease = 5,
                    HandoffId = handoff,
                    Nonce = Guid.NewGuid().ToString("N"),
                    StopSafetyTransactionId = Guid.NewGuid().ToString("N"),
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 9,
                    PersistenceDrained = true,
                    HardwareResourcesReleased = true,
                    ExecutionAuthorizationRevoked = true,
                    CallbacksIsolated = true,
                    ConfigSnapshotManifestSha256 = new string('a', 64),
                    RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                    RelaunchPermitGeneration = 4,
                    RelaunchPermitId = Guid.NewGuid().ToString("N"),
                    RelaunchPermitNonceSha256 = new string('b', 64),
                    TimestampUtcTicks = DateTime.UtcNow.Ticks
                }
            };
            var roundTrip = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(message));
            Assert(roundTrip.ProtocolVersion == 4 &&
                   roundTrip.Type == WatchdogMessageType.SafetyHandoffAccepted &&
                   roundTrip.SafetyHandoff != null &&
                   roundTrip.SafetyHandoff.SessionId == session &&
                   roundTrip.SafetyHandoff.SessionGeneration == 3 &&
                   roundTrip.SafetyHandoff.SessionLease == 5 &&
                   roundTrip.SafetyHandoff.HandoffId == handoff &&
                   roundTrip.SafetyHandoff.ExecutionAuthorizationRevoked &&
                   roundTrip.SafetyHandoff.CallbacksIsolated &&
                   roundTrip.SafetyHandoff.RelaunchDisposition ==
                       WatchdogRelaunchDisposition.PreserveApprovedPermit &&
                   roundTrip.SafetyHandoff.RelaunchPermitGeneration == 4 &&
                   roundTrip.SafetyHandoff.ConfigSnapshotManifestSha256 ==
                       new string('a', 64),
                "v4安全交接DTO未保持精确身份与进程隔离证明");
        }

        private static void TypedTakeoverExitRejectsPermitAba()
        {
            Assert(WatchdogExitDispositionPolicy.ResolveApplicationExit(
                       false, WatchdogExitDisposition.NormalCompletionExit) ==
                   WatchdogExitDisposition.NormalCompletionExit &&
                   WatchdogExitDispositionPolicy.ResolveApplicationExit(
                       false, WatchdogExitDisposition.OperatorExit) ==
                   WatchdogExitDisposition.OperatorExit &&
                   WatchdogExitDispositionPolicy.ResolveApplicationExit(
                       true, WatchdogExitDisposition.NormalCompletionExit) ==
                   WatchdogExitDisposition.TakeoverReplacementExit,
                "人工、正常完成和接管替换的强类型退出分流不完整。");
            var session = Guid.NewGuid().ToString("N");
            var permitId = Guid.NewGuid().ToString("N");
            var permitNonce = Guid.NewGuid().ToString("N");
            var permitNonceSha = WatchdogTakeoverPermitBindingPolicy.HashNonce(permitNonce);
            var takeoverId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var exit = new WatchdogApplicationExitReceipt
            {
                SessionId = session,
                SessionGeneration = 5,
                SessionLease = 9,
                ExitIntentId = Guid.NewGuid().ToString("N"),
                TakeoverTransactionId = takeoverId,
                ExitDisposition = WatchdogExitDisposition.TakeoverReplacementExit,
                RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                RelaunchPermitGeneration = 12,
                RelaunchPermitId = permitId,
                RelaunchPermitNonceSha256 = permitNonceSha,
                Revision = 1,
                State = WatchdogApplicationExitState.Requested,
                MainProcessId = 4321,
                MainProcessStartUtcTicks = now.AddMinutes(-1).Ticks,
                RequestedUtcTicks = now.Ticks,
                DiagnosticDeadlineUtcTicks = now.AddSeconds(25).Ticks,
                HardDeadlineUtcTicks = now.AddSeconds(30).Ticks
            };
            var authority = new DurableRelaunchAuthorityRecord
            {
                SessionId = session,
                State = DurableRelaunchPermitState.Approved,
                Generation = 12,
                PermitId = permitId,
                PermitNonce = permitNonce
            };
            Assert(exit.IsValidFor(session) && exit.PreservesApprovedPermit,
                "schema 2接管退出没有形成强类型Permit保留围栏。");
            Assert(!WatchdogHost.ShouldTerminateSidecarAfterApplicationExit(exit),
                "接管旧进程退出后仍会终止负责替换它的Sidecar。");
            Assert(WatchdogTakeoverPermitBindingPolicy.Matches(
                    authority, session, exit.RelaunchPermitGeneration,
                    exit.RelaunchPermitId, exit.RelaunchPermitNonceSha256),
                "精确Permit身份没有匹配已批准的耐久权限记录。");
            Assert(!WatchdogTakeoverPermitBindingPolicy.Matches(
                    authority, session, exit.RelaunchPermitGeneration + 1,
                    exit.RelaunchPermitId, exit.RelaunchPermitNonceSha256) &&
                   !WatchdogTakeoverPermitBindingPolicy.Matches(
                    authority, session, exit.RelaunchPermitGeneration,
                    Guid.NewGuid().ToString("N"), exit.RelaunchPermitNonceSha256) &&
                   !WatchdogTakeoverPermitBindingPolicy.Matches(
                    authority, session, exit.RelaunchPermitGeneration,
                    exit.RelaunchPermitId,
                    WatchdogTakeoverPermitBindingPolicy.HashNonce(
                        Guid.NewGuid().ToString("N"))),
                "代次、PermitId或nonce变化后仍能消费旧许可，存在ABA风险。");
            Assert(!WatchdogTakeoverPermitBindingPolicy.Matches(
                    authority, Guid.NewGuid().ToString("N"),
                    exit.RelaunchPermitGeneration, exit.RelaunchPermitId,
                    exit.RelaunchPermitNonceSha256),
                "跨会话的Permit仍可匹配接管退出围栏。");
            authority.State = DurableRelaunchPermitState.Failed;
            Assert(!WatchdogTakeoverPermitBindingPolicy.Matches(
                    authority, session, exit.RelaunchPermitGeneration,
                    exit.RelaunchPermitId, exit.RelaunchPermitNonceSha256),
                "已失败的Permit仍可授权旧进程退出或新进程拉起。");
            authority.State = DurableRelaunchPermitState.Approved;

            var closing = new WatchdogClosingTombstone
            {
                SessionId = session,
                SessionGeneration = 5,
                SessionLease = 9,
                StateVersion = 1,
                State = WatchdogClosingTombstoneState.Closing,
                TakeoverTransactionId = takeoverId,
                ExitDisposition = WatchdogExitDisposition.TakeoverReplacementExit,
                RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                RelaunchPermitGeneration = 12,
                RelaunchPermitId = permitId,
                RelaunchPermitNonceSha256 = permitNonceSha
            };
            Assert(closing.IsValidFor(session) && closing.PreservesApprovedPermit &&
                   WatchdogHost.EvaluateCloseFenceAction(
                       false, closing, false, false) == WatchdogCloseFenceAction.None,
                "schema 4关闭围栏错误撤销了已精确绑定的接管许可。");

            var legacy = CreateClosing(3, WatchdogClosingTombstoneState.Closing);
            legacy.CloseIntent = "WatchdogTakeover";
            Assert(!legacy.PreservesApprovedPermit &&
                   WatchdogHost.EvaluateCloseFenceAction(
                       false, legacy, false, false) ==
                       WatchdogCloseFenceAction.SuppressRelaunch,
                "缺少强类型字段的旧schema接管记录被不安全地自动续跑。");
            exit.ExitDisposition = WatchdogExitDisposition.OperatorExit;
            exit.RelaunchDisposition = WatchdogRelaunchDisposition.Forbidden;
            exit.RelaunchPermitGeneration = 0;
            exit.RelaunchPermitId = string.Empty;
            exit.RelaunchPermitNonceSha256 = string.Empty;
            exit.TakeoverTransactionId = string.Empty;
            Assert(WatchdogHost.ShouldTerminateSidecarAfterApplicationExit(exit),
                "人工退出后Sidecar被错误保留并可能重新拉起程序。");
        }

        private static void SafetyConfigSnapshotIsImmutableAndVerified()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "MTTFTest.ConfigSnapshotV42." + Guid.NewGuid().ToString("N"));
            var journal = Path.Combine(root, "journal");
            var appConfig = Path.Combine(root, "app", "Config");
            var projectConfig = Path.Combine(root, "project", "Config");
            Directory.CreateDirectory(journal);
            Directory.CreateDirectory(appConfig);
            Directory.CreateDirectory(projectConfig);
            var required = new[]
            {
                "AIConfig.xml", "AOConfig.xml", "DOConfig.xml",
                "PowerSupplyConfig.xml", "TestConfig.xml"
            };
            try
            {
                foreach (var file in required)
                    File.WriteAllText(Path.Combine(appConfig, file),
                        "<Config Source=\"Application\" Name=\"" + file + "\" />");
                File.WriteAllText(Path.Combine(projectConfig, "TestConfig.xml"),
                    "<Config Source=\"Project\" />");
                var handoffId = Guid.NewGuid().ToString("N");
                var created = WatchdogSafetyConfigSnapshotStore.Create(
                    journal, handoffId, appConfig, projectConfig, "2.13.0.42-test");
                Assert(created.Succeeded && created.Manifest.SchemaVersion == 1 &&
                       created.Manifest.Files.Length == required.Length &&
                       created.Manifest.Files.Any(file =>
                           file.RelativePath == "TestConfig.xml" &&
                           file.Role == "ProjectTestConfig") &&
                       File.ReadAllText(Path.Combine(
                           created.ConfigDirectory, "TestConfig.xml")).Contains("Project"),
                    "不可变配置快照没有包含完整硬件配置或项目TestConfig覆盖。");
                var verified = WatchdogSafetyConfigSnapshotStore.Validate(
                    journal, handoffId, created.ConfigDirectory,
                    created.ManifestPath, created.ManifestSha256);
                Assert(verified.Succeeded,
                    "未修改的配置快照未能通过schema 1清单验证。");
                var wrongHandoff = WatchdogSafetyConfigSnapshotStore.Validate(
                    journal, Guid.NewGuid().ToString("N"),
                    created.ConfigDirectory, created.ManifestPath,
                    created.ManifestSha256);
                Assert(!wrongHandoff.Succeeded &&
                       wrongHandoff.Error.Contains("HandoffPathMismatch"),
                    "配置快照路径可被跨Handoff复用。");

                File.AppendAllText(Path.Combine(created.ConfigDirectory, "AOConfig.xml"),
                    "<!--tampered-->");
                var tampered = WatchdogSafetyConfigSnapshotStore.Validate(
                    journal, handoffId, created.ConfigDirectory,
                    created.ManifestPath, created.ManifestSha256);
                Assert(!tampered.Succeeded &&
                       tampered.Error.Contains("FileHashMismatch"),
                    "配置内容被篡改后仍获准启动安全关闭Worker。");

                File.Delete(Path.Combine(appConfig, "AOConfig.xml"));
                var incomplete = WatchdogSafetyConfigSnapshotStore.Create(
                    journal, Guid.NewGuid().ToString("N"), appConfig,
                    projectConfig, "2.13.0.42-test");
                Assert(!incomplete.Succeeded &&
                       incomplete.Error.Contains("SnapshotIncomplete"),
                    "配置源缺失AOConfig.xml时仍创建了不完整安全快照。");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void ApplicationExitReceiptIsExactAndMonotonic()
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "MTTFTest.ApplicationExitV40." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var session = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            try
            {
                var receipt = new WatchdogApplicationExitReceipt
                {
                    SessionId = session,
                    SessionGeneration = 4,
                    SessionLease = 8,
                    ExitIntentId = Guid.NewGuid().ToString("N"),
                    Revision = 1,
                    State = WatchdogApplicationExitState.Requested,
                    MainProcessId = 1234,
                    MainProcessStartUtcTicks = now.AddMinutes(-1).Ticks,
                    RequestedUtcTicks = now.Ticks,
                    DiagnosticDeadlineUtcTicks = now.AddSeconds(25).Ticks,
                    HardDeadlineUtcTicks = now.AddSeconds(30).Ticks,
                    Reason = "UnitTest"
                };
                WatchdogApplicationExitReceiptStore.WriteThrough(directory, receipt);
                var wire = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(
                    new WatchdogMessage
                    {
                        Type = WatchdogMessageType.ApplicationExitRequested,
                        SessionId = session,
                        CorrelationId = receipt.ExitIntentId,
                        ApplicationExit = receipt
                    }));
                Assert(wire.ApplicationExit != null &&
                       wire.ApplicationExit.MainProcessId == 1234 &&
                       wire.ApplicationExit.MainProcessStartUtcTicks ==
                       receipt.MainProcessStartUtcTicks &&
                       wire.ApplicationExit.HardDeadlineUtcTicks ==
                       receipt.HardDeadlineUtcTicks,
                    "应用退出意图线格式丢失精确进程或截止时间");

                receipt.Revision = 2;
                receipt.State = WatchdogApplicationExitState.DiagnosticsCaptured;
                WatchdogApplicationExitReceiptStore.WriteThrough(directory, receipt);
                var regressed = new WatchdogApplicationExitReceipt
                {
                    SessionId = session,
                    SessionGeneration = 4,
                    SessionLease = 8,
                    ExitIntentId = receipt.ExitIntentId,
                    Revision = 3,
                    State = WatchdogApplicationExitState.Requested,
                    MainProcessId = receipt.MainProcessId,
                    MainProcessStartUtcTicks = receipt.MainProcessStartUtcTicks,
                    RequestedUtcTicks = receipt.RequestedUtcTicks,
                    DiagnosticDeadlineUtcTicks = receipt.DiagnosticDeadlineUtcTicks,
                    HardDeadlineUtcTicks = receipt.HardDeadlineUtcTicks
                };
                AssertThrows(
                    () => WatchdogApplicationExitReceiptStore.WriteThrough(directory, regressed),
                    "应用退出意图允许状态倒退");
                Assert(WatchdogLifecyclePolicy.IsTerminalMessage(
                        WatchdogMessageType.ApplicationExitRequested),
                    "应用退出意图未进入终态消息策略");
            }
            finally
            {
                DeleteFile(WatchdogJournalPaths.LocalApplicationExitPath(session));
                DeleteFile(WatchdogJournalPaths.LocalApplicationExitPath(session) + ".bak");
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static WatchdogClosingTombstone CreateClosing(
            int schemaVersion,
            WatchdogClosingTombstoneState state)
        {
            return new WatchdogClosingTombstone
            {
                SchemaVersion = schemaVersion,
                SessionId = Guid.NewGuid().ToString("N"),
                SessionGeneration = 1,
                SessionLease = 1,
                StateVersion = 1,
                State = state
            };
        }

        private static void DeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void AssertThrows(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(message);
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
