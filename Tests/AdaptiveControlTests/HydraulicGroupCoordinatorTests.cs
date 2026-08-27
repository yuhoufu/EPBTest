using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            Run("双液压全局槽仅并发建压一次并共享电机锚点", GlobalSlotBuildsTogetherAndSharesAnchor, ref passed);
            Run("全局槽成员快照不可变", GlobalSlotMembershipIsImmutable, ref passed);
            Run("单液压失败时健康组释压并跳过半槽", GlobalSlotFailureReleasesHealthyGroup, ref passed);
            Run("正式槽按实际到达300ms窗口准入且迟到滚入下一槽", FormalAdmissionWindowSkipsLateChannel, ref passed);
            Run("正式槽一组失败时健康组保留lease继续提交", FormalPartialFailureKeepsHealthyLease, ref passed);
            Run("正式槽Join/Skip重复竞态一万次必然收敛", FormalJoinSkipRaceConvergesTenThousandTimes, ref passed);
            Run("液压恢复意图完整映射且拒绝Recovery来源", HydraulicRecoveryIntentMappingIsStrict, ref passed);
            Run("全局槽整批取消保持取消语义", GlobalSlotCancellationIsNotHardwareFailure, ref passed);
            Run("液压非末成员等待全组低压确认", NonLastMemberWaitsForSafePressure, ref passed);
            Run("液压释放超时产生指定硬故障", ReleaseTimeoutIsExplicit, ref passed);
            Run("陈旧低压不得通过释压确认", StaleLowPressureCannotConfirmRelease, ref passed);
            Run("液压同代次重复进入不重新登记已释放成员", SameGenerationReentryDoesNotReAddReleasedMember, ref passed);
            Run("已完成液压代次不阻碍不同成员重新开始", CompletedGenerationAllowsFreshMembership, ref passed);
            Run("液压通道作用域作废后代次完整归还", ChannelLeaseScopesAlwaysCloseGeneration, ref passed);
            Run("已完成ForceRelease仅按精确原因退役旧租约", ForceReleasedScopeRetirementIsExact, ref passed);
            Run("StopAll强制撤权不等待缺员液压屏障", StopAllForceAbortDoesNotWaitForMissingMember, ref passed);
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

        private static void GlobalSlotBuildsTogetherAndSharesAnchor()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var runId = Guid.NewGuid();
            var key = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Formal, 42);
            var participants = new Dictionary<int, IReadOnlyList<int>>
            {
                [1] = new[] { 1, 2, 3 },
                [2] = new[] { 7, 8, 9 }
            };
            var calls = new ConcurrentDictionary<int, int>();
            var starts = new ConcurrentDictionary<int, DateTime>();
            var plannedBuildUtc = DateTime.UtcNow.AddMilliseconds(30);
            var wallClockUtc = DateTime.UtcNow;

            async Task<HydraulicCycleLease> Enter(int hydraulicId, IReadOnlyList<int> members,
                CancellationToken token)
            {
                calls.AddOrUpdate(hydraulicId, 1, (_, value) => value + 1);
                var buildStartedUtc = DateTime.UtcNow;
                starts[hydraulicId] = buildStartedUtc;
                await Task.Delay(hydraulicId == 1 ? 20 : 70, token).ConfigureAwait(false);
                var reachedUtc = DateTime.UtcNow;
                var qualification = new PressureQualification(
                    hydraulicId, 1, 70, 70, reachedUtc, 10, 69, 71, 70, 7,
                    buildStartedUtc);
                return new HydraulicCycleLease(
                    new HydraulicGenerationKey(runId, hydraulicId, HydraulicPhaseKind.Formal, 42),
                    members,
                    qualification,
                    reachedUtc,
                    Task.CompletedTask,
                    1);
            }

            var first = coordinator.EnterAsync(
                key, participants, plannedBuildUtc, wallClockUtc, 1000, 160, 20,
                Enter, (_, __) => Task.CompletedTask, CancellationToken.None);
            var second = coordinator.EnterAsync(
                key, participants, plannedBuildUtc, wallClockUtc, 1000, 160, 20,
                Enter, (_, __) => Task.CompletedTask, CancellationToken.None);
            Task.WaitAll(first, second);
            var result = first.Result;

            Assert(ReferenceEquals(first.Result, second.Result),
                "同一全局槽的等待者未复用唯一结果。");
            Assert(calls.Count == 2 && calls.All(pair => pair.Value == 1),
                "同一全局槽重复触发了液压建压。");
            Assert(Math.Abs((starts[1] - starts[2]).TotalMilliseconds) < 100,
                "双液压建压未从同一并发门发起。");
            Assert(!result.HasFailures && result.MotorAnchorUtc.HasValue &&
                   result.MotorDeadlineUtc.HasValue,
                "双液压成功后未生成公共电机窗口。");
            var lastQualified = result.Groups.Values.Max(item => item.QualifiedUtc.Value);
            Assert(result.MotorAnchorUtc.Value >= lastQualified.AddMilliseconds(20),
                "公共电机锚点早于最后一组达压加保护裕量。");
            Assert(result.MotorDeadlineUtc.Value >
                   result.MotorAnchorUtc.Value.AddMilliseconds(160),
                "公共截止点未覆盖尾批相位。");
        }

        private static void GlobalSlotMembershipIsImmutable()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var runId = Guid.NewGuid();
            var key = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Learning, 3);
            Task<HydraulicCycleLease> Enter(int hydraulicId, IReadOnlyList<int> members,
                CancellationToken token)
            {
                var now = DateTime.UtcNow;
                return Task.FromResult(new HydraulicCycleLease(
                    new HydraulicGenerationKey(runId, hydraulicId, HydraulicPhaseKind.Learning, 3),
                    members,
                    new PressureQualification(hydraulicId, 1, 70, 70, now, 0, 70, 70, 70, 7, now),
                    now,
                    Task.CompletedTask,
                    1));
            }

            var original = new Dictionary<int, IReadOnlyList<int>> { [1] = new[] { 1, 2 } };
            coordinator.EnterAsync(key, original, DateTime.UtcNow, DateTime.UtcNow,
                1000, 800, 10, Enter, (_, __) => Task.CompletedTask, CancellationToken.None)
                .GetAwaiter().GetResult();
            try
            {
                var changed = new Dictionary<int, IReadOnlyList<int>> { [1] = new[] { 1 } };
                coordinator.EnterAsync(key, changed, DateTime.UtcNow, DateTime.UtcNow,
                        1000, 800, 10, Enter, (_, __) => Task.CompletedTask, CancellationToken.None)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("同一全局槽接受了变化后的成员集合。");
            }
            catch (InvalidOperationException ex)
            {
                Assert(ex.Message.Contains("GlobalHydraulicSlotMembersImmutable"),
                    "成员变化未返回稳定的不可变错误码。");
            }
        }

        private static void FormalAdmissionWindowSkipsLateChannel()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var runId = Guid.NewGuid();
            var planned = DateTime.UtcNow;
            var wall = planned;
            Task<HydraulicCycleLease> Enter(
                int hydraulicId,
                IReadOnlyList<int> members,
                CancellationToken token)
            {
                var now = DateTime.UtcNow;
                return Task.FromResult(new HydraulicCycleLease(
                    new HydraulicGenerationKey(runId, hydraulicId, HydraulicPhaseKind.Formal, 50),
                    members,
                    new PressureQualification(
                        hydraulicId, 1, 70, 70, now, 0, 70, 70, 70, 7, now),
                    now,
                    Task.CompletedTask,
                    1));
            }

            var slot50 = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Formal, 50);
            var healthy = coordinator.JoinFormalAsync(
                slot50, 1, 5, 30, planned, wall, 1000, 800, 10,
                Enter, (_, __) => Task.CompletedTask,
                CancellationToken.None, CancellationToken.None);
            var healthyResult = healthy.GetAwaiter().GetResult();
            var late = coordinator.JoinFormalAsync(
                    slot50, 1, 4, 30, planned, wall, 1000, 800, 10,
                    Enter, (_, __) => Task.CompletedTask,
                    CancellationToken.None, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(healthyResult.Admitted && !late.Admitted &&
                   late.SkipReason == "AdmissionWindowClosed" &&
                   healthyResult.Slot.Groups[1].Participants.SequenceEqual(new[] { 5 }),
                "迟到EPB4仍污染当前正式槽，或健康EPB5被静态缺员阻塞");

            var next = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Formal, 51);
            var nextPlanned = planned.AddSeconds(1);
            var first = coordinator.JoinFormalAsync(
                next, 1, 4, 30, nextPlanned, nextPlanned, 1000, 800, 10,
                Enter, (_, __) => Task.CompletedTask,
                CancellationToken.None, CancellationToken.None);
            var second = coordinator.JoinFormalAsync(
                next, 1, 5, 30, nextPlanned, nextPlanned, 1000, 800, 10,
                Enter, (_, __) => Task.CompletedTask,
                CancellationToken.None, CancellationToken.None);
            Task.WaitAll(first, second);
            Assert(first.Result.Admitted && second.Result.Admitted,
                "当前槽迟到通道未能在下一正式槽重新准入");
        }

        private static void FormalPartialFailureKeepsHealthyLease()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var runId = Guid.NewGuid();
            var key = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Formal, 60);
            var planned = DateTime.UtcNow;
            var released = 0;
            Task<HydraulicCycleLease> Enter(
                int hydraulicId,
                IReadOnlyList<int> members,
                CancellationToken token)
            {
                if (hydraulicId == 2)
                    throw new HydraulicBuildException("FormalHydraulic2Failed");
                var now = DateTime.UtcNow;
                return Task.FromResult(new HydraulicCycleLease(
                    new HydraulicGenerationKey(runId, hydraulicId, HydraulicPhaseKind.Formal, 60),
                    members,
                    new PressureQualification(
                        hydraulicId, 1, 70, 70, now, 0, 70, 70, 70, 7, now),
                    now,
                    Task.CompletedTask,
                    1));
            }
            var group1 = coordinator.JoinFormalAsync(
                key, 1, 5, 20, planned, planned, 1000, 800, 10,
                Enter, (_, __) => { Interlocked.Increment(ref released); return Task.CompletedTask; },
                CancellationToken.None, CancellationToken.None);
            var group2 = coordinator.JoinFormalAsync(
                key, 2, 11, 20, planned, planned, 1000, 800, 10,
                Enter, (_, __) => { Interlocked.Increment(ref released); return Task.CompletedTask; },
                CancellationToken.None, CancellationToken.None);
            Task.WaitAll(group1, group2);
            var slot = group1.Result.Slot;
            Assert(slot.HasFailures && !slot.ShouldDeferHealthyGroups &&
                   slot.AlignmentState == "DegradedHealthyGroupsContinue" &&
                   slot.MotorAnchorUtc.HasValue && slot.GetLeaseOrThrow(1) != null &&
                   released == 0,
                "正式槽局部失败仍回退或释放了已成功的健康液压组");
        }

        private static void FormalJoinSkipRaceConvergesTenThousandTimes()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var runId = Guid.NewGuid();
            var key = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Formal, 70);
            var planned = DateTime.UtcNow;
            Task<HydraulicCycleLease> Enter(
                int hydraulicId,
                IReadOnlyList<int> members,
                CancellationToken token)
            {
                var now = DateTime.UtcNow;
                return Task.FromResult(new HydraulicCycleLease(
                    new HydraulicGenerationKey(runId, hydraulicId, HydraulicPhaseKind.Formal, 70),
                    members,
                    new PressureQualification(
                        hydraulicId, 1, 70, 70, now, 0, 70, 70, 70, 7, now),
                    now,
                    Task.CompletedTask,
                    1));
            }
            var admitted = coordinator.JoinFormalAsync(
                key, 1, 5, 20, planned, planned, 1000, 800, 10,
                Enter, (_, __) => Task.CompletedTask,
                CancellationToken.None, CancellationToken.None);
            Parallel.For(0, 10000, index =>
                coordinator.SkipFormalSlot(key, 4, "SyntheticRace" + (index % 3)));
            var result = admitted.GetAwaiter().GetResult();
            var skipped = coordinator.JoinFormalAsync(
                    key, 1, 4, 20, planned, planned, 1000, 800, 10,
                    Enter, (_, __) => Task.CompletedTask,
                    CancellationToken.None, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result.Admitted && !skipped.Admitted &&
                   result.Slot.Groups[1].Participants.SequenceEqual(new[] { 5 }),
                "一万次Join/Skip幂等竞态后槽未收敛或出现静态缺员");
        }

        private static void HydraulicRecoveryIntentMappingIsStrict()
        {
            var expected = new Dictionary<HydraulicPhaseKind, RecoveryTargetPhase>
            {
                [HydraulicPhaseKind.PreRelease] = RecoveryTargetPhase.Startup,
                [HydraulicPhaseKind.SingleChannel] = RecoveryTargetPhase.Startup,
                [HydraulicPhaseKind.Learning] = RecoveryTargetPhase.Learning,
                [HydraulicPhaseKind.Qualification] = RecoveryTargetPhase.Qualification,
                [HydraulicPhaseKind.Formal] = RecoveryTargetPhase.Formal
            };
            foreach (var pair in expected)
            {
                var intent = HydraulicRecoveryIntent.Create(
                    pair.Key,
                    Guid.NewGuid(),
                    9,
                    1,
                    new[] { 5, 4, 5 },
                    Guid.NewGuid(),
                    Guid.NewGuid());
                Assert(intent.SourcePhase == pair.Key && intent.TargetPhase == pair.Value &&
                       intent.Channels.SequenceEqual(new[] { 4, 5 }),
                    $"液压恢复来源阶段{pair.Key}映射错误");
            }
            try
            {
                HydraulicRecoveryIntent.Create(
                    HydraulicPhaseKind.Recovery,
                    Guid.NewGuid(), 1, 1, new[] { 4 }, Guid.NewGuid(), Guid.NewGuid());
                throw new InvalidOperationException("Recovery来源阶段未被拒绝");
            }
            catch (ArgumentException ex)
            {
                Assert(ex.ParamName == "sourcePhase", "Recovery来源拒绝未暴露稳定编程错误");
            }
        }

        private static void GlobalSlotFailureReleasesHealthyGroup()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var runId = Guid.NewGuid();
            var key = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Qualification, 8);
            var released = Array.Empty<int>();
            async Task<HydraulicCycleLease> Enter(int hydraulicId, IReadOnlyList<int> members,
                CancellationToken token)
            {
                await Task.Yield();
                if (hydraulicId == 2)
                    throw new HydraulicBuildException("P2BuildFailed");
                var now = DateTime.UtcNow;
                return new HydraulicCycleLease(
                    new HydraulicGenerationKey(runId, hydraulicId, HydraulicPhaseKind.Qualification, 8),
                    members,
                    new PressureQualification(hydraulicId, 1, 70, 70, now, 0, 70, 70, 70, 7, now),
                    now,
                    Task.CompletedTask,
                    1);
            }

            var result = coordinator.EnterAsync(
                    key,
                    new Dictionary<int, IReadOnlyList<int>>
                    {
                        [1] = new[] { 1, 2 },
                        [2] = new[] { 7, 8 }
                    },
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    1000,
                    800,
                    10,
                    Enter,
                    (channels, _) =>
                    {
                        released = channels.ToArray();
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert(result.HasFailures && result.ShouldDeferHealthyGroups &&
                   !result.MotorAnchorUtc.HasValue,
                "单组失败后仍生成了半槽电机锚点。");
            Assert(released.SequenceEqual(new[] { 1, 2 }),
                "降级槽未释放已成功建压的健康组成员。");
            try
            {
                result.GetLeaseOrThrow(2);
                throw new InvalidOperationException("失败液压组未重抛原始异常。");
            }
            catch (HydraulicBuildException ex)
            {
                Assert(ex.Message.Contains("P2BuildFailed"),
                    "失败液压组丢失原始异常语义。");
            }
        }

        private static void GlobalSlotCancellationIsNotHardwareFailure()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                coordinator.EnterAsync(
                        new GlobalHydraulicSlotKey(Guid.NewGuid(), HydraulicPhaseKind.Formal, 1),
                        new Dictionary<int, IReadOnlyList<int>>
                        {
                            [1] = new[] { 1 },
                            [2] = new[] { 7 }
                        },
                        DateTime.UtcNow,
                        DateTime.UtcNow,
                        1000,
                        0,
                        10,
                        (_, __, token) => Task.FromCanceled<HydraulicCycleLease>(token),
                        (_, __) => Task.CompletedTask,
                        cts.Token)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("整批取消被错误转换为降级液压结果。");
            }
            catch (OperationCanceledException)
            {
                // expected
            }
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

        private static void StopAllForceAbortDoesNotWaitForMissingMember()
        {
            var coordinator = NewCoordinator(
                () => 80.0,
                () => Task.CompletedTask,
                stableMs: 0,
                timeoutMs: 300,
                barrierTimeoutMs: 15000);
            var runId = Guid.NewGuid();
            var key = new HydraulicGenerationKey(runId, 2, HydraulicPhaseKind.Formal, 99);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 9 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            var first = coordinator.CreateChannelScope(lease, 8);
            var missing = coordinator.CreateChannelScope(lease, 9);
            var clock = Stopwatch.StartNew();
            var aborted = coordinator.ForceAbortRun(runId, "StopAllRegression");
            clock.Stop();
            var snapshot = coordinator.ProbeGroupHealth(2);
            Assert(aborted >= 3 && first.IsClosed && missing.IsClosed,
                "强制撤权没有终态化全部代次和通道租约");
            Assert(clock.ElapsedMilliseconds < 1000,
                "强制撤权仍等待了缺员液压屏障");
            Assert(snapshot.ActiveGenerationCount == 0 &&
                   snapshot.ActiveLeaseCount == 0 &&
                   snapshot.GenerationGateAvailable,
                "强制撤权后液压软件所有权未收敛：" + snapshot);
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

        private static void ForceReleasedScopeRetirementIsExact()
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
                timeoutMs: 300);
            var key = new HydraulicGenerationKey(
                Guid.NewGuid(),
                2,
                HydraulicPhaseKind.Formal,
                77);
            var lease = coordinator.EnterGenerationAsync(
                    key,
                    new[] { 8 },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            var scope = coordinator.CreateChannelScope(lease, 8);
            Assert(!coordinator.TryRetireForceReleasedScope(
                    scope,
                    2,
                    "exact-release"),
                "活动代次租约在ForceRelease前被错误退役。");

            coordinator.ForceReleaseAsync(2, "exact-release")
                .GetAwaiter().GetResult();
            Assert(!coordinator.TryRetireForceReleasedScope(
                    scope,
                    2,
                    "different-release"),
                "不匹配的ForceRelease异常被错误吞掉。");
            Assert(!coordinator.TryRetireForceReleasedScope(
                    scope,
                    1,
                    "exact-release"),
                "不匹配液压组的旧租约被错误退役。");
            Assert(coordinator.TryRetireForceReleasedScope(
                    scope,
                    2,
                    "exact-release") &&
                   scope.IsClosed,
                "匹配本次已完成ForceRelease的旧租约未能安全退役。");
            Assert(!coordinator.TryRetireForceReleasedScope(
                    scope,
                    2,
                    "exact-release"),
                "同一旧租约被重复退役。");
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
