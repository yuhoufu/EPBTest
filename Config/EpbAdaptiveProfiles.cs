using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Serialization;

namespace Config
{
    /// <summary>
    /// EPB 自适应控制模型集合。一个项目对应一个文件，每个通道对应一个 Profile。
    /// </summary>
    [XmlRoot("EpbAdaptiveProfiles")]
    public sealed class EpbAdaptiveProfiles
    {
        [XmlAttribute("ModelVersion")]
        public int ModelVersion { get; set; } = EpbAdaptiveProfile.CurrentModelVersion;

        [XmlElement("Profile")]
        public List<EpbAdaptiveProfile> Profiles { get; set; } = new List<EpbAdaptiveProfile>();
    }

    /// <summary>
    /// 一次终态 OFF 的因果时间线。四个时刻使用 UTC，分别表示算法作出断电决定、
    /// NI 写开始、物理写完成事件以及新鲜电流样本首次确认清零。
    /// </summary>
    public sealed class EpbDoTimingObservation
    {
        public DateTime DecisionUtc { get; set; }
        public DateTime DoWriteStartedUtc { get; set; }
        public DateTime DoWriteCompletedUtc { get; set; }
        public DateTime? CurrentClearedUtc { get; set; }

        public EpbDoTimingObservation Clone()
        {
            return new EpbDoTimingObservation
            {
                DecisionUtc = DecisionUtc,
                DoWriteStartedUtc = DoWriteStartedUtc,
                DoWriteCompletedUtc = DoWriteCompletedUtc,
                CurrentClearedUtc = CurrentClearedUtc
            };
        }

        public bool TryGetDurations(
            out double decisionToWriteStartedMs,
            out double doWriteDurationMs,
            out double decisionToWriteCompletedMs,
            out double currentClearAfterWriteMs)
        {
            decisionToWriteStartedMs = double.NaN;
            doWriteDurationMs = double.NaN;
            decisionToWriteCompletedMs = double.NaN;
            currentClearAfterWriteMs = double.NaN;
            if (!TryNormalizeUtc(DecisionUtc, out var decisionUtc) ||
                !TryNormalizeUtc(DoWriteStartedUtc, out var startedUtc) ||
                !TryNormalizeUtc(DoWriteCompletedUtc, out var completedUtc) ||
                startedUtc < decisionUtc ||
                completedUtc < startedUtc)
                return false;

            decisionToWriteStartedMs = (startedUtc - decisionUtc).TotalMilliseconds;
            doWriteDurationMs = (completedUtc - startedUtc).TotalMilliseconds;
            decisionToWriteCompletedMs = (completedUtc - decisionUtc).TotalMilliseconds;
            if (CurrentClearedUtc.HasValue &&
                TryNormalizeUtc(CurrentClearedUtc.Value, out var clearedUtc) &&
                clearedUtc >= completedUtc)
                currentClearAfterWriteMs = (clearedUtc - completedUtc).TotalMilliseconds;
            return IsFiniteNonNegative(decisionToWriteStartedMs) &&
                   IsFiniteNonNegative(doWriteDurationMs) &&
                   IsFiniteNonNegative(decisionToWriteCompletedMs);
        }

        private static bool TryNormalizeUtc(DateTime value, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (value == default || value == DateTime.MinValue || value == DateTime.MaxValue)
                return false;
            utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return true;
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        }
    }

    /// <summary>
    /// 单个 EPB 通道的自适应统计模型。
    /// </summary>
    public sealed class EpbAdaptiveProfile
    {
        // V6 在 V5 安全控制语义上增加明确的 DO 因果时间线和每通道 P95。
        // V5 的学习样本仍与当前策略兼容，可原位迁移；V1-V4 可能混入旧输出队列
        // 和 ThresholdBeforeLoadRise 误分类观测，不能跨策略直接复用。
        public const int CurrentModelVersion = 6;
        public const int MinimumCompatibleModelVersion = 5;
        private const int HistoryCapacity = 30;
        private const double MinimumCutoffSlopeAperMs = 0.001;
        private const double MaximumCutoffLeadMs = 100.0;
        private const double MaximumCurrentClearMs = 5000.0;

        [XmlAttribute]
        public int Channel { get; set; }

        [XmlAttribute]
        public int ModelVersion { get; set; } = CurrentModelVersion;

        public double ForwardEmptyCurrentA { get; set; }
        public double ForwardEmptyMadA { get; set; }
        public double ReverseEmptyCurrentA { get; set; }
        public double ReverseEmptyMadA { get; set; }
        public double ForwardClampMedianMs { get; set; }
        public double ForwardClampMadMs { get; set; }
        public double ReverseReleaseMedianMs { get; set; }
        public double ReverseReleaseMadMs { get; set; }
        public int ValidSampleCount { get; set; }
        public int ConsecutiveDeviationCount { get; set; }
        public double ForwardCutoffLeadMedianMs { get; set; }
        public double ForwardCutoffLeadMadMs { get; set; }
        public double ForwardPhysicalTailMedianMs { get; set; }
        public double ForwardPhysicalTailMadMs { get; set; }
        public double ForwardDoCompletionMedianMs { get; set; }
        public double ForwardDoCompletionMadMs { get; set; }
        public double ForwardDoCompletionP95Ms { get; set; }
        public double ForwardDoStartDelayMedianMs { get; set; }
        public double ForwardDoStartDelayP95Ms { get; set; }
        public double ForwardDoWriteMedianMs { get; set; }
        public double ForwardDoWriteP95Ms { get; set; }
        public double ForwardCurrentClearMedianMs { get; set; }
        public double ForwardCurrentClearP95Ms { get; set; }
        public double ForwardPeakErrorMedianA { get; set; }
        public double ForwardPeakErrorMadA { get; set; }
        public int ValidCutoffSampleCount { get; set; }
        public int ValidDoCompletionSampleCount { get; set; }
        public int ValidDoTimingSampleCount { get; set; }
        public int ValidCurrentClearSampleCount { get; set; }
        public int ConsecutiveForwardOvershootCount { get; set; }
        public int ConsecutivePeakEvidenceMismatchCount { get; set; }
        public int ConsecutiveForwardStallCount { get; set; }
        public DateTime UpdatedUtc { get; set; }

