using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using PressureSample = IO.NI.PressureSample;

namespace AdaptiveControlTests
{
    internal static class HydraulicGroupCoordinatorTests
    {
        public static int RunRecoveryEvidenceRegression()
        {
            var passed = 0;
            Run("DAQ压力失新不再阻塞协调器安全重建", StalePressureAllowsDeenergizedCoordinatorRebuild, ref passed);
            return passed;
        }

        public static int RunAll()
        {
            var passed = 0;
            Run("液压非末成员等待全组低压确认", NonLastMemberWaitsForSafePressure, ref passed);
            Run("液压释放超时产生指定硬故障", ReleaseTimeoutIsExplicit, ref passed);
            Run("陈旧低压不得通过释压确认", StaleLowPressureCannotConfirmRelease, ref passed);
            Run("液压同代次重复进入不重新登记已释放成员", SameGenerationReentryDoesNotReAddReleasedMember, ref passed);
            Run("已完成液压代次不阻碍不同成员重新开始", CompletedGenerationAllowsFreshMembership, ref passed);
            Run("液压通道作用域作废后代次完整归还", ChannelLeaseScopesAlwaysCloseGeneration, ref passed);
            Run("液压组重建替换旧Gate并递增Epoch", RebuildGroupRestoresFreshStartHealth, ref passed);
            Run("DAQ压力失新不再阻塞协调器安全重建", StalePressureAllowsDeenergizedCoordinatorRebuild, ref passed);
            Run("迟到旧Epoch租约不得释放新代次成员", LateOldEpochLeaseCannotReleaseNewGeneration, ref passed);
            Run("液压代次屏障缺员在一个周期内超时", GenerationBarrierTimeoutIsBounded, ref passed);
            Run("全员到齐后释压时间不计入屏障超时", SafePressureWaitDoesNotConsumeBarrierTimeout, ref passed);
            Run("正式阶段在两相位之间启动时整组滚到同一槽", FormalStartBetweenPhasesUsesOneFutureSlot, ref passed);
            Run("液压样本无效或陈旧时资格判定失败", InvalidPressureSamplesAreRejected, ref passed);
            Run("首次保压下降仅触发软件自愈", FirstPressureLossIsRecoverable, ref passed);
            Run("保压连续三代次下降才确认硬件报警", ThirdPressureLossConfirmsHardwareFault, ref passed);
            Run("压力样本陈旧只触发软件自愈", StalePressureLossIsRecoverable, ref passed);
            Run("液压任务取消不得确认硬件报警", CanceledHydraulicWorkIsNotHardware, ref passed);
            Run("未知液压异常不得绕过连续确认", UnknownHydraulicFaultIsNotHardware, ref passed);
            Run("报警电源组仅在无兄弟通道活动时关闭", PowerGroupIdlePredicateIsScoped, ref passed);
            return passed;
        }

        private static void NonLastMemberWaitsForSafePressure()
        {
            var pressure = 80.0;
            var releaseCount = 0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                async () =>
                {
                    Interlocked.Increment(ref releaseCount);
                    await Task.Delay(30).ConfigureAwait(false);
                    Volatile.Write(ref pressure, 4.0);
                },
                stableMs: 40,
                timeoutMs: 500);

            coordinator.EnterElectricalPhaseAsync(8, CancellationToken.None).GetAwaiter().GetResult();
            coordinator.EnterElectricalPhaseAsync(9, CancellationToken.None).GetAwaiter().GetResult();

            var first = coordinator.MarkVoltageReleaseAsync(8);
            Thread.Sleep(30);
            Assert(!first.IsCompleted, "非末成员在液压实际释放前已越过屏障。");
            Assert(releaseCount == 0, "尚有成员在途时提前触发了液压释放。");

            var clock = Stopwatch.StartNew();
            var last = coordinator.MarkVoltageReleaseAsync(9);
            Task.WaitAll(first, last);
            Assert(releaseCount == 1, "同一液压轮次应只执行一次释放动作。");
            Assert(clock.ElapsedMilliseconds >= 60, "未等待压力低于阈值并连续稳定。");
        }

