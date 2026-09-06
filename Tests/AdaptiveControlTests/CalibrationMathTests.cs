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
            Run("AO多点校正拟合新线性公式", AoCalibrationFitsLinearFormula, ref passed);
            Run("AO线性拟合拒绝无电压跨度数据", AoCalibrationRejectsZeroVoltageSpan, ref passed);
            Run("同命令压力更新旧点且不生成重复点", MatchingCommandPressureUpdatesExistingPoint, ref passed);
            Run("部分更新校正点识别新旧数据混用风险", MixedCalibrationDetectsStaleHistoricalPoints, ref passed);
            Run("压力校正气缸映射与120bar限幅", PressureCalibrationMappingAndLimit, ref passed);
            Run("压力校正按复位DO后AO顺序输出", PressureCalibrationOutputOrdering, ref passed);
            Run("压力校正采样过期立即回零", PressureCalibrationStaleSampleTrips, ref passed);
            Run("压力校正超过120bar立即回零", PressureCalibrationOverpressureTrips, ref passed);
            Run("压力校正后台看门狗独立触发保护", PressureCalibrationWatchdogTrips, ref passed);
            Run("AO线性公式保存生成备份并可重载", CalibrationStoreIsAtomicAndReloadable, ref passed);
            return passed;
        }

        private static void AoCalibrationFitsLinearFormula()
        {
            var points = new[]
            {
                (Voltage: 0.2, Pressure: 0.0),
                (Voltage: 1.2, Pressure: 40.0),
                (Voltage: 1.95, Pressure: 70.0),
                (Voltage: 2.95, Pressure: 110.0)
            };
            Assert(CalibrationMath.TryFitPressureLine(
                    points,
                    out var scaleK,
                    out var offset,
                    out var rSquared,
                    out var error),
                error);
            Assert(Math.Abs(scaleK - 40) < 1e-9, "多点拟合斜率错误");
            Assert(Math.Abs(offset + 8) < 1e-9, "多点拟合截距错误");
            Assert(Math.Abs(rSquared - 1) < 1e-12, "完全线性数据的R²不是1");
        }

        private static void AoCalibrationRejectsZeroVoltageSpan()
        {
            Assert(!CalibrationMath.TryFitPressureLine(
                    new[] { (Voltage: 2.0, Pressure: 40.0), (Voltage: 2.0, Pressure: 70.0) },
                    out _, out _, out _, out var error) && error.Contains("电压"),
                "相同AO电压仍被接受用于线性拟合");
        }

        private static void MatchingCommandPressureUpdatesExistingPoint()
        {
            var existingCommands = new[] { 20.0, 100.0, 100.008, 120.0 };
            Assert(CalibrationMath.FindMatchingCommandIndex(existingCommands, 100.006, 0.01) == 2,
                "未优先更新容差内最接近的命令压力点");
            Assert(CalibrationMath.FindMatchingCommandIndex(existingCommands, 100.02, 0.01) == -1,
                "容差外命令压力被错误当作已有点更新");
            Assert(CalibrationMath.FindMatchingCommandIndex(existingCommands, 120.0, 0.01) == 3,
                "完全相同的命令压力未命中已有点");
        }

        private static void MixedCalibrationDetectsStaleHistoricalPoints()
        {
            var oneChanged = CalibrationMath.AnalyzeMixedCalibration(new[]
            {
                (Voltage: 1.0, Pressure: 30.0, IsChanged: true, IsHistorical: true),
                (Voltage: 2.0, Pressure: 60.0, IsChanged: false, IsHistorical: true),
                (Voltage: 3.0, Pressure: 90.0, IsChanged: false, IsHistorical: true)
            });
            Assert(oneChanged.HasMixedData && oneChanged.HasMaterialImpact &&
                   !oneChanged.CanEvaluateChangedTrend,
                "只更新一个历史点时未提示无法独立评估新趋势");

            var stalePoint = CalibrationMath.AnalyzeMixedCalibration(new[]
            {
                (Voltage: 1.0, Pressure: 32.0, IsChanged: true, IsHistorical: true),
                (Voltage: 2.0, Pressure: 72.0, IsChanged: true, IsHistorical: true),
                (Voltage: 3.0, Pressure: 90.0, IsChanged: false, IsHistorical: true)
            });
            Assert(stalePoint.HasMixedData && stalePoint.CanEvaluateChangedTrend &&
                   stalePoint.MaxUnchangedDeviationBar > stalePoint.EvaluationToleranceBar &&
                   stalePoint.HasMaterialImpact,
                "未识别出旧点明显偏离本次更新趋势");

            var allChanged = CalibrationMath.AnalyzeMixedCalibration(new[]
            {
                (Voltage: 1.0, Pressure: 32.0, IsChanged: true, IsHistorical: true),
                (Voltage: 2.0, Pressure: 72.0, IsChanged: true, IsHistorical: true)
            });
            Assert(!allChanged.HasMixedData && !allChanged.HasMaterialImpact,
                "全部历史点均已更新时仍误报新旧数据混用");
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

        private static void CalibrationStoreIsAtomicAndReloadable()
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBCalibrationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var aoPath = Path.Combine(root, "AOConfig.xml");
                File.WriteAllText(aoPath,
                    "<AOConfig><MinVoltage>0</MinVoltage><MaxVoltage>10</MaxVoltage>" +
                    "<MinPressure>0</MinPressure><MaxPressure>200</MaxPressure><Devices><Device>" +
                    "<Name>Cylinder1</Name><PhysicalChannel>Dev1/ao0</PhysicalChannel>" +
                    "<ScaleK>40</ScaleK><Offset>-8</Offset><VoltageToPressureTable />" +
                    "</Device></Devices></AOConfig>");
                CalibrationConfigStore.SaveAoLinearCalibration(
                    aoPath,
                    "Cylinder1",
                    new[]
                    {
                        new AoCalibrationPoint { Pressure = 0, Voltage = 0.2, CommandPressure = 0 },
                        new AoCalibrationPoint { Pressure = 70, Voltage = 1.95, CommandPressure = 75 },
                        new AoCalibrationPoint { Pressure = 110, Voltage = 2.95, CommandPressure = 115 }
                    },
                    0,
                    10);
                var ao = ConfigLoader.LoadAO(aoPath, Config.NullLogger.Instance);
                Assert(Math.Abs(ao.Devices["Cylinder1"].ScaleK - 40) < 1e-9 &&
                       Math.Abs(ao.Devices["Cylinder1"].Offset + 8) < 1e-9,
                    "AO线性公式保存后无法重载");
                Assert(ao.Devices["Cylinder1"].VoltageToPressure.Count == 3,
                    "AO校正审计点保存后无法重载");
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
