using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using DataOperation;
using System.Buffers;
using System.Reflection;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class V217StabilityTests
    {
        internal static int RunAll()
        {
            HashIdentityUsesValidatedBytes();
            ConcurrentPublicationCannotBeFromTheFuture();
            InvalidOrOldSamplesRemainRejected();
            RecoveryCannotCleanupARejoinedPeer();
            RetirementCannotRevokeSuccessorPermit();
            DurableRestartWindowsKeepRetrying();
            BusinessRecoveryRequiresActualCommittedCycle();
            PipePeerCannotBlockExchangeForever();
            RawSuppressionStillDrainsAcceptedTail();
            MechanicalProgressIsNotConfigurationIdentity();
            ClosingSharesPhysicalStopAndFinishesPersistence();
            Console.WriteLine("PASS V217 11/11 哈希、新鲜度、代次、冷却及实际圈验证");
            return 11;
        }

        private static void ClosingSharesPhysicalStopAndFinishesPersistence()
        {
            var root = Path.Combine(Path.GetTempPath(), "V217-Close-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var writer = new EpbDiskWriter(new DataRetentionPolicy
                { DataStorePath = Path.Combine(root, "data"), IndexAndExportPath = Path.Combine(root, "index"), FileSizeMb = 1 });
                var recorder = new DiskWriterRecorderAdapter(writer);
                using var queue = new DaqPersistenceCoordinator(() => recorder, Config.NullLogger.Instance,
                    8, 4, 1, 1000, 100, 2000, 1);
                var manager = (EpbManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(EpbManager));
                typeof(EpbManager).GetField("_persistence", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, queue);
                var close = typeof(EpbManager).GetMethod("CompleteStopRequestForSource", BindingFlags.Instance | BindingFlags.NonPublic);
                var physical = new TaskCompletionSource<StopSafetyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var first = (Task<StopSafetyResult>)close.Invoke(manager, new object[] { physical.Task, StopSource.ApplicationClosing });
                var duplicate = (Task<StopSafetyResult>)close.Invoke(manager, new object[] { physical.Task, StopSource.ProgramExit });
                Assert(ReferenceEquals(first, duplicate) && !first.IsCompleted, "重复关闭未等待同一物理停止");
                var id = Guid.NewGuid();
                physical.SetResult(new StopSafetyResult { SafetyTransactionId = id, Source = StopSource.ManualUi, PersistenceBoundaryConfirmed = true });
                Assert(first.Wait(3000) && first.Result.SafetyTransactionId == id && first.Result.PersistenceBoundaryConfirmed,
                    "数据收尾改写了物理停止身份或没有完成");
                var disposed = typeof(DaqPersistenceCoordinator).GetField("_resourcesDisposed", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert((int)disposed.GetValue(queue) == 1, "复用人工停止结果后漏掉最终写盘器关闭");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void RawSuppressionStillDrainsAcceptedTail()
        {
            var root = Path.Combine(Path.GetTempPath(), "V217-Tail-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var writer = new EpbDiskWriter(new DataRetentionPolicy
                { DataStorePath = Path.Combine(root, "data"), IndexAndExportPath = Path.Combine(root, "index"), FileSizeMb = 1 });
                var recorder = new DiskWriterRecorderAdapter(writer);
                using var queue = new DaqPersistenceCoordinator(() => recorder, Config.NullLogger.Instance,
                    16, 8, 2, 1000, 100, 2000, 1);
                writer.BeginCycleAtDaqBoundary(4, 401, DateTime.UtcNow, "Dev1", 1, 0);
                queue.SuppressAfter("Dev1", DateTime.UtcNow, 1, Guid.NewGuid());
                for (var seq = 1; seq <= 3; seq++)
                {
                    var times = ArrayPool<DateTime>.Shared.Rent(1); times[0] = DateTime.UtcNow;
                    var currents = ArrayPool<double>.Shared.Rent(1); currents[0] = seq;
                    var pressure = ArrayPool<double>.Shared.Rent(1); pressure[0] = 0;
                    Assert(queue.Enqueue(new DaqDiskBatch("Dev1", 1, seq, 1, times,
                        new[] { new DaqDiskChannelBatch(4, currents) }, pressure, null, Stopwatch.GetTimestamp())), "尾批未接收");
                }
                var result = queue.WaitForDurablePrefixDetailedAsync("Dev1", 3, 3000, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Completed && result.Persisted == 3 && writer.GetCurrentCycleSampleCount(4) == 3,
                    "控制截止静默跳过原始尾批或制造空队列边界等待");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void MechanicalProgressIsNotConfigurationIdentity()
        {
            var path = Path.Combine(Path.GetTempPath(), "V217-Config-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var type = typeof(MTEmbTest.FrmEpbMainMonitor).Assembly.GetType("MTEmbTest.UnattendedRunCheckpointStore", true);
                var read = type.GetMethod("ReadStableConfiguration", BindingFlags.NonPublic | BindingFlags.Static);
                string Xml(int count, bool enabled) => "<Test><EpbRecords><Record><Id>4</Id><Enabled>" + enabled +
                    "</Enabled><TotalCount>200000</TotalCount><MechanicalCycleCount>" + count +
                    "</MechanicalCycleCount></Record></EpbRecords></Test>";
                File.WriteAllText(path, Xml(100, true)); var first = (byte[])read.Invoke(null, new object[] { path });
                File.WriteAllText(path, Xml(101, true)); var next = (byte[])read.Invoke(null, new object[] { path });
                Assert(first.SequenceEqual(next), "机械次数变化导致恢复配置身份拒绝");
                File.WriteAllText(path, Xml(101, false)); var changed = (byte[])read.Invoke(null, new object[] { path });
                Assert(!first.SequenceEqual(changed), "启用集合变化被剔除身份验证");
            }
            finally { File.Delete(path); }
        }

        private static void PipePeerCannotBlockExchangeForever()
        {
            foreach (var write in new[] { false, true })
            {
                var name = "EPB-V217-Test-" + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
                var accepted = server.WaitForConnectionAsync();
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(2000);
                Assert(accepted.Wait(2000), "管道测试服务未连接");
                var task = Task.Run(() =>
                {
                    try
                    {
                        using var deadline = new PipeExchangeDeadline(client, 100);
                        if (write) { var bytes = new byte[8 * 1024 * 1024]; client.Write(bytes, 0, bytes.Length); }
                        else client.ReadByte();
                        return false;
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException)
                    { return true; }
                });
                Assert(task.Wait(3000) && task.Result, "不读/不答的对端使管道无限阻塞");
            }
        }

        private static void DurableRestartWindowsKeepRetrying()
        {
            var directory = Path.Combine(Path.GetTempPath(), "V217-Restart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "window.json");
                var now = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc);
                for (var window = 0; window < 4; window++)
                {
                    for (var attempt = 0; attempt < 3; attempt++)
                        Assert(new RecoveryRestartWindowStore(path).TryReserve(Guid.NewGuid().ToString("N"),
                            now.AddMinutes(window * 10).AddSeconds(attempt), out _), "新窗口未恢复重试额度");
                    Assert(!new RecoveryRestartWindowStore(path).TryReserve(Guid.NewGuid().ToString("N"),
                        now.AddMinutes(window * 10).AddSeconds(3), out var due), "监督进程重启绕过额度");
                    Assert(due == now.AddMinutes((window + 1) * 10), "冷却不是等待最早记录移出窗口");
                }
                Assert(ContinuousRecoveryPolicy.CanRetrySoftwareFailure("LearningWorkerHung", false), "未知软件错误永久阻断");
                Assert(!ContinuousRecoveryPolicy.CanRetrySoftwareFailure("ManualStop", false) &&
                    !ContinuousRecoveryPolicy.CanRetrySoftwareFailure("PressureHigh", true), "人工或硬件锁被软件冷却解除");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }

        private static void BusinessRecoveryRequiresActualCommittedCycle()
        {
            var run = Guid.NewGuid();
            using var attempt = new CycleAttemptContext(run, 7, "Dev1", 4, 11, CycleAttemptKind.FormalRecovery, 1);
            Assert(!EpbManager.IsVerifiedBusinessRejoin(run, 7, 10, attempt, true), "只有动作没有数据就发布恢复成功");
            attempt.CompleteOnce(() => true, _ => { });
            Assert(EpbManager.IsVerifiedBusinessRejoin(run, 7, 10, attempt, true), "有效圈提交后仍未确认恢复");
            Assert(!EpbManager.IsVerifiedBusinessRejoin(run, 8, 10, attempt, true) &&
                !EpbManager.IsVerifiedBusinessRejoin(run, 7, 11, attempt, true), "旧执行代次或旧尝试确认了新恢复");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("V217: " + message);
        }

        private static void HashIdentityUsesValidatedBytes()
        {
            const string hash = "b7719b967cdc42b780422fbd4d7bc45fac122ee4f9e914672bd6e253afeca206";
            Assert(SupervisorProtocol.Sha256Equals(hash, hash.ToUpperInvariant()), "等价SHA被大小写拒绝");
            Assert(!SupervisorProtocol.Sha256Equals(hash, "0" + hash.Substring(1)), "不同程序被接受");
            Assert(!SupervisorProtocol.Sha256Equals(hash, hash + " "), "非规范长度被接受");
            Assert(!SupervisorProtocol.Sha256Equals(new string('g', 64), new string('g', 64)), "非法十六进制被接受");
            Assert(!SupervisorProtocol.Sha256Equals(null, null), "缺少身份被接受");
        }

        private static FastControlBatchMetadata Batch(long generation, long capture) =>
            new FastControlBatchMetadata(generation, 100, DateTime.UtcNow, DateTime.UtcNow,
                capture, capture, 0, 2000, ClockState.WarmingUp, 0, 0, 1, 1,
                FastSignalQualityFlags.None, 0, 10, DaqReaderLagState.Healthy);

        private sealed class PublicationSlot
        {
            internal DaqControlPublication Value;
            internal int Stop;
        }

        private static void ConcurrentPublicationCannotBeFromTheFuture()
        {
            var slot = new PublicationSlot();
            var producer = Task.Run(() =>
            {
                while (Volatile.Read(ref slot.Stop) == 0)
                {
                    var tick = Stopwatch.GetTimestamp();
                    Volatile.Write(ref slot.Value, new DaqControlPublication(Batch(1, tick), tick));
                }
            });
            var observed = 0;
            try
            {
                Assert(SpinWait.SpinUntil(() => Volatile.Read(ref slot.Value) != null, 3000), "发布线程未启动");
                for (var i = 0; i < 100000; i++)
                {
                    var publication = DaqControlPublication.Capture(ref slot.Value, out var now);
                    var snapshot = new DaqFreshnessSnapshot();
                    publication.Apply(snapshot, 1, now, 100);
                    Assert(snapshot.LastControlProcessedMonotonicTicks <= now &&
                        !double.IsInfinity(snapshot.ControlProcessedAgeMs), "新批次被读成无限年龄");
                    observed++;
                }
            }
            finally
            {
                Volatile.Write(ref slot.Stop, 1);
                Assert(producer.Wait(3000), "发布线程没有退出");
            }
            Assert(observed == 100000, "并发快照观察数量不足");
        }

        private static void InvalidOrOldSamplesRemainRejected()
        {
            var now = Stopwatch.GetTimestamp();
            var old = now - Stopwatch.Frequency;
            var snapshot = new DaqFreshnessSnapshot { CallbackAgeMs = 1000, ControlEnqueueAgeMs = 1000 };
            new DaqControlPublication(Batch(1, old), old).Apply(snapshot, 1, now, 100);
            Assert(!snapshot.IsFresh && !new DaqLivenessDeviceState().Observe(true, true, false,
                snapshot, 250, 1500, 5000).Trip, "控制数据仍须拒绝陈旧样本，存活监督应按旧版阈值观察");
            new DaqControlPublication(Batch(1, now), now).Apply(snapshot, 2, now, 100);
            Assert(!snapshot.IsFresh && snapshot.RejectionReason == "GenerationMismatch", "旧代次允许上电");
            new DaqControlPublication(Batch(1, now), now + 1).Apply(snapshot, 1, now, 100);
            Assert(!snapshot.IsFresh, "非法未来时钟被强制当新鲜");
            snapshot.RejectionReason = "SnapshotGenerationChanged";
            Assert(!new DaqLivenessDeviceState().Observe(true, true, false, snapshot, 75, 100, 250).Trip,
                "跨代无效快照触发设备重建");
        }

        private static void RecoveryCannotCleanupARejoinedPeer()
        {
            var run = Guid.NewGuid();
            var owner = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var contract = new RecoveryContractSnapshot(owner, run, 1, owner,
                RecoveryOwnerKind.DaqRecovery, RecoveryTargetPhase.Formal, "回归", now,
                now.AddSeconds(60), new[] { 4, 5 });
            var pending = new ChannelRuntimeStateChangedEvent
            {
                Channel = 4, RunId = run, RunEpoch = 1, State = ChannelRuntimeState.Recovering,
                CorrelationId = owner, RecoveryOwnerId = owner, TimestampUtc = now.AddSeconds(1)
            };
            Assert(EpbManager.CanCleanupRecoveryChannel(contract, pending, run, 1), "本合同未重入成员无法收口");
            var ready = pending.Clone();
            ready.Channel = 5;
            ready.State = ChannelRuntimeState.Running;
            ready.RecoveryOwnerId = Guid.Empty;
            Assert(!EpbManager.CanCleanupRecoveryChannel(contract, ready, run, 1), "整组清理会删除已重入同伴");
            using var oldAttempt = new CycleAttemptContext(run, 1, "Dev1", 4, 10, CycleAttemptKind.FormalBatch, 1);
            using var successor = new CycleAttemptContext(run, 1, "Dev1", 4, 11, CycleAttemptKind.FormalRecovery, 2);
            Assert(EpbManager.RecoveryOwnsAttempt(contract, 10, oldAttempt) &&
                !EpbManager.RecoveryOwnsAttempt(contract, 10, successor),
                "旧恢复等待新圈终结导致持续运行时无法退役");
            Assert(!EpbManager.CanCleanupRecoveryChannel(contract, pending, run, 2), "旧epoch仍能清理");
            pending.RecoveryOwnerId = Guid.NewGuid();
            Assert(!EpbManager.CanCleanupRecoveryChannel(contract, pending, run, 1), "旧合同清理继任恢复owner");
            pending.State = ChannelRuntimeState.ManualStopped;
            Assert(!EpbManager.CanCleanupRecoveryChannel(contract, pending, run, 1), "恢复清理覆盖人工停止");
        }

        private static void RetirementCannotRevokeSuccessorPermit()
        {
            var fence = new ChannelExecutionFence();
            var old = fence.Authorize(5, 1);
            Assert(fence.RevokeIfCurrent(old) && !fence.IsCurrent(old), "旧参与者未实际撤权");
            var successor = fence.Authorize(5, 1);
            Assert(!fence.RevokeIfCurrent(old) && fence.IsCurrent(successor), "迟到退役撤销新执行体");
            Assert(old.RevocationToken.IsCancellationRequested && !successor.RevocationToken.IsCancellationRequested,
                "旧/新执行取消边界混用");
        }
    }
}
