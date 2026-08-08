using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Config;

namespace Controller.Adaptive
{
    /// <summary>
    /// 与具体试验项目无关的 EPB 程序级安全策略。
    /// 唯一外部配置源为 EXE 同名配置文件的 appSettings；项目 TestConfig.xml 不参与覆盖。
    /// </summary>
    public sealed class EpbProgramSafetySettings
    {
        public const string SafetyPolicyVersion = "2026.08.08.2";

        public const int DefaultForwardProgressConfirmMs = 200;
        public const double DefaultForwardMinimumRiseSlopeAperMs = 0.001;
        public const int DefaultForwardProgressDeadlineMs = 3000;
        public const int DefaultForwardNearTargetConfirmMs = 200;
        public const double DefaultForwardAcceptableUndershootA = 0.8;
        public const int DefaultReverseProgressConfirmMs = 200;
        public const double DefaultReverseMinimumDecaySlopeAperMs = 0.001;
        public const int DefaultReverseProgressDeadlineMs = 2500;
        public const double DefaultOffCurrentClearThresholdA = 0.1;
        public const int DefaultOffCurrentClearTimeoutMs = 1000;
        public const double DefaultPeakEvidenceMismatchToleranceA = 1.0;
        public const int DefaultPeakEvidenceMaximumLagMs = 100;

        private readonly Dictionary<string, string> _sources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int ForwardProgressConfirmMs { get; private set; }
        public double ForwardMinimumRiseSlopeAperMs { get; private set; }
        public int ForwardProgressDeadlineMs { get; private set; }
        public int ForwardNearTargetConfirmMs { get; private set; }
        public double ForwardAcceptableUndershootA { get; private set; }
        public int ReverseProgressConfirmMs { get; private set; }
        public double ReverseMinimumDecaySlopeAperMs { get; private set; }
        public int ReverseProgressDeadlineMs { get; private set; }
        public double OffCurrentClearThresholdA { get; private set; }
        public int OffCurrentClearTimeoutMs { get; private set; }
        public double PeakEvidenceMismatchToleranceA { get; private set; }
        public int PeakEvidenceMaximumLagMs { get; private set; }

        public static EpbProgramSafetySettings Load(IAppLogger logger = null)
        {
            NameValueCollection values = null;
            try
            {
                values = ConfigurationManager.AppSettings;
            }
            catch (Exception ex)
            {
                logger?.Warn(
                    $"读取 EXE 程序级 EPB 安全配置失败，将使用编译安全默认值：{ex.Message}",
                    "EPB");
            }

            return FromAppSettings(values, logger);
        }

        public static EpbProgramSafetySettings FromAppSettings(
            NameValueCollection values,
            IAppLogger logger = null)
        {
            var result = new EpbProgramSafetySettings();

            result.ForwardProgressConfirmMs = result.ReadIntAtLeast(
                values,
                "EpbForwardProgressConfirmMs",
                DefaultForwardProgressConfirmMs,
                DefaultForwardProgressConfirmMs,
                logger);
            result.ForwardMinimumRiseSlopeAperMs = result.ReadPositiveDouble(
                values,
                "EpbForwardMinimumRiseSlopeAperMs",
                DefaultForwardMinimumRiseSlopeAperMs,
                0.00001,
                logger);
            result.ForwardProgressDeadlineMs = result.ReadIntAtLeast(
                values,
                "EpbForwardProgressDeadlineMs",
                DefaultForwardProgressDeadlineMs,
                Math.Max(DefaultForwardProgressDeadlineMs, result.ForwardProgressConfirmMs),
                logger);
            result.ForwardNearTargetConfirmMs = result.ReadIntInRange(
                values,
                "EpbForwardNearTargetConfirmMs",
                DefaultForwardNearTargetConfirmMs,
                100,
                result.ForwardProgressConfirmMs,
                logger);
            result.ForwardAcceptableUndershootA = result.ReadPositiveDouble(
                values,
                "EpbForwardAcceptableUndershootA",
                DefaultForwardAcceptableUndershootA,
                0.1,
                logger);
            result.ReverseProgressConfirmMs = result.ReadIntAtLeast(
                values,
                "EpbReverseProgressConfirmMs",
                DefaultReverseProgressConfirmMs,
                DefaultReverseProgressConfirmMs,
                logger);
            result.ReverseMinimumDecaySlopeAperMs = result.ReadPositiveDouble(
                values,
                "EpbReverseMinimumDecaySlopeAperMs",
                DefaultReverseMinimumDecaySlopeAperMs,
                0.00001,
                logger);
            result.ReverseProgressDeadlineMs = result.ReadIntAtLeast(
                values,
                "EpbReverseProgressDeadlineMs",
                DefaultReverseProgressDeadlineMs,
                Math.Max(DefaultReverseProgressDeadlineMs, result.ReverseProgressConfirmMs),
                logger);
            result.OffCurrentClearThresholdA = result.ReadPositiveDouble(
                values,
                "EpbOffCurrentClearThresholdA",
                DefaultOffCurrentClearThresholdA,
                0.01,
                logger);
            result.OffCurrentClearTimeoutMs = result.ReadIntAtLeast(
                values,
                "EpbOffCurrentClearTimeoutMs",
                DefaultOffCurrentClearTimeoutMs,
                DefaultOffCurrentClearTimeoutMs,
                logger);
            result.PeakEvidenceMismatchToleranceA = result.ReadPositiveDouble(
                values,
                "EpbPeakEvidenceMismatchToleranceA",
                DefaultPeakEvidenceMismatchToleranceA,
                DefaultPeakEvidenceMismatchToleranceA,
                logger);
            result.PeakEvidenceMaximumLagMs = result.ReadIntAtLeast(
                values,
                "EpbPeakEvidenceMaximumLagMs",
                DefaultPeakEvidenceMaximumLagMs,
                20,
                logger);

            logger?.Info(
                "EPB 程序级安全策略已加载：" + result.ToLogText(),
                "EPB");
            return result;
        }

