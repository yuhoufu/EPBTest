using System;
using System.Collections.Generic;
using System.Linq;

namespace Config
{
    public sealed class CalibrationMixAnalysis
    {
        public bool HasMixedData { get; internal set; }
        public int ChangedPointCount { get; internal set; }
        public int UnchangedHistoricalPointCount { get; internal set; }
        public bool CanEvaluateChangedTrend { get; internal set; }
        public double ChangedScaleK { get; internal set; } = double.NaN;
        public double ChangedOffset { get; internal set; } = double.NaN;
        public double MixedRSquared { get; internal set; } = double.NaN;
        public double MaxUnchangedDeviationBar { get; internal set; } = double.NaN;
        public double EvaluationToleranceBar { get; internal set; } = double.NaN;

        public bool HasMaterialImpact =>
            HasMixedData &&
            (!CanEvaluateChangedTrend ||
             !CalibrationMath.IsFinite(MixedRSquared) ||
             MixedRSquared < 0.995 ||
             MaxUnchangedDeviationBar > EvaluationToleranceBar);
    }

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

        /// <summary>
        /// 在已有命令压力中查找与当前命令最接近、且落在容差内的点。
        /// 返回 -1 表示应新增校正点；返回非负索引表示应原位更新，避免重复命令压力。
        /// </summary>
        public static int FindMatchingCommandIndex(
            IEnumerable<double> commandPressures,
            double commandPressure,
            double toleranceBar)
        {
            if (commandPressures == null || !IsFinite(commandPressure) ||
                !IsFinite(toleranceBar) || toleranceBar < 0)
                return -1;

            var bestIndex = -1;
            var bestDifference = double.MaxValue;
            var index = 0;
            foreach (var candidate in commandPressures)
            {
                if (IsFinite(candidate))
                {
                    var difference = Math.Abs(candidate - commandPressure);
                    if (difference <= toleranceBar && difference < bestDifference)
                    {
                        bestIndex = index;
                        bestDifference = difference;
                    }
                }
                index++;
            }
            return bestIndex;
        }

        /// <summary>
        /// 分析“本次更新点 + 未更新历史点”混合拟合时，旧点是否可能偏离本次更新趋势。
        /// 新增点和更新后的历史点都应标记为 IsChanged；从配置载入且未更新的点标记为 IsHistorical。
        /// </summary>
        public static CalibrationMixAnalysis AnalyzeMixedCalibration(
            IEnumerable<(double Voltage, double Pressure, bool IsChanged, bool IsHistorical)> points)
        {
            var values = points?
                .Where(x => AreFinite(x.Voltage, x.Pressure))
                .ToArray() ?? Array.Empty<(double Voltage, double Pressure, bool IsChanged, bool IsHistorical)>();
            var changed = values.Where(x => x.IsChanged).ToArray();
            var unchangedHistorical = values.Where(x => x.IsHistorical && !x.IsChanged).ToArray();
            var result = new CalibrationMixAnalysis
            {
                HasMixedData = changed.Length > 0 && unchangedHistorical.Length > 0,
                ChangedPointCount = changed.Length,
                UnchangedHistoricalPointCount = unchangedHistorical.Length
            };

            if (!result.HasMixedData) return result;

            if (TryFitPressureLine(
                    values.Select(x => (x.Voltage, x.Pressure)),
                    out _,
                    out _,
                    out var mixedRSquared,
                    out _))
                result.MixedRSquared = mixedRSquared;

            if (!TryFitPressureLine(
                    changed.Select(x => (x.Voltage, x.Pressure)),
                    out var changedScaleK,
                    out var changedOffset,
                    out _,
                    out _))
                return result;

            result.CanEvaluateChangedTrend = true;
            result.ChangedScaleK = changedScaleK;
            result.ChangedOffset = changedOffset;
            result.MaxUnchangedDeviationBar = unchangedHistorical.Max(x =>
                Math.Abs(x.Pressure - (x.Voltage * changedScaleK + changedOffset)));
            var pressureRange = values.Max(x => x.Pressure) - values.Min(x => x.Pressure);
            result.EvaluationToleranceBar = Math.Max(2.0, pressureRange * 0.02);
            return result;
        }

        private static bool AreFinite(params double[] values) =>
            values != null && values.All(IsFinite);
    }
}
