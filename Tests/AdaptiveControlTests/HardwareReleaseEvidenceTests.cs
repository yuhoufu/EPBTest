using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class HardwareReleaseEvidenceTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Check(ref passed, "NI 释放请求和空回调表不能冒充原生资源退出", () =>
            {
                var evidence = new HardwareReleaseEvidence();
                Assert(!evidence.Capture().FullyReleased, "fresh object reported released");
                evidence.RequestRelease(); evidence.CloseCallbackAdmission();
                Assert(!evidence.Capture().NativeResourcesReleased, "request became native proof");
                evidence.CompleteNativeRelease();
                Assert(evidence.Capture().FullyReleased, "completed boundary was not reported");
                var rejected = false;
                try { evidence.RegisterCallback(); }
                catch (ObjectDisposedException) { rejected = true; }
                Assert(rejected, "callback admission reopened after retirement");
            });
            Check(ref passed, "NI 旧代次回调未退出时保留登记且重复释放幂等", () =>
            {
                var evidence = new HardwareReleaseEvidence();
                var first = evidence.RegisterCallback(); var oldGeneration = evidence.RegisterCallback();
                evidence.RequestRelease(); evidence.CompleteNativeRelease(); evidence.CloseCallbackAdmission();
                Parallel.For(0, 16, _ => first.Dispose());
                var pending = evidence.Capture();
                Assert(pending.NativeResourcesReleased && !pending.CallbacksIsolated &&
                       pending.PendingCallbacks == 1, "old generation callback was lost");
                oldGeneration.Dispose();
                Assert(evidence.Capture().FullyReleased, "late exit did not close actual boundary");
            });
            Check(ref passed, "NI 原生释放异常永久锁存且重复成功不能洗白", () =>
            {
                var evidence = new HardwareReleaseEvidence();
                evidence.RecordFailure("Task.Dispose", new InvalidOperationException("driver failed"));
                evidence.RequestRelease(); evidence.CloseCallbackAdmission();
                evidence.CompleteNativeRelease(); evidence.CompleteNativeRelease();
                evidence.RecordFailure("later", new Exception("different"));
                Assert(!evidence.Capture().FullyReleased && !evidence.Capture().NativeResourcesReleased &&
                       evidence.Capture().Failure == "Task.Dispose: driver failed", "failure latch cleared");
            });
            Check(ref passed, "EngineHost 共用 NI 退出屏障拒绝缺证据与超时", () =>
            {
                var evidence = new HardwareReleaseEvidence();
                ExpectIOException(() => HardwareReleaseBarrier.RequireReleasedAsync(
                    () => new HardwareReleaseSnapshot[] { null }, 20, CancellationToken.None).GetAwaiter().GetResult());
                ExpectIOException(() => HardwareReleaseBarrier.RequireReleasedAsync(
                    () => new[] { evidence.Capture() }, 20, CancellationToken.None).GetAwaiter().GetResult());
                Assert(!evidence.Capture().ReleaseRequested, "observer mutated resource lifecycle");
                evidence.RecordFailure("dispose", new Exception("synthetic failure"));
                ExpectIOException(() => HardwareReleaseBarrier.RequireReleasedAsync(
                    () => new[] { evidence.Capture() }, 20, CancellationToken.None).GetAwaiter().GetResult());
            });
            Check(ref passed, "NI 退出屏障只在旧回调真正结束后允许调用方继续", () =>
            {
                var evidence = new HardwareReleaseEvidence(); var callback = evidence.RegisterCallback();
                evidence.RequestRelease(); evidence.CompleteNativeRelease(); evidence.CloseCallbackAdmission();
                var wait = HardwareReleaseBarrier.RequireReleasedAsync(
                    () => new[] { evidence.Capture() }, 5000, CancellationToken.None);
                Assert(!wait.IsCompleted, "barrier allowed replacement while callback was active");
                callback.Dispose(); wait.GetAwaiter().GetResult();
                Assert(evidence.Capture().FullyReleased, "completion did not preserve evidence");
            });
            Check(ref passed, "DO 有界 Dispose 返回后仍等待真实完成回调退出", DoCompletionExit);
            Check(ref passed, "DO 控制器清空设备表后仍持有旧完成回调退出证据", DoControllerCompletionExit);
            Check(ref passed, "DAQ 监督任务排空超时不清空活动退出登记", BackgroundExit);
            Check(ref passed, "AO 与 DO 无硬件实例释放前后状态和重复释放", () =>
            {
                using (var ao = new AoController(new AoConfig()))
                using (var digital = new DoController(new DoConfig()))
                {
                    Assert(!ao.CaptureReleaseEvidence().FullyReleased &&
                           !digital.CaptureReleaseEvidence().FullyReleased, "live controllers claimed released");
                    ao.Dispose(); digital.Dispose(); ao.Dispose(); digital.Dispose();
                    Assert(ao.CaptureReleaseEvidence().FullyReleased &&
                           digital.CaptureReleaseEvidence().FullyReleased, "empty controllers failed retirement");
                    Assert(!ao.WritePressure("missing", 0), "disposed AO accepted write");
                }
            });
            Check(ref passed, "AI 无硬件启动实例等待真实管线终止后给出退出事实", () =>
            {
                // 仅构造真实处理线程，不调用 Start，不创建 NI Task，不访问现场硬件。
                foreach (var device in new[] { "Dev1", "Dev2" })
                using (var acquirer = new TwoDeviceAiAcquirer(new AiConfigDetail
                {
                    Records = new List<AiConfigDetailRecord>
                    {
                        new AiConfigDetailRecord { 序号 = 1, 物理通道 = device + "/ai0", 参数名 = "EPB1_current",
                            单位 = "A", 变换斜率 = 1, 是否启用 = 1 }
                    }
                }, 2000, 100, 3))
                {
                    Assert(!acquirer.CaptureReleaseEvidence().FullyReleased, "running pipelines claimed exit");
                    acquirer.Dispose(); acquirer.Dispose();
                    Assert(SpinWait.SpinUntil(() => acquirer.CaptureReleaseEvidence().FullyReleased, 10000),
                        "AI finalizer failed: " + acquirer.CaptureReleaseEvidence().Failure);
                }
            });
            return passed;
        }

        private static void DoCompletionExit()
        {
            var evidence = new HardwareReleaseEvidence();
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var worker = new DoController.HighPriorityDoWorker("ReleaseEvidence", releaseEvidence: evidence))
            {
                try
                {
                    worker.InvokeHi(1, (channels, timing) => true, 500, telemetry =>
                    {
                        entered.Set(); release.Wait();
                    });
                    Assert(entered.Wait(5000), "completion callback did not enter");
                    evidence.RequestRelease(); worker.Dispose();
                    evidence.CompleteNativeRelease(); evidence.CloseCallbackAdmission();
                    Assert(!evidence.Capture().CallbacksIsolated && evidence.Capture().PendingCallbacks > 0,
                        "bounded worker Join falsely acknowledged callback isolation");
                    release.Set();
                    Assert(SpinWait.SpinUntil(() => evidence.Capture().FullyReleased, 5000), "callback did not exit");
                }
                finally { release.Set(); }
            }
        }

        private static void BackgroundExit()
        {
            var evidence = new HardwareReleaseEvidence();
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var supervisor = new CoalescingTaskSupervisor(Config.NullLogger.Instance, evidence))
            {
                try
                {
                    Assert(supervisor.TryRun("old-generation", () => { entered.Set(); release.Wait(); }), "not admitted");
                    Assert(entered.Wait(5000), "background did not enter");
                    evidence.RequestRelease(); supervisor.StopAccepting();
                    Assert(!supervisor.StopAcceptingAndDrain(20), "blocked task falsely drained");
                    evidence.CompleteNativeRelease(); evidence.CloseCallbackAdmission();
                    Assert(!evidence.Capture().CallbacksIsolated, "timeout discarded background ownership");
                    Assert(!supervisor.TryRun("new-generation", () => { }), "closed supervisor accepted new action");
                    release.Set();
                    Assert(supervisor.StopAcceptingAndDrain(5000), "released task failed to drain");
                    Assert(SpinWait.SpinUntil(() => evidence.Capture().FullyReleased, 5000), "late task not observed");
                }
                finally { release.Set(); }
            }
        }

        private static void DoControllerCompletionExit()
        {
            var config = new DoConfig();
            config.Epb.Add(new DoEpbRecord { Enabled = true, Channel = 1,
                Pos = "Dev1/port0/line0", Neg = "Dev1/port0/line1", Default = "全关" });
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var controller = new DoController(config))
            {
                controller.ConfigureLogicalDeviceContextForAcceptance();
                controller.HighPriorityOffPhysicalWriter = new LogicalWriter();
                try
                {
                    Assert(controller.TrySubmitEpbOffHighPriority(1,
                        telemetry => { entered.Set(); release.Wait(); }, out _), "logical OFF not admitted");
                    Assert(entered.Wait(5000), "controller callback did not enter");
                    controller.Dispose(); controller.Dispose();
                    var pending = controller.CaptureReleaseEvidence();
                    Assert(pending.ReleaseRequested && pending.NativeResourcesReleased &&
                           !pending.CallbacksIsolated, "cleared device map discarded live callback");
                    Assert(!controller.TrySubmitEpbOffHighPriority(1, telemetry => { }, out _),
                        "disposed controller accepted OFF callback on replacement worker");
                    release.Set();
                    Assert(SpinWait.SpinUntil(() => controller.CaptureReleaseEvidence().FullyReleased, 5000),
                        "controller failed to observe late callback exit");
                }
                finally { release.Set(); }
            }
        }

        private sealed class LogicalWriter : IHighPriorityOffPhysicalWriter
        {
            public bool TryWrite(string deviceName, IReadOnlyList<int> channels, bool[] nextStates) => true;
        }

        private static void Check(ref int passed, string name, Action test)
        { test(); passed++; Console.WriteLine("PASS " + name); }
        private static void Assert(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }
        private static void ExpectIOException(Action action)
        {
            try { action(); }
            catch (System.IO.IOException) { return; }
            throw new InvalidOperationException("Unproven release was accepted.");
        }
    }
}