        public EpbAdaptiveSafetyLimits ToAdaptiveSafetyLimits()
        {
            return new EpbAdaptiveSafetyLimits
            {
                ForwardProgressConfirmMs = ForwardProgressConfirmMs,
                ForwardMinimumRiseSlopeAperMs = ForwardMinimumRiseSlopeAperMs,
                ForwardProgressDeadlineMs = ForwardProgressDeadlineMs,
                ForwardNearTargetConfirmMs = ForwardNearTargetConfirmMs,
                ForwardAcceptableUndershootA = ForwardAcceptableUndershootA,
                ReverseProgressConfirmMs = ReverseProgressConfirmMs,
                ReverseMinimumDecaySlopeAperMs = ReverseMinimumDecaySlopeAperMs,
                ReverseProgressDeadlineMs = ReverseProgressDeadlineMs,
                OffCurrentClearThresholdA = OffCurrentClearThresholdA,
                OffCurrentClearTimeoutMs = OffCurrentClearTimeoutMs
            }.Normalized();
        }

        public string ToLogText()
        {
            return
                $"Policy={SafetyPolicyVersion} " +
                $"FwdConfirm={ForwardProgressConfirmMs}ms " +
                $"FwdSlope={ForwardMinimumRiseSlopeAperMs:F6}A/ms " +
                $"FwdDeadline={ForwardProgressDeadlineMs}ms " +
                $"NearTargetConfirm={ForwardNearTargetConfirmMs}ms " +
                $"AcceptableUndershoot={ForwardAcceptableUndershootA:F3}A " +
                $"RevConfirm={ReverseProgressConfirmMs}ms " +
                $"RevSlope={ReverseMinimumDecaySlopeAperMs:F6}A/ms " +
                $"RevDeadline={ReverseProgressDeadlineMs}ms " +
                $"OffClear={OffCurrentClearThresholdA:F3}A/{OffCurrentClearTimeoutMs}ms " +
                $"PeakMismatchTolerance={PeakEvidenceMismatchToleranceA:F3}A " +
                $"PeakEvidenceMaxLag={PeakEvidenceMaximumLagMs}ms";
        }

        public string ToAuditLogText()
        {
            return string.Join(
                "; ",
                new[]
                {
                    AuditValue("EpbForwardProgressConfirmMs", ForwardProgressConfirmMs),
                    AuditValue("EpbForwardMinimumRiseSlopeAperMs", ForwardMinimumRiseSlopeAperMs),
                    AuditValue("EpbForwardProgressDeadlineMs", ForwardProgressDeadlineMs),
                    AuditValue("EpbForwardNearTargetConfirmMs", ForwardNearTargetConfirmMs),
                    AuditValue("EpbForwardAcceptableUndershootA", ForwardAcceptableUndershootA),
                    AuditValue("EpbReverseProgressConfirmMs", ReverseProgressConfirmMs),
                    AuditValue("EpbReverseMinimumDecaySlopeAperMs", ReverseMinimumDecaySlopeAperMs),
                    AuditValue("EpbReverseProgressDeadlineMs", ReverseProgressDeadlineMs),
                    AuditValue("EpbOffCurrentClearThresholdA", OffCurrentClearThresholdA),
                    AuditValue("EpbOffCurrentClearTimeoutMs", OffCurrentClearTimeoutMs),
                    AuditValue("EpbPeakEvidenceMismatchToleranceA", PeakEvidenceMismatchToleranceA),
                    AuditValue("EpbPeakEvidenceMaximumLagMs", PeakEvidenceMaximumLagMs)
                });
        }

        public string SaveEffectiveSnapshot(string projectConfigDirectory, IAppLogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(projectConfigDirectory)) return null;

