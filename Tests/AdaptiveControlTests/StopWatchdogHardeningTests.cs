using System;
using Controller;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class StopWatchdogHardeningTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("Watchdog v2与v1心跳向后兼容", ProtocolV2IsAdditive, ref passed);
            Run("ManualStopIntent保留Kill权限", ManualStopIntentKeepsAuthority, ref passed);
            Run("启动失败和恢复失败不得伪装会话终止", RetryableFailuresAreNotTerminal, ref passed);
            Run("Stop阶段五秒无进展必须接管", StopStageStallTriggersTakeover, ref passed);
            Run("无RunId但逻辑对象残留必须接管", LogicalResidueTriggersTakeover, ref passed);
            Run("恢复对象与RecoveryActive矛盾必须接管", RecoveryEvidenceMismatchTriggersTakeover, ref passed);
            Run("正式圈有物理动作但90秒无计数进展必须接管", FormalProgressStallTriggersTakeover, ref passed);
            Run("逻辑静止必须包含无上电通道", LogicalQuiescenceIncludesEnergization, ref passed);
            Run("停止超时结果永久禁止同进程重启", TimedOutStopCannotRestartInProcess, ref passed);
            return passed;
        }

        private static void ProtocolV2IsAdditive()
        {
            var message = new WatchdogMessage
            {
                Type = WatchdogMessageType.Heartbeat,
                SessionId = "v2",
                Heartbeat = new WatchdogHeartbeat
                {
                    StopAllActive = true,
                    StopStage = "ClearRecoveryOwners",
                    StopProgressVersion = 7,
                    TimerCount = 10,
                    RunnerCount = 10
                }
            };
            var roundTrip = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(message));
            Assert(roundTrip.ProtocolVersion == 2 && roundTrip.Heartbeat.StopAllActive &&
                   roundTrip.Heartbeat.StopProgressVersion == 7 &&
                   roundTrip.Heartbeat.TimerCount == 10,
                "v2停止字段未能序列化往返");

            var v1 = WatchdogProtocol.Deserialize(
                "{\"ProtocolVersion\":1,\"Type\":\"Heartbeat\",\"SessionId\":\"v1\"," +
                "\"Heartbeat\":{\"Sequence\":3,\"RunActive\":false}}");
            Assert(v1.ProtocolVersion == 1 && v1.Heartbeat.Sequence == 3 &&
                   !v1.Heartbeat.StopAllActive,
                "v1心跳不能由v2协议模型兼容反序列化");
        }

        private static void ManualStopIntentKeepsAuthority()
        {
            Assert(!SessionRevocationPolicy.IsRevoked(false, true, false),
                "ManualStopIntent被错误当作Session撤权");
            Assert(WatchdogProcessIdentityPolicy.CanKillOldProcess(false, true, true),
                "人工停止15秒超时后不能终止身份匹配的旧进程");
        }

        private static void RetryableFailuresAreNotTerminal()
        {
            Assert(WatchdogLifecyclePolicy.IsRetryableFailure(WatchdogMessageType.BatchStartFailed) &&
                   WatchdogLifecyclePolicy.IsRetryableFailure(WatchdogMessageType.RecoveryAttemptFailed),
                "可重试失败未进入显式重试协议");
            Assert(!WatchdogLifecyclePolicy.IsTerminalMessage(WatchdogMessageType.BatchStartFailed) &&
                   !WatchdogLifecyclePolicy.IsTerminalMessage(WatchdogMessageType.RecoveryAttemptFailed) &&
                   WatchdogLifecyclePolicy.IsTerminalMessage(WatchdogMessageType.ApplicationClosing),
                "可重试失败被错误归类为ApplicationClosing/RunStopped终态");
        }

        private static void StopStageStallTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, true, false, true, 1, false, false, false, 5, false,
                    stopAllActive: true),
                "Stop阶段五秒无进展未触发接管");
        }

        private static void LogicalResidueTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, false, false, false, 5, false,
                    logicalResidue: true),
                "RunActive=false且Timer/Runner/CTS残留五秒未触发接管");
        }

        private static void RecoveryEvidenceMismatchTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, false, false, false, 0, false,
                    inconsistentRecoveryEvidence: true),
                "恢复对象存在但RecoveryActive=false未触发接管");
        }

        private static void FormalProgressStallTriggersTakeover()
        {
            Assert(WatchdogTakeoverPolicy.SelectFormalProgressTimeoutSeconds(15000) == 90 &&
                   WatchdogTakeoverPolicy.SelectFormalProgressTimeoutSeconds(60000) == 270,
                "正式进展超时未按四个配置周期并保留90秒下限计算");
            Assert(WatchdogTakeoverPolicy.ShouldTakeover(
                    false, false, false, true, 1, false, false, false, 90, true,
                    formalProgressStalled: true),
                "正式圈计数停滞且仍有物理动作时未触发接管");
        }

        private static void LogicalQuiescenceIncludesEnergization()
        {
            var snapshot = new LogicalQuiescenceSnapshot { EnergizedChannelCount = 1 };
            Assert(!snapshot.IsQuiescent, "仍有上电通道时逻辑静止被误判成立");
            snapshot.EnergizedChannelCount = 0;
            Assert(snapshot.IsQuiescent, "运行资源全部清空后逻辑静止未成立");
        }

        private static void TimedOutStopCannotRestartInProcess()
        {
            var result = new StopSafetyResult
            {
                Outcome = StopSafetyOutcome.SafeButRestartRequired,
                TimedOut = true,
                RequiresProcessRestart = true,
                MotorOffCommandSucceeded = true,
                PowerOffConfirmed = true,
                PressureSafeConfirmed = true,
                PersistenceBoundaryConfirmed = true,
                LogicalQuiescenceConfirmed = true
            };
            Assert(!result.CanRestartInProcess && result.CanReleaseAcquisition,
                "停止超时结果仍允许同进程开始，或丢失已确认物理安全证据");
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
