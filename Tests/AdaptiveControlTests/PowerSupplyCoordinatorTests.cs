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
            Run("OUTP ON初始5A浪涌回零后允许启动", StartupFiveAmpTransientIsAllowed, ref passed);
            Run("启动电压爬升后连续稳定可通过", StartupVoltageRampIsAllowed, ref passed);
            Run("启动电压持续过低才超时回滚", StartupLowVoltageTimeoutRollsBackOutput, ref passed);
            Run("启动电流不回零则关电并禁止启动", StartupZeroTimeoutRollsBackOutput, ref passed);
            Run("完整预检写入回读并确认关闭", ValidPreflightAndShutdown, ref passed);
            Run("恢复复核不循环健康电源输出", RevalidationDoesNotCycleHealthyOutput, ref passed);
            Run("计划关闭不产生意外掉电故障", PlannedShutdownIsNotUnexpectedOutputOff, ref passed);
            Run("电源保护新鲜回读才确认为硬件故障", ProtectionTripIsHardwareConfirmed, ref passed);
            Run("陈旧PSU限流回读不能确认双源过流", StaleTelemetryIsNotFreshFaultEvidence, ref passed);
            Run("成功遥测超过500ms只诊断不报警", SlowSuccessfulTelemetryDoesNotFault, ref passed);
            Run("电源通信恢复会清零动作圈计数", CommunicationRecoveryResetsMissCycles, ref passed);
            Run("同组连续8个动作槽无遥测才报警", CommunicationFaultRequiresEightUniqueGroupSlots, ref passed);
            Run("停机等待在途遥测完成后再关闭输出", ShutdownWaitsForInFlightTelemetry, ref passed);
            Run("三个并发OFF请求共用一个安全任务", ConcurrentShutdownRequestsShareOneOwner, ref passed);
            Run("单组断线不误报关闭且不阻塞其他组", SafetyDisableIsStructuredAndIsolated, ref passed);
            Run("电源故障只联动对应组且新预检自动清旧锁存", FaultIsScopedAndFreshPreflightClearsLatch, ref passed);
            return passed;
        }

        private static void SafetyDisableIsStructuredAndIsolated()
        {
            var config = NewConfig();
            var clients = NewClients(config);
            clients[1].FailConnect = true;
            using (var coordinator = NewCoordinator(config, clients))
            {
                var results = coordinator.DisableAllForSafetyAsync(
                        "FaultInjectionDisconnectedGroup",
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                var failed = results.Single(item => item.ElectricalGroupId == 1);
                Assert(!failed.ConfirmedOff &&
                       failed.Outcome != PowerSafetyDisableOutcome.ConfirmedOff,
                    "客户端未连接被误报为输出已关闭");
                Assert(results.Where(item => item.ElectricalGroupId != 1)
                           .All(item => item.ConfirmedOff),
                    "一个组连接失败阻塞了其他电源组独立关闭");
            }
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

        private static void StartupFiveAmpTransientIsAllowed()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.StartupZeroStableMs = 100;
            config.StartupZeroTimeoutMs = 1000;
            var clients = NewClients(config);
            clients[1].OutputCurrentSequence.Enqueue(5.0);
            clients[1].OutputCurrentSequence.Enqueue(4.5);
            clients[1].OutputCurrentSequence.Enqueue(0.1);
            clients[1].OutputCurrentSequence.Enqueue(0.1);
            clients[1].OutputCurrentSequence.Enqueue(0.1);

            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(clients[1].OutputEnabled,
                    "程控电源刚ON的4至5A短暂波动被误判为启动故障");
                Assert(coordinator.HasEnergizationPermit(1, out var reason),
                    "浪涌回零并稳定后仍未签发电机动作许可：" + reason);
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

        private static void StartupVoltageRampIsAllowed()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.StartupVoltageStableMs = 100;
            config.StartupZeroStableMs = 100;
            config.StartupZeroTimeoutMs = 1000;
            var clients = NewClients(config);
            clients[1].OutputVoltageSequence.Enqueue(5.856);
            clients[1].OutputVoltageSequence.Enqueue(12.0);
            clients[1].OutputVoltageSequence.Enqueue(21.0);
            clients[1].OutputVoltageSequence.Enqueue(24.0);
            clients[1].OutputVoltageSequence.Enqueue(24.0);

            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(clients[1].OutputEnabled,
                    "启动电压在超时内爬升并稳定后仍被单帧低压拒绝。");
                Assert(clients[1].OutputSnapshotReadCount >= 5,
                    "启动电压未覆盖爬升和连续稳定窗口。");
            }
        }

        private static void StartupLowVoltageTimeoutRollsBackOutput()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.StartupVoltageStableMs = 100;
            config.StartupZeroStableMs = 100;
            config.StartupZeroTimeoutMs = 250;
            var clients = NewClients(config);
            clients[1].DefaultOutputVoltage = 5.0;

            using (var coordinator = NewCoordinator(config, clients))
            {
                AssertThrows<InvalidOperationException>(() =>
                    coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                        .GetAwaiter().GetResult());
                Assert(!clients[1].OutputEnabled && clients[1].OutputOffCount == 1,
                    "启动电压持续低于下限超时后未回滚关闭输出。");
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

        private static void SlowSuccessfulTelemetryDoesNotFault()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.TelemetryDelayWarnMs = 100;
            config.TelemetryStaleMs = 100;
            config.CommunicationRetryMs = 50;
            var clients = NewClients(config);
            using (var delayed = new ManualResetEventSlim(false))
            using (var coordinator = NewCoordinator(config, clients))
            {
                var faults = new List<PowerSupplyFault>();
                var faultGate = new object();
                coordinator.FaultRaised += fault =>
                {
                    lock (faultGate) faults.Add(fault);
                };
                coordinator.TelemetryUpdated += telemetry =>
                {
                    if (telemetry.ElectricalGroupId == 1 &&
                        telemetry.EventCode == "TelemetryDelayed")
                        delayed.Set();
                };
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();

                clients[1].SnapshotReadDelayMs = 150;
                Assert(delayed.Wait(TimeSpan.FromSeconds(3)),
                    "成功但耗时超过阈值的遥测没有产生延迟诊断事件");
                Assert(!coordinator.GetRuntimeState(1).CommunicationDegraded,
                    "成功慢遥测被错误标记为通信中断");
                Assert(coordinator.HasEnergizationPermit(1, out var reason),
                    "成功慢遥测错误撤销动作许可：" + reason);
                lock (faultGate)
                    Assert(faults.Count == 0, "成功慢遥测错误升级为电源故障");
            }
        }

        private static void CommunicationRecoveryResetsMissCycles()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.TelemetryDelayWarnMs = 100;
            config.TelemetryStaleMs = 100;
            config.CommunicationRetryMs = 50;
            config.CommunicationAlarmMaxMs = 5000;
            var clients = NewClients(config);
            using (var degraded = new ManualResetEventSlim(false))
            using (var recovered = new ManualResetEventSlim(false))
            using (var coordinator = NewCoordinator(config, clients))
            {
                coordinator.TelemetryUpdated += telemetry =>
                {
                    if (telemetry.ElectricalGroupId != 1) return;
                    if (telemetry.EventCode == "CommunicationDegraded") degraded.Set();
                    if (telemetry.EventCode == "TelemetryRecovered") recovered.Set();
                };
                coordinator.PrepareAndEnableAsync(new[] { 1, 2 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                clients[1].FailSnapshotReads = true;
                clients[1].FailConnect = true;
                Assert(degraded.Wait(TimeSpan.FromSeconds(3)), "未进入通信降级状态");

                for (var slot = 1L; slot <= 7; slot++)
                {
                    coordinator.RecordSuccessfulActionCycle(1, slot);
                    coordinator.RecordSuccessfulActionCycle(2, slot);
                }
                var beforeRecovery = coordinator.GetRuntimeState(1);
                Assert(beforeRecovery.ConsecutiveCommunicationMissCycles == 7,
                    $"同组双通道被重复计圈：{beforeRecovery.ConsecutiveCommunicationMissCycles}");
                Assert(coordinator.HasEnergizationPermit(1, out var degradedReason),
                    "不足8圈即撤销动作许可：" + degradedReason);

                clients[1].FailSnapshotReads = false;
                clients[1].FailConnect = false;
                Assert(recovered.Wait(TimeSpan.FromSeconds(3)), "通信恢复未被监控链确认");
                var afterRecovery = coordinator.GetRuntimeState(1);
                Assert(!afterRecovery.CommunicationDegraded &&
                       afterRecovery.ConsecutiveCommunicationMissCycles == 0,
                    "任意一次成功遥测未清零降级状态和连续动作圈计数");
                Assert(coordinator.HasEnergizationPermit(1, out var reason),
                    "通信恢复后动作许可未恢复：" + reason);
            }
        }

        private static void CommunicationFaultRequiresEightUniqueGroupSlots()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            config.TelemetryDelayWarnMs = 100;
            config.TelemetryStaleMs = 100;
            config.CommunicationRetryMs = 50;
            config.CommunicationAlarmConfirmCycles = 8;
            config.CommunicationAlarmMaxMs = 5000;
            var clients = NewClients(config);
            using (var degraded = new ManualResetEventSlim(false))
            using (var faulted = new ManualResetEventSlim(false))
            using (var coordinator = NewCoordinator(config, clients))
            {
                var faults = new List<PowerSupplyFault>();
                var faultGate = new object();
                coordinator.TelemetryUpdated += telemetry =>
                {
                    if (telemetry.ElectricalGroupId == 1 &&
                        telemetry.EventCode == "CommunicationDegraded")
                        degraded.Set();
                };
                coordinator.FaultRaised += fault =>
                {
                    lock (faultGate) faults.Add(fault);
                    faulted.Set();
                };
                coordinator.PrepareAndEnableAsync(new[] { 1, 2 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                clients[1].FailSnapshotReads = true;
                clients[1].FailConnect = true;
                Assert(degraded.Wait(TimeSpan.FromSeconds(3)), "未进入通信降级状态");

                for (var slot = 1L; slot <= 7; slot++)
                {
                    coordinator.RecordSuccessfulActionCycle(1, slot);
                    coordinator.RecordSuccessfulActionCycle(2, slot);
                }
                Assert(!faulted.IsSet, "少于8个组动作槽时提前报警");
                coordinator.RecordSuccessfulActionCycle(2, 8);
                Assert(faulted.Wait(TimeSpan.FromSeconds(2)), "第8个组动作槽仍未升级通信故障");
                coordinator.RecordSuccessfulActionCycle(1, 8);

                lock (faultGate)
                {
                    Assert(faults.Count == 1, "同一组动作槽由两个通道重复触发故障");
                    Assert(faults[0].Code == "CommunicationUnavailableConfirmed" &&
                           faults[0].Classification == FaultClassification.SystemFault,
                        "8圈通信故障的代码或分类不正确");
                }
                Assert(!coordinator.HasEnergizationPermit(1, out _),
                    "8圈确认后仍保留动作许可");
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

        private static void FaultIsScopedAndFreshPreflightClearsLatch()
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
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert(clients[1].OutputEnabled, "实时预检已恢复正常但旧锁存仍阻碍重新启动。");
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

        private static void ConcurrentShutdownRequestsShareOneOwner()
        {
            var config = NewConfig();
            config.PollIntervalMs = 50;
            var clients = NewClients(config);
            using (var coordinator = NewCoordinator(config, clients))
            {
                var faults = new List<PowerSupplyFault>();
                coordinator.FaultRaised += fault => faults.Add(fault);
                coordinator.PrepareAndEnableAsync(new[] { 1 }, CancellationToken.None)
                    .GetAwaiter().GetResult();

                clients[1].BlockOutputOff = true;
                var first = coordinator.DisableGroupAsync(1, "first", CancellationToken.None);
                Assert(clients[1].OutputOffStarted.Wait(TimeSpan.FromSeconds(2)),
                    "首个OFF请求未进入测试阻塞点。");
                var second = coordinator.DisableGroupAsync(1, "second", CancellationToken.None);
                var third = coordinator.DisableGroupAsync(1, "third", CancellationToken.None);
                Thread.Sleep(50);
                Assert(!first.IsCompleted && !second.IsCompleted && !third.IsCompleted,
                    "并发OFF请求未等待同一在途安全任务。");

                clients[1].AllowOutputOff.Set();
                Task.WaitAll(first, second, third);
                Assert(clients[1].OutputOffCount == 1 && !clients[1].OutputEnabled,
                    "三个并发OFF请求未合并为一次硬件操作。");
                Assert(!faults.Any(fault => fault.Code == "OutputOffUnverified"),
                    "同方向OFF合并期间仍产生了取消型故障锁存。");
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
                TelemetryDelayWarnMs = 500,
                TelemetryStaleMs = 500,
                CommunicationAlarmConfirmCycles = 8,
                CommunicationAlarmMaxMs = 120000,
                CommunicationRetryMs = 500,
                CcTripMs = 300,
                LowVoltageTripMs = 300,
                NearLimitWarnRatio = 0.9,
                StartupZeroCurrentA = 0.5,
                StartupZeroStableMs = 100,
                StartupVoltageStableMs = 100,
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
            public Queue<double> OutputVoltageSequence { get; } = new Queue<double>();
            public double DefaultOutputCurrent { get; set; }
            public double? DefaultOutputVoltage { get; set; }
            public int OutputSnapshotReadCount { get; private set; }
            public bool BlockSnapshotReads { get; set; }
            public ManualResetEventSlim SnapshotReadStarted { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim AllowSnapshotRead { get; } = new ManualResetEventSlim(false);
            public bool BlockOutputOff { get; set; }
            public bool FailConnect { get; set; }
            public bool FailSnapshotReads { get; set; }
            public int SnapshotReadDelayMs { get; set; }
            public ManualResetEventSlim OutputOffStarted { get; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim AllowOutputOff { get; } = new ManualResetEventSlim(false);

            public Task<PswSnapshot> ConnectAsync(CancellationToken token)
            {
                if (FailConnect)
                    throw new InvalidOperationException("Injected connect failure");
                IsConnected = true;
                return Task.FromResult(Snapshot());
            }

            public Task DisconnectAsync(CancellationToken token)
            {
                IsConnected = false;
                return Task.CompletedTask;
            }

            public async Task<PswSnapshot> ReadSnapshotAsync(CancellationToken token)
            {
                if (FailSnapshotReads)
                {
                    IsConnected = false;
                    throw new IOException("Injected snapshot communication failure");
                }
                if (BlockSnapshotReads)
                {
                    SnapshotReadStarted.Set();
                    // 模拟已经进入不可取消的底层NetworkStream读取；停机必须等待
                    // 该在途I/O退出，不能仅靠取消令牌假定它已经结束。
                    await Task.Run(() => AllowSnapshotRead.Wait())
                        .ConfigureAwait(false);
                }
                var delayMs = SnapshotReadDelayMs;
                if (delayMs > 0)
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                return Snapshot();
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

            public async Task<bool> SetOutputAsync(bool enabled, CancellationToken token)
            {
                if (!enabled && BlockOutputOff)
                {
                    OutputOffStarted.Set();
                    await Task.Run(() => AllowOutputOff.Wait(token), token).ConfigureAwait(false);
                }
                if (enabled) OutputOnCount++;
                else OutputOffCount++;
                OutputEnabled = enabled;
                return enabled;
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
                    MeasuredVoltage = OutputEnabled
                        ? OutputVoltageSequence.Count > 0
                            ? OutputVoltageSequence.Dequeue()
                            : DefaultOutputVoltage ?? Voltage
                        : 0,
                    MeasuredCurrent = measuredCurrent,
                    MeasuredPower = OutputEnabled ? Voltage * measuredCurrent : 0,
                    OperationStatus = ConstantCurrent ? 1024 : 256,
                    ProtectionTripped = ProtectionTripped
                };
            }

            public void Dispose()
            {
                AllowSnapshotRead.Set();
                AllowOutputOff.Set();
                SnapshotReadStarted.Dispose();
                AllowSnapshotRead.Dispose();
                OutputOffStarted.Dispose();
                AllowOutputOff.Dispose();
                IsConnected = false;
            }
        }
    }
}
