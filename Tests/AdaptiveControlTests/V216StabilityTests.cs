using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class V216StabilityTests
    {
        internal static int RunAll()
        {
            StartupRetryCommitsBeforeWorkerReturns();
            StartupRetryCancellationCannotReauthorize();
            StartupRetryOldEpochCannotReauthorize();
            ConcurrentStartupRetriesKeepTheirOwners();
            return 4;
        }

        private static void StartupRetryCommitsBeforeWorkerReturns()
        {
            using (var fixture = new StartupFixture())
            {
                fixture.Retry(4, 1, CancellationToken.None).GetAwaiter().GetResult();
                fixture.AssertReady(4);
                Console.WriteLine("PASS V216 T01 真实启动重试先提交RetryReady再完成worker");
            }
        }

        private static void StartupRetryCancellationCannotReauthorize()
        {
            using (var fixture = new StartupFixture())
            using (var cts = new CancellationTokenSource())
            {
                var task = fixture.Retry(4, 500, cts.Token);
                fixture.WaitRecovering(4);
                cts.Cancel();
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                Assert(fixture.State(4).State != ChannelRuntimeState.Starting,
                    "取消后启动worker重新授权");
                Assert(!fixture.Manager.HasActiveRecoveryContract(4, 41), "取消后owner残留");
                Console.WriteLine("PASS V216 T02 真实启动重试取消不重新授权");
            }
        }

        private static void StartupRetryOldEpochCannotReauthorize()
        {
            using (var fixture = new StartupFixture())
            {
                var task = fixture.Retry(4, 200, CancellationToken.None);
                fixture.WaitRecovering(4);
                fixture.SetField("_runEpoch", 42L);
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
                Assert(fixture.State(4).State != ChannelRuntimeState.Starting,
                    "旧epoch启动worker重新授权");
                Console.WriteLine("PASS V216 T02 旧代际启动重试不重新授权");
            }
        }

        private static void ConcurrentStartupRetriesKeepTheirOwners()
        {
            using (var fixture = new StartupFixture())
            {
                Task.WhenAll(StartupFixture.Channels.Select(channel =>
                    fixture.Retry(channel, 20, CancellationToken.None))).GetAwaiter().GetResult();
                foreach (var channel in StartupFixture.Channels) fixture.AssertReady(channel);
                Console.WriteLine("PASS V216 T03 六通道真实启动重试互不覆盖owner");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class OffWriter : IHighPriorityOffPhysicalWriter
        {
            public bool TryWrite(string deviceName, IReadOnlyList<int> channels, bool[] nextStates)
            {
                Assert(nextStates.All(value => !value), "重试OFF物理边界包含上电位");
                return true;
            }
        }

        private sealed class StartupFixture : IDisposable
        {
            internal static readonly int[] Channels = { 4, 5, 7, 8, 9, 12 };
            internal readonly EpbManager Manager;
            private readonly Guid _runId = Guid.NewGuid();
            private readonly DoController _do;
            private readonly AoController _ao;
            private readonly TwoDeviceAiAcquirer _acq;

            internal StartupFixture()
            {
                var config = new GlobalConfig
                {
                    AO = new AoConfig(), DO = new DoConfig(),
                    Test = new TestConfig { TestName = "V216StartupSeam", TestTarget = 1, TestPeriod = 15 }
                };
                foreach (var record in config.Test.EnsureEpbRecords(12))
                    record.Enabled = Channels.Contains(record.Id);
                for (var i = 0; i < Channels.Length; i++)
                    config.DO.Epb.Add(new DoEpbRecord
                    {
                        Channel = Channels[i], Enabled = true,
                        Pos = "Dev1/port0/line" + (i * 2),
                        Neg = "Dev1/port0/line" + (i * 2 + 1), Default = "全关"
                    });
                _do = new DoController(config.DO) { HighPriorityOffPhysicalWriter = new OffWriter() };
                _do.ConfigureLogicalDeviceContextForAcceptance();
                _ao = new AoController(config.AO);
                _acq = new TwoDeviceAiAcquirer(new AiConfigDetail
                {
                    Records = new List<AiConfigDetailRecord>
                    {
                        new AiConfigDetailRecord { 序号 = 1, 物理通道 = "Dev1/ai0", 参数名 = "EPB4_current", 单位 = "A", 变换斜率 = 1, 是否启用 = 1 },
                        new AiConfigDetailRecord { 序号 = 2, 物理通道 = "Dev2/ai0", 参数名 = "EPB10_current", 单位 = "A", 变换斜率 = 1, 是否启用 = 1 }
                    }
                }, 2000, 100, 3);
                Manager = new EpbManager(config, _do, _ao, _acq, Config.NullLogger.Instance);
                SetField("_activeBatchId", _runId);
                SetField("_runEpoch", 41L);
            }

            internal void SetField(string name, object value) => typeof(EpbManager)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Manager, value);

            internal Task Retry(int channel, int delayMs, CancellationToken token) =>
                (Task)typeof(EpbManager).GetMethod("RunStartupPositioningRetryIncidentAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Manager,
                    new object[] { channel, _runId, 1, "InjectedSampleStale", "一次采样瞬态", delayMs, token, "V216RetryOff" });

            internal ChannelRuntimeStateChangedEvent State(int channel) =>
                Manager.GetChannelRuntimeStates().Single(state => state.Channel == channel);

            internal void WaitRecovering(int channel) => Assert(SpinWait.SpinUntil(
                () => Manager.GetChannelRuntimeStates().Any(state => state.Channel == channel &&
                    state.State == ChannelRuntimeState.Recovering), 3000), "真实入口未进入Recovering");

            internal void AssertReady(int channel)
            {
                var state = State(channel);
                Assert(state.State == ChannelRuntimeState.Starting &&
                    state.ReasonCode == "StartupPositioningRetryReady" && state.RunId == _runId && state.RunEpoch == 41,
                    "重试没有在worker返回前提交RetryReady: " + state.State + "/" + state.ReasonCode);
                Assert(!Manager.HasActiveRecoveryContract(channel, 41), "就绪后owner未释放");
            }

            public void Dispose()
            {
                Manager.ReleaseHardwareForRestart();
                _acq.Dispose(); _ao.Dispose(); _do.Dispose();
            }
        }
    }
}