        [XmlArrayItem("Value")]
        public List<double> ForwardEmptyHistoryA { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ReverseEmptyHistoryA { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ForwardClampHistoryMs { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ReverseReleaseHistoryMs { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ForwardCutoffLeadHistoryMs { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ForwardPhysicalTailHistoryMs { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ForwardDoCompletionHistoryMs { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ForwardDoStartDelayHistoryMs { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ForwardDoWriteHistoryMs { get; set; } = new List<double>();

        [XmlArrayItem("Value")]
        public List<double> ForwardCurrentClearHistoryMs { get; set; } = new List<double>();

        public EpbDoTimingObservation LastForwardDoTiming { get; set; }

        [XmlArrayItem("Value")]
        public List<double> ForwardPeakErrorHistoryA { get; set; } = new List<double>();

        [XmlIgnore]
        public bool IsStable => ValidSampleCount >= 5;

        [XmlIgnore]
        public bool HasCutoffPrediction =>
            ValidCutoffSampleCount > 0 && ForwardCutoffLeadMedianMs >= 0;

        public bool AddSuccessfulCycle(
            double forwardEmptyA,
            double reverseEmptyA,
            double forwardClampMs,
            double reverseReleaseMs)
        {
            var deviated = IsStable &&
                           (RelativeDeviation(forwardClampMs, ForwardClampMedianMs) > 0.30 ||
                            RelativeDeviation(reverseReleaseMs, ReverseReleaseMedianMs) > 0.30);

            ConsecutiveDeviationCount = deviated ? ConsecutiveDeviationCount + 1 : 0;

            AddBounded(ForwardEmptyHistoryA, forwardEmptyA);
            AddBounded(ReverseEmptyHistoryA, reverseEmptyA);
            AddBounded(ForwardClampHistoryMs, forwardClampMs);
            AddBounded(ReverseReleaseHistoryMs, reverseReleaseMs);

            ForwardEmptyCurrentA = Median(ForwardEmptyHistoryA);
            ForwardEmptyMadA = Mad(ForwardEmptyHistoryA, ForwardEmptyCurrentA);
            ReverseEmptyCurrentA = Median(ReverseEmptyHistoryA);
            ReverseEmptyMadA = Mad(ReverseEmptyHistoryA, ReverseEmptyCurrentA);
            ForwardClampMedianMs = Median(ForwardClampHistoryMs);
            ForwardClampMadMs = Mad(ForwardClampHistoryMs, ForwardClampMedianMs);
            ReverseReleaseMedianMs = Median(ReverseReleaseHistoryMs);
            ReverseReleaseMadMs = Mad(ReverseReleaseHistoryMs, ReverseReleaseMedianMs);
            ValidSampleCount++;
            ModelVersion = CurrentModelVersion;
            UpdatedUtc = DateTime.UtcNow;

            return ConsecutiveDeviationCount >= 3;
        }

        /// <summary>
        /// 将一次有效的正向断电观测写入独立控流模型。
        /// 等效提前时间使用实际尾部电流增量除以断电判定时斜率，
        /// 从而可随下一圈实时斜率自动缩放提前断电电流。
        /// </summary>
        public bool TryAddCutoffObservation(
            double targetA,
            double cutoffCurrentA,
            double cutoffSlopeAperMs,
            double actualPeakA,
            out double equivalentLeadMs)
        {
            return TryAddCutoffObservation(
                targetA,
                cutoffCurrentA,
                cutoffSlopeAperMs,
                actualPeakA,
                double.NaN,
                out equivalentLeadMs);
        }

        /// <summary>
        /// 写入断电观测，并把控制器至 DO 驱动返回的实测完成时间从物理尾升时间中分离。
        /// 下一圈提前量由“物理尾升中位数 + 本通道 DO 完成延迟中位数”组成；单圈新值在
        /// 已有稳定历史的鲁棒范围内限幅，避免一次线程调度尖峰污染长期模型。
        /// </summary>
        public bool TryAddCutoffObservation(
            double targetA,
            double cutoffCurrentA,
            double cutoffSlopeAperMs,
            double actualPeakA,
            double doCompletionMs,
            out double equivalentLeadMs)
        {
            return TryAddCutoffObservationCore(
                targetA,
                cutoffCurrentA,
                cutoffSlopeAperMs,
                actualPeakA,
                doCompletionMs,
                null,
                out equivalentLeadMs);
        }

        /// <summary>
        /// 使用四时刻因果证据写入断电观测。总完成延迟用于现有提前量模型；
        /// 决策至写开始、写持续和完成至电流清零分别形成独立的 median/P95 分布。
        /// </summary>
        public bool TryAddCutoffObservation(
            double targetA,
            double cutoffCurrentA,
            double cutoffSlopeAperMs,
            double actualPeakA,
            EpbDoTimingObservation doTiming,
            out double equivalentLeadMs)
        {
            var doCompletionMs = double.NaN;
            if (doTiming != null &&
                doTiming.TryGetDurations(
                    out _,
                    out _,
                    out var measuredCompletionMs,
                    out _))
                doCompletionMs = measuredCompletionMs;
            return TryAddCutoffObservationCore(
                targetA,
                cutoffCurrentA,
                cutoffSlopeAperMs,
                actualPeakA,
                doCompletionMs,
                doTiming,
                out equivalentLeadMs);
        }

        private bool TryAddCutoffObservationCore(
            double targetA,
            double cutoffCurrentA,
            double cutoffSlopeAperMs,
            double actualPeakA,
            double doCompletionMs,
            EpbDoTimingObservation doTiming,
            out double equivalentLeadMs)
        {
            equivalentLeadMs = 0;
            if (!IsFinitePositive(targetA) ||
                !IsFiniteNonNegative(cutoffCurrentA) ||
                !IsFinitePositive(actualPeakA) ||
                !IsFinitePositive(cutoffSlopeAperMs) ||
                cutoffSlopeAperMs < MinimumCutoffSlopeAperMs)
                return false;

            EnsureHistoryLists();

            var tailRiseA = Math.Max(0, actualPeakA - cutoffCurrentA);
            equivalentLeadMs = Math.Min(
                MaximumCutoffLeadMs,
                tailRiseA / cutoffSlopeAperMs);

            var hasDoCompletion = IsFiniteNonNegative(doCompletionMs);
            if (hasDoCompletion)
            {
                var boundedDoCompletionMs = Math.Min(MaximumCutoffLeadMs, doCompletionMs);
                var physicalTailMs = Math.Max(0, equivalentLeadMs - boundedDoCompletionMs);
                AddBoundedRobust(ForwardPhysicalTailHistoryMs, physicalTailMs);
                AddBoundedRobust(ForwardDoCompletionHistoryMs, boundedDoCompletionMs);
                ForwardPhysicalTailMedianMs = Median(ForwardPhysicalTailHistoryMs);
                ForwardPhysicalTailMadMs = Mad(
                    ForwardPhysicalTailHistoryMs,
                    ForwardPhysicalTailMedianMs);
                ForwardDoCompletionMedianMs = Median(ForwardDoCompletionHistoryMs);
                ForwardDoCompletionMadMs = Mad(
                    ForwardDoCompletionHistoryMs,
                    ForwardDoCompletionMedianMs);
                ForwardDoCompletionP95Ms = Percentile(
                    ForwardDoCompletionHistoryMs,
                    0.95);
                ValidDoCompletionSampleCount++;

                if (doTiming != null &&
                    doTiming.TryGetDurations(
                        out var startDelayMs,
                        out var writeDurationMs,
                        out _,
                        out var currentClearMs))
                {
                    AddBoundedRobust(
                        ForwardDoStartDelayHistoryMs,
                        Math.Min(MaximumCutoffLeadMs, startDelayMs));
                    AddBoundedRobust(
                        ForwardDoWriteHistoryMs,
                        Math.Min(MaximumCutoffLeadMs, writeDurationMs));
                    ForwardDoStartDelayMedianMs = Median(ForwardDoStartDelayHistoryMs);
                    ForwardDoStartDelayP95Ms = Percentile(
                        ForwardDoStartDelayHistoryMs,
                        0.95);
                    ForwardDoWriteMedianMs = Median(ForwardDoWriteHistoryMs);
                    ForwardDoWriteP95Ms = Percentile(ForwardDoWriteHistoryMs, 0.95);
                    ValidDoTimingSampleCount++;

                    if (IsFiniteNonNegative(currentClearMs))
                    {
                        AddBoundedRobust(
                            ForwardCurrentClearHistoryMs,
                            Math.Min(MaximumCurrentClearMs, currentClearMs));
                        ForwardCurrentClearMedianMs = Median(ForwardCurrentClearHistoryMs);
                        ForwardCurrentClearP95Ms = Percentile(
                            ForwardCurrentClearHistoryMs,
                            0.95);
                        ValidCurrentClearSampleCount++;
                    }
                    LastForwardDoTiming = doTiming.Clone();
                }

                equivalentLeadMs = Math.Min(
                    MaximumCutoffLeadMs,
                    ForwardPhysicalTailMedianMs + ForwardDoCompletionMedianMs);
            }

            AddBoundedRobust(ForwardCutoffLeadHistoryMs, equivalentLeadMs);
            AddBoundedSigned(ForwardPeakErrorHistoryA, actualPeakA - targetA);
            ForwardCutoffLeadMedianMs = Median(ForwardCutoffLeadHistoryMs);
            ForwardCutoffLeadMadMs = Mad(ForwardCutoffLeadHistoryMs, ForwardCutoffLeadMedianMs);
            ForwardPeakErrorMedianA = Median(ForwardPeakErrorHistoryA);
            ForwardPeakErrorMadA = Mad(ForwardPeakErrorHistoryA, ForwardPeakErrorMedianA);
            ValidCutoffSampleCount++;
            ModelVersion = CurrentModelVersion;
            UpdatedUtc = DateTime.UtcNow;
            return true;
        }

        public int GetForwardSoftLimitMs()
        {
            if (!IsStable || ForwardClampMedianMs <= 0) return 0;
            return (int)Math.Ceiling(ForwardClampMedianMs + Math.Max(1000.0, 4.0 * ForwardClampMadMs));
        }

        /// <summary>
        /// 返回预测峰值的正向系统偏差补偿。
        /// 中位数抵消长期偏高，额外一个 MAD 为离散性留出鲁棒余量；
        /// 上限避免异常历史样本导致过早断电。
        /// </summary>
        public double GetForwardPeakBiasCorrectionA(double maximumA = 1.0)
        {
            if (ValidCutoffSampleCount < 5) return 0;
            var correction = ForwardPeakErrorMedianA + ForwardPeakErrorMadA;
            if (double.IsNaN(correction) || double.IsInfinity(correction) || correction <= 0)
                return 0;
            return Math.Min(Math.Max(0, maximumA), correction);
        }

        /// <summary>
        /// 更新正向完整峰值超过永久报警线的连续正式圈数；回到线内立即清零。
        /// </summary>
        public int UpdateForwardOvershootStreak(double peakErrorA, double warningDeltaA)
        {
            var exceeded =
                !double.IsNaN(peakErrorA) &&
                !double.IsInfinity(peakErrorA) &&
                peakErrorA > Math.Max(0, warningDeltaA);
            ConsecutiveForwardOvershootCount = exceeded
                ? ConsecutiveForwardOvershootCount + 1
                : 0;
            UpdatedUtc = DateTime.UtcNow;
            return ConsecutiveForwardOvershootCount;
        }

        public int UpdateForwardPermanentOvershootStreak(bool exceeded)
        {
            ConsecutiveForwardOvershootCount = exceeded
                ? ConsecutiveForwardOvershootCount + 1
                : 0;
            ModelVersion = CurrentModelVersion;
            UpdatedUtc = DateTime.UtcNow;
            return ConsecutiveForwardOvershootCount;
        }

        /// <summary>
        /// 更新有效峰值证据偏差的连续圈数；任一有效匹配圈立即清零。
        /// 调用方必须先排除捕获无效和证据处理滞后，避免把数据质量问题计为物理偏差。
        /// </summary>
        public int UpdatePeakEvidenceMismatchStreak(bool mismatched)
        {
            ConsecutivePeakEvidenceMismatchCount = mismatched
                ? ConsecutivePeakEvidenceMismatchCount + 1
                : 0;
            ModelVersion = CurrentModelVersion;
            UpdatedUtc = DateTime.UtcNow;
            return ConsecutivePeakEvidenceMismatchCount;
        }

        /// <summary>
        /// 更新正向低于合格下限的平台停滞连续圈数；任一正常或近目标圈立即清零。
        /// </summary>
        public int UpdateForwardStallStreak(bool lowTargetPlateau)
        {
            ConsecutiveForwardStallCount = lowTargetPlateau
                ? ConsecutiveForwardStallCount + 1
                : 0;
            ModelVersion = CurrentModelVersion;
            UpdatedUtc = DateTime.UtcNow;
            return ConsecutiveForwardStallCount;
        }

        /// <summary>
        /// 显式重新开始/恢复时清除上一运行留下的瞬态连续故障计数，保留已经学习到的
        /// 电流、时间和控流历史。连续故障只允许由本次运行的新完整圈重新建立。
        /// </summary>
        public bool ResetTransientFaultStreaks()
        {
            var changed = ConsecutiveDeviationCount != 0 ||
                          ConsecutiveForwardOvershootCount != 0 ||
                          ConsecutivePeakEvidenceMismatchCount != 0 ||
                          ConsecutiveForwardStallCount != 0;
            ConsecutiveDeviationCount = 0;
            ConsecutiveForwardOvershootCount = 0;
            ConsecutivePeakEvidenceMismatchCount = 0;
            ConsecutiveForwardStallCount = 0;
            if (changed)
            {
                ModelVersion = CurrentModelVersion;
                UpdatedUtc = DateTime.UtcNow;
            }
            return changed;
        }

        public EpbAdaptiveProfile Clone()
        {
            return new EpbAdaptiveProfile
            {
                Channel = Channel,
                ModelVersion = ModelVersion,
                ForwardEmptyCurrentA = ForwardEmptyCurrentA,
                ForwardEmptyMadA = ForwardEmptyMadA,
                ReverseEmptyCurrentA = ReverseEmptyCurrentA,
                ReverseEmptyMadA = ReverseEmptyMadA,
                ForwardClampMedianMs = ForwardClampMedianMs,
                ForwardClampMadMs = ForwardClampMadMs,
                ReverseReleaseMedianMs = ReverseReleaseMedianMs,
                ReverseReleaseMadMs = ReverseReleaseMadMs,
                ValidSampleCount = ValidSampleCount,
                ConsecutiveDeviationCount = ConsecutiveDeviationCount,
                ForwardCutoffLeadMedianMs = ForwardCutoffLeadMedianMs,
                ForwardCutoffLeadMadMs = ForwardCutoffLeadMadMs,
                ForwardPhysicalTailMedianMs = ForwardPhysicalTailMedianMs,
                ForwardPhysicalTailMadMs = ForwardPhysicalTailMadMs,
                ForwardDoCompletionMedianMs = ForwardDoCompletionMedianMs,
                ForwardDoCompletionMadMs = ForwardDoCompletionMadMs,
                ForwardDoCompletionP95Ms = ForwardDoCompletionP95Ms,
                ForwardDoStartDelayMedianMs = ForwardDoStartDelayMedianMs,
                ForwardDoStartDelayP95Ms = ForwardDoStartDelayP95Ms,
                ForwardDoWriteMedianMs = ForwardDoWriteMedianMs,
                ForwardDoWriteP95Ms = ForwardDoWriteP95Ms,
                ForwardCurrentClearMedianMs = ForwardCurrentClearMedianMs,
                ForwardCurrentClearP95Ms = ForwardCurrentClearP95Ms,
                ForwardPeakErrorMedianA = ForwardPeakErrorMedianA,
                ForwardPeakErrorMadA = ForwardPeakErrorMadA,
                ValidCutoffSampleCount = ValidCutoffSampleCount,
                ValidDoCompletionSampleCount = ValidDoCompletionSampleCount,
                ValidDoTimingSampleCount = ValidDoTimingSampleCount,
                ValidCurrentClearSampleCount = ValidCurrentClearSampleCount,
                ConsecutiveForwardOvershootCount = ConsecutiveForwardOvershootCount,
                ConsecutivePeakEvidenceMismatchCount = ConsecutivePeakEvidenceMismatchCount,
                ConsecutiveForwardStallCount = ConsecutiveForwardStallCount,
                UpdatedUtc = UpdatedUtc,
                ForwardEmptyHistoryA = new List<double>(ForwardEmptyHistoryA ?? new List<double>()),
                ReverseEmptyHistoryA = new List<double>(ReverseEmptyHistoryA ?? new List<double>()),
                ForwardClampHistoryMs = new List<double>(ForwardClampHistoryMs ?? new List<double>()),
                ReverseReleaseHistoryMs = new List<double>(ReverseReleaseHistoryMs ?? new List<double>()),
                ForwardCutoffLeadHistoryMs =
                    new List<double>(ForwardCutoffLeadHistoryMs ?? new List<double>()),
                ForwardPhysicalTailHistoryMs =
                    new List<double>(ForwardPhysicalTailHistoryMs ?? new List<double>()),
                ForwardDoCompletionHistoryMs =
                    new List<double>(ForwardDoCompletionHistoryMs ?? new List<double>()),
                ForwardDoStartDelayHistoryMs =
                    new List<double>(ForwardDoStartDelayHistoryMs ?? new List<double>()),
                ForwardDoWriteHistoryMs =
                    new List<double>(ForwardDoWriteHistoryMs ?? new List<double>()),
                ForwardCurrentClearHistoryMs =
                    new List<double>(ForwardCurrentClearHistoryMs ?? new List<double>()),
                LastForwardDoTiming = LastForwardDoTiming?.Clone(),
                ForwardPeakErrorHistoryA =
                    new List<double>(ForwardPeakErrorHistoryA ?? new List<double>())
            };
        }

        /// <summary>
        /// 原位恢复到指定快照。用于软件自愈作废一次已经执行、但证据未能可靠落盘的尝试，
        /// 确保作废圈既不计正式次数，也不污染后续控制模型和连续故障计数。
        /// </summary>
        public void RestoreFrom(EpbAdaptiveProfile snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var copy = snapshot.Clone();
            Channel = copy.Channel;
            ModelVersion = copy.ModelVersion;
            ForwardEmptyCurrentA = copy.ForwardEmptyCurrentA;
            ForwardEmptyMadA = copy.ForwardEmptyMadA;
            ReverseEmptyCurrentA = copy.ReverseEmptyCurrentA;
            ReverseEmptyMadA = copy.ReverseEmptyMadA;
            ForwardClampMedianMs = copy.ForwardClampMedianMs;
            ForwardClampMadMs = copy.ForwardClampMadMs;
            ReverseReleaseMedianMs = copy.ReverseReleaseMedianMs;
            ReverseReleaseMadMs = copy.ReverseReleaseMadMs;
            ValidSampleCount = copy.ValidSampleCount;
            ConsecutiveDeviationCount = copy.ConsecutiveDeviationCount;
            ForwardCutoffLeadMedianMs = copy.ForwardCutoffLeadMedianMs;
            ForwardCutoffLeadMadMs = copy.ForwardCutoffLeadMadMs;
            ForwardPhysicalTailMedianMs = copy.ForwardPhysicalTailMedianMs;
            ForwardPhysicalTailMadMs = copy.ForwardPhysicalTailMadMs;
            ForwardDoCompletionMedianMs = copy.ForwardDoCompletionMedianMs;
            ForwardDoCompletionMadMs = copy.ForwardDoCompletionMadMs;
            ForwardDoCompletionP95Ms = copy.ForwardDoCompletionP95Ms;
            ForwardDoStartDelayMedianMs = copy.ForwardDoStartDelayMedianMs;
            ForwardDoStartDelayP95Ms = copy.ForwardDoStartDelayP95Ms;
            ForwardDoWriteMedianMs = copy.ForwardDoWriteMedianMs;
            ForwardDoWriteP95Ms = copy.ForwardDoWriteP95Ms;
            ForwardCurrentClearMedianMs = copy.ForwardCurrentClearMedianMs;
            ForwardCurrentClearP95Ms = copy.ForwardCurrentClearP95Ms;
            ForwardPeakErrorMedianA = copy.ForwardPeakErrorMedianA;
            ForwardPeakErrorMadA = copy.ForwardPeakErrorMadA;
            ValidCutoffSampleCount = copy.ValidCutoffSampleCount;
            ValidDoCompletionSampleCount = copy.ValidDoCompletionSampleCount;
            ValidDoTimingSampleCount = copy.ValidDoTimingSampleCount;
            ValidCurrentClearSampleCount = copy.ValidCurrentClearSampleCount;
            ConsecutiveForwardOvershootCount = copy.ConsecutiveForwardOvershootCount;
            ConsecutivePeakEvidenceMismatchCount = copy.ConsecutivePeakEvidenceMismatchCount;
            ConsecutiveForwardStallCount = copy.ConsecutiveForwardStallCount;
            UpdatedUtc = copy.UpdatedUtc;
            ForwardEmptyHistoryA = copy.ForwardEmptyHistoryA;
            ReverseEmptyHistoryA = copy.ReverseEmptyHistoryA;
            ForwardClampHistoryMs = copy.ForwardClampHistoryMs;
            ReverseReleaseHistoryMs = copy.ReverseReleaseHistoryMs;
            ForwardCutoffLeadHistoryMs = copy.ForwardCutoffLeadHistoryMs;
            ForwardPhysicalTailHistoryMs = copy.ForwardPhysicalTailHistoryMs;
            ForwardDoCompletionHistoryMs = copy.ForwardDoCompletionHistoryMs;
            ForwardDoStartDelayHistoryMs = copy.ForwardDoStartDelayHistoryMs;
            ForwardDoWriteHistoryMs = copy.ForwardDoWriteHistoryMs;
            ForwardCurrentClearHistoryMs = copy.ForwardCurrentClearHistoryMs;
            LastForwardDoTiming = copy.LastForwardDoTiming;
            ForwardPeakErrorHistoryA = copy.ForwardPeakErrorHistoryA;
        }

        internal void UpgradeCompatibleModel()
        {
            EnsureHistoryLists();
            if (ForwardDoCompletionHistoryMs.Count > 0)
            {
                ForwardDoCompletionMedianMs = Median(ForwardDoCompletionHistoryMs);
                ForwardDoCompletionMadMs = Mad(
                    ForwardDoCompletionHistoryMs,
                    ForwardDoCompletionMedianMs);
                ForwardDoCompletionP95Ms = Percentile(
                    ForwardDoCompletionHistoryMs,
                    0.95);
            }
            if (ForwardDoStartDelayHistoryMs.Count > 0)
            {
                ForwardDoStartDelayMedianMs = Median(ForwardDoStartDelayHistoryMs);
                ForwardDoStartDelayP95Ms = Percentile(
                    ForwardDoStartDelayHistoryMs,
                    0.95);
            }
            if (ForwardDoWriteHistoryMs.Count > 0)
            {
                ForwardDoWriteMedianMs = Median(ForwardDoWriteHistoryMs);
                ForwardDoWriteP95Ms = Percentile(ForwardDoWriteHistoryMs, 0.95);
            }
            if (ForwardCurrentClearHistoryMs.Count > 0)
            {
                ForwardCurrentClearMedianMs = Median(ForwardCurrentClearHistoryMs);
                ForwardCurrentClearP95Ms = Percentile(
                    ForwardCurrentClearHistoryMs,
                    0.95);
            }
            ModelVersion = CurrentModelVersion;
        }

        private void EnsureHistoryLists()
        {
            ForwardEmptyHistoryA = ForwardEmptyHistoryA ?? new List<double>();
            ReverseEmptyHistoryA = ReverseEmptyHistoryA ?? new List<double>();
            ForwardClampHistoryMs = ForwardClampHistoryMs ?? new List<double>();
            ReverseReleaseHistoryMs = ReverseReleaseHistoryMs ?? new List<double>();
            ForwardCutoffLeadHistoryMs = ForwardCutoffLeadHistoryMs ?? new List<double>();
            ForwardPhysicalTailHistoryMs = ForwardPhysicalTailHistoryMs ?? new List<double>();
            ForwardDoCompletionHistoryMs = ForwardDoCompletionHistoryMs ?? new List<double>();
            ForwardDoStartDelayHistoryMs = ForwardDoStartDelayHistoryMs ?? new List<double>();
            ForwardDoWriteHistoryMs = ForwardDoWriteHistoryMs ?? new List<double>();
            ForwardCurrentClearHistoryMs = ForwardCurrentClearHistoryMs ?? new List<double>();
            ForwardPeakErrorHistoryA = ForwardPeakErrorHistoryA ?? new List<double>();
        }

        private static void AddBounded(List<double> values, double value)
        {
            if (values == null || double.IsNaN(value) || double.IsInfinity(value) || value < 0) return;
            values.Add(value);
            while (values.Count > HistoryCapacity) values.RemoveAt(0);
        }

        private static void AddBoundedSigned(List<double> values, double value)
        {
            if (values == null || double.IsNaN(value) || double.IsInfinity(value)) return;
            values.Add(value);
            while (values.Count > HistoryCapacity) values.RemoveAt(0);
        }

        private static void AddBoundedRobust(List<double> values, double value)
        {
            if (values == null || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                return;
            if (values.Count >= 5)
            {
                var median = Median(values);
                var mad = Mad(values, median);
                var maximumStep = Math.Max(5.0, 3.0 * mad);
                value = Math.Max(0, Math.Min(median + maximumStep, Math.Max(median - maximumStep, value)));
            }
            AddBounded(values, value);
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        }

        private static double Median(IEnumerable<double> source)
        {
            var values = (source ?? Enumerable.Empty<double>())
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x))
                .OrderBy(x => x)
                .ToArray();
            if (values.Length == 0) return 0;
            var mid = values.Length / 2;
            return values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2.0 : values[mid];
        }

        private static double Mad(IEnumerable<double> source, double median)
        {
            return Median((source ?? Enumerable.Empty<double>()).Select(x => Math.Abs(x - median)));
        }

        private static double Percentile(IEnumerable<double> source, double fraction)
        {
            var values = (source ?? Enumerable.Empty<double>())
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x))
                .OrderBy(x => x)
                .ToArray();
            if (values.Length == 0) return 0;
            var boundedFraction = Math.Max(0, Math.Min(1, fraction));
            var index = Math.Max(
                0,
                Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * boundedFraction) - 1));
            return values[index];
        }

        private static double RelativeDeviation(double actual, double baseline)
        {
            if (baseline <= 1e-9) return 0;
            return Math.Abs(actual - baseline) / baseline;
        }
    }

    /// <summary>
    /// The model file was replaced but the pre-transaction bytes could not be
    /// restored.  This is a fatal persistence boundary: callers must stop the
    /// learning transaction and retain <see cref="BackupPath"/> for recovery.
    /// It intentionally derives from <see cref="OperationCanceledException"/>
    /// so the existing learning orchestration does not route this condition
    /// through its ordinary software self-healing retry loop.
    /// </summary>
    public sealed class EpbAdaptiveProfilePersistenceFatalException : OperationCanceledException
    {
        public EpbAdaptiveProfilePersistenceFatalException(
            string message,
            string backupPath,
            Exception writeFailure,
            Exception rollbackFailure)
            : base(message, rollbackFailure)
        {
            BackupPath = backupPath ?? string.Empty;
            WriteFailure = writeFailure;
            RollbackFailure = rollbackFailure;
        }

        public string BackupPath { get; }
        public Exception WriteFailure { get; }
        public Exception RollbackFailure { get; }
        public bool IsFatal => true;
    }

    /// <summary>
    /// 项目级 EPB 自适应模型存储。写入采用同目录临时文件 + Replace，避免半写文件。
    /// </summary>
    public sealed class EpbAdaptiveProfileStore
    {
        /// <summary>
        /// Optional deterministic fault hook used by disk durability tests.
        /// The callback is invoked after the atomic replace/copy and before the
        /// read-back.  Production leaves it null.
        /// </summary>
        public static Action<string> SaveWithReceiptFailureInjection { get; set; }

        private readonly object _gate = new object();
        private readonly string _path;
        private readonly IAppLogger _log;
        private EpbAdaptiveProfiles _document;

        public EpbAdaptiveProfileStore(string projectConfigDirectory, IAppLogger log = null)
        {
            if (string.IsNullOrWhiteSpace(projectConfigDirectory))
                throw new ArgumentException("项目 Config 目录不能为空。", nameof(projectConfigDirectory));

            _path = Path.Combine(projectConfigDirectory, "EpbAdaptiveProfiles.xml");
            _log = log ?? NullLogger.Instance;
            _document = LoadDocument();
        }

        public string FilePath => _path;

        public EpbAdaptiveProfile GetOrCreate(int channel)
        {
            if (channel < 1 || channel > 12) throw new ArgumentOutOfRangeException(nameof(channel));
            lock (_gate)
            {
                var profile = _document.Profiles.FirstOrDefault(x => x.Channel == channel);
                if (profile == null)
                {
                    profile = new EpbAdaptiveProfile { Channel = channel };
                    _document.Profiles.Add(profile);
                }

                return profile.Clone();
            }
        }

        public void Save(EpbAdaptiveProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.Channel < 1 || profile.Channel > 12)
                throw new ArgumentOutOfRangeException(nameof(profile.Channel));

            lock (_gate)
            {
                var index = _document.Profiles.FindIndex(x => x.Channel == profile.Channel);
                if (index >= 0) _document.Profiles[index] = profile.Clone();
                else _document.Profiles.Add(profile.Clone());

                _document.ModelVersion = EpbAdaptiveProfile.CurrentModelVersion;
                SaveDocumentAtomic(_document);
            }
        }

        /// <summary>
        /// Persist one channel profile and return a read-back receipt.  The
        /// existing Save API remains unchanged for formal-cycle callers; this
        /// stricter path is used by learning finalization so a caller can prove
        /// that the model reached disk before publishing a successful manifest.
        /// </summary>
        public string SaveWithReceipt(EpbAdaptiveProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            lock (_gate)
            {
                var before = new EpbAdaptiveProfiles
                {
                    ModelVersion = _document.ModelVersion,
                    Profiles = (_document.Profiles ?? new List<EpbAdaptiveProfile>())
                        .Where(item => item != null)
                        .Select(item => item.Clone())
                        .ToList()
                };
                var diskBackup = _path + ".save-with-receipt." + Guid.NewGuid().ToString("N") + ".bak";
                var hadDiskFile = File.Exists(_path);
                var keepDiskBackup = false;
                try
                {
                    if (hadDiskFile) File.Copy(_path, diskBackup, true);
                    var index = _document.Profiles.FindIndex(item => item.Channel == profile.Channel);
                    if (index >= 0) _document.Profiles[index] = profile.Clone();
                    else _document.Profiles.Add(profile.Clone());
                    _document.ModelVersion = EpbAdaptiveProfile.CurrentModelVersion;
                    SaveDocumentAtomic(_document);

                    SaveWithReceiptFailureInjection?.Invoke("AfterReplaceBeforeReadback");

                    // Deserialize the bytes that are now on disk, rather than
                    // trusting the in-memory document that was just updated.
                    var persisted = LoadDocument();
                    var readback = persisted.Profiles.FirstOrDefault(item =>
                        item != null && item.Channel == profile.Channel);
                    if (!ProfilesMatch(readback, profile))
                        throw new InvalidDataException(
                            $"EPB[{profile.Channel}] 自适应模型写入后读回不一致。");
                    _document = persisted;
                    using (var stream = File.OpenRead(_path))
                    using (var sha = SHA256.Create())
                        return "sha256:" + BitConverter.ToString(sha.ComputeHash(stream))
                            .Replace("-", string.Empty).ToLowerInvariant();
                }
                catch (Exception writeFailure)
                {
                    // A Replace may have succeeded even when deserialize/readback
                    // fails.  Restore the exact pre-transaction bytes on disk and
                    // verify the restoration before rethrowing.
                    try
                    {
                        // Tests and field diagnostics can force the rollback
                        // boundary to fail.  Never swallow that second failure:
                        // the backup must remain available for operator recovery.
                        SaveWithReceiptFailureInjection?.Invoke("BeforeRollback");
                        // Keep the shorter alias for existing fault-injection
                        // harnesses that label the same boundary simply
                        // "Rollback".
                        SaveWithReceiptFailureInjection?.Invoke("Rollback");
                        RestoreDiskSnapshot(diskBackup, hadDiskFile);
                    }
                    catch (Exception restoreEx)
                    {
                        keepDiskBackup = hadDiskFile && File.Exists(diskBackup);
                        _log.Warn(
                            $"自适应模型读回失败后磁盘回滚复核失败：{restoreEx.Message}；文件={_path}",
                            "EPB");
                        _document = before;
                        throw new EpbAdaptiveProfilePersistenceFatalException(
                            $"EPB[{profile.Channel}] 自适应模型写入失败且磁盘回滚失败；" +
                            $"必须停止当前学习事务并保留备份：{diskBackup}",
                            diskBackup,
                            writeFailure,
                            restoreEx);
                    }
                    // SaveDocumentAtomic may fail after the working document
                    // was modified.  Restore that snapshot so a failed learning
                    // transaction cannot advance the model in memory.
                    _document = before;
                    throw;
                }
                finally
                {
                    // A failed rollback is itself a durable incident.  Keep the
                    // exact pre-transaction backup instead of deleting the only
                    // known-good copy in cleanup.
                    if (!keepDiskBackup)
                    {
                        try { if (File.Exists(diskBackup)) File.Delete(diskBackup); } catch { }
                    }
                }
            }
        }

        private void RestoreDiskSnapshot(string backupPath, bool hadDiskFile)
        {
            if (!hadDiskFile)
            {
                if (File.Exists(_path)) File.Delete(_path);
                if (File.Exists(_path))
                    throw new IOException("回滚后模型文件仍存在。");
                return;
            }
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("模型回滚备份不存在。", backupPath);
            var restoreTemp = _path + ".rollback." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(backupPath, restoreTemp, true);
                if (File.Exists(_path))
                {
                    try { File.Replace(restoreTemp, _path, null, true); }
                    catch (IOException) { File.Copy(restoreTemp, _path, true); }
                    catch (PlatformNotSupportedException) { File.Copy(restoreTemp, _path, true); }
                }
                else File.Move(restoreTemp, _path);
                if (!FilesEqual(backupPath, _path))
                    throw new InvalidDataException("模型文件回滚后字节校验不一致。");
            }
            finally
            {
                try { if (File.Exists(restoreTemp)) File.Delete(restoreTemp); } catch { }
            }
        }

        private static bool FilesEqual(string left, string right)
        {
            try
            {
                var leftInfo = new FileInfo(left);
                var rightInfo = new FileInfo(right);
                if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
                    return false;
                using (var a = File.OpenRead(left))
                using (var b = File.OpenRead(right))
                {
                    var leftBuffer = new byte[8192];
                    var rightBuffer = new byte[8192];
                    int leftRead;
                    while ((leftRead = a.Read(leftBuffer, 0, leftBuffer.Length)) > 0)
                    {
                        var rightRead = b.Read(rightBuffer, 0, rightBuffer.Length);
                        if (rightRead != leftRead || !leftBuffer.Take(leftRead).SequenceEqual(rightBuffer.Take(rightRead)))
                            return false;
                    }
                    return b.ReadByte() < 0;
                }
            }
            catch { return false; }
        }

        private static bool ProfilesMatch(EpbAdaptiveProfile actual, EpbAdaptiveProfile expected)
        {
            return actual != null && expected != null &&
                   actual.Channel == expected.Channel &&
                   actual.ModelVersion == expected.ModelVersion &&
                   actual.ValidSampleCount == expected.ValidSampleCount &&
                   actual.UpdatedUtc == expected.UpdatedUtc &&
                   Math.Abs(actual.ForwardClampMedianMs - expected.ForwardClampMedianMs) <= 1e-9 &&
                   Math.Abs(actual.ReverseReleaseMedianMs - expected.ReverseReleaseMedianMs) <= 1e-9;
        }

        private EpbAdaptiveProfiles LoadDocument()
        {
            if (!File.Exists(_path)) return new EpbAdaptiveProfiles();

            try
            {
                using (var stream = File.OpenRead(_path))
                {
                    var serializer = new XmlSerializer(typeof(EpbAdaptiveProfiles));
                    var loaded = serializer.Deserialize(stream) as EpbAdaptiveProfiles;
                    if (loaded == null) throw new InvalidDataException("反序列化结果为空。");
                    loaded.Profiles = loaded.Profiles ?? new List<EpbAdaptiveProfile>();
                    var requiresPolicyReset =
                        loaded.ModelVersion < EpbAdaptiveProfile.MinimumCompatibleModelVersion ||
                        loaded.Profiles.Any(
                            x => x != null &&
                                  x.ModelVersion < EpbAdaptiveProfile.MinimumCompatibleModelVersion);
                    if (requiresPolicyReset)
                    {
                        var backup = _path + ".pre-v" +
                                     EpbAdaptiveProfile.CurrentModelVersion.ToString(
                                         CultureInfo.InvariantCulture) + "." +
                                     DateTime.UtcNow.ToString(
                                         "yyyyMMdd_HHmmss",
                                         CultureInfo.InvariantCulture);
                        try { File.Copy(_path, backup, false); }
                        catch (Exception backupEx)
                        {
                            _log.Warn(
                                $"旧自适应模型失效前备份失败，将继续以空模型启动：{backupEx.Message}；原文件={_path}",
                                "EPB");
                        }

                        loaded.Profiles = loaded.Profiles
                            .Where(x => x != null && x.Channel >= 1 && x.Channel <= 12)
                            .Select(x => new EpbAdaptiveProfile { Channel = x.Channel })
                            .ToList();
                        loaded.ModelVersion = EpbAdaptiveProfile.CurrentModelVersion;
                        _log.Warn(
                            $"检测到旧控制策略模型，已保留原文件并使历史学习结果失效；" +
                            $"所有启用卡钳必须重新完成5个完整有效学习圈。ModelVersion=" +
                            $"{EpbAdaptiveProfile.CurrentModelVersion}，Backup={backup}",
                            "EPB");
                    }

                    loaded.Profiles = loaded.Profiles.Where(x => x != null).ToList();
                    foreach (var profile in loaded.Profiles)
                        profile.UpgradeCompatibleModel();
                    loaded.ModelVersion = EpbAdaptiveProfile.CurrentModelVersion;
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                var backup = _path + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                try { File.Copy(_path, backup, false); } catch { /* 保留原文件优先，备份失败不阻断启动 */ }
                _log.Warn($"自适应模型文件损坏，已回退为空模型：{ex.Message}；原文件={_path}", "EPB");
                return new EpbAdaptiveProfiles();
            }
        }

        private void SaveDocumentAtomic(EpbAdaptiveProfiles document)
        {
            var directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory ?? throw new InvalidOperationException("模型目录无效。"));

            var tempPath = _path + ".tmp";
            var backupPath = _path + ".bak";
            var serializer = new XmlSerializer(typeof(EpbAdaptiveProfiles));

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                serializer.Serialize(stream, document);
                stream.Flush(true);
            }

            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(tempPath, _path, backupPath, true);
                    return;
                }
                catch (IOException)
                {
                    // 某些文件系统不支持 Replace，下面使用覆盖复制兜底。
                }
                catch (PlatformNotSupportedException)
                {
                    // 使用覆盖复制兜底。
                }
            }

            File.Copy(tempPath, _path, true);
            File.Delete(tempPath);
        }
    }
}
