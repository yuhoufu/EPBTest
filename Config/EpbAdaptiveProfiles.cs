using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// 单个 EPB 通道的自适应统计模型。
    /// </summary>
    public sealed class EpbAdaptiveProfile
    {
        public const int CurrentModelVersion = 3;
        private const int HistoryCapacity = 30;
        private const double MinimumCutoffSlopeAperMs = 0.001;
        private const double MaximumCutoffLeadMs = 100.0;

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
        public double ForwardPeakErrorMedianA { get; set; }
        public double ForwardPeakErrorMadA { get; set; }
        public int ValidCutoffSampleCount { get; set; }
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
            equivalentLeadMs = 0;
            if (!IsFinitePositive(targetA) ||
                !IsFiniteNonNegative(cutoffCurrentA) ||
                !IsFinitePositive(actualPeakA) ||
                !IsFinitePositive(cutoffSlopeAperMs) ||
                cutoffSlopeAperMs < MinimumCutoffSlopeAperMs)
                return false;

            var tailRiseA = Math.Max(0, actualPeakA - cutoffCurrentA);
            equivalentLeadMs = Math.Min(
                MaximumCutoffLeadMs,
                tailRiseA / cutoffSlopeAperMs);

            AddBounded(ForwardCutoffLeadHistoryMs, equivalentLeadMs);
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
                ForwardPeakErrorMedianA = ForwardPeakErrorMedianA,
                ForwardPeakErrorMadA = ForwardPeakErrorMadA,
                ValidCutoffSampleCount = ValidCutoffSampleCount,
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
            ForwardPeakErrorMedianA = copy.ForwardPeakErrorMedianA;
            ForwardPeakErrorMadA = copy.ForwardPeakErrorMadA;
            ValidCutoffSampleCount = copy.ValidCutoffSampleCount;
            ConsecutiveForwardOvershootCount = copy.ConsecutiveForwardOvershootCount;
            ConsecutivePeakEvidenceMismatchCount = copy.ConsecutivePeakEvidenceMismatchCount;
            ConsecutiveForwardStallCount = copy.ConsecutiveForwardStallCount;
            UpdatedUtc = copy.UpdatedUtc;
            ForwardEmptyHistoryA = copy.ForwardEmptyHistoryA;
            ReverseEmptyHistoryA = copy.ReverseEmptyHistoryA;
            ForwardClampHistoryMs = copy.ForwardClampHistoryMs;
            ReverseReleaseHistoryMs = copy.ReverseReleaseHistoryMs;
            ForwardCutoffLeadHistoryMs = copy.ForwardCutoffLeadHistoryMs;
            ForwardPeakErrorHistoryA = copy.ForwardPeakErrorHistoryA;
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

        private static double RelativeDeviation(double actual, double baseline)
        {
            if (baseline <= 1e-9) return 0;
            return Math.Abs(actual - baseline) / baseline;
        }
    }

    /// <summary>
    /// 项目级 EPB 自适应模型存储。写入采用同目录临时文件 + Replace，避免半写文件。
    /// </summary>
    public sealed class EpbAdaptiveProfileStore
    {
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
                    foreach (var profile in loaded.Profiles.Where(x => x != null))
                    {
                        profile.ForwardEmptyHistoryA =
                            profile.ForwardEmptyHistoryA ?? new List<double>();
                        profile.ReverseEmptyHistoryA =
                            profile.ReverseEmptyHistoryA ?? new List<double>();
                        profile.ForwardClampHistoryMs =
                            profile.ForwardClampHistoryMs ?? new List<double>();
                        profile.ReverseReleaseHistoryMs =
                            profile.ReverseReleaseHistoryMs ?? new List<double>();
                        profile.ForwardCutoffLeadHistoryMs =
                            profile.ForwardCutoffLeadHistoryMs ?? new List<double>();
                        profile.ForwardPeakErrorHistoryA =
                            profile.ForwardPeakErrorHistoryA ?? new List<double>();
                    }
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