            try
            {
                Directory.CreateDirectory(projectConfigDirectory);
                var path = Path.Combine(projectConfigDirectory, "EpbProgramSafetyEffective.xml");
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    Encoding = new UTF8Encoding(false)
                };
                using (var writer = XmlWriter.Create(path, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("EpbProgramSafetyEffective");
                    writer.WriteAttributeString("policyVersion", SafetyPolicyVersion);
                    writer.WriteAttributeString(
                        "capturedUtc",
                        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    WriteValue(writer, "EpbForwardProgressConfirmMs", ForwardProgressConfirmMs);
                    WriteValue(writer, "EpbForwardMinimumRiseSlopeAperMs", ForwardMinimumRiseSlopeAperMs);
                    WriteValue(writer, "EpbForwardProgressDeadlineMs", ForwardProgressDeadlineMs);
                    WriteValue(writer, "EpbForwardNearTargetConfirmMs", ForwardNearTargetConfirmMs);
                    WriteValue(writer, "EpbForwardAcceptableUndershootA", ForwardAcceptableUndershootA);
                    WriteValue(writer, "EpbReverseProgressConfirmMs", ReverseProgressConfirmMs);
                    WriteValue(writer, "EpbReverseMinimumDecaySlopeAperMs", ReverseMinimumDecaySlopeAperMs);
                    WriteValue(writer, "EpbReverseProgressDeadlineMs", ReverseProgressDeadlineMs);
                    WriteValue(writer, "EpbOffCurrentClearThresholdA", OffCurrentClearThresholdA);
                    WriteValue(writer, "EpbOffCurrentClearTimeoutMs", OffCurrentClearTimeoutMs);
                    WriteValue(writer, "EpbPeakEvidenceMismatchToleranceA", PeakEvidenceMismatchToleranceA);
                    WriteValue(writer, "EpbPeakEvidenceMaximumLagMs", PeakEvidenceMaximumLagMs);
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }

                logger?.Info($"EPB 程序级安全策略快照已保存：{path}", "EPB");
                return path;
            }
            catch (Exception ex)
            {
                logger?.Warn($"保存 EPB 程序级安全策略快照失败：{ex.Message}", "EPB");
                return null;
            }
        }

        private void WriteValue(XmlWriter writer, string key, object value)
        {
            writer.WriteStartElement("add");
            writer.WriteAttributeString("key", key);
            writer.WriteAttributeString(
                "value",
                Convert.ToString(value, CultureInfo.InvariantCulture));
            writer.WriteAttributeString(
                "source",
                _sources.TryGetValue(key, out var source)
                    ? source
                    : "compiled-default");
            writer.WriteEndElement();
        }

        private string AuditValue(string key, object value)
        {
            return
                $"{key}={Convert.ToString(value, CultureInfo.InvariantCulture)}" +
                $"({(_sources.TryGetValue(key, out var source) ? source : "compiled-default")})";
        }

        private int ReadIntAtLeast(
            NameValueCollection values,
            string key,
            int fallback,
            int minimum,
            IAppLogger logger)
        {
            var raw = values?[key];
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                _sources[key] = "compiled-default";
                if (!string.IsNullOrWhiteSpace(raw))
                    logger?.Warn($"{key}='{raw}' 非法，使用安全默认值 {fallback}。", "EPB");
                return fallback;
            }

            if (parsed < minimum)
            {
                _sources[key] = "compiled-default";
                logger?.Warn(
                    $"{key}={parsed} 低于有效下限 {minimum}，使用安全默认值 {fallback}。",
                    "EPB");
                return fallback;
            }

            _sources[key] = "appSettings";
            return parsed;
        }

        private int ReadIntInRange(
            NameValueCollection values,
            string key,
            int fallback,
            int minimum,
            int maximum,
            IAppLogger logger)
        {
            var raw = values?[key];
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                _sources[key] = "compiled-default";
                if (!string.IsNullOrWhiteSpace(raw))
                    logger?.Warn($"{key}='{raw}' 非法，使用安全默认值 {fallback}。", "EPB");
                return Math.Max(minimum, Math.Min(maximum, fallback));
            }

            var normalized = Math.Max(minimum, Math.Min(maximum, parsed));
            _sources[key] = normalized == parsed ? "appSettings" : "appSettings-clamped";
            if (normalized != parsed)
                logger?.Warn($"{key}={parsed} 超出安全范围 [{minimum},{maximum}]，运行时钳制为 {normalized}。", "EPB");
            return normalized;
        }

        private double ReadPositiveDouble(
            NameValueCollection values,
            string key,
            double fallback,
            double minimum,
            IAppLogger logger)
        {
            var raw = values?[key];
            if (!double.TryParse(
                    raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed) ||
                double.IsNaN(parsed) ||
                double.IsInfinity(parsed))
            {
                _sources[key] = "compiled-default";
                if (!string.IsNullOrWhiteSpace(raw))
                    logger?.Warn($"{key}='{raw}' 非法，使用安全默认值 {fallback}。", "EPB");
                return fallback;
            }

            if (parsed < minimum)
            {
                _sources[key] = "compiled-default";
                logger?.Warn(
                    $"{key}={parsed} 低于有效下限 {minimum}，使用安全默认值 {fallback}。",
                    "EPB");
                return fallback;
            }

            _sources[key] = "appSettings";
            return parsed;
        }
    }
}
