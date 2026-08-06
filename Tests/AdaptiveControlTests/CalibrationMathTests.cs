using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Config;
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
