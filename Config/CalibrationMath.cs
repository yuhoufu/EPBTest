using System;
using System.Collections.Generic;
using System.Linq;

namespace Config
{
    /// <summary>液压 AO 线性校正的纯计算逻辑。</summary>
    public static class CalibrationMath
    {
        private const double Epsilon = 1e-9;

        /// <summary>
        /// 使用多个“AO 电压—实测压力”点，以最小二乘法拟合：
        /// pressure = voltage × scaleK + offset。
        /// </summary>
        public static bool TryFitPressureLine(
            IEnumerable<(double Voltage, double Pressure)> points,
            out double scaleK,
            out double offset,
            out double rSquared,
            out string error)
        {
            scaleK = double.NaN;
            offset = double.NaN;
            rSquared = double.NaN;
            error = string.Empty;
            if (points == null)
            {
                error = "校正点不能为空。";
                return false;
            }

            var values = points.ToArray();
            if (values.Length < 2)
            {
                error = "至少需要两个有效校正点。";
                return false;
            }
            if (values.Any(p => !AreFinite(p.Voltage, p.Pressure)))
            {
                error = "校正点必须是有限数值。";
                return false;
            }

            var meanVoltage = values.Average(p => p.Voltage);
            var meanPressure = values.Average(p => p.Pressure);
            var voltageVariance = values.Sum(p =>
                (p.Voltage - meanVoltage) * (p.Voltage - meanVoltage));
            if (voltageVariance <= Epsilon)
            {
                error = "AO 电压必须包含至少两个不同值。";
                return false;
            }

            var covariance = values.Sum(p =>
                (p.Voltage - meanVoltage) * (p.Pressure - meanPressure));
            scaleK = covariance / voltageVariance;
            offset = meanPressure - scaleK * meanVoltage;
            if (!AreFinite(scaleK, offset) || scaleK <= Epsilon)
            {
                error = "拟合得到的压力斜率必须大于零。";
                return false;
            }

            var total = values.Sum(p =>
                (p.Pressure - meanPressure) * (p.Pressure - meanPressure));
            if (total <= Epsilon)
            {
                error = "实测压力必须包含至少两个不同值。";
                return false;
            }

            var fittedScaleK = scaleK;
            var fittedOffset = offset;
            var residual = values.Sum(p =>
            {
                var delta = p.Pressure - (p.Voltage * fittedScaleK + fittedOffset);
                return delta * delta;
            });
            rSquared = Math.Max(0.0, Math.Min(1.0, 1.0 - residual / total));
            return IsFinite(rSquared);
        }

        public static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool AreFinite(params double[] values) =>
            values != null && values.All(IsFinite);
    }
}
