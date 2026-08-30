using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Controller.Alarm
{
    /// <summary>
    /// 将稳定的英文故障码转换成面向现场用户的中文提示。
    /// 原始故障码和 Reason 仍写入日志及证据文件，避免影响检索和兼容性。
    /// </summary>
    public static class AlarmMessageLocalizer
    {
        private static readonly IReadOnlyDictionary<string, string> CodeNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PeakEvidenceMismatch"] = "峰值证据连续偏差",
                ["PeakCaptureInvalid"] = "峰值数据捕获无效",
                ["ForwardPeakOvershoot2AConfirmed"] = "连续8圈峰值超过目标2A，卡钳已停用",
                ["ForwardPeakOvershoot"] = "正向峰值过冲",
                ["ForwardCurrentRiseStall"] = "正向电流上升停滞",
                ["ForwardCurrentRiseStalled"] = "正向电流上升停滞",
                ["ForwardLowPlateauConfirmed"] = "连续8圈夹紧电流停留在合格下限以下，卡钳已停用",
                ["ForwardLoadRiseNotStarted"] = "连续3次未检测到夹紧电流爬坡，卡钳已停用",
                ["OpenCircuitOrOutputFault"] =
                    "上电后连续约200ms电流不高于0.10A，疑似卡钳、线束或驱动支路开路；本次运行已隔离该通道，下次启动将重新检测",
                ["ForwardFastOverCurrentCutoff"] = "峰值快速保护已断开正向供电，本圈继续释放",
                ["FastRiseCandidate"] = "快速夹紧候选已断开正向供电，正在等待完整数据确认",
                ["ClampReachedNearTargetPlateau"] = "接近目标的高负载平台，已停止正向供电并继续释放",
                ["RapidLoadRiseWithoutObservedEmpty"] = "检测到负载快速上升，但本圈尚未取得空载电流基线",
                ["DaqSampleStale"] = "采集数据过期",
                ["BackgroundQueueFull"] = "采集后台队列已满",
                ["ControlQueueFull"] = "控制数据队列已满",
                ["ControlLatencyExceeded"] = "实时控制处理延迟超过安全上限",
                ["ConfirmedOverCurrent"] = "完整速率证据确认过流",
                ["FastPathSignalInvalid"] = "快速电流信号无效",
                ["FastPathEvidenceUnavailable"] = "快速过流完整速率证据不可用",
                ["ControlProducerReentry"] = "采集控制生产区发生重入",
                ["ControlSequenceDiscontinuity"] = "快速控制批次序号不连续",
                ["ControlBatchDuplicate"] = "快速控制批次重复",
                ["ControlBatchOutOfOrder"] = "快速控制批次倒序",
                ["ControlMonotonicTickInvalid"] = "快速控制单调时钟无效",
                ["FastPathFilterStateInvalid"] = "快速滤波状态无效",
                ["DaqSampleTimelineFuture"] = "采集样本时间轴异常超前",
                ["DaqClockModelInvalid"] = "采集时钟模型正在自动恢复",
                ["DaqWallClockStep"] = "系统时间跳变，采集时间轴正在自动重建",
                ["DaqClockRecoveryFailed"] = "采集时钟自动恢复失败",
                ["DaqClockRecoveryLimitExceeded"] = "采集时钟恢复次数达到安全上限",
                ["DaqCallbackStale"] = "采集回调中断",
                ["DaqPersistenceLag"] = "采集持久化积压",
                ["DaqPersistenceRecoveryTimeout"] = "采集持久化恢复超时",
                ["RepeatedDaqSampleStale"] = "采集数据连续过期",
                ["DaqRecoveryFailed"] = "采集设备恢复失败",
                ["DaqTaskRecreateFailed"] = "采集任务重建失败",
                ["DaqStartPreflightFailed"] = "启动前采集健康检查失败",
                ["OffCurrentUnverifiableDaqStale"] = "采集数据过期，无法确认断电电流",
                ["ForwardAbsoluteOnTimeExceeded"] = "正向上电时间超过安全上限",
                ["ReverseAbsoluteOnTimeExceeded"] = "反向上电时间超过安全上限",
                ["ForwardEndedWithoutClamp"] = "正向动作未确认夹紧",
                ["ReverseEndedWithoutRelease"] = "反向动作未确认释放",
                ["AbnormalHighCurrentPlateau"] = "电流异常高位停滞",
                ["HydraulicPressureLost"] = "液压保压资格丢失",
                ["PressureSampleUnavailable"] = "压力采样暂不可用，正在自动恢复",
                ["HydraulicSampleStale"] = "液压压力样本已过期，禁止使用旧样本判定",
                ["HydraulicSampleUnavailable"] = "液压压力样本不可用，正在安全隔离",
                ["HydraulicPressureBelowMinimum"] = "液压压力真实低于安全下限",
                ["HydraulicBuildTimeout"] = "液压建压超时",
                ["PressureSampleStale"] = "压力采样数据过期，无法确认释压状态",
                ["HydraulicReleaseTimeout"] = "液压释压超时",
                ["HydraulicBarrierTimeout"] = "液压组同步等待超时",
                ["PressureSampleInvalid"] = "压力采样无效",
                ["PressureReaderUnavailable"] = "压力采集不可用",
                ["SharedPowerLimiting"] = "共享电源持续限流",
                ["TelemetryStale"] = "电源遥测数据过期",
                ["ProtectionTrip"] = "电源保护触发",
                ["SustainedCurrentLimit"] = "电源持续限流",
                ["SustainedLowVoltage"] = "电源输出电压持续偏低",
                ["UnexpectedOutputOff"] = "电源输出意外关闭",
                ["OutputOffUnverified"] = "电源关闭状态未确认",
                ["ForwardCommandFailed"] = "正向输出命令失败",
                ["ReverseCommandFailed"] = "反向输出命令失败",
                ["ForwardPositioningTimeout"] = "启动正向定位超时",
                ["ReverseReleaseTimeout"] = "启动反向释放超时",
                ["ForwardOffCurrentNotCleared"] = "正向断电后电流未清零",
                ["InvalidCurrentSample"] = "电流采样无效",
                ["ChannelFault"] = "卡钳通道故障"
            };

        public static string ToUserMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "系统检测到异常，已执行安全保护。";

            // 释压超时若根因是采样陈旧，必须优先呈现“无法确认”，不能落入
            // 普通液压超时提示后误导现场排查泄漏或制动液。
            if (raw.IndexOf("HydraulicReleaseTimeout", StringComparison.OrdinalIgnoreCase) >= 0 &&
                raw.IndexOf("PressureSampleStale", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var staleDetails = BuildDetails(raw);
                return "压力采样数据过期，无法确认释压状态" +
                       (string.IsNullOrWhiteSpace(staleDetails) ? "。" : "：" + staleDetails + "。");
            }

            foreach (var item in CodeNames)
            {
                if (raw.IndexOf(item.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var details = BuildDetails(raw);
                return item.Value + (string.IsNullOrWhiteSpace(details) ? "。" : "：" + details + "。");
            }

            if (ContainsChinese(raw))
                return ReplaceFieldLabels(raw);

            return "系统检测到异常，已执行安全保护；详细诊断信息已写入日志。";
        }

        /// <summary>
        /// 观察/诊断事件专用提示。ChannelWarningRaised 不代表永久报警或整批安全接管，
        /// 不能复用硬故障的“已执行安全保护”兜底文案误导现场判断。
        /// </summary>
        public static string ToUserWarningMessage(string raw)
        {
            var localized = ToUserMessage(raw);
            if (localized.StartsWith(
                    "系统检测到异常，已执行安全保护",
                    StringComparison.Ordinal))
                localized = "检测到控制参数偏离正常范围，原始诊断信息已写入日志。";

            if (!string.IsNullOrWhiteSpace(raw) &&
                raw.IndexOf("RapidLoadRiseWithoutObservedEmpty", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return localized.TrimEnd('。') +
                       "；这是诊断事件，状态机未改变，不计入故障连续次数，也不会单独触发卡钳永久停机。";
            }

            return localized.TrimEnd('。') +
                   "；这是观察事件，不会单独触发卡钳永久停机。";
        }

        public static string GetCodeName(string code)
        {
            if (!string.IsNullOrWhiteSpace(code) && CodeNames.TryGetValue(code, out var name))
                return name;
            return "控制系统故障";
        }

        public static string GetWarningName(AdaptiveWarningCode code)
        {
            switch (code)
            {
                case AdaptiveWarningCode.ForwardPeakOvershootWarning:
                    return "正向峰值过冲预警";
                case AdaptiveWarningCode.ForwardCurrentRiseStallWarning:
                    return "正向电流上升停滞预警";
                case AdaptiveWarningCode.PeakEvidenceMismatchWarning:
                    return "峰值证据偏差预警";
                case AdaptiveWarningCode.PeakEvidenceLagWarning:
                    return "峰值完整数据处理滞后预警";
                case AdaptiveWarningCode.PeakEvidenceTimestampMissing:
                    return "峰值证据时间戳缺失诊断";
                case AdaptiveWarningCode.RapidLoadRiseDiagnostic:
                    return "本圈空载基线缺失诊断";
                default:
                    return "控制预警";
            }
        }

        public static string GetScopeName(FaultScope scope)
        {
            switch (scope)
            {
                case FaultScope.Channel: return "卡钳通道";
                case FaultScope.ElectricalGroup: return "电源组";
                case FaultScope.HydraulicGroup: return "液压组";
                case FaultScope.DaqGroup: return "采集设备组";
                case FaultScope.Global: return "整机";
                default: return "控制范围";
            }
        }

        private static string BuildDetails(string raw)
        {
            var parts = new List<string>();
            AddMetric(parts, raw, "Peak", "峰值");
            AddMetric(parts, raw, "QuickPeak", "快速峰值");
            AddMetric(parts, raw, "FullRatePeak", "完整数据峰值");
            AddMetric(parts, raw, "Target", "目标");
            AddMetric(parts, raw, "I", "当前电流");
            AddMetric(parts, raw, "Floor", "合格下限");
            AddMetric(parts, raw, "Slope", "平台斜率");
            AddMetric(parts, raw, "ConfirmMs", "确认时长");
            AddMetric(parts, raw, "confirm", "确认时长");
            AddMetric(parts, raw, "Error", "偏差");
            AddMetric(parts, raw, "Actual", "实际压力");
            AddMetric(parts, raw, "Minimum", "最低压力");
            AddMetric(parts, raw, "AgeMs", "样本年龄");
            AddMetric(parts, raw, "Streak", "连续次数");

            var reason = GetValue(raw, "Reason");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                switch (reason)
                {
                    case "NoSample": parts.Add("原因=没有压力样本"); break;
                    case "InvalidValue": parts.Add("原因=压力值无效"); break;
                    case "StaleSample": parts.Add("原因=压力样本过期"); break;
                    case "BelowMinimum": parts.Add("原因=压力低于下限"); break;
                }
            }
            return string.Join("，", parts);
        }

        private static void AddMetric(List<string> parts, string raw, string key, string label)
        {
            var value = GetValue(raw, key);
            if (!string.IsNullOrWhiteSpace(value)) parts.Add(label + "=" + value);
        }

        private static string GetValue(string raw, string key)
        {
            var match = Regex.Match(
                raw ?? string.Empty,
                @"(?:^|\s)" + Regex.Escape(key) + @"=([^\s;，]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value.TrimEnd('。', '.', ',') : string.Empty;
        }

        private static bool ContainsChinese(string text)
        {
            return Regex.IsMatch(text ?? string.Empty, @"[\u3400-\u9fff]");
        }

        private static string ReplaceFieldLabels(string raw)
        {
            return (raw ?? string.Empty)
                .Replace("QuickPeak=", "快速峰值=")
                .Replace("FullRatePeak=", "完整数据峰值=")
                .Replace("Peak=", "峰值=")
                .Replace("Target=", "目标=")
                .Replace("Error=", "偏差=")
                .Replace("Floor=", "合格下限=")
                .Replace("Slope=", "斜率=")
                .Replace("Window=", "窗口=");
        }
    }
}
