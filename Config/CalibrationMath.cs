using System;
using System.Collections.Generic;
using System.Linq;

namespace Config
{
    /// <summary>传感器与液压 AO 标定的纯计算逻辑。</summary>
    public static class CalibrationMath
    {
        private const double Epsilon = 1e-9;

        /// <summary>
        /// 根据两个“原始电压—参考工程值”点计算 AI 配置。
        /// 工程值公式为：(raw - zero) * scale + offset。
        /// </summary>
        public static bool TryCalculateSensorTwoPoint(
            double raw1,
            double reference1,
            double raw2,
            double reference2,
            out double scale,
            out double offset,
            out double zero,
            out string error)
        {
            scale = 0;
            offset = 0;
            zero = 0;
            error = string.Empty;

            if (!AreFinite(raw1, reference1, raw2, reference2))
            {
                error = "标定点必须是有限数值。";
                return false;
            }

            var rawSpan = raw2 - raw1;
            if (Math.Abs(rawSpan) <= Epsilon)
            {
                error = "两个原始电压不能相同。";
                return false;
            }

            scale = (reference2 - reference1) / rawSpan;
            if (!IsFinite(scale) || Math.Abs(scale) <= Epsilon)
            {
                error = "两个参考值不能相同，且计算斜率必须有效。";
                return false;
            }

            zero = raw1;
            offset = reference1;
            return true;
        }

        /// <summary>
        /// 使用“实际压力—AO 电压”标定点，按压力反算电压。
        /// 标定范围外沿最近两点线性外推，最终仍受 AO 电压上下限保护。
        /// </summary>
        public static bool TryMapPressureToVoltage(
            IEnumerable<(double Voltage, double Pressure)> points,
            double targetPressure,
            double minVoltage,
            double maxVoltage,
            out double voltage)
        {
            voltage = double.NaN;
            if (points == null || !AreFinite(targetPressure, minVoltage, maxVoltage) ||
                maxVoltage <= minVoltage)
                return false;

            var ordered = points
                .Where(p => AreFinite(p.Voltage, p.Pressure) &&
                            p.Voltage >= minVoltage - Epsilon &&
                            p.Voltage <= maxVoltage + Epsilon)
                .OrderBy(p => p.Pressure)
                .ToArray();

            if (ordered.Length < 2) return false;

            for (var i = 1; i < ordered.Length; i++)
            {
                if (ordered[i].Pressure - ordered[i - 1].Pressure <= Epsilon ||
                    ordered[i].Voltage - ordered[i - 1].Voltage <= Epsilon)
                    return false;
            }

            var lower = ordered[0];
            var upper = ordered[1];
            if (targetPressure >= ordered[ordered.Length - 1].Pressure)
            {
                lower = ordered[ordered.Length - 2];
                upper = ordered[ordered.Length - 1];
            }
            else if (targetPressure > ordered[0].Pressure)
            {
                for (var i = 1; i < ordered.Length; i++)
                {
                    if (targetPressure <= ordered[i].Pressure)
                    {
                        lower = ordered[i - 1];
                        upper = ordered[i];
                        break;
                    }
                }
            }

            var ratio = (targetPressure - lower.Pressure) /
                        (upper.Pressure - lower.Pressure);
            var mapped = lower.Voltage + ratio * (upper.Voltage - lower.Voltage);
            voltage = Math.Max(minVoltage, Math.Min(maxVoltage, mapped));
            return IsFinite(voltage);
        }

        public static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool AreFinite(params double[] values) =>
            values != null && values.All(IsFinite);
    }
}
