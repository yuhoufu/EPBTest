using System;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using Controller.Adaptive;
using Controller.Alarm;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class WarningOverlayRecoveryTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("预警圈SuccessWithWarning不能自清", WarningCycleCannotClearItself, ref passed);
            Run("下一可信Success圈允许恢复", SubsequentTrustedSuccessCanRecover, ref passed);
            Run("EPB12现场attempt8到attempt9回放恢复", FieldEpb12ReplayRecovers, ref passed);
            Run("非完整圈与非Success结果禁止恢复", IncompleteOrNonSuccessCannotRecover, ref passed);
            Run("旧RunEpoch禁止清除新运行预警", OldRunEpochCannotRecover, ref passed);
            Run("原子清除保留代码与审计身份", AtomicClearPreservesAuditIdentity, ref passed);
            Run("旧Revision不能清除新预警", StaleRevisionCannotClearNewWarning, ref passed);
            Run("A类恢复不能清除B类预警", WarningCodeMismatchCannotClear, ref passed);
            Run("一万次发布清除竞争中新预警始终胜出", TenThousandPublishClearRacesKeepNewestWarning, ref passed);
            Run("结构化overlay与异步字符串通知职责隔离", StructuredOverlayPortIsIndependentFromMessage, ref passed);
            Run("UI收到ActiveFalse立即恢复运行文本", UiPresentationImmediatelyReflectsClear, ref passed);
            Run("清除Revision可拒绝迟到的激活事件", ClearedRevisionRejectsLateUiEvent, ref passed);
            Run("Watchdog下一心跳同步ActiveFalse与新Revision", WatchdogProgressReflectsClear, ref passed);
            return passed;
        }

        private static void WarningCycleCannotClearItself()
        {
            var runId = Guid.NewGuid();
            var warning = ActiveWarning(runId, 7, 8, -43, "ForwardPeakOvershootWarning");
            Assert(!EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, runId, 7, 8, -43, Outcome(EpbCycleOutcomeKind.SuccessWithWarning)),
                "预警圈被错误允许清除。\n");
            Assert(!EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, runId, 7, 8, -43, Outcome(EpbCycleOutcomeKind.Success)),
                "相同Attempt的结果被错误允许清除。\n");
        }

        private static void SubsequentTrustedSuccessCanRecover()
        {
            var runId = Guid.NewGuid();
            var warning = ActiveWarning(runId, 9, 40, 100, "ForwardCurrentRiseStallWarning");
            Assert(EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, runId, 9, 41, 101, Outcome(EpbCycleOutcomeKind.Success)),
                "后续可信Success圈未被识别为恢复证据。\n");
        }

        private static void FieldEpb12ReplayRecovers()
        {
            var runId = Guid.Parse("5bc2c21a-0fe2-4e3c-9ad6-3eb3a4c8fdc1");
            var warning = ActiveWarning(runId, 1, 8, -43, "ForwardPeakOvershootWarning");
            Assert(!EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, runId, 1, 8, -43, Outcome(EpbCycleOutcomeKind.SuccessWithWarning)),
                "现场+0.907A预警圈不应清除。\n");
            Assert(EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, runId, 1, 9, -42, Outcome(EpbCycleOutcomeKind.Success)),
                "现场下一圈+0.460A Success应清除。\n");
        }

        private static void IncompleteOrNonSuccessCannotRecover()
        {
            var runId = Guid.NewGuid();
            var warning = ActiveWarning(runId, 3, 5, 5, "PeakEvidenceLagWarning");
            foreach (var kind in new[]
                     {
                         EpbCycleOutcomeKind.SuccessWithWarning,
                         EpbCycleOutcomeKind.SoftwareRecovery,
                         EpbCycleOutcomeKind.Canceled,
                         EpbCycleOutcomeKind.HardFault
                     })
            {
                Assert(!EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                        warning, runId, 3, 6, 6, Outcome(kind)),
                    $"{kind}被错误允许清除。\n");
            }

            var incomplete = Outcome(EpbCycleOutcomeKind.Success);
            incomplete.Stage = EpbCurrentStage.Hold;
            Assert(!EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, runId, 3, 6, 6, incomplete),
                "未完成释放的Success被错误允许清除。\n");
        }

        private static void OldRunEpochCannotRecover()
        {
            var runId = Guid.NewGuid();
            var warning = ActiveWarning(runId, 12, 10, 10, "ForwardPeakOvershootWarning");
            Assert(!EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, runId, 11, 11, 11, Outcome(EpbCycleOutcomeKind.Success)),
                "旧RunEpoch错误清除了新运行预警。\n");
            Assert(!EpbManager.IsTrustedWarningOverlayRecoveryCandidate(
                    warning, Guid.NewGuid(), 12, 11, 11, Outcome(EpbCycleOutcomeKind.Success)),
                "其他RunId错误清除了当前预警。\n");
        }

        private static void AtomicClearPreservesAuditIdentity()
        {
            var store = new ChannelWarningOverlayStore();
            var runId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var raised = store.Publish(new ChannelWarningOverlayChangedEvent
            {
                Channel = 12,
                Active = true,
                WarningCode = "ForwardPeakOvershootWarning",
                WarningText = "+0.907A",
                RunId = runId,
                RunEpoch = 4,
                CorrelationId = correlationId,
                RaisedAttemptId = 8,
                RaisedCycleNumber = -43,
                RaisedUtc = DateTime.UtcNow.AddSeconds(-15)
            });

            Assert(store.TryClearIfCurrent(
                    12, runId, 4, raised.Revision, raised.WarningCode,
                    9, -42, correlationId, "SubsequentTrustedCycleHealthy:Learning",
                    out var cleared),
                "精确身份的原子清除失败。\n");
            Assert(!cleared.Active && cleared.Revision == raised.Revision + 1,
                "清除事件Active/Revision错误。\n");
            Assert(cleared.WarningCode == raised.WarningCode &&
                   cleared.RaisedAttemptId == 8 && cleared.ClearedAttemptId == 9 &&
                   cleared.RaisedCycleNumber == -43 && cleared.ClearedCycleNumber == -42 &&
                   cleared.CorrelationId == correlationId &&
                   cleared.ClearReason == "SubsequentTrustedCycleHealthy:Learning",
                "清除事件没有保留完整审计身份。\n");
        }

        private static void StaleRevisionCannotClearNewWarning()
        {
            var store = new ChannelWarningOverlayStore();
            var runId = Guid.NewGuid();
            var first = store.Publish(ActiveWarning(runId, 2, 1, 1, "WarningA"));
            var second = store.Publish(ActiveWarning(runId, 2, 2, 2, "WarningB"));
            Assert(!store.TryClearIfCurrent(
                    12, runId, 2, first.Revision, first.WarningCode,
                    3, 3, Guid.NewGuid(), "stale", out _),
                "旧Revision清除了新预警。\n");
            var current = store.Get(12);
            Assert(current.Active && current.WarningCode == "WarningB" &&
                   current.Revision == second.Revision,
                "新预警没有保持激活。\n");
        }

        private static void WarningCodeMismatchCannotClear()
        {
            var store = new ChannelWarningOverlayStore();
            var runId = Guid.NewGuid();
            var current = store.Publish(ActiveWarning(runId, 6, 20, 20, "WarningB"));
            Assert(!store.TryClearIfCurrent(
                    12, runId, 6, current.Revision, "WarningA",
                    21, 21, Guid.NewGuid(), "wrong-category", out _),
                "A类恢复错误清除了B类预警。\n");
            Assert(store.Get(12).Active, "B类预警未保持激活。\n");
        }

        private static void TenThousandPublishClearRacesKeepNewestWarning()
        {
            var store = new ChannelWarningOverlayStore();
            var runId = Guid.NewGuid();
            const int iterations = 10000;
            for (var i = 1; i <= iterations; i++)
            {
                var oldWarning = store.Publish(ActiveWarning(runId, 15, i * 2L, i, "WarningA"));
                Parallel.Invoke(
                    () => store.TryClearIfCurrent(
                        12, runId, 15, oldWarning.Revision, oldWarning.WarningCode,
                        i * 2L + 1, i + 1, Guid.NewGuid(), "race", out _),
                    () => store.Publish(ActiveWarning(
                        runId, 15, i * 2L + 1, i + 1, "WarningB")));
                var current = store.Get(12);
                Assert(current.Active && current.WarningCode == "WarningB",
                    $"第{i}次竞争中旧清除覆盖了新预警。\n");
            }
        }

        private static void StructuredOverlayPortIsIndependentFromMessage()
        {
            var overlays = 0;
            var messages = 0;
            var overlayLifecycle = 0;
            var port = new EpbManagerAdaptiveLifecyclePort(
                warningRaised: (_, __) => Interlocked.Increment(ref messages),
                warningOverlayRaised: _ => Interlocked.Increment(ref overlays),
                observer: evt =>
                {
                    if (evt.Kind == EpbManagerAdaptiveLifecycleEventKind.WarningOverlay)
                        Interlocked.Increment(ref overlayLifecycle);
                });
            port.OnRunnerWarningOverlayRaised(new AdaptiveWarningEvent
            {
                Channel = 12,
                Code = AdaptiveWarningCode.ForwardPeakOvershootWarning,
                Reason = "structured"
            });
            Assert(overlays == 1 && messages == 0 && overlayLifecycle == 1,
                "结构化overlay错误依赖字符串通知。\n");
            port.OnRunnerWarningRaised(12, "compatibility-message");
            Assert(overlays == 1 && messages == 1,
                "兼容字符串通知错误重复发布overlay。\n");
        }

        private static void UiPresentationImmediatelyReflectsClear()
        {
            var state = new ChannelRuntimeStateChangedEvent
            {
                Channel = 12,
                State = ChannelRuntimeState.Running,
                RunEpoch = 31
            };
            var active = new ChannelWarningOverlayChangedEvent
            {
                Channel = 12,
                Active = true,
                RunEpoch = 31
            };
            Assert(FrmEpbMainMonitor.IsWarningOverlayActiveForState(state, active),
                "同代Active预警未显示。\n");
            Assert(FrmEpbMainMonitor.GetRuntimeStateDisplayText(state.State, true) == "运行 · 预警",
                "激活文本错误。\n");
            active.Active = false;
            Assert(!FrmEpbMainMonitor.IsWarningOverlayActiveForState(state, active) &&
                   FrmEpbMainMonitor.GetRuntimeStateDisplayText(state.State, false) == "运行",
                "Active=false后UI未立即恢复运行文本。\n");
            active.Active = true;
            active.RunEpoch = 30;
            Assert(!FrmEpbMainMonitor.IsWarningOverlayActiveForState(state, active),
                "旧代预警污染了新运行UI。\n");
        }

        private static void ClearedRevisionRejectsLateUiEvent()
        {
            var active = new ChannelWarningOverlayChangedEvent
            {
                Channel = 12,
                Active = true,
                Revision = 10,
                TimestampUtc = DateTime.UtcNow
            };
            var cleared = active.Clone();
            cleared.Active = false;
            cleared.Revision = 11;
            Assert(cleared.IsNewerThan(active), "清除事件不是更高Revision。\n");
            Assert(!active.IsNewerThan(cleared), "迟到激活事件可覆盖清除事件。\n");
        }

        private static void WatchdogProgressReflectsClear()
        {
            var progress = new WatchdogChannelProgressSnapshot { Channel = 12 };
            var cleared = new ChannelWarningOverlayChangedEvent
            {
                Channel = 12,
                Active = false,
                WarningCode = "ForwardPeakOvershootWarning",
                Revision = 22
            };
            var mapped = EpbManager.ApplyWarningOverlayToWatchdogProgress(progress, cleared);
            Assert(!mapped.WarningActive &&
                   mapped.WarningRevision == 22 &&
                   mapped.WarningCode == "ForwardPeakOvershootWarning",
                "Watchdog心跳没有同步清除状态或新Revision。\n");
        }

        private static ChannelWarningOverlayChangedEvent ActiveWarning(
            Guid runId,
            long runEpoch,
            long attemptId,
            int cycleNumber,
            string code)
        {
            return new ChannelWarningOverlayChangedEvent
            {
                Channel = 12,
                Active = true,
                WarningCode = code,
                WarningText = code,
                TimestampUtc = DateTime.UtcNow,
                RaisedUtc = DateTime.UtcNow,
                CorrelationId = Guid.NewGuid(),
                RunId = runId,
                RunEpoch = runEpoch,
                RaisedAttemptId = attemptId,
                RaisedCycleNumber = cycleNumber
            };
        }

        private static EpbCycleOutcome Outcome(EpbCycleOutcomeKind kind)
        {
            return new EpbCycleOutcome
            {
                Kind = kind,
                Stage = EpbCurrentStage.Released
            };
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("PASS WarningOverlayRecovery: " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