        private static void ReleaseTimeoutIsExplicit()
        {
            var coordinator = NewCoordinator(
                () => 42,
                () => Task.CompletedTask,
                stableMs: 20,
                timeoutMs: 80);
            coordinator.EnterElectricalPhaseAsync(10, CancellationToken.None).GetAwaiter().GetResult();

            try
            {
                coordinator.MarkVoltageReleaseAsync(10).GetAwaiter().GetResult();
                throw new InvalidOperationException("压力未下降时未触发液压释放超时。");
            }
            catch (HydraulicReleaseTimeoutException ex)
            {
                Assert(
                    ex.Message.Contains("HydraulicReleaseTimeout"),
                    "液压释放超时未保留稳定故障代码。");
            }
        }

        private static void SameGenerationReentryDoesNotReAddReleasedMember()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 10,
                timeoutMs: 300,
                barrierTimeoutMs: 300,
                targetBar: 70,
                holdDropConfirmMs: 50);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Formal, 42);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();

            var firstRelease = coordinator.MarkVoltageReleaseAsync(lease, 8);
            Thread.Sleep(20);
            Assert(!firstRelease.IsCompleted, "首成员释放后不应越过全组屏障。");

            var sameLease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(ReferenceEquals(lease.Qualification, sameLease.Qualification), "同代次未复用资格结果。");

            var secondRelease = coordinator.MarkVoltageReleaseAsync(sameLease, 11);
            Task.WaitAll(firstRelease, secondRelease);
        }

        private static void CompletedGenerationAllowsFreshMembership()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 0,
                timeoutMs: 300,
                barrierTimeoutMs: 300);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.PreRelease, 1);
            var original = coordinator.EnterGenerationAsync(key, new[] { 8, 9 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Task.WaitAll(
                coordinator.MarkVoltageReleaseAsync(original, 8),
                coordinator.MarkVoltageReleaseAsync(original, 9));

            // 模拟“整批[8,9]停止后只重启EPB8”。完成代次必须已清理，不能再以
            // Existing=[8,9] Requested=[8] 阻碍操作者重新开始。
            Volatile.Write(ref pressure, 80.0);
            var restarted = coordinator.EnterGenerationAsync(key, new[] { 8 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(restarted.Members.Count == 1 && restarted.Members[0] == 8,
                "完成代次仍保留旧成员快照，阻碍单卡钳重启。 ");
            coordinator.MarkVoltageReleaseAsync(restarted, 8).GetAwaiter().GetResult();
        }

        private static void ChannelLeaseScopesAlwaysCloseGeneration()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 0,
                timeoutMs: 300,
                barrierTimeoutMs: 300);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Learning, 9);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 9 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            var first = coordinator.CreateChannelScope(lease, 8);
            var second = coordinator.CreateChannelScope(lease, 9);

            Task.WaitAll(
                first.AbortAsync("BeginLearningCycleFailed"),
                second.CompleteAsync());
            var snapshot = coordinator.ProbeGroupHealth(2);
            Assert(first.IsClosed && second.IsClosed, "通道液压作用域未进入关闭终态。 ");
            Assert(snapshot.IsHealthyForFreshStart,
                "作用域全部关闭后液压组仍不可重新开始：" + snapshot);
        }

        private static void RebuildGroupRestoresFreshStartHealth()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 0,
                timeoutMs: 300,
                barrierTimeoutMs: 300);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Recovery, 10);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 9 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            var firstScope = coordinator.CreateChannelScope(lease, 8);
            var secondScope = coordinator.CreateChannelScope(lease, 9);
            var before = coordinator.ProbeGroupHealth(2);
            Assert(!before.IsHealthyForFreshStart && before.ActiveGenerationCount == 1 &&
                   before.ActiveLeaseCount == 2,
                "测试前置未建立活动液压代次。 ");

            var rebuilt = coordinator.RebuildGroupAsync(
                    2,
                    "RegressionTest",
                    1000,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(rebuilt.IsHealthyForFreshStart,
                "重建后液压组仍不可重新开始：" + rebuilt);
            Assert(rebuilt.CoordinatorEpoch > before.CoordinatorEpoch,
                "液压组重建未递增协调器Epoch。 ");
            Assert(rebuilt.RebuildCount == before.RebuildCount + 1,
                "液压组重建次数未准确记录。 ");
            Assert(firstScope.IsClosed && secondScope.IsClosed && rebuilt.ActiveLeaseCount == 0,
                "液压组重建未终结并清除旧作用域租约。 ");
        }

        private static void StalePressureAllowsDeenergizedCoordinatorRebuild()
        {
            var staleTick = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
            var releases = 0;
            var config = new TestConfig();
            var hydraulic = new HydraulicItem
            {
                Id = 2,
                Enabled = true,
                ReleaseSafePressureBar = 5,
                ReleaseStableMs = 10,
                ReleaseTimeoutMs = 40,
                PressureSampleMaxAgeMs = 100
            };
            hydraulic.Members.Add(8);
            config.Hydraulics.Add(hydraulic);
            var coordinator = new HydraulicGroupCoordinator(
                config,
                _ => new PressureSample(2, 0, DateTime.UtcNow, staleTick),
                _ =>
                {
                    Interlocked.Increment(ref releases);
                    return Task.CompletedTask;
                },
                NullLogger.Instance);

            var rebuilt = coordinator.RebuildGroupAsync(
                    2,
                    "DaqGenerationRecovery",
                    1000,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(releases == 1, "压力失新时未先执行DO/AO去能量化");
            Assert(rebuilt.IsHealthyForFreshStart,
                "压力失新形成循环依赖，协调器无法重建：" + rebuilt);
        }

        private static void LateOldEpochLeaseCannotReleaseNewGeneration()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 0,
                timeoutMs: 300,
                barrierTimeoutMs: 300);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Recovery, 11);
            var oldLease = coordinator.EnterGenerationAsync(key, new[] { 8, 9 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            coordinator.RebuildGroupAsync(2, "EpochIsolation", 1000, CancellationToken.None)
                .GetAwaiter().GetResult();

            Volatile.Write(ref pressure, 80.0);
            var newLease = coordinator.EnterGenerationAsync(key, new[] { 8, 9 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(newLease.CoordinatorEpoch > oldLease.CoordinatorEpoch,
                "重建后的同Key代次没有使用新Epoch。 ");
            try
            {
                coordinator.MarkVoltageReleaseAsync(oldLease, 8).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 旧代次由重建明确终结，迟到调用应观察旧终态。
            }

            var snapshot = coordinator.ProbeGroupHealth(2);
            Assert(snapshot.PendingMembers.SequenceEqual(new[] { 8, 9 }),
                "迟到旧Epoch租约污染了新代次成员状态：" + snapshot);
            Task.WaitAll(
                coordinator.MarkVoltageReleaseAsync(newLease, 8),
                coordinator.MarkVoltageReleaseAsync(newLease, 9));
        }

        private static void StaleLowPressureCannotConfirmRelease()
        {
            var staleTick = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
            var config = new TestConfig();
            var hydraulic = new HydraulicItem
            {
                Id = 2,
                Enabled = true,
                ReleaseSafePressureBar = 5,
                ReleaseStableMs = 10,
                ReleaseTimeoutMs = 50,
                PressureSampleMaxAgeMs = 100
            };
            hydraulic.Members.Add(10);
            config.Hydraulics.Add(hydraulic);
            var coordinator = new HydraulicGroupCoordinator(
                config,
                _ => new PressureSample(2, 0, DateTime.UtcNow, staleTick),
                _ => Task.CompletedTask,
                NullLogger.Instance);
            coordinator.EnterElectricalPhaseAsync(10, CancellationToken.None).GetAwaiter().GetResult();
            try
            {
                coordinator.MarkVoltageReleaseAsync(10).GetAwaiter().GetResult();
                throw new InvalidOperationException("陈旧0bar样本错误通过释压确认。");
            }
            catch (HydraulicReleaseTimeoutException ex)
            {
                Assert(ex.Message.Contains("PressureSampleStaleSample"),
                    "陈旧压力超时未记录样本陈旧原因。");
            }
        }

        private static void GenerationBarrierTimeoutIsBounded()
        {
            var coordinator = NewCoordinator(
                () => 80,
                () => Task.CompletedTask,
                stableMs: 10,
                timeoutMs: 200,
                barrierTimeoutMs: 80);
            ControlFault publishedFault = null;
            coordinator.FaultRaised += fault => publishedFault = fault;
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Formal, 7);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            try
            {
                coordinator.MarkVoltageReleaseAsync(lease, 8).GetAwaiter().GetResult();
                throw new InvalidOperationException("缺少成员释放时未触发屏障超时。");
            }
            catch (HydraulicBarrierTimeoutException ex)
            {
                Assert(ex.Message.Contains("Pending=[11]"), "屏障超时未记录缺失成员。");
                Assert(publishedFault != null, "屏障超时未发布液压组故障。");
                Assert(publishedFault.Classification == FaultClassification.SystemFault,
                    "仅有同步缺员证据时错误确认成硬件故障。");
            }
        }

        private static void SafePressureWaitDoesNotConsumeBarrierTimeout()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                async () =>
                {
                    // 现场复现中全员已到释放点（Pending=[]），但低压确认跨过了
                    // BarrierTimeout；这段时间应受 ReleaseTimeoutMs 约束。
                    await Task.Delay(100).ConfigureAwait(false);
                    Volatile.Write(ref pressure, 0.0);
                },
                stableMs: 20,
                timeoutMs: 500,
                barrierTimeoutMs: 60);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Learning, 1);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 9 }, CancellationToken.None)
                .GetAwaiter().GetResult();

            var first = coordinator.MarkVoltageReleaseAsync(lease, 8);
            Thread.Sleep(20);
            var second = coordinator.MarkVoltageReleaseAsync(lease, 9);
            Task.WaitAll(first, second);
        }

        private static void FormalStartBetweenPhasesUsesOneFutureSlot()
        {
            // 现场：t0=19:57:00，正式阶段于 19:58:45.502 启动，正好处于
            // slot7 的 0ms 与 800ms 电气相位之间。旧逻辑让两类通道分属 slot8/slot7。
            var t0 = new DateTime(2026, 8, 5, 19, 57, 0, DateTimeKind.Utc);
            var started = new DateTime(2026, 8, 5, 19, 58, 45, 502, DateTimeKind.Utc);
            var firstSlot = EpbManager.CalculateFirstFutureFormalSlot(t0, started, 15000);

            Assert(firstSlot == 8, "正式阶段未滚到下一完整零相位槽。");
            var callbackUtc = t0.AddMilliseconds(firstSlot * 15000.0);
            Assert(callbackUtc == new DateTime(2026, 8, 5, 19, 59, 0, DateTimeKind.Utc),
                "正式阶段整组回调锚点计算错误。");

            // 电气相位只改变资格后的上电时刻，不得再改变液压代次键。
            var zeroPhaseKey = new HydraulicGenerationKey(Guid.Empty, 2, HydraulicPhaseKind.Formal, firstSlot);
            var latePhaseKey = new HydraulicGenerationKey(Guid.Empty, 2, HydraulicPhaseKind.Formal, firstSlot);
            Assert(zeroPhaseKey.Equals(latePhaseKey), "同组电气相位被错误拆分到不同液压槽。");
        }

        private static void InvalidPressureSamplesAreRejected()
        {
            var now = Stopwatch.GetTimestamp();
            Assert(!HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, 0, DateTime.UtcNow, now), 70, 100),
                "0bar 被错误判为压力合格");
            Assert(!HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, double.NaN, DateTime.UtcNow, now), 70, 100),
                "NaN 被错误判为压力合格");
            Assert(!HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, 80, DateTime.UtcNow,
                        now - Stopwatch.Frequency), 70, 100),
                "陈旧压力样本被错误判为合格");
            Assert(HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, 80, DateTime.UtcNow, Stopwatch.GetTimestamp()), 70, 100),
                "新鲜达标压力未通过资格判定");
            Assert(HydraulicController.ClassifyPressureSampleFailure(
                       default(PressureSample), 70, 100) ==
                   HydraulicPressureFailureReason.NoSample,
                "无压力样本未被识别为无样本");
            Assert(HydraulicController.ClassifyPressureSampleFailure(
                       new PressureSample(2, 80, DateTime.UtcNow,
                           now - Stopwatch.Frequency), 70, 100) ==
                   HydraulicPressureFailureReason.StaleSample,
                "陈旧压力样本未被识别为样本过期");
            Assert(HydraulicController.ClassifyPressureSampleFailure(
                       new PressureSample(2, 60, DateTime.UtcNow,
                           Stopwatch.GetTimestamp()), 70, 100) ==
                   HydraulicPressureFailureReason.BelowMinimum,
                "新鲜低压样本未被识别为压力不足");
        }

        private static void FirstPressureLossIsRecoverable()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 10,
                timeoutMs: 300,
                barrierTimeoutMs: 300,
                targetBar: 70,
                holdDropConfirmMs: 50);
            ControlFault publishedFault = null;
            coordinator.FaultRaised += fault => publishedFault = fault;
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Formal, 99);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Volatile.Write(ref pressure, 50.0);
            Thread.Sleep(1150);
            try
            {
                coordinator.MarkVoltageReleaseAsync(lease, 8).GetAwaiter().GetResult();
                throw new InvalidOperationException("保压持续下降未触发液压故障。");
            }
            catch (HydraulicPressureLostException)
            {
            }
            Assert(publishedFault != null &&
                   publishedFault.Classification == FaultClassification.SystemFault,
                "首次保压下降被过早确认成硬件报警。");
            Assert(publishedFault.Reason.Contains("Confirmation=1/3"),
                "首次保压下降未记录连续代次证据。");
        }

        private static void ThirdPressureLossConfirmsHardwareFault()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 10,
                timeoutMs: 300,
                barrierTimeoutMs: 300,
                targetBar: 70,
                holdDropConfirmMs: 1000);
            var published = new System.Collections.Generic.List<ControlFault>();
            coordinator.FaultRaised += fault => published.Add(fault);
            var runId = Guid.NewGuid();

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Volatile.Write(ref pressure, 80.0);
                var key = new HydraulicGenerationKey(
                    runId,
                    2,
                    HydraulicPhaseKind.Formal,
                    100 + attempt);
                var lease = coordinator.EnterGenerationAsync(
                        key,
                        new[] { 8, 11 },
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                Volatile.Write(ref pressure, 50.0);
                Thread.Sleep(1150);
                try
                {
                    coordinator.MarkVoltageReleaseAsync(lease, 8).GetAwaiter().GetResult();
                }
                catch (HydraulicPressureLostException)
                {
                }
            }

            Assert(published.Count == 3, "三次保压下降未逐代次发布证据。");
            Assert(published[0].Classification == FaultClassification.SystemFault &&
                   published[1].Classification == FaultClassification.SystemFault,
                "前两次保压下降被过早确认成硬件报警。");
            Assert(published[2].Classification == FaultClassification.HardwareConfirmed &&
                   published[2].Reason.Contains("Confirmation=3/3"),
                "第三次连续保压下降未确认硬件报警。");
        }

        private static void StalePressureLossIsRecoverable()
        {
            var staleTick = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2;
            var config = new TestConfig();
            var hydraulic = new HydraulicItem
            {
                Id = 2,
                Enabled = true,
                PressureThresholdBar = 70,
                PressureToleranceBar = 10,
                HoldDropToleranceBar = 5,
                HoldDropConfirmMs = 100,
                PressureSampleMaxAgeMs = 100,
                ReleaseSafePressureBar = 5,
                ReleaseStableMs = 10,
                ReleaseTimeoutMs = 300,
                BarrierTimeoutMs = 300
            };
            hydraulic.Members.Add(8);
            config.Hydraulics.Add(hydraulic);
            var coordinator = new HydraulicGroupCoordinator(
                config,
                _ => new PressureSample(2, 70, DateTime.UtcNow, staleTick),
                _ => Task.CompletedTask,
                NullLogger.Instance);
            ControlFault publishedFault = null;
            coordinator.FaultRaised += fault => publishedFault = fault;
            var lease = coordinator.EnterGenerationAsync(
                    new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Formal, 200),
                    new[] { 8 },
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            Thread.Sleep(1150);
            try
            {
                coordinator.MarkVoltageReleaseAsync(lease, 8).GetAwaiter().GetResult();
            }
            catch (HydraulicPressureLostException)
            {
            }

            Assert(hydraulic.EffectiveHoldDropToleranceBar == 10 &&
                   hydraulic.EffectiveHoldDropConfirmMs == 1000,
                "旧保压参数未提升到建压容差与1秒确认下限。");
            Assert(publishedFault != null &&
                   publishedFault.Code == "PressureSampleUnavailable" &&
                   publishedFault.Classification == FaultClassification.SystemFault,
                "陈旧压力样本仍被错误发布成硬件保压丢失。");
        }

        private static void CanceledHydraulicWorkIsNotHardware()
        {
            Assert(
                HydraulicGroupCoordinator.ClassifyFault(new TaskCanceledException("pause")) ==
                FaultClassification.SoftwareTransient,
                "控制流取消被错误确认成液压硬件报警。");
        }

        private static void UnknownHydraulicFaultIsNotHardware()
        {
            Assert(
                HydraulicGroupCoordinator.ClassifyFault(new InvalidOperationException("software")) ==
                FaultClassification.SystemFault,
                "无压力证据的未知异常绕过连续三代次确认成为硬件报警。");
        }

        private static HydraulicGroupCoordinator NewCoordinator(
            Func<double> readPressure,
            Func<Task> release,
            int stableMs,
            int timeoutMs,
            int barrierTimeoutMs = 0,
            int targetBar = 0,
            int holdDropConfirmMs = 100)
        {
            var config = new TestConfig();
            var hydraulic = new HydraulicItem
            {
                Id = 2,
                Enabled = true,
                ReleaseSafePressureBar = 5,
                ReleaseStableMs = stableMs,
                ReleaseTimeoutMs = timeoutMs,
                BarrierTimeoutMs = barrierTimeoutMs,
                BuildStableMs = 0,
                PressureThresholdBar = targetBar,
                HoldDropConfirmMs = holdDropConfirmMs
            };
            hydraulic.Members.Add(8);
            hydraulic.Members.Add(9);
            hydraulic.Members.Add(10);
            hydraulic.Members.Add(11);
            config.Hydraulics.Add(hydraulic);
            return new HydraulicGroupCoordinator(
                config,
                _ => readPressure(),
                _ => release(),
                NullLogger.Instance);
        }

        private static void PowerGroupIdlePredicateIsScoped()
        {
            var members = new[] { 10, 11, 12 };
            Assert(
                !EpbManager.IsPowerGroupIdle(
                    members,
                    channel => channel == 11,
                    channel => false),
                "兄弟通道仍参与液压时错误判定为空闲电源组。");
            Assert(
                !EpbManager.IsPowerGroupIdle(
                    members,
                    channel => false,
                    channel => channel == 12),
                "兄弟通道计时器仍运行时错误判定为空闲电源组。");
            Assert(
                EpbManager.IsPowerGroupIdle(
                    members,
                    channel => false,
                    channel => false),
                "组内已无活动通道时未判定为空闲。");
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
