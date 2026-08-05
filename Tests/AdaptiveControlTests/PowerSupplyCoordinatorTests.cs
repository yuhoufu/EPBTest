using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using PowerSupply.Core;

namespace AdaptiveControlTests
{
    internal static class PowerSupplyCoordinatorTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("电源安全配置缺项禁止启动", InvalidSafetyConfigIsRejected, ref passed);
            Run("启动前输出已开启先关闭再改参", PreExistingOutputOnIsSafelyDisabled, ref passed);
            Run("OUTP ON后等待空载电流稳定回零", StartupWaitsForCurrentToReturnToZero, ref passed);
            Run("启动电流不回零则关电并禁止启动", StartupZeroTimeoutRollsBackOutput, ref passed);
            Run("完整预检写入回读并确认关闭", ValidPreflightAndShutdown, ref passed);
            Run("恢复复核不循环健康电源输出", RevalidationDoesNotCycleHealthyOutput, ref passed);
            Run("计划关闭不产生意外掉电故障", PlannedShutdownIsNotUnexpectedOutputOff, ref passed);
            Run("电源保护新鲜回读才确认为硬件故障", ProtectionTripIsHardwareConfirmed, ref passed);
            Run("陈旧PSU限流回读不能确认双源过流", StaleTelemetryIsNotFreshFaultEvidence, ref passed);
            Run("停机等待在途遥测完成后再关闭输出", ShutdownWaitsForInFlightTelemetry, ref passed);
            Run("电源硬故障只联动对应电气组", FaultIsScopedAndManuallyReset, ref passed);
            return passed;
        }

        private static void InvalidSafetyConfigIsRejected()
        {
            var config = NewConfig();
            config.Supplies[0].OvpV = null;
            AssertThrows<InvalidDataException>(() => PowerSupplyConfigLoader.Validate(config));
        }

        private static void PreExistingOutputOnIsSafelyDisabled()
        {
            var config = NewConfig();
            var clients = NewClients(config);
            clients[1].OutputEnabled = true;
            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(clients[1].OutputOffCount == 1, "启动前 OUTP ON 未先关闭输出。");
                Assert(!clients[1].SetpointWrittenWhileOutputOn, "OUTP OFF 确认前写入了设定值。");
                Assert(clients[1].SetpointWriteCount == 4, "安全设定值未完整写入。");
                Assert(clients[1].OutputEnabled, "完成安全设定后未重新开启输出。");
            }
        }

        private static void ValidPreflightAndShutdown()
        {
            var config = NewConfig();
            var clients = NewClients(config);
            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1, 3, 4 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(clients[1].OutputEnabled && clients[2].OutputEnabled,
                    "选中通道对应的两组电源未全部开启。");
                Assert(!clients[3].OutputEnabled && !clients[4].OutputEnabled,
                    "未选电气组的电源被错误开启。");
                Assert(clients[1].Voltage == 24 && clients[1].Current == 20 &&
                       clients[1].Ovp == 27 && clients[1].Ocp == 22,
                    "安全设定值未完整写入并回读。");
                coordinator.DisableAllAsync("test", CancellationToken.None).GetAwaiter().GetResult();
                Assert(!clients[1].OutputEnabled && !clients[2].OutputEnabled,
                    "停止后未确认输出关闭。");
            }
        }

        private static void StartupWaitsForCurrentToReturnToZero()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.StartupZeroStableMs = 100;
            config.StartupZeroTimeoutMs = 1000;
            var clients = NewClients(config);
            clients[1].OutputCurrentSequence.Enqueue(4.0);
            clients[1].OutputCurrentSequence.Enqueue(2.0);
            clients[1].OutputCurrentSequence.Enqueue(0.2);
            clients[1].OutputCurrentSequence.Enqueue(0.2);
            clients[1].OutputCurrentSequence.Enqueue(0.2);

            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(clients[1].OutputSnapshotReadCount >= 5,
                    "电源启动瞬态尚未回零就允许启动。");
                Assert(clients[1].OutputEnabled, "电流稳定回零后电源未保持开启。");
            }
        }

        private static void RevalidationDoesNotCycleHealthyOutput()
        {
            var config = NewConfig();
            var clients = NewClients(config);
            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var onCount = clients[1].OutputOnCount;
                var offCount = clients[1].OutputOffCount;

                coordinator.RevalidateEnabledAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(clients[1].OutputEnabled, "恢复复核后健康电源未保持开启。");
                Assert(clients[1].OutputOnCount == onCount && clients[1].OutputOffCount == offCount,
                    "恢复复核对健康电源执行了无意义的 OFF/ON 循环。");
            }
        }

        private static void PlannedShutdownIsNotUnexpectedOutputOff()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            var clients = NewClients(config);
            using (var coordinator = NewCoordinator(config, clients))
            {
                var faults = new List<PowerSupplyFault>();
                coordinator.FaultRaised += faults.Add;
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                coordinator.DisableGroupAsync(1, "planned-test", CancellationToken.None)
                    .GetAwaiter().GetResult();
                Thread.Sleep(100);
                Assert(!faults.Any(x => x.Code == "UnexpectedOutputOff"),
                    "本程序计划关闭被监控误判为意外掉电。");
            }
        }

        private static void ProtectionTripIsHardwareConfirmed()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            var clients = NewClients(config);
            using (var signal = new ManualResetEventSlim(false))
            using (var coordinator = NewCoordinator(config, clients))
            {
                PowerSupplyFault raised = null;
                coordinator.FaultRaised += fault =>
                {
                    raised = fault;
                    signal.Set();
                };
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                clients[1].ProtectionTripped = true;
                Assert(signal.Wait(TimeSpan.FromSeconds(2)), "新鲜保护状态未产生故障事件。");
                Assert(raised != null && raised.Code == "ProtectionTrip" &&
                       raised.Classification == FaultClassification.HardwareConfirmed,
                    "明确的电源保护动作未归类为 HardwareConfirmed。");
            }
        }

        private static void StaleTelemetryIsNotFreshFaultEvidence()
        {
            var config = NewConfig();
            config.TelemetryStaleMs = 200;
            var clients = NewClients(config);
            clients[1].SnapshotTimestampUtc = DateTime.UtcNow.AddSeconds(-5);
            clients[1].ConstantCurrent = true;
            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(!coordinator.HasFreshPowerFaultEvidence(1),
                    "陈旧PSU限流回读被错误当作双源过流的独立新鲜证据。");
            }
        }

        private static void StartupZeroTimeoutRollsBackOutput()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.StartupZeroStableMs = 100;
            config.StartupZeroTimeoutMs = 250;
            var clients = NewClients(config);
            clients[1].DefaultOutputCurrent = 4.0;

            using (var coordinator = NewCoordinator(config, clients))
            {
                AssertThrows<InvalidOperationException>(() =>
                    coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                        .GetAwaiter().GetResult());
                Assert(!clients[1].OutputEnabled && clients[1].OutputOffCount == 1,
                    "启动电流不回零时未回滚关闭电源输出。");
            }
        }

        private static void FaultIsScopedAndManuallyReset()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.CcTripMs = 50;
            var clients = NewClients(config);
            using (var signal = new ManualResetEventSlim(false))
            using (var coordinator = NewCoordinator(config, clients))
            {
                PowerSupplyFault raised = null;
                coordinator.FaultRaised += fault =>
                {
                    raised = fault;
                    signal.Set();
                };
                coordinator.PrepareAndEnableAsync(new[] { 1, 2, 4 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                clients[1].ConstantCurrent = true;
                Assert(signal.Wait(TimeSpan.FromSeconds(2)), "未在规定时间内锁存 CC 故障。");
                Assert(raised != null && raised.ElectricalGroupId == 1, "故障组识别错误。");
                Assert(raised.Classification == FaultClassification.SystemFault,
                    "单独的电源限流证据被错误提升为硬件确认报警。");
                Assert(raised.AffectedChannels.SequenceEqual(new[] { 1, 2 }),
                    "组级故障联动范围越过了本次所选通道。");

                coordinator.DisableGroupAsync(1, "fault", CancellationToken.None).GetAwaiter().GetResult();
                clients[1].ConstantCurrent = false;
                coordinator.ResetFaultAsync(1, CancellationToken.None).GetAwaiter().GetResult();
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(clients[1].OutputEnabled, "人工复位后未能重新完成预检。");
            }
        }

        private static void ShutdownWaitsForInFlightTelemetry()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            var clients = NewClients(config);
            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();

                clients[1].BlockSnapshotReads = true;
                Assert(clients[1].SnapshotReadStarted.Wait(TimeSpan.FromSeconds(2)),
                    "监控遥测未进入测试阻塞点。");

                var stopTask = coordinator.DisableGroupAsync(1, "race-test", CancellationToken.None);
                Thread.Sleep(100);
                Assert(!stopTask.IsCompleted, "停机未等待在途遥测完成。");
                Assert(clients[1].OutputOffCount == 0, "在途遥测未退出前已发送 OUTP OFF。");

                clients[1].AllowSnapshotRead.Set();
                stopTask.GetAwaiter().GetResult();
                Assert(clients[1].OutputOffCount == 1 && !clients[1].OutputEnabled,
                    "遥测退出后未正常关闭并确认电源输出。");
            }
        }

        private static PowerSupplyCoordinator NewCoordinator(
            PowerSupplyFleetConfig config,
            IReadOnlyDictionary<int, FakePswClient> clients)
        {
            return new PowerSupplyCoordinator(
                config,
                NewGroups(),
                NullLogger.Instance,
                supply => clients[supply.Id]);
        }

        private static Dictionary<int, FakePswClient> NewClients(PowerSupplyFleetConfig config)
        {
            return config.Supplies.ToDictionary(
                supply => supply.Id,
                supply => new FakePswClient(supply.Id));
        }

        private static PowerSupplyFleetConfig NewConfig()
        {
            var config = new PowerSupplyFleetConfig
            {
                PollIntervalMs = 100,
                TelemetryStaleMs = 500,
                CcTripMs = 300,
                LowVoltageTripMs = 300,
                NearLimitWarnRatio = 0.9,
                StartupZeroCurrentA = 0.5,
                StartupZeroStableMs = 100,
                StartupZeroTimeoutMs = 1000
            };
            for (var id = 1; id <= 4; id++)
            {
                config.Supplies.Add(new PowerSupplyDeviceConfig
                {
                    Id = id,
                    ElectricalGroupId = id,
                    DisplayName = "PSU" + id,
                    Host = "192.168.1." + (100 + id),
                    Port = 2268,
                    ExpectedModel = "PSW 30-72",
                    VoltageV = 24,
                    CurrentA = 20,
                    OvpV = 27,
                    OcpA = 22,
                    MinimumOutputVoltageV = 20,
                    SetpointTolerance = 0.05
                });
            }
            return config;
        }

        private static IReadOnlyList<ElectricalGroup> NewGroups()
        {
            var groups = new List<ElectricalGroup>();
            for (var id = 1; id <= 4; id++)
            {
                var group = new ElectricalGroup { Id = id };
                group.Members.AddRange(Enumerable.Range((id - 1) * 3 + 1, 3));
                groups.Add(group);
            }
            return groups;
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

        private static void AssertThrows<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("预期异常未抛出：" + typeof(T).Name);
        }

        private sealed class FakePswClient : IPswClient
        {
            public FakePswClient(int id)
            {
                Endpoint = new PswEndpoint { Id = id, DisplayName = "PSU" + id, Host = "127.0.0.1" };
            }

            public PswEndpoint Endpoint { get; }
            public bool IsConnected { get; private set; }
            public string Identity => "GW INSTEK,PSW 30-72,SN,1.0";
            public bool IsVerifiedPsw => true;
            public PswCapabilities Capabilities => new PswCapabilities
            {
                Model = "PSW 30-72",
                MaxVoltage = 30,
                MaxCurrent = 72,
                MinOvp = 1,
                MaxOvp = 33,
                MinOcp = 1,
                MaxOcp = 79
            };
            public bool OutputEnabled { get; set; }
            public bool ConstantCurrent { get; set; }
            public bool ProtectionTripped { get; set; }
            public DateTime? SnapshotTimestampUtc { get; set; }
            public double Voltage { get; private set; }
            public double Current { get; private set; }
            public double? Ovp { get; private set; }
            public double? Ocp { get; private set; }
            public int SetpointWriteCount { get; private set; }
            public int OutputOffCount { get; private set; }
            public int OutputOnCount { get; private set; }
            public bool SetpointWrittenWhileOutputOn { get; private set; }
            public Queue<double> OutputCurrentSequence { get; } = new Queue<double>();
            public double DefaultOutputCurrent { get; set; }
            public int OutputSnapshotReadCount { get; private set; }
            public bool BlockSnapshotReads { get; set; }
            public ManualResetEventSlim SnapshotReadStarted { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim AllowSnapshotRead { get; } = new ManualResetEventSlim(false);

            public Task<PswSnapshot> ConnectAsync(CancellationToken token)
            {
                IsConnected = true;
                return Task.FromResult(Snapshot());
            }

            public Task DisconnectAsync(CancellationToken token)
            {
                IsConnected = false;
                return Task.CompletedTask;
            }

            public Task<PswSnapshot> ReadSnapshotAsync(CancellationToken token)
            {
                if (!BlockSnapshotReads) return Task.FromResult(Snapshot());
                SnapshotReadStarted.Set();
                return Task.Run(() =>
                {
                    AllowSnapshotRead.Wait();
                    return Snapshot();
                });
            }

            public Task<double> SetVoltageAsync(double value, CancellationToken token)
            {
                SetpointWrittenWhileOutputOn |= OutputEnabled;
                SetpointWriteCount++;
                Voltage = value;
                return Task.FromResult(value);
            }

            public Task<double> SetCurrentAsync(double value, CancellationToken token)
            {
                SetpointWrittenWhileOutputOn |= OutputEnabled;
                SetpointWriteCount++;
                Current = value;
                return Task.FromResult(value);
            }

            public Task<double> SetOvpAsync(double value, CancellationToken token)
            {
                SetpointWrittenWhileOutputOn |= OutputEnabled;
                SetpointWriteCount++;
                Ovp = value;
                return Task.FromResult(value);
            }

            public Task<double> SetOcpAsync(double value, CancellationToken token)
            {
                SetpointWrittenWhileOutputOn |= OutputEnabled;
                SetpointWriteCount++;
                Ocp = value;
                return Task.FromResult(value);
            }

            public Task<bool> SetOutputAsync(bool enabled, CancellationToken token)
            {
                if (enabled) OutputOnCount++;
                else OutputOffCount++;
                OutputEnabled = enabled;
                return Task.FromResult(enabled);
            }

            public Task<IReadOnlyList<string>> ReadErrorQueueAsync(CancellationToken token) =>
                Task.FromResult<IReadOnlyList<string>>(new[] { "0,\"No error\"" });

            public Task<string> SendRawAsync(
                string command,
                bool expectResponse,
                CancellationToken token) => Task.FromResult(string.Empty);

            private PswSnapshot Snapshot()
            {
                var measuredCurrent = 0.0;
                if (OutputEnabled)
                {
                    OutputSnapshotReadCount++;
                    measuredCurrent = OutputCurrentSequence.Count > 0
                        ? OutputCurrentSequence.Dequeue()
                        : DefaultOutputCurrent;
                }
                return new PswSnapshot
                {
                    TimestampUtc = SnapshotTimestampUtc ?? DateTime.UtcNow,
                    SupplyId = Endpoint.Id,
                    IsConnected = IsConnected,
                    Identity = Identity,
                    IsVerifiedPsw = true,
                    Capabilities = Capabilities,
                    OutputEnabled = OutputEnabled,
                    SetVoltage = Voltage,
                    SetCurrent = Current,
                    Ovp = Ovp,
                    Ocp = Ocp,
                    MeasuredVoltage = OutputEnabled ? Voltage : 0,
                    MeasuredCurrent = measuredCurrent,
                    MeasuredPower = OutputEnabled ? Voltage * measuredCurrent : 0,
                    OperationStatus = ConstantCurrent ? 1024 : 256,
                    ProtectionTripped = ProtectionTripped
                };
            }

            public void Dispose()
            {
                AllowSnapshotRead.Set();
                SnapshotReadStarted.Dispose();
                AllowSnapshotRead.Dispose();
                IsConnected = false;
            }
        }
    }
}
