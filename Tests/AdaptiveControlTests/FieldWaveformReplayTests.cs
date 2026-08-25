using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using Controller.Adaptive;
using Controller.Alarm;
using IO.NI;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Deterministic replay of the two field records used by the V20 incident
    /// review. The full CSVs are copied to the test output and their byte hash
    /// is checked before replay so a locally substituted waveform cannot make
    /// the regression appear green.
    /// </summary>
    internal static class FieldWaveformReplayTests
    {
        private const string V20File = "field_v20_epb10_cycle_-002094.csv";
        private const string V20Sha256 =
            "6EB22261187CE1B771C1B4C5FE0FD5300F5B1990E8906D3DA9F2B8DAC75644D8";
        private const int V20Length = 201146;
        private const string V15File = "field_v15_epb10_cycle_084529.csv";
        private const string V15Sha256 =
            "B08CFB7D8840A51B2A3C2C012C14DF9A5C28730E94DC82D048F6DADB10E82C90";
        private const int V15Length = 1229777;

        internal static int RunAll()
        {
            Assert(
                EpbProgramSafetySettings.SafetyPolicyVersion == "2026.08.22.1",
                "P0安全策略版本未锁定为2026.08.22.1");
            ReplayV20RapidRiseIsDiagnosticOnly();
            ReplayV15DoesNotEmitRapidRiseDiagnostic();
            ReplayFullRateEvidenceChainAndIdentityGate();
            RiseCandidateContinuityAndGenerationAreSticky();
            QualifiedRiseDropStillTripsOnlyAfterConfirmedDrop();
            ProvisionalEvidenceExpiresWhenSamplingStalls();
            ReplayV15CompletesNormalLoadRisePath();
            ProductionRunnerDiagnosticOverlayIsRunScoped();
            DiagnosticOverlayAggregatesByRunEpochAndCode();
            DiagnosticOverlayFlushDoesNotHoldLockOrDuplicateSummary();
            return 10;
        }

        private static void ReplayV20RapidRiseIsDiagnosticOnly()
        {
            var path = FixturePath(V20File);
            AssertFixture(path, V20Length, V20Sha256);
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);

            var diagnosticCount = 0;
            var diagnosticStage = EpbCurrentStage.Idle;
            var diagnosticMutatedState = false;
            string fieldHardFaultReason = null;
            var rapidStateTransition = false;
            foreach (var sample in ReadSamples(path))
            {
                var decision = machine.OnSample(sample.Tick, sample.CurrentA);
                if (decision.HardFault && fieldHardFaultReason == null)
                    fieldHardFaultReason = decision.Reason;
                if (decision.StateChanged &&
                    decision.Reason?.IndexOf(
                        "RapidLoadRiseWithoutObservedEmpty",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    rapidStateTransition = true;
            }

            // 该封存是 2kHz 完整证据流；它应当验证“没有把历史模型冒充本圈
            // LoadRise 证据”，而不是强行制造 10ms 控制回调的诊断条件。
            Assert(!rapidStateTransition,
                "V20完整波形仍由快速负载提示迁移了LoadRise。");
            Assert(fieldHardFaultReason == null ||
                   fieldHardFaultReason.IndexOf(
                       "AbnormalLoadRiseDrop",
                       StringComparison.OrdinalIgnoreCase) < 0,
                "V20完整波形由无本圈证据的负载回落规则触发硬故障：" + fieldHardFaultReason);

            // The same field-shaped fast head is the deterministic P0-2 gate:
            // it may emit one diagnostic, but no state/lifecycle mutation.
            var rapidMachine = NewMachine();
            rapidMachine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);
            var rapidDecisions = new[]
            {
                rapidMachine.OnSample(Tick(46), 0.054),
                rapidMachine.OnSample(Tick(94), 1.719),
                rapidMachine.OnSample(Tick(109), 2.318)
            };
            foreach (var decision in rapidDecisions)
            {
                if (!decision.DiagnosticWarning) continue;
                diagnosticCount++;
                diagnosticStage = decision.Stage;
                diagnosticMutatedState |= decision.SoftWarning ||
                                          decision.HardFault ||
                                          decision.ClampReached ||
                                          decision.ReleaseCompleted;
                Assert(
                    decision.DiagnosticCode == "RapidLoadRiseWithoutObservedEmpty" &&
                    !decision.LoadRiseEvidenceQualified,
                    "V20快速负载诊断缺少证据门禁标志。");
                Assert(
                    decision.DiagnosticEvent != null &&
                    decision.DiagnosticEvent.Message.IndexOf("StateUnchanged=true", StringComparison.Ordinal) >= 0,
                    "V20诊断未记录状态不变证据。");
            }
            Assert(diagnosticCount == 1,
                "V20快速负载诊断未按一次/圈限频：" + diagnosticCount);
            Assert(diagnosticStage == EpbCurrentStage.EmptyTravel,
                "V20快速负载诊断错误改变为LoadRise：" + diagnosticStage);
            Assert(!diagnosticMutatedState,
                "V20 DiagnosticOnly 修改了状态机生命周期或保护动作。");
        }

        private static void ReplayV15DoesNotEmitRapidRiseDiagnostic()
        {
            var path = FixturePath(V15File);
            AssertFixture(path, V15Length, V15Sha256);
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 9000, 15, 0, 3);

            var diagnosticCount = 0;
            var diagnosticTick = 0L;
            var diagnosticCurrent = 0.0;
            foreach (var sample in ReadSamples(path))
            {
                var decision = machine.OnSample(sample.Tick, sample.CurrentA);
                if (decision.DiagnosticWarning &&
                    string.Equals(
                        decision.DiagnosticCode,
                        "RapidLoadRiseWithoutObservedEmpty",
                        StringComparison.Ordinal))
                {
                    diagnosticCount++;
                    diagnosticTick = sample.Tick;
                    diagnosticCurrent = sample.CurrentA;
                }
            }

            // The V15 control trace forms a normal per-cycle empty window before
            // the load rise; it is the negative control for the V20 regression.
            Assert(diagnosticCount == 0,
                $"V15对照波形错误产生无空载基线快速负载诊断：{diagnosticCount} tick={diagnosticTick} I={diagnosticCurrent:F3}");
        }

        private static void ReplayFullRateEvidenceChainAndIdentityGate()
        {
            var synthetic = BuildSyntheticValleyThenRise();
            var token = new PeakCaptureToken
            {
                CaptureId = Guid.NewGuid(),
                TestRunId = Guid.NewGuid(),
                RunEpoch = 11,
                Channel = 10,
                CycleNumber = 2094
            };
            var syntheticEvidence = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidence(
                token,
                synthetic.Currents,
                synthetic.Timestamps,
                generation: 17,
                startAcceptedSequence: 100,
                startProcessedSequence: 80);
            Assert(
                syntheticEvidence.InrushPeakA >= 2.0 &&
                syntheticEvidence.ValleyWindowQualified &&
                syntheticEvidence.ValleyWindowMs >= 100 &&
                syntheticEvidence.ValleyEquivalentSampleCount >= 8,
                "固定容量全速率证据未形成100ms/8桶稳定谷值。");
            Assert(
                syntheticEvidence.StartAcceptedSequence == 100 &&
                syntheticEvidence.StartProcessedSequence == 80 &&
                syntheticEvidence.ProcessedSequence > syntheticEvidence.StartProcessedSequence,
                "accepted/processed起点没有保持独立，或processed不是实际处理水印。");
            Assert(
                syntheticEvidence.FullRateValleyThenRise &&
                EpbAdaptiveCurrentStateMachine.HasQualifiedLoadRiseEvidence(syntheticEvidence),
                "合格FullRateValleyThenRise证据未通过身份/新鲜度门禁。");

            var machine = NewMachine();
            machine.ArmForward(Tick(0), 0, 9000, 15, 0, 3);
            EpbAdaptiveDecision last = null;
            for (var i = 0; i < synthetic.Currents.Length; i++)
            {
                last = machine.OnSampleReusable(
                    synthetic.TickValues[i],
                    synthetic.Currents[i],
                    syntheticEvidence.InrushPeakA,
                    syntheticEvidence,
                    new EpbAdaptiveDecision());
                if (last.Stage == EpbCurrentStage.LoadRise) break;
            }
            Assert(
                last != null && last.Stage == EpbCurrentStage.LoadRise &&
                last.LoadRiseEvidenceQualified,
                "合格全速率谷值后上升证据未推进自适应状态机LoadRise。");

            var stale = syntheticEvidence;
            stale.IsFresh = false;
            stale.Fresh = false;
            stale.FreshnessValid = false;
            stale.IsQualified = false;
            stale.LoadRiseEvidenceQualified = false;
            Assert(
                !EpbAdaptiveCurrentStateMachine.HasQualifiedLoadRiseEvidence(stale),
                "过期全速率证据错误地通过LoadRise门禁。");

            var wrongGeneration = syntheticEvidence;
            wrongGeneration.IsGenerationMatched = false;
            wrongGeneration.GenerationMatched = false;
            wrongGeneration.IsQualified = false;
            wrongGeneration.LoadRiseEvidenceQualified = false;
            Assert(
                !EpbAdaptiveCurrentStateMachine.HasQualifiedLoadRiseEvidence(wrongGeneration),
                "代次不匹配的全速率证据错误地通过LoadRise门禁。");

            // 两份真实现场封存也必须走同一个固定容量 tracker；这里检查完整
            // 身份/峰值/序列链已经被回放，而不是只调用快速状态机。
            AssertRealFixtureReplay(V20File, V20Length, V20Sha256, -2094);
            AssertRealFixtureReplay(V15File, V15Length, V15Sha256, 84529);
        }

        private static void RiseCandidateContinuityAndGenerationAreSticky()
        {
            var shortWave = BuildCandidateWaveform(8, fallbackAfterRise: false);
            var shortToken = NewEvidenceToken(2100);
            var shortEvidence = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidence(
                shortToken,
                shortWave.Currents,
                shortWave.Timestamps,
                generation: 21,
                startAcceptedSequence: 1000);
            Assert(
                shortEvidence.RiseContinuousSampleCount == 8 &&
                shortEvidence.RiseContinuousDurationMs < 4.0 &&
                !shortEvidence.Qualified,
                "7/8点以下或4ms边界前的上升候选错误地通过。");

            var boundaryWave = BuildCandidateWaveform(9, fallbackAfterRise: false);
            var boundaryToken = NewEvidenceToken(2101);
            var boundaryEvidence = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidence(
                boundaryToken,
                boundaryWave.Currents,
                boundaryWave.Timestamps,
                generation: 21,
                startAcceptedSequence: 1000);
            Assert(
                boundaryEvidence.RiseContinuousSampleCount >= 9 &&
                boundaryEvidence.RiseContinuousDurationMs >= 4.0 &&
                boundaryEvidence.RiseContinuityQualified &&
                boundaryEvidence.LinearRiseSlopeAperMs >= boundaryEvidence.MinimumRequiredSlopeAperMs &&
                boundaryEvidence.RobustRiseSlopeAperMs >= boundaryEvidence.MinimumRequiredSlopeAperMs &&
                boundaryEvidence.Qualified,
                "满足连续样本/最小时长/鲁棒线性斜率边界的上升未通过。");

            foreach (var spikeCount in new[] { 1, 2 })
            {
                var spikeWave = BuildCandidateWaveform(spikeCount, fallbackAfterRise: true);
                var spikeEvidence = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidence(
                    NewEvidenceToken(2110 + spikeCount),
                    spikeWave.Currents,
                    spikeWave.Timestamps,
                    generation: 21,
                    startAcceptedSequence: 1000);
                Assert(
                    spikeEvidence.RiseContinuousSampleCount == 0 &&
                    !spikeEvidence.FullRateValleyThenRise &&
                    !spikeEvidence.Qualified,
                    $"单/双点尖峰回落未撤销候选：count={spikeCount}。");
            }

            var generations = Enumerable.Repeat(21L, boundaryWave.Currents.Length).ToArray();
            generations[Math.Max(0, generations.Length - 12)] = 22;
            var sticky = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidenceWithGenerations(
                NewEvidenceToken(2120),
                boundaryWave.Currents,
                boundaryWave.Timestamps,
                generation: 21,
                generations,
                startAcceptedSequence: 1000);
            Assert(
                !sticky.GenerationMatched &&
                !sticky.IsGenerationMatched &&
                !sticky.Qualified,
                "DAQ generation错代后恢复到旧代次仍被粘性放行。");

            var expected = boundaryToken;
            var mismatches = new[]
            {
                Mutate(boundaryEvidence, e => { e.CaptureId = Guid.NewGuid(); return e; }),
                Mutate(boundaryEvidence, e => { e.RunId = Guid.NewGuid(); return e; }),
                Mutate(boundaryEvidence, e => { e.TestRunId = Guid.NewGuid(); return e; }),
                Mutate(boundaryEvidence, e => { e.RunEpoch++; return e; }),
                Mutate(boundaryEvidence, e => { e.Channel++; return e; }),
                Mutate(boundaryEvidence, e => { e.CycleNumber++; return e; }),
                Mutate(boundaryEvidence, e => { e.DaqGeneration++; return e; }),
                Mutate(boundaryEvidence, e => { e.Generation++; return e; }),
                Mutate(boundaryEvidence, e => { e.StartAcceptedSequence++; return e; }),
                Mutate(boundaryEvidence, e => { e.StartProcessedSequence++; return e; })
            };
            foreach (var mismatch in mismatches)
                Assert(
                    !EpbAdaptiveCurrentStateMachine.HasQualifiedLoadRiseEvidence(
                        mismatch,
                        expected),
                    "身份字段错配错误地通过严格证据门禁。");

            var expired = boundaryEvidence;
            expired.EvidenceAgeMs = expired.MaximumEvidenceAgeMs + 1;
            expired.EvidenceAgeValid = false;
            Assert(
                !EpbAdaptiveCurrentStateMachine.HasQualifiedLoadRiseEvidence(expired),
                "超过最大证据年龄的上升仍被放行。");
        }

        private static void ProvisionalEvidenceExpiresWhenSamplingStalls()
        {
            var wave = BuildSyntheticValleyThenRise();
            var token = NewEvidenceToken(2300);
            var evidence = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidence(
                token,
                wave.Currents,
                wave.Timestamps,
                generation: 31,
                startAcceptedSequence: 900,
                startProcessedSequence: 700);
            Assert(evidence.Qualified && evidence.IsProvisional,
                "采样停滞回放的前置证据未形成合格 provisional 证据。");

            // Re-run through the production tracker and take the snapshot only
            // after its monotonic freshness window has elapsed.  Generation is
            // unchanged; the rejection must therefore come from the last
            // processed sample age, not from a cutoff-only calculation.
            var stalled = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidence(
                token,
                wave.Currents,
                wave.Timestamps,
                generation: 31,
                startAcceptedSequence: 900,
                startProcessedSequence: 700,
                idleAfterReplayMs: 300);
            Assert(stalled.IsProvisional &&
                   !stalled.FreshnessValid &&
                   !stalled.Qualified &&
                   !stalled.LoadRiseEvidenceQualified &&
                   stalled.EvidenceAgeMs > stalled.MaximumEvidenceAgeMs,
                "generation不变但采样停滞超过阈值时，生产tracker仍放行provisional证据。");
            Assert(!EpbAdaptiveCurrentStateMachine.HasQualifiedLoadRiseEvidence(stalled),
                "过期provisional证据错误通过状态机LoadRise门禁。");

            var expected = token;
            var cutoffMismatch = evidence;
            cutoffMismatch.CutoffSequence = cutoffMismatch.StartAcceptedSequence + 1;
            Assert(!EpbAdaptiveCurrentStateMachine.HasQualifiedLoadRiseEvidence(
                    cutoffMismatch,
                    expected),
                "cutoff边界被篡改后仍通过严格身份/新鲜度门禁。");
        }

        private static void ReplayV15CompletesNormalLoadRisePath()
        {
            var path = FixturePath(V15File);
            AssertFixture(path, V15Length, V15Sha256);
            var machine = NewMachine();
            // The fixture is a complete physical cycle. Its forward
            // energizing segment reaches the measured 14.34A plateau, so use
            // that physical target and let the state machine observe the
            // normal LoadRise/Clamp path without a false startup open-circuit
            // timeout.
            machine.ArmForward(Tick(0), 1000, 9000, 14, 0, 3);
            var sawLoadRise = false;
            var sawClampReached = false;
            var hardFault = string.Empty;
            var transitions = new System.Text.StringBuilder();
            var previousStage = EpbCurrentStage.Idle;
            long hardFaultTick = 0;
            double hardFaultCurrent = 0;
            foreach (var sample in ReadSamples(path))
            {
                var decision = machine.OnSample(sample.Tick, sample.CurrentA);
                if (decision.Stage != previousStage)
                {
                    transitions.Append($" {sample.Tick}:{sample.CurrentA:F3}->{decision.Stage}/{decision.Reason};");
                    previousStage = decision.Stage;
                }
                sawLoadRise |= decision.Stage == EpbCurrentStage.LoadRise;
                sawClampReached |= decision.ClampReached ||
                                   decision.Stage == EpbCurrentStage.ClampReached;
                if (decision.HardFault && string.IsNullOrEmpty(hardFault))
                {
                    hardFault = decision.Reason;
                    hardFaultTick = sample.Tick;
                    hardFaultCurrent = sample.CurrentA;
                }
                // Clamp is the forward-stage terminal boundary; the remaining
                // fixture samples belong to the reverse/hold portion and are
                // intentionally not fed to the forward state machine.
                if (decision.ClampReached)
                    break;
            }
            // The replay is a complete physical cycle.  A LoadRise decision
            // by itself is not enough evidence that the production chain
            // reached its forward terminal boundary: the fixture must pass
            // the real ClampReached decision before the replay is accepted.
            Assert(sawClampReached,
                "V15完整正常圈未推进到生产ClampReached终态。");
            Assert(string.IsNullOrEmpty(hardFault),
                $"V15完整正常圈出现硬故障：{hardFault} tick={hardFaultTick} I={hardFaultCurrent:F3} stage={machine.Stage} transitions={transitions}");
        }

        private static void ProductionRunnerDiagnosticOverlayIsRunScoped()
        {
            using var doController = new DoController(new DoConfig());
            using var aoController = new AoController(new AoConfig());
            var hydraulic = new HydraulicController(
                doController,
                new TestConfig(),
                _ => 0.0,
                aoController);
            var profile = new EpbAdaptiveProfile
            {
                Channel = 10,
                ForwardEmptyCurrentA = 0.6135815,
                ForwardEmptyMadA = 0.0251518,
                ForwardClampMedianMs = 3000,
                ForwardClampMadMs = 100,
                ValidSampleCount = 5
            };
            var runner = new EpbCycleRunner(
                10,
                2,
                _ => 0.0,
                doController,
                null,
                hydraulic,
                15.0,
                1000,
                peakIgnoreMs: 80,
                epbControlMode: EpbControlMode.AdaptiveCurrent,
                adaptiveShadowMode: false,
                adaptiveProfile: profile,
                programSafetySettings: new EpbProgramSafetySettings());
            var begin = typeof(EpbCycleRunner).GetMethod(
                "BeginAdaptiveForwardMonitoring",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(begin != null, "未找到生产Runner自适应正向监控入口。");

            var warningCount = 0;
            var alarmCount = 0;
            var recoverableFaultCount = 0;
            var warningEvidenceCount = 0;
            var decisionCount = 0;
            var detailCount = 0;
            var summaryCount = 0;
            var summaryTotalCount = 0;
            var channelPausedCount = 0;
            var runtimeStateTransitionCount = 0;
            var timerPauseCount = 0;
            var safeIdleCount = 0;
            var stopCount = 0;
            var systemFaultCount = 0;
            // This is the exact binding component used by EpbManager.  Keep
            // callbacks as named delegates so the same production detach path
            // can be exercised after the 10k-cycle run.
            Action<int, string> alarmHandler = (_, __) =>
                Interlocked.Increment(ref alarmCount);
            Action<int, string> warningHandler = (_, __) =>
                Interlocked.Increment(ref warningCount);
            Action<AdaptiveWarningEvent> warningEvidenceHandler = _ =>
                Interlocked.Increment(ref warningEvidenceCount);
            Action<int, string> recoverableHandler = (_, __) =>
                Interlocked.Increment(ref recoverableFaultCount);
            Action<AdaptiveDecisionTraceSample> decisionHandler = _ =>
                Interlocked.Increment(ref decisionCount);
            Action<AdaptiveDiagnosticOverlaySnapshot> detailHandler = snapshot =>
            {
                Assert(snapshot.Key.DiagnosticCode == "RapidLoadRiseWithoutObservedEmpty" &&
                       snapshot.Count == 1,
                    "Runner首次DiagnosticOnly明细不是稳定代码/一次事件。");
                Interlocked.Increment(ref detailCount);
            };
            Action<AdaptiveDiagnosticOverlaySnapshot> summaryHandler = snapshot =>
            {
                Assert(snapshot.Key.DiagnosticCode == "RapidLoadRiseWithoutObservedEmpty" &&
                       snapshot.Count == 10000,
                    "Runner运行级DiagnosticOnly摘要未合并10000圈。");
                summaryTotalCount = snapshot.Count;
                Interlocked.Increment(ref summaryCount);
            };
            // This recording port is attached through the same production
            // AdaptiveRunnerEventBinding overload used by EpbManager.  The
            // lifecycle counters are fed only by actual lifecycle publications
            // (not by a post-run zero assignment).
            var lifecyclePort = new EpbManagerAdaptiveLifecyclePort(
                alarmRaised: alarmHandler,
                warningRaised: warningHandler,
                warningEvidenceRaised: warningEvidenceHandler,
                recoverableFaultRaised: recoverableHandler,
                decisionObserved: decisionHandler,
                diagnosticObserved: detailHandler,
                diagnosticSummaryObserved: summaryHandler,
                observer: lifecycle =>
                {
                    switch (lifecycle.Kind)
                    {
                        case EpbManagerAdaptiveLifecycleEventKind.ChannelPaused:
                            Interlocked.Increment(ref channelPausedCount);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.RuntimeStateChanged:
                            Interlocked.Increment(ref runtimeStateTransitionCount);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.TimerPause:
                            Interlocked.Increment(ref timerPauseCount);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.SafeIdle:
                            Interlocked.Increment(ref safeIdleCount);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.Stop:
                            Interlocked.Increment(ref stopCount);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.SystemFault:
                            Interlocked.Increment(ref systemFaultCount);
                            break;
                    }
                });
            AdaptiveRunnerEventBinding.Attach(
                runner,
                lifecyclePort);

            var tickStep = Math.Max(1L, Stopwatch.Frequency / 1000);
            var runStopwatch = Stopwatch.StartNew();
            for (var cycle = 0; cycle < 10000; cycle++)
            {
                // ResetTransientRunState is the production rejoin operation;
                // it must not clear the run-scoped diagnostic aggregation.
                if (cycle == 5000)
                    runner.ResetTransientRunState();
                begin.Invoke(runner, new object[] { 1000 });
                var start = Stopwatch.GetTimestamp();
                runner.FeedCurrentSample(
                    10,
                    start + 85 * tickStep,
                    0.054,
                    DateTime.UtcNow);
                runner.FeedCurrentSample(
                    10,
                    start + 94 * tickStep,
                    1.719,
                    DateTime.UtcNow.AddMilliseconds(9));
                runner.FeedCurrentSample(
                    10,
                    start + 109 * tickStep,
                    2.318,
                    DateTime.UtcNow.AddMilliseconds(24));
            }

            Assert(detailCount == 1,
                "生产Runner DiagnosticOnly明细未按整个Run限频为一次：" + detailCount);
            Assert(warningCount == 0 && alarmCount == 0 &&
                   recoverableFaultCount == 0 && warningEvidenceCount == 0,
                $"DiagnosticOnly经Manager绑定链进入控制事件：Warning={warningCount} Alarm={alarmCount} " +
                $"Recoverable={recoverableFaultCount} Evidence={warningEvidenceCount}");
            Assert(runner.AdaptiveDiagnosticOverlayCount == 1,
                "生产Runner运行级诊断聚合键被圈号/重入拆散。");
            Assert(channelPausedCount == 0 &&
                   runtimeStateTransitionCount == 0 &&
                   timerPauseCount == 0 &&
                   safeIdleCount == 0 &&
                   stopCount == 0 &&
                   systemFaultCount == 0,
                "正常10000圈DiagnosticOnly产生了Manager生命周期异常转移。");
            var captured = runner.CaptureAdaptiveProfile();
            Assert(captured.ConsecutiveDeviationCount == 0 &&
                   captured.ConsecutiveForwardOvershootCount == 0 &&
                   captured.ConsecutiveForwardStallCount == 0,
                "10000圈DiagnosticOnly改变了模型故障连续数。");

            var overlayEntriesBeforeFlush = runner.AdaptiveDiagnosticOverlayCount;
            AdaptiveRunnerEventBinding.Detach(
                runner,
                lifecyclePort,
                flushDiagnosticOverlay: true);
            Assert(summaryCount == 1 && summaryTotalCount == 10000 &&
                   runner.AdaptiveDiagnosticOverlayCount == 0,
                "生产Runner运行结束未只发布一次完整DiagnosticOnly摘要。");
            runStopwatch.Stop();
            Console.WriteLine(
                "METRIC FieldRunner10000 Cycles=10000 ElapsedMs={0} DetailEvents={1} DecisionEvents={2} WarningRaised={3} AlarmRaised={4} RecoverableFault={5} SummaryEvents={6} SummaryCount={7} OverlayEntries={8} Paused={9} RuntimeTransitions={10} TimerPause={11} SafeIdle={12} Stop={13} SystemFault={14} FaultStreak=0",
                runStopwatch.ElapsedMilliseconds,
                detailCount,
                decisionCount,
                warningCount,
                alarmCount,
                recoverableFaultCount,
                summaryCount,
                summaryTotalCount,
                overlayEntriesBeforeFlush,
                channelPausedCount,
                runtimeStateTransitionCount,
                timerPauseCount,
                safeIdleCount,
                stopCount,
                systemFaultCount);

            VerifyAdaptiveLifecyclePortPublishesControlOutputs();
        }

        private static void VerifyAdaptiveLifecyclePortPublishesControlOutputs()
        {
            var counts = new int[6];
            var runId = Guid.NewGuid();
            const long runEpoch = 41;
            var port = new EpbManagerAdaptiveLifecyclePort(
                observer: lifecycle =>
                {
                    switch (lifecycle.Kind)
                    {
                        case EpbManagerAdaptiveLifecycleEventKind.ChannelPaused:
                            Interlocked.Increment(ref counts[0]);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.RuntimeStateChanged:
                            Interlocked.Increment(ref counts[1]);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.TimerPause:
                            Interlocked.Increment(ref counts[2]);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.SafeIdle:
                            Interlocked.Increment(ref counts[3]);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.Stop:
                            Interlocked.Increment(ref counts[4]);
                            break;
                        case EpbManagerAdaptiveLifecycleEventKind.SystemFault:
                            Interlocked.Increment(ref counts[5]);
                            break;
                    }
                    Assert(lifecycle.RunId == runId && lifecycle.RunEpoch == runEpoch,
                        "Adaptive生命周期port丢失生产RunId/RunEpoch身份。");
                });

            port.PublishChannelPaused(10, runId, runEpoch);
            port.PublishRuntimeState(new ChannelRuntimeStateChangedEvent
            {
                Channel = 10,
                State = ChannelRuntimeState.Running,
                ReasonCode = "Running",
                RunId = runId,
                RunEpoch = runEpoch
            });
            port.PublishTimerPause(10, "TimerPaused", runId, runEpoch);
            port.PublishSafeIdle(10, "SafeIdle", runId, runEpoch);
            port.PublishStop(10, "Stop", runId, runEpoch);
            port.PublishSystemFault(
                new ControlFault(
                    "TestSystemFault",
                    "port probe",
                    FaultScope.Global,
                    new[] { 10 },
                    null,
                    DateTime.UtcNow,
                    Guid.NewGuid(),
                    FaultClassification.SystemFault,
                    FaultRecoveryPolicy.UnattendedBatchRecycle,
                    runId),
                runId,
                runEpoch);
            Assert(counts.All(value => value == 1),
                "Adaptive生命周期port控制输出未逐类发布：" +
                string.Join(",", counts));
        }

        private static void QualifiedRiseDropStillTripsOnlyAfterConfirmedDrop()
        {
            var wave = BuildCandidateWaveform(9, fallbackAfterRise: false);
            var token = NewEvidenceToken(2200);
            var evidence = TwoDeviceAiAcquirer.ReplayEpbLoadRiseEvidence(
                token,
                wave.Currents,
                wave.Timestamps,
                generation: 23,
                startAcceptedSequence: 2000);
            Assert(evidence.Qualified, "正例波形未取得合格全速率上升证据。");

            var machine = NewMachine();
            machine.ArmForward(Tick(0), 0, 9000, 15, 0, 3);
            var now = 0L;
            EpbAdaptiveDecision decision = null;
            for (var i = 0; i < wave.Currents.Length; i++)
            {
                now = wave.TickValues[i];
                decision = machine.OnSampleReusable(
                    now,
                    wave.Currents[i],
                    0,
                    evidence,
                    new EpbAdaptiveDecision());
                if (decision.Stage == EpbCurrentStage.LoadRise) break;
            }
            Assert(
                decision != null &&
                decision.Stage == EpbCurrentStage.LoadRise &&
                decision.LoadRiseEvidenceQualified,
                "合格证据未使状态机进入LoadRise。");

            machine.OnSample(Tick(205), 4.5);
            machine.OnSample(Tick(310), 1.5);
            var drop = machine.OnSample(Tick(420), 1.5);
            Assert(
                drop.HardFault &&
                drop.Reason != null &&
                drop.Reason.IndexOf("AbnormalLoadRiseDrop", StringComparison.Ordinal) >= 0,
                "合格上升后的真实回落未触发AbnormalLoadRiseDrop正例。");
        }

        private static void AssertRealFixtureReplay(
            string fileName,
            int expectedLength,
            string expectedSha,
            int cycleNumber)
        {
            var samples = ReadSamples(FixturePath(fileName));
            AssertFixture(FixturePath(fileName), expectedLength, expectedSha);
            var currents = new double[samples.Length];
            var timestamps = new DateTime[samples.Length];
            var ticks = new long[samples.Length];
            var baseUtc = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
            var firstTick = samples.Length == 0 ? 0 : samples[0].Tick;
            for (var i = 0; i < samples.Length; i++)
            {
                currents[i] = samples[i].CurrentA;
                ticks[i] = samples[i].Tick;
                timestamps[i] = baseUtc.AddTicks(
                    (long)((samples[i].Tick - firstTick) *
                           (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
            }

            // Use the instance capture path rather than constructing a final
            // evidence snapshot.  The replay seeds only the two independent
            // watermarks, calls BeginEpbCurrentPeak, feeds the production
            // PeakTracker, and obtains every sample gate through TryPeek.
            using var acquirer = NewReplayAcquirer();
            acquirer.ConfigureReplayWatermarks("Dev1", generation: 19, processedSequence: 400);
            var token = acquirer.BeginEpbCurrentPeak(
                10,
                Guid.NewGuid(),
                cycleNumber,
                runEpoch: 12,
                daqGeneration: 19,
                startAcceptedSequence: 500,
                forwardMinimumRiseSlopeAperMs: 0.001);
            var machine = NewMachine();
            machine.ArmForward(Tick(0), 100, 9000, 14, 0, 3);
            var callbackIndex = 0;
            var sawLoadRise = false;
            var sawEvidenceQualified = false;
            var sawFullRateLoadRise = false;
            var sawClampReached = false;
            string hardFault = null;
            var sawAbnormalLoadRiseDrop = false;
            var qualifiedEvidenceSeen = false;
            var qualifiedEvidence = default(EpbLoadRiseEvidenceSnapshot);
            var firstQualifiedIndex = -1;
            var lastQualifiedIndex = -1;
            var firstQualifiedCurrent = 0.0;
            var lastQualifiedCurrent = 0.0;
            var firstLoadRiseReason = string.Empty;
            Func<long, double, EpbLoadRiseEvidenceSnapshot, bool> replayCallback =
                (ignoredTick, current, sampleEvidence) =>
                {
                    var decision = machine.OnSampleReusable(
                        Tick(callbackIndex * 0.5),
                        current,
                        sampleEvidence.InrushPeakA,
                        sampleEvidence,
                        new EpbAdaptiveDecision());
                    if (sampleEvidence.Qualified)
                    {
                        qualifiedEvidenceSeen = true;
                        qualifiedEvidence = sampleEvidence;
                        if (firstQualifiedIndex < 0)
                        {
                            firstQualifiedIndex = callbackIndex;
                            firstQualifiedCurrent = current;
                        }
                        lastQualifiedIndex = callbackIndex;
                        lastQualifiedCurrent = current;
                    }
                    callbackIndex++;
                    sawLoadRise |= decision.Stage == EpbCurrentStage.LoadRise;
                    sawEvidenceQualified |= decision.LoadRiseEvidenceQualified;
                    if (decision.HardFault &&
                        decision.Reason?.IndexOf(
                            "AbnormalLoadRiseDrop",
                            StringComparison.Ordinal) >= 0)
                        sawAbnormalLoadRiseDrop = true;
                    if (decision.Stage == EpbCurrentStage.LoadRise &&
                        string.IsNullOrEmpty(firstLoadRiseReason))
                        firstLoadRiseReason = decision.Reason ?? string.Empty;
                    if (string.Equals(
                            decision.Reason,
                            "LoadRiseFullRateValleyThenRise",
                            StringComparison.Ordinal))
                        sawFullRateLoadRise = true;
                    sawClampReached |= decision.ClampReached ||
                                       decision.Stage == EpbCurrentStage.ClampReached;
                    if (decision.HardFault && hardFault == null)
                        hardFault = decision.Reason;

                    // V15 is expected to reach ClampReached on the real
                    // production chain.  Stop at that decision so the final
                    // TryPeek still represents the qualifying sample window,
                    // rather than the later falling tail invalidating it.
                    return cycleNumber < 0 ||
                           !(decision.ClampReached ||
                             decision.Stage == EpbCurrentStage.ClampReached);
                };
            var replaySucceeded = acquirer.ReplayEpbCurrentPeakSamples(
                token,
                currents,
                timestamps,
                generation: 19,
                replayCallback,
                out var evidence);
            Assert(replaySucceeded,
                "现场波形未通过实例PeakTracker→TryPeek完整回放链：" + fileName);
            Assert(evidence.TokenValid && evidence.IsFresh,
                "现场波形回放未绑定Capture/Run/新鲜序列：" + fileName);
            Assert(evidence.DaqGeneration == 19 && evidence.ProcessedSequence > 500,
                "现场波形回放未保留DAQ generation/processed sequence：" + fileName);
            Assert(evidence.InrushPeakA > 0,
                "现场波形回放未取得inrush peak：" + fileName);
            Console.WriteLine(
                "METRIC RealFixtureQualified {0} First={1}@{2:F3} Last={3}@{4:F3} " +
                "EvidenceQualified={5} EvidenceFullRate={6} SawEvidenceQualified={7}",
                fileName,
                firstQualifiedIndex,
                firstQualifiedCurrent,
                lastQualifiedIndex,
                lastQualifiedCurrent,
                evidence.Qualified,
                evidence.FullRateValleyThenRise,
                sawEvidenceQualified);

            if (cycleNumber < 0)
            {
                Console.WriteLine(
                    "METRIC RealFixture {0} Qualified={1} FullRate={2} " +
                    "ValleyMs={3:F1} ValleyEq={4} RiseSamples={5} RiseMs={6:F1} " +
                    "RiseA={7:F3} Stage={8} SawLoadRise={9} SawClamp={10} HardFault={11}",
                    fileName,
                    evidence.Qualified,
                    evidence.FullRateValleyThenRise,
                    evidence.ValleyWindowMs,
                    evidence.ValleyEquivalentSampleCount,
                    evidence.RiseContinuousSampleCount,
                    evidence.RiseContinuousDurationMs,
                    evidence.PostValleyRiseA,
                    machine.Stage,
                    sawLoadRise,
                    sawClampReached,
                    hardFault ?? string.Empty);
                Assert(!evidence.LoadRiseEvidenceQualified && !evidence.Qualified,
                    "V20快速现场波形错误取得LoadRise全速率资格。");
                Assert(!sawFullRateLoadRise,
                    "V20快速现场波形经生产全速率证据错误进入LoadRise。");
                Assert(!qualifiedEvidenceSeen,
                    "V20快速现场波形经生产tracker错误取得全速率资格。");
                Assert(!sawAbnormalLoadRiseDrop,
                    "V20现场波形经生产证据链错误触发AbnormalLoadRiseDrop：" + hardFault);
            }
            else
            {
                Console.WriteLine(
                    "METRIC RealFixture {0} Qualified={1} FullRate={2} " +
                    "ValleyMs={3:F1} ValleyEq={4} RiseSamples={5} RiseMs={6:F1} " +
                    "RiseA={7:F3} Stage={8} SawLoadRise={9} SawClamp={10} " +
                    "SawFullRateLoadRise={11} HardFault={12}",
                    fileName,
                    evidence.Qualified,
                    evidence.FullRateValleyThenRise,
                    evidence.ValleyWindowMs,
                    evidence.ValleyEquivalentSampleCount,
                    evidence.RiseContinuousSampleCount,
                    evidence.RiseContinuousDurationMs,
                    evidence.PostValleyRiseA,
                    machine.Stage,
                    sawLoadRise,
                    sawClampReached,
                    sawFullRateLoadRise,
                    hardFault ?? string.Empty);
                // The V15 record reaches the normal per-cycle 10 ms empty
                // baseline before its full-rate ramp.  The production state
                // machine is therefore allowed to qualify LoadRise through
                // that baseline even though the strict full-rate valley/rise
                // tracker deliberately rejects this noisy ramp as a sealed
                // FullRateValleyThenRise shape.  The assertion still proves
                // that the decision consumed the real TryPeek snapshot.
                Assert(sawEvidenceQualified,
                    "V15完整现场波形经生产OnSampleReusable未取得本圈LoadRise证据。");
                // Keep this second V15 entry point strict as well.  The
                // callback stops only at the real ClampReached decision, so a
                // LoadRise-only replay cannot silently pass as a complete
                // physical cycle.
                Assert(sawClampReached,
                    "V15完整现场波形经生产OnSampleReusable未进入ClampReached终态。");
                Assert(string.IsNullOrEmpty(hardFault),
                    "V15完整现场波形经生产证据链出现硬故障：" + hardFault);
            }
        }

        private static TwoDeviceAiAcquirer NewReplayAcquirer()
        {
            var cfg = new AiConfigDetail();
            cfg.Records.Add(new AiConfigDetailRecord
            {
                序号 = 0,
                物理通道 = "Dev1/ai0",
                参数名 = "EPB10_current",
                单位 = "A",
                变换斜率 = 1,
                变换截距 = 0,
                参数类型 = "电流",
                是否启用 = 1
            });
            // The production acquirer owns one median stream per DAQ device;
            // keep the unused side valid as well so this fixture still uses
            // the normal two-device constructor and background pipeline.
            cfg.Records.Add(new AiConfigDetailRecord
            {
                序号 = 1,
                物理通道 = "Dev2/ai0",
                参数名 = "Auxiliary_dummy",
                单位 = "V",
                变换斜率 = 1,
                变换截距 = 0,
                参数类型 = "辅助",
                是否启用 = 1
            });
            return new TwoDeviceAiAcquirer(
                cfg,
                sampleRate: 2000,
                samplesPerChannel: 1,
                medianLens: 1);
        }

        private static SyntheticSamples BuildSyntheticValleyThenRise()
        {
            const double sampleMs = 0.5;
            const int count = 620;
            var currents = new double[count];
            var timestamps = new DateTime[count];
            var ticks = new long[count];
            var baseUtc = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < count; i++)
            {
                var ms = i * sampleMs;
                double current;
                if (ms < 20)
                    current = 0.05 + ms * 0.10;
                else if (ms < 40)
                    current = 2.05 - (ms - 20) * 0.0775;
                else if (ms < 180)
                    current = 0.50 + ((i % 5) - 2) * 0.005;
                else
                    current = 0.50 + Math.Min(2.0, (ms - 180) * 0.025);
                currents[i] = current;
                ticks[i] = Tick(ms);
                timestamps[i] = baseUtc.AddTicks(
                    (long)(ms * TimeSpan.TicksPerMillisecond));
            }
            return new SyntheticSamples(currents, timestamps, ticks);
        }

        private static SyntheticSamples BuildCandidateWaveform(
            int riseSamples,
            bool fallbackAfterRise)
        {
            const double sampleMs = 0.5;
            var riseCount = Math.Max(1, riseSamples);
            var finalMs = 180.0 +
                          (riseCount - 1 + (fallbackAfterRise ? 1 : 0)) * sampleMs;
            var count = (int)Math.Round(finalMs / sampleMs) + 1;
            var currents = new double[count];
            var timestamps = new DateTime[count];
            var ticks = new long[count];
            var baseUtc = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < count; i++)
            {
                var ms = i * sampleMs;
                double current;
                if (ms < 20)
                    current = 0.05 + ms * 0.10;
                else if (ms < 40)
                    current = 2.05 - (ms - 20) * 0.0775;
                else if (ms < 180)
                    current = 0.50;
                else
                {
                    var riseIndex = (int)Math.Round((ms - 180.0) / sampleMs);
                    if (fallbackAfterRise && riseIndex == 1)
                        current = 0.50;
                    else if (fallbackAfterRise && riseIndex == 0)
                        current = 1.10;
                    else
                        current = 1.00 + Math.Min(
                            Math.Max(0, riseCount - 1) * 0.08,
                            Math.Max(0, riseIndex) * 0.08);
                }
                currents[i] = current;
                ticks[i] = Tick(ms);
                timestamps[i] = baseUtc.AddTicks(
                    (long)(ms * TimeSpan.TicksPerMillisecond));
            }
            return new SyntheticSamples(currents, timestamps, ticks);
        }

        private static PeakCaptureToken NewEvidenceToken(int cycle)
        {
            return new PeakCaptureToken
            {
                CaptureId = Guid.NewGuid(),
                TestRunId = Guid.NewGuid(),
                RunEpoch = 11,
                Channel = 10,
                CycleNumber = cycle
            };
        }

        private static EpbLoadRiseEvidenceSnapshot Mutate(
            EpbLoadRiseEvidenceSnapshot evidence,
            Func<EpbLoadRiseEvidenceSnapshot, EpbLoadRiseEvidenceSnapshot> mutation)
        {
            return mutation(evidence);
        }

        private static void DiagnosticOverlayAggregatesByRunEpochAndCode()
        {
            var runId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var aggregator = new AdaptiveDiagnosticRunAggregator();
            var key = new AdaptiveDiagnosticKey(
                runId,
                runEpoch: 3,
                channel: 10,
                diagnosticCode: "RapidLoadRiseWithoutObservedEmpty");
            Assert(
                aggregator.Observe(key, now, "first", out var first) && first.Count == 1,
                "DiagnosticOnly首次事件未进入运行级叠加层。");
            Assert(
                !aggregator.Observe(key, now.AddSeconds(1), "second", out var second) &&
                second.Count == 2,
                "DiagnosticOnly同RunEpoch/通道/代码未聚合。");
            var nextEpoch = new AdaptiveDiagnosticKey(
                runId,
                runEpoch: 4,
                channel: 10,
                diagnosticCode: "RapidLoadRiseWithoutObservedEmpty");
            Assert(
                !aggregator.Observe(nextEpoch, now, "same-run-rejoin", out var sameRun) &&
                sameRun.Count == 3,
                "DiagnosticOnly同Run跨RunEpoch/rejoin未复用同一汇总。");
            var flushCount = 0;
            aggregator.Flush(_ => flushCount++);
            Assert(flushCount == 1 && aggregator.Count == 0,
                "DiagnosticOnly结束汇总未一次清空运行级叠加层。");
        }

        private static void DiagnosticOverlayFlushDoesNotHoldLockOrDuplicateSummary()
        {
            var runId = Guid.NewGuid();
            var key = new AdaptiveDiagnosticKey(
                runId,
                runEpoch: 1,
                channel: 10,
                diagnosticCode: "DiagnosticOnly");
            var aggregator = new AdaptiveDiagnosticRunAggregator();
            aggregator.Observe(key, DateTime.UtcNow, "first", out _);

            var callbackEntered = new ManualResetEventSlim(false);
            var flush = Task.Run(() => aggregator.Flush(_ =>
            {
                callbackEntered.Set();
                Thread.Sleep(200);
            }));
            Assert(callbackEntered.Wait(1000),
                "DiagnosticOnly Flush回调未启动。");

            // Observe must be able to detach a new entry while a slow sink is
            // still running; a sink is never allowed to hold the control lock.
            var observe = Task.Run(() => aggregator.Observe(
                new AdaptiveDiagnosticKey(runId, 2, 10, "DiagnosticOnly"),
                DateTime.UtcNow,
                "during-flush",
                out _));
            Assert(observe.Wait(100),
                "慢DiagnosticOnly观察者阻塞了运行级聚合器Detach/Observe。");
            Assert(flush.Wait(2000),
                "DiagnosticOnly慢观察者未在可控时间内返回。");

            // The concurrent entry is a new observation and may be flushed
            // separately.  A cleared entry must never be emitted twice.
            var secondFlushCount = 0;
            aggregator.Flush(_ => secondFlushCount++);
            Assert(secondFlushCount == 1 && aggregator.Count == 0,
                "Flush清空顺序未保留并发新诊断。");

            var throwingCount = 0;
            aggregator.Observe(key, DateTime.UtcNow, "throwing", out _);
            aggregator.Flush(_ =>
            {
                throwingCount++;
                throw new InvalidOperationException("diagnostic sink failure");
            });
            Assert(throwingCount == 1 && aggregator.Count == 0,
                "抛异常DiagnosticOnly观察者未安全清空快照。");
            var duplicateCount = 0;
            aggregator.Flush(_ => duplicateCount++);
            Assert(duplicateCount == 0,
                "DiagnosticOnly摘要在Flush后被重复发布。");
        }

        private sealed class SyntheticSamples
        {
            internal SyntheticSamples(double[] currents, DateTime[] timestamps, long[] tickValues)
            {
                Currents = currents;
                Timestamps = timestamps;
                TickValues = tickValues;
            }

            internal double[] Currents { get; }
            internal DateTime[] Timestamps { get; }
            internal long[] TickValues { get; }
        }

        private static EpbAdaptiveCurrentStateMachine NewMachine()
        {
            var profile = new EpbAdaptiveProfile
            {
                Channel = 10,
                ForwardEmptyCurrentA = 0.6135815,
                ForwardEmptyMadA = 0.0251518,
                ForwardClampMedianMs = 3000,
                ForwardClampMadMs = 100,
                ValidSampleCount = 5
            };
            return new EpbAdaptiveCurrentStateMachine(profile);
        }

        private static string FixturePath(string fileName)
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Fixtures",
                fileName);
        }

        private static void AssertFixture(string path, int expectedLength, string expectedSha256)
        {
            Assert(File.Exists(path), "缺少现场波形fixture：" + path);
            var info = new FileInfo(path);
            Assert(info.Length == expectedLength,
                $"现场波形长度不一致：{path} actual={info.Length} expected={expectedLength}");
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var actual = BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
                Assert(actual == expectedSha256,
                    $"现场波形SHA256不一致：{path} actual={actual} expected={expectedSha256}");
            }
        }

        private static Sample[] ReadSamples(string path)
        {
            return File.ReadLines(path)
                .Skip(1)
                .Select(line => line.Split(','))
                .Where(parts => parts.Length >= 5)
                .Select(parts => new Sample(
                    Tick(double.Parse(parts[1], CultureInfo.InvariantCulture) * 1000.0),
                    double.Parse(parts[4], CultureInfo.InvariantCulture)))
                .ToArray();
        }

        private static long Tick(double elapsedMs)
        {
            return Stopwatch.Frequency +
                   (long)Math.Round(elapsedMs * Stopwatch.Frequency / 1000.0);
        }

        private readonly struct Sample
        {
            internal Sample(long tick, double currentA)
            {
                Tick = tick;
                CurrentA = currentA;
            }

            internal long Tick { get; }
            internal double CurrentA { get; }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
