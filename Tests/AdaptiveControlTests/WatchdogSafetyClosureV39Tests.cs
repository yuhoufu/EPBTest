using System;
using System.IO;
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
                   roundTrip.SafetyHandoff.CallbacksIsolated,
                "v4安全交接DTO未保持精确身份与进程隔离证明");
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
