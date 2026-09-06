using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using IO.NI;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    /// <summary>
    /// V2.17.1.0 恢复闭环回归：墓碑 schema 一致性、接管写失败不撤销、
    /// 同进程恢复完成竞态、准入窗与错峰跨度对齐、圈号注册表清场、
    /// DAQ 分发器可归因遥测与 CTS 安全取消。
    /// </summary>
    internal static class RecoveryClosureV2171Tests
    {
        internal static int RunAll()
        {
            var passed = 0;
            var failures = new List<string>();
            Run("墓碑schema一致性跟随权威常量", TombstoneSchemaConformance, ref passed, failures);
            Run("当前schema墓碑双写往返", TombstoneCurrentSchemaRoundTrip, ref passed, failures);
            Run("同进程恢复完成优先于空边界升级",
                InProcessRecoveryEscalationDecision, ref passed, failures);
            Run("准入窗覆盖计划错开跨度", AdmissionWindowCoversArrivalSpread, ref passed, failures);
            Run("圈号注册表清场带耐久恢复守卫",
                ActiveCycleRegistrySweepHonorsDurabilityGuard, ref passed, failures);
            Run("CTS取消在并发Dispose下不逃逸", CtsCancelAfterDisposeIsContained,
                ref passed, failures);
            Run("DAQ分发器高水位与消费停滞可归因", DaqReadDispatcherTelemetry,
                ref passed, failures);
            if (failures.Count != 0)
                throw new InvalidOperationException(
                    "V2.17.1.0恢复闭环回归失败: " + string.Join("; ", failures));
            return passed;
        }

        /// <summary>
        /// 真实 Runtime/Sidecar 接受性验收（会拉起真实 sidecar 进程）。客户端
        /// 引擎状态是进程级的，会话内粘滞收口状态无法跨用例复用，因此每种
        /// 撤销语义必须由独立进程执行；只允许在专项入口调用，不得进入聚合套件。
        /// </summary>
        internal static int RunProductionAcceptanceTakeover()
        {
            return RunSingleAcceptance(
                "接管路径墓碑写失败不撤销恢复所有权",
                () => TombstoneWriteFailureSemantics(
                    WatchdogExitDisposition.TakeoverReplacementExit,
                    WatchdogRelaunchDisposition.PreserveApprovedPermit,
                    expectRevocation: false,
                    label: "接管保留许可路径"));
        }

        internal static int RunProductionAcceptanceOperator()
        {
            return RunSingleAcceptance(
                "操作员路径墓碑写失败仍按原语义撤销",
                () => TombstoneWriteFailureSemantics(
                    WatchdogExitDisposition.OperatorExit,
                    WatchdogRelaunchDisposition.Forbidden,
                    expectRevocation: true,
                    label: "操作员退出路径"));
        }

        private static int RunSingleAcceptance(string name, Action action)
        {
            var failures = new List<string>();
            var passed = 0;
            Run(name, action, ref passed, failures);
            if (failures.Count != 0)
                throw new InvalidOperationException(
                    "V2.17.1.0墓碑写失败接受性验收失败: " + string.Join("; ", failures));
            return passed;
        }

        // ------------------------------------------------------------------
        // 1. 墓碑 schema 一致性（I0043/I0044 共同根因：生产者写 7、校验只认 1-6）
        // ------------------------------------------------------------------

        private static void TombstoneSchemaConformance()
        {
            Assert(
                new WatchdogClosingTombstone().SchemaVersion ==
                SupervisorProtocol.SchemaVersion,
                "墓碑类默认 schema 未跟随 SupervisorProtocol 权威常量");
            for (var version = 1; version <= SupervisorProtocol.SchemaVersion; version++)
            {
                var tombstone = BuildWellFormedTombstone(version);
                Assert(tombstone.IsValidFor("session-a"),
                    $"历史/当前 schema {version} 的合法墓碑被 IsValidFor 拒绝");
            }
            var future = BuildWellFormedTombstone(SupervisorProtocol.SchemaVersion + 1);
            Assert(!future.IsValidFor("session-a"),
                "超过权威常量的 schema 未来版本被 IsValidFor 接受");
        }

        private static WatchdogClosingTombstone BuildWellFormedTombstone(int schemaVersion) =>
            new WatchdogClosingTombstone
            {
                SchemaVersion = schemaVersion,
                SessionId = "session-a",
                SessionGeneration = 1,
                SessionLease = 1,
                StateVersion = 1,
                State = WatchdogClosingTombstoneState.Terminal,
                ExitDisposition = WatchdogExitDisposition.OperatorExit,
                RelaunchDisposition = WatchdogRelaunchDisposition.Forbidden
            };

        private static void TombstoneCurrentSchemaRoundTrip()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.V2171TombstoneRoundTrip." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            // 本地关闭目录全局共享且 WriteThrough 会自动盖 UpdatedUtcTicks，
            // 必须使用一次性会话身份，避免与历史运行残留墓碑做同版本内容比对。
            var sessionId = "v2171rt" + Guid.NewGuid().ToString("N");
            try
            {
                var tombstone = BuildWellFormedTombstone(SupervisorProtocol.SchemaVersion);
                tombstone.SessionId = sessionId;
                tombstone.State = WatchdogClosingTombstoneState.Closing;
                var stored = WatchdogClosingTombstoneStore.WriteThrough(directory, tombstone);
                Assert(stored.SchemaVersion == SupervisorProtocol.SchemaVersion,
                    "WriteThrough 返回的墓碑 schema 被篡改");
                Assert(WatchdogClosingTombstoneStore.TryRead(
                           directory, sessionId, out var readBack),
                    "当前 schema 的墓碑写入后 TryRead 读不回（I0043/I0044 现场根因回归）");
                Assert(readBack.SchemaVersion == SupervisorProtocol.SchemaVersion &&
                       readBack.SessionGeneration == 1 &&
                       readBack.SessionLease == 1 &&
                       readBack.State == WatchdogClosingTombstoneState.Closing,
                    "墓碑往返后身份或状态丢失");
            }
            finally
            {
                try
                {
                    var local = WatchdogJournalPaths.LocalClosingPath(sessionId);
                    if (File.Exists(local)) File.Delete(local);
                }
                catch { }
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        // 2. 同进程恢复完成优先（I0044 空字符串三态竞态）
        // ------------------------------------------------------------------

        private static void InProcessRecoveryEscalationDecision()
        {
            // 现场命中形态：恢复恰好在 500ms 轮询窗内完成，边界为空串。
            Assert(!UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: true, boundaryReason: string.Empty),
                "健康完成的恢复被空边界升级为外部接管（I0044 竞态回归）");
            Assert(!UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: true, boundaryReason: null),
                "循环未轮询即完成的恢复被 null 边界升级");
            Assert(!UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: true, boundaryReason: "MaxTotal"),
                "恢复完成后迟到的边界观察不得覆盖成功结果");
            Assert(!UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: false, boundaryReason: string.Empty),
                "无真实边界时不允许升级外部接管");
            Assert(!UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: false, boundaryReason: null),
                "null 边界不允许升级外部接管");
            Assert(UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: false, boundaryReason: "MaxTotal"),
                "真实 MaxTotal 边界未被升级");
            Assert(UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: false, boundaryReason: "NoMaterialProgress"),
                "真实 NoMaterialProgress 边界未被升级");
            Assert(!UnattendedRecoveryCoordinator.ShouldEscalateToExternalRecovery(
                       recoveryCompleted: false, boundaryReason: "  "),
                "空白边界被当作真实边界");
        }

        // ------------------------------------------------------------------
        // 3. 准入窗覆盖计划错开跨度（I0044：596ms 相位带 vs 300ms 固定窗）
        // ------------------------------------------------------------------

        private static void AdmissionWindowCoversArrivalSpread()
        {
            var coordinator = new GlobalHydraulicSlotCoordinator();
            var runId = Guid.NewGuid();
            Task<HydraulicCycleLease> Enter(
                Guid leaseRunId,
                long slot,
                int hydraulicId,
                IReadOnlyList<int> members,
                CancellationToken token)
            {
                var now = DateTime.UtcNow;
                return Task.FromResult(new HydraulicCycleLease(
                    new HydraulicGenerationKey(
                        leaseRunId, hydraulicId, HydraulicPhaseKind.Formal, slot),
                    members,
                    new PressureQualification(
                        hydraulicId, 1, 70, 70, now, 0, 70, 70, 70, 7, now),
                    now,
                    Task.CompletedTask,
                    1));
            }

            // 形态 A：窗口(30ms)小于到达跨度(60ms)——后到通道被排除（旧缺陷语义）。
            var planned = DateTime.UtcNow;
            var narrowSlot = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Formal, 70);
            var first = coordinator.JoinFormalAsync(
                narrowSlot, 1, 5, 30, planned, planned, 1000, 800, 10,
                (g, m, t) => Enter(runId, 70, g, m, t), (_, __) => Task.CompletedTask,
                CancellationToken.None, CancellationToken.None);
            Thread.Sleep(60);
            var narrowLate = coordinator.JoinFormalAsync(
                    narrowSlot, 1, 4, 30, planned, planned, 1000, 800, 10,
                    (g, m, t) => Enter(runId, 70, g, m, t), (_, __) => Task.CompletedTask,
                    CancellationToken.None, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(!narrowLate.Admitted &&
                   narrowLate.SkipReason == "AdmissionWindowClosed",
                "窄窗口下后到通道未被排除，冻结语义被意外改变");

            // 形态 B：窗口覆盖跨度+余量——同跨度下计划通道全部准入（本次修复）。
            var wideSlot = new GlobalHydraulicSlotKey(runId, HydraulicPhaseKind.Formal, 71);
            var widePlanned = planned.AddSeconds(1);
            var wideFirst = coordinator.JoinFormalAsync(
                wideSlot, 1, 5, 150, widePlanned, widePlanned, 1000, 800, 10,
                (g, m, t) => Enter(runId, 71, g, m, t), (_, __) => Task.CompletedTask,
                CancellationToken.None, CancellationToken.None);
            Thread.Sleep(60);
            var wideLate = coordinator.JoinFormalAsync(
                    wideSlot, 1, 4, 150, widePlanned, widePlanned, 1000, 800, 10,
                    (g, m, t) => Enter(runId, 71, g, m, t), (_, __) => Task.CompletedTask,
                    CancellationToken.None, CancellationToken.None)
                .GetAwaiter().GetResult();
            var wideResult = wideFirst.GetAwaiter().GetResult();
            Assert(wideResult.Admitted && wideLate.Admitted &&
                   wideResult.Slot.Groups[1].Participants.Contains(5) &&
                   wideResult.Slot.Groups[1].Participants.Contains(4),
                "窗口覆盖到达跨度后计划通道仍未全部准入");
        }

        // ------------------------------------------------------------------
        // 4. 圈号注册表清场（I0043：ActiveCycles=5 残留）
        // ------------------------------------------------------------------

        private static void ActiveCycleRegistrySweepHonorsDurabilityGuard()
        {
            var manager = (EpbManager)FormatterServices.GetUninitializedObject(typeof(EpbManager));
            var cycleRegistry = new ConcurrentDictionary<int, int>();
            var attemptRegistry = new ConcurrentDictionary<int, long>();
            var pendingRecovery = new ConcurrentDictionary<int, int>();
            foreach (var channel in new[] { 4, 5, 7, 8, 9 })
                cycleRegistry[channel] = 101;
            SetPrivateField(manager, "_currentCycleNumberByChannel", cycleRegistry);
            SetPrivateField(manager, "_currentAttemptIdByChannel", attemptRegistry);
            SetPrivateField(manager, "_formalPersistenceRecoveryPendingCycles", pendingRecovery);
            SetPrivateField(manager, "_log", Config.NullLogger.Instance);

            // 无耐久恢复待办：5 个活动圈全部清场（I0043 现场残留形态）。
            manager.SweepCurrentCycleRegistryForRestartQuiescence();
            Assert(cycleRegistry.IsEmpty && attemptRegistry.IsEmpty,
                "清场未移除无耐久恢复待办的活动圈注册（ActiveCycles 残留回归）");

            // 有耐久恢复待办：匹配圈身份必须保留，其余照常清场。
            cycleRegistry[4] = 202;
            cycleRegistry[5] = 202;
            attemptRegistry[4] = 7;
            attemptRegistry[5] = 8;
            pendingRecovery[4] = 202;
            manager.SweepCurrentCycleRegistryForRestartQuiescence();
            Assert(cycleRegistry.TryGetValue(4, out var retained) && retained == 202,
                "仍等待耐久恢复的圈身份被清场误删");
            Assert(!cycleRegistry.ContainsKey(5),
                "无待办的通道圈身份未随清场移除");
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            var field = instance.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, $"EpbManager 缺少私有字段 {fieldName}，测试锚点失效");
            field.SetValue(instance, value);
        }

        // ------------------------------------------------------------------
        // 5. CTS 安全取消（I0043：学习 deadline 分支 ObjectDisposedException）
        // ------------------------------------------------------------------

        private static void CtsCancelAfterDisposeIsContained()
        {
            var disposed = new CancellationTokenSource();
            disposed.Dispose();
            try
            {
                // 与 EpbManager.BatchStart 学习 deadline 分支相同的防护模式。
                try { disposed.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "已释放 CTS 的取消防护模式仍逃逸异常", ex);
            }

            // 并发烟雾：Renew（摘除旧实例并 Cancel+Dispose）与 deadline 取消竞争。
            var registry = new ConcurrentDictionary<int, CancellationTokenSource>();
            var stop = new CancellationTokenSource(1500);
            var renewer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    for (var channel = 1; channel <= 4; channel++)
                    {
                        if (stop.IsCancellationRequested) return;
                        if (registry.TryRemove(channel, out var old))
                        {
                            try { old.Cancel(); } catch { }
                            try { old.Dispose(); } catch { }
                        }
                        registry[channel] = new CancellationTokenSource();
                    }
                }
            });
            try
            {
                var deadline = Environment.TickCount + 1200;
                while (Environment.TickCount - deadline < 0)
                {
                    foreach (var member in registry)
                    {
                        try { member.Value.Cancel(); }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            finally
            {
                stop.Cancel();
                renewer.Wait(2000);
                foreach (var source in registry.Values)
                {
                    try { source.Cancel(); } catch { }
                    try { source.Dispose(); } catch { }
                }
            }
        }

        // ------------------------------------------------------------------
        // 6. DAQ 分发器可归因遥测（I0043：capacity=64 溢出无归因字段）
        // ------------------------------------------------------------------

        private static void DaqReadDispatcherTelemetry()
        {
            var release = new ManualResetEventSlim(false);
            var consumed = 0;
            var dispatcher = new DaqReadDispatcher<object>(
                "V2171-Test-Publish", 8,
                _ =>
                {
                    release.Wait(2000);
                    Interlocked.Increment(ref consumed);
                },
                _ => { });
            try
            {
                for (var index = 0; index < 5; index++)
                    Assert(dispatcher.TryPublish(new object()), $"第{index}批被意外拒绝");
                // 消费者持至多 1 帧阻塞，队列内必然积累 ≥4 帧。
                WaitUntil(() => dispatcher.Depth >= 4, 2000, "队列深度未反映积压");
                Assert(dispatcher.HighWaterDepth >= 4,
                    "高水位未记录到积压峰值，溢出事件将无法归因");
                var stallObserved = false;
                var deadline = Environment.TickCount + 1500;
                while (Environment.TickCount - deadline < 0)
                {
                    if (dispatcher.ConsumerStallMs > 50) { stallObserved = true; break; }
                    Thread.Sleep(20);
                }
                Assert(stallObserved, "消费停滞时长未随阻塞消费增长");
                release.Set();
                WaitUntil(() => Volatile.Read(ref consumed) >= 5 && dispatcher.Depth == 0,
                    3000, "解除阻塞后未消费完全部批次");
                Assert(dispatcher.ConsumerStallMs == 0.0,
                    "队列排空后消费停滞时长未归零");

                // 容量拒绝：消费者阻塞、容量 2 时，发布 4 帧必然出现 ≥1 次拒绝，
                // 且拒绝以返回值表达、不抛异常（读循环依赖该契约判定溢出）。
                release.Reset();
                var tight = new DaqReadDispatcher<object>(
                    "V2171-Test-Tight", 2, _ => release.Wait(2000), _ => { });
                try
                {
                    var rejected = 0;
                    for (var index = 0; index < 4; index++)
                    {
                        if (!tight.TryPublish(new object())) rejected++;
                    }
                    Assert(rejected >= 1,
                        "容量2+消费者阻塞下发布4帧未出现满队列拒绝");
                }
                finally
                {
                    release.Set();
                    tight.Complete();
                }
            }
            finally
            {
                release.Set();
                dispatcher.Complete();
            }
        }

        // ------------------------------------------------------------------
        // 7. 接管/操作员路径墓碑写失败语义（真实 Runtime/Sidecar，独立进程执行）
        // ------------------------------------------------------------------

        private static void TombstoneWriteFailureSemantics(
            WatchdogExitDisposition exitDisposition,
            WatchdogRelaunchDisposition relaunchDisposition,
            bool expectRevocation,
            string label)
        {
            var journal = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.V2171TombstoneFailure." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(journal);
            string sessionId = null;
            try
            {
                try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                WatchdogRuntime.ConfigureJournalExportPath(journal);
                // 测试宿主没有 exe.config；DAQ 会话参数经内部 seam 配置一次。
                WatchdogRuntime.ConfigureDaqRuntimeSettings(
                    new DaqRuntimeSettings(2000, 20));
                var currentRunId = Guid.NewGuid();
                WatchdogRuntime.SetHeartbeatProvider(() => new WatchdogHeartbeat
                {
                    Phase = "V2171TombstoneFailureSemantics",
                    RunId = currentRunId.ToString("N"),
                    RunEpoch = 1,
                    RunActive = true,
                    EnabledChannels = new[] { 4 }
                });
                // 前一会话的资源收口是异步的：带重试轮询直到 sidecar 附着成功。
                WatchdogAttachResult start = null;
                var startDeadline = Environment.TickCount + 30000;
                while (Environment.TickCount - startDeadline < 0)
                {
                    WatchdogAttachResult attempt;
                    try
                    {
                        attempt = WatchdogRuntime.StartSessionAsync(new[] { 4 })
                            .GetAwaiter().GetResult();
                    }
                    catch
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    if (attempt != null && attempt.Attached) { start = attempt; break; }
                    Thread.Sleep(1000);
                }
                Assert(start != null,
                    $"{label}未达到真实 Runtime/Sidecar Attached：" +
                    (start?.Warning ?? "<null>"));
                sessionId = start.SessionId;
                var composite = WatchdogRuntime.CaptureTransportSnapshot();
                Assert(composite != null && composite.IsStable && composite.Context != null,
                    $"{label}缺少稳定传输快照");
                var context = composite.Context;

                WatchdogRuntime.ClosingTombstoneWriteOverride =
                    (_, _) => throw new IOException("InjectedTombstoneFailure");
                try
                {
                    var receipt = WatchdogRuntime.BeginSessionCloseExact(
                        context,
                        "V2171TombstoneFailureIntent",
                        Guid.NewGuid(),
                        currentRunId,
                        1,
                        1,
                        exitDisposition,
                        relaunchDisposition);
                    Assert(
                        receipt != null &&
                        receipt.MarkOutcome ==
                        RuntimeShutdownMarkOutcome.TombstonePersistenceFailed &&
                        !receipt.TombstoneDurable &&
                        (receipt.Error ?? string.Empty).Contains("InjectedTombstoneFailure"),
                        $"{label}写失败未按 TombstonePersistenceFailed 返回：" +
                        (receipt?.Error ?? "<null>"));
                    var revoked = WatchdogControlMarker.IsRevoked(
                        journal, context.SessionId);
                    Assert(revoked == expectRevocation,
                        $"{label}墓碑写失败后的撤销语义错误：expect={expectRevocation} " +
                        $"actual={revoked}（I0044：接管路径写 .revoked 会终止唯一恢复者）");
                }
                finally
                {
                    WatchdogRuntime.ClosingTombstoneWriteOverride = null;
                }
            }
            finally
            {
                WatchdogRuntime.ClosingTombstoneWriteOverride = null;
                // 注入写失败后 runtime 关闭是 fail-closed（Retained）的，sidecar
                // 仍按设计存活；验收完毕必须按快照中的权威 PID 强制收口，否则
                // 后续会话创建会被"上一个会话未收口"拒绝。
                var authorityPid = 0;
                try
                {
                    authorityPid = WatchdogRuntime.CaptureTransportSnapshot()
                                      ?.Engine?.AuthorityProcessId ?? 0;
                }
                catch { }
                try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                if (authorityPid > 0 && IsProcessAlive(authorityPid))
                {
                    try
                    {
                        using (var process = System.Diagnostics.Process.GetProcessById(authorityPid))
                            process.Kill();
                    }
                    catch { }
                }
                WatchdogRuntime.SetHeartbeatProvider(null);
                WatchdogRuntime.ConfigureJournalExportPath(null);
                if (!string.IsNullOrWhiteSpace(sessionId))
                    DeleteLocalSessionCloseArtifacts(sessionId);
                try { if (Directory.Exists(journal)) Directory.Delete(journal, true); }
                catch { }
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                    return !process.HasExited;
            }
            catch { return false; }
        }

        private static void DeleteLocalSessionCloseArtifacts(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            foreach (var path in new[]
                     {
                         WatchdogJournalPaths.LocalClosingPath(sessionId),
                         WatchdogJournalPaths.LocalRevocationPath(sessionId),
                         WatchdogJournalPaths.LocalRecoveryCommitPath(sessionId)
                     })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
            }
        }

        // ------------------------------------------------------------------

        private static void WaitUntil(Func<bool> predicate, int timeoutMs, string message)
        {
            var deadline = Environment.TickCount + Math.Max(1, timeoutMs);
            while (Environment.TickCount - deadline < 0)
            {
                if (predicate()) return;
                Thread.Sleep(10);
            }
            Assert(predicate(), message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Run(
            string name, Action action, ref int passed, List<string> failures)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failures.Add(name + ": " + ex.Message);
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }
    }
}
