using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Config;
using Controller;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class CalibrationMathTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("传感器双点标定命中两个参考点", SensorTwoPointMatchesReferences, ref passed);
            Run("AO多点标定按压力插值并受电压限幅", AoCalibrationInterpolatesAndClamps, ref passed);
            Run("历史无效AO表安全回退", InvalidLegacyAoTableIsRejected, ref passed);
            Run("压力校正气缸映射与120bar限幅", PressureCalibrationMappingAndLimit, ref passed);
            Run("压力校正按复位DO后AO顺序输出", PressureCalibrationOutputOrdering, ref passed);
            Run("压力校正采样过期立即回零", PressureCalibrationStaleSampleTrips, ref passed);
            Run("压力校正超过120bar立即回零", PressureCalibrationOverpressureTrips, ref passed);
            Run("压力校正后台看门狗独立触发保护", PressureCalibrationWatchdogTrips, ref passed);
            Run("未输出电流压力仅在UI副本置零", UiOutputZeroPolicyIsSignalSpecific, ref passed);
            Run("标定配置保存生成备份并可重载", CalibrationStoresAreAtomicAndReloadable, ref passed);
            return passed;
        }

        private static void SensorTwoPointMatchesReferences()
        {
            Assert(CalibrationMath.TryCalculateSensorTwoPoint(
                    1.002, 0, 2.122, 70,
                    out var scale, out var offset, out var zero, out var error),
                error);
            Assert(Math.Abs(zero - 1.002) < 1e-12, "零点未使用第一个原始电压");
            Assert(Math.Abs(offset) < 1e-12, "截距未使用第一个参考值");
            Assert(Math.Abs((1.002 - zero) * scale + offset) < 1e-9, "点1换算错误");
            Assert(Math.Abs((2.122 - zero) * scale + offset - 70) < 1e-9, "点2换算错误");
        }

        private static void AoCalibrationInterpolatesAndClamps()
        {
            var points = new List<(double Voltage, double Pressure)>
            {
                (0, 0),
                (2.0, 60),
                (2.4, 70),
                (4.0, 120)
            };
            Assert(CalibrationMath.TryMapPressureToVoltage(points, 65, 0, 10, out var voltage),
                "有效标定表未被接受");
            Assert(Math.Abs(voltage - 2.2) < 1e-9, "65bar 未在 60/70bar 两点间正确插值");
            Assert(CalibrationMath.TryMapPressureToVoltage(points, 500, 0, 10, out voltage),
                "范围外压力未使用末段外推");
            Assert(voltage <= 10 && voltage >= 0, "外推电压未限幅");
        }

        private static void InvalidLegacyAoTableIsRejected()
        {
            var points = new List<(double Voltage, double Pressure)> { (0, 0), (100, 6) };
            Assert(!CalibrationMath.TryMapPressureToVoltage(points, 70, 0, 10, out _),
                "超出 AO 量程的历史标定点不应参与控制");
        }

        private static void PressureCalibrationMappingAndLimit()
        {
            Assert(PressureCalibrationCoordinator.TryResolveDevice(
                    "Cylinder1", out var id1, out var pressure1) && id1 == 1 && pressure1 == "Pressure_1",
                "Cylinder1 映射错误");
            Assert(PressureCalibrationCoordinator.TryResolveDevice(
                    "Cylinder2", out var id2, out var pressure2) && id2 == 2 && pressure2 == "Pressure_2",
                "Cylinder2 映射错误");

            var hardware = new FakePressureCalibrationHardware();
            using var coordinator = NewPressureCalibrationCoordinator(hardware);
            Assert(Math.Abs(coordinator.EffectivePressureLimitBar - 120) < 1e-12,
                "校正压力未限制为120bar");
            var result = coordinator.Output("Cylinder1", 120.01);
            Assert(!result.Success, "超过120bar的命令未被拒绝");
            Assert(hardware.Calls.Count == 0, "非法压力命令仍操作了硬件");

            hardware.Sample = FreshPressureSample(1, 120.1);
            result = coordinator.Output("Cylinder1", 70);
            Assert(!result.Success && !hardware.Calls.Contains("DO1=1"),
                "输出前已处于过压状态时仍打开了压力DO");
        }

        private static void PressureCalibrationOutputOrdering()
        {
            var hardware = new FakePressureCalibrationHardware();
            using var coordinator = NewPressureCalibrationCoordinator(hardware);
            var result = coordinator.Output("Cylinder1", 70);
            Assert(result.Success, result.Error);
            var expected = new[]
            {
                "DO1=0", "DO2=0", "AOCylinder1=0", "AOCylinder2=0",
                "DO1=1", "AOCylinder1=70"
            };
            Assert(hardware.Calls.Take(expected.Length).SequenceEqual(expected),
                "输出顺序不是先全复位、再开选中DO、最后写AO：" + string.Join(",", hardware.Calls));
        }

        private static void PressureCalibrationStaleSampleTrips()
        {
            var hardware = new FakePressureCalibrationHardware();
            using var coordinator = NewPressureCalibrationCoordinator(hardware);
            Assert(coordinator.Output("Cylinder2", 70).Success, "前置输出失败");
            hardware.Sample = new PressureSample(2, 70, DateTime.UtcNow, 0);
            var safety = coordinator.CheckSafety();
            Assert(!safety.Safe && safety.Tripped && !coordinator.IsOutputActive,
                "采样过期未触发停止");
            Assert(hardware.Calls.Contains("DO2=0") && hardware.Calls.Last() == "AOCylinder2=0",
                "采样过期后未执行DO关闭与AO回零");
        }

        private static void PressureCalibrationOverpressureTrips()
        {
            var hardware = new FakePressureCalibrationHardware();
            using var coordinator = NewPressureCalibrationCoordinator(hardware);
            Assert(coordinator.Output("Cylinder1", 100).Success, "前置输出失败");
            hardware.Sample = FreshPressureSample(1, 120.1);
            var safety = coordinator.CheckSafety();
            Assert(!safety.Safe && safety.Tripped && safety.Error.Contains("超过"),
                "超过120bar未触发保护");
            Assert(!coordinator.IsOutputActive, "过压后仍保持输出状态");
        }

        private static void PressureCalibrationWatchdogTrips()
        {
            var hardware = new FakePressureCalibrationHardware();
            using var coordinator = NewPressureCalibrationCoordinator(hardware, 10);
            using var tripped = new ManualResetEventSlim();
            coordinator.SafetyTripped += result =>
            {
                if (result.Tripped) tripped.Set();
            };
            Assert(coordinator.Output("Cylinder1", 70).Success, "前置输出失败");
            hardware.Sample = new PressureSample(1, 70, DateTime.UtcNow, 0);
            Assert(tripped.Wait(1000), "后台看门狗未在1秒内触发保护");
            Assert(!coordinator.IsOutputActive, "后台保护触发后仍保持输出状态");
        }

        private static PressureCalibrationCoordinator NewPressureCalibrationCoordinator(
            FakePressureCalibrationHardware hardware,
            int watchdogIntervalMs = 0)
        {
            var ao = new AoConfig
            {
                MinVoltage = 0,
                MaxVoltage = 10,
                MinPressure = 0,
                MaxPressure = 200
            };
            ao.Devices["Cylinder1"] = new AoDevice { Name = "Cylinder1", ScaleK = 40 };
            ao.Devices["Cylinder2"] = new AoDevice { Name = "Cylinder2", ScaleK = 40 };
            var test = new TestConfig();
            test.Hydraulics.Add(new HydraulicItem { Id = 1, Enabled = true, PressureSampleMaxAgeMs = 100 });
            test.Hydraulics.Add(new HydraulicItem { Id = 2, Enabled = true, PressureSampleMaxAgeMs = 100 });
            return new PressureCalibrationCoordinator(
                hardware,
                ao,
                test,
                Config.NullLogger.Instance,
                watchdogIntervalMs);
        }

        private static PressureSample FreshPressureSample(int hydraulicId, double pressure) =>
            new PressureSample(hydraulicId, pressure, DateTime.UtcNow, Stopwatch.GetTimestamp());

        private sealed class FakePressureCalibrationHardware : IPressureCalibrationHardware
        {
            public readonly List<string> Calls = new List<string>();
            public PressureSample Sample = FreshPressureSample(1, 0);

            public bool SetPressureDo(int hydraulicId, bool enabled)
            {
                Calls.Add($"DO{hydraulicId}={(enabled ? 1 : 0)}");
                return true;
            }

            public AoWriteResult WritePressure(string deviceName, double pressureBar)
            {
                Calls.Add($"AO{deviceName}={pressureBar:G}");
                return new AoWriteResult(true, deviceName, pressureBar, pressureBar / 40.0);
            }

            public PressureSample ReadPressureSample(int hydraulicId) =>
                Sample.HydraulicId == hydraulicId
                    ? Sample
                    : FreshPressureSample(hydraulicId, Sample.ValueBar);

            public void Dispose()
            {
            }
        }

        private static void UiOutputZeroPolicyIsSignalSpecific()
        {
            Assert(TwoDeviceAiAcquirer.ShouldForceUiZero("EPB4_current", _ => false, _ => true),
                "未输出 EPB 电流未置零");
            Assert(!TwoDeviceAiAcquirer.ShouldForceUiZero("EPB4_current", _ => true, _ => false),
                "已输出 EPB 电流被错误置零");
            Assert(TwoDeviceAiAcquirer.ShouldForceUiZero("Pressure_2", _ => true, _ => false),
                "未输出气缸压力未置零");
            Assert(!TwoDeviceAiAcquirer.ShouldForceUiZero("Force", _ => false, _ => false),
                "非电流/压力信号被错误置零");
            Assert(!TwoDeviceAiAcquirer.ShouldForceUiZero("Pressure_1", _ => true, _ => throw new Exception()),
                "状态查询异常时不应静默掩盖真实压力");
        }

        private static void CalibrationStoresAreAtomicAndReloadable()
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBCalibrationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var aiPath = Path.Combine(root, "AIConfig.xml");
                File.WriteAllText(aiPath,
                    "<AIConfigDetail><Records><序号>1</序号><物理通道>Dev1/ai6</物理通道>" +
                    "<参数名>Pressure_1</参数名><单位>Bar</单位><变换斜率>1</变换斜率>" +
                    "<变换截距>0</变换截距><参数类型>管路压力</参数类型><是否启用>1</是否启用>" +
                    "<零位漂移>0</零位漂移></Records></AIConfigDetail>");
                CalibrationConfigStore.SaveSensorCalibrations(aiPath, new[]
                {
                    new SensorCalibrationUpdate
                    {
                        ParameterName = "Pressure_1", Scale = 62.5, Offset = 0, Zero = 1.003
                    }
                });
                var ai = AiConfigLoader.Load(aiPath).Records.Single();
                Assert(Math.Abs(ai.变换斜率 - 62.5) < 1e-12 && Math.Abs(ai.零位漂移 - 1.003) < 1e-12,
                    "AI 标定保存后无法重载");
                Assert(File.Exists(aiPath + ".bak"), "AI 保存未生成备份");

                var aoPath = Path.Combine(root, "AOConfig.xml");
                File.WriteAllText(aoPath,
                    "<AOConfig><MinVoltage>0</MinVoltage><MaxVoltage>10</MaxVoltage>" +
                    "<MinPressure>0</MinPressure><MaxPressure>200</MaxPressure><Devices><Device>" +
                    "<Name>Cylinder1</Name><PhysicalChannel>Dev1/ao0</PhysicalChannel>" +
                    "<ScaleK>40</ScaleK><Offset>-8</Offset><VoltageToPressureTable />" +
                    "</Device></Devices></AOConfig>");
                CalibrationConfigStore.SaveAoCalibrations(
                    aoPath,
                    new Dictionary<string, IReadOnlyList<AoCalibrationPoint>>
                    {
                        ["Cylinder1"] = new[]
                        {
                            new AoCalibrationPoint { Pressure = 0, Voltage = 0, CommandPressure = 0 },
                            new AoCalibrationPoint { Pressure = 70, Voltage = 2.4, CommandPressure = 80 }
                        }
                    },
                    0,
                    10);
                var ao = ConfigLoader.LoadAO(aoPath, Config.NullLogger.Instance);
                Assert(ao.Devices["Cylinder1"].VoltageToPressure.Count == 2,
                    "AO 标定保存后无法重载");
                Assert(XDocument.Load(aoPath).Descendants("CommandPressure").Any(),
                    "AO 观测命令未保留用于审计");
                Assert(File.Exists(aoPath + ".bak"), "AO 保存未生成备份");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
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
