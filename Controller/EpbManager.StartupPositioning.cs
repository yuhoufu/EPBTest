using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Config;

namespace Controller
{
    public sealed partial class EpbManager
    {
        internal async Task PublishStartupPositioningFailureAsync(StartupPositioningResult result)
        {
            if (result == null || result.Succeeded) return;
            var reason =
                $"StartupPositioningFailed Stage={result.Stage} Code={result.Code} " +
                $"Peak={result.PeakCurrentA:F3}A Elapsed={result.ElapsedMs}ms Detail={result.Reason}";
            var classification = ClassifyStartupPositioningFailure(
                IsStartupPositioningOverCurrent(result),
                HasFreshStartupPowerEvidence(result?.Channel ?? 0),
                IsStartupPositioningOutputControlFailure(result));
            if (classification != FaultClassification.HardwareConfirmed &&
                !TryEnsureSoftwareRecoveryOutputOff(
                    result.Channel,
                    "StartupPositioningSelfHealing"))
            {
                reason += " OutputOffCommandFailed：保持电源组安全自恢复，断电确认后继续启动定位。";
            }
            var fault = new ControlFault(
                string.IsNullOrWhiteSpace(result.Code) ? "StartupPositioningFailed" : result.Code,
                reason,
                FaultScope.Channel,
                new[] { result.Channel },
                null,
                DateTime.UtcNow,
                Guid.NewGuid(),
                classification);

            if (classification != FaultClassification.HardwareConfirmed)
            {
                PublishChannelRuntimeState(
                    result.Channel,
                    ChannelRuntimeState.Recovering,
                    "StartupPositioningSelfHealing",
                    "启动定位未获得硬件故障双证据；已安全断电，按软件瞬态继续自愈。" + reason,
                    affectedChannels: fault.AffectedChannels,
                    correlationId: fault.CorrelationId);
                _log?.Warn(
                    $"EPB[{result.Channel}] 启动定位未获得硬件故障双证据，保持自愈。" +
                    $"CorrelationId={fault.CorrelationId:N} {reason}",
                    "EPB");
                FlushPersistentLog();
                NonCriticalObserver.Invoke(
                    ControlFaultRaised,
                    fault,
                    ex => _log?.Warn($"启动定位自愈观察者异常，已隔离：{ex.Message}", "EPB"));
                try { ExportStartupPositioningSnapshot(result, fault); }
                catch (Exception ex)
                {
                    _log?.Warn($"EPB[{result.Channel}] 启动定位快照导出失败：{ex.Message}", "落盘");
                }
                return;
            }

            NotifyRunAuthorizationRevoking(
                StopSource.AlarmInterlock,
                reason,
                nameof(PublishStartupPositioningFailureAsync),
                fault.CorrelationId,
                FaultScope.Channel);

            _alarmStopLatch.TryRequestStop(result.Channel);
            _log?.Error(
                $"EPB[{result.Channel}] 启动定位失败并隔离。CorrelationId={fault.CorrelationId:N} {reason}",
                "报警");
            FlushPersistentLog();
            PublishFaultRuntimeStates(fault, result.Channel);
            NonCriticalObserver.Invoke(
                ControlFaultRaised,
                fault,
                ex => _log?.Warn($"启动定位故障观察者异常，已隔离：{ex.Message}", "EPB"));
            NonCriticalObserver.Invoke(
                ChannelAlarmRaised,
                result.Channel,
                reason,
                ex => _log?.Warn($"启动定位报警观察者异常，已隔离：{ex.Message}", "EPB"));
            try
            {
                if (Alarm != null)
                    await Alarm.SetAlarmAsync(result.Channel, true, reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{result.Channel}] 启动定位失败后报警灯输出失败：{ex.Message}", "报警");
            }

            try
            {
                ExportStartupPositioningSnapshot(result, fault);
            }
            catch (Exception ex)
            {
                var message = $"EPB[{result.Channel}] 启动定位快照导出失败：{ex.Message}";
                _log?.Warn(message, "落盘");
                NonCriticalObserver.Invoke(
                    SnapshotExportFailed,
                    message,
                    observerEx => _log?.Warn(
                        $"启动定位快照失败观察者异常，已隔离：{observerEx.Message}",
                        "落盘"));
            }
        }

        internal bool IsStartupPositioningHardwareConfirmed(StartupPositioningResult result)
        {
            if (result == null || result.Succeeded) return false;
            return ClassifyStartupPositioningFailure(
                       IsStartupPositioningOverCurrent(result),
                       HasFreshStartupPowerEvidence(result.Channel),
                       IsStartupPositioningOutputControlFailure(result)) ==
                   FaultClassification.HardwareConfirmed;
        }

        internal static FaultClassification ClassifyStartupPositioningFailure(
            bool overCurrent,
            bool freshIndependentPowerEvidence,
            bool outputControlFailure = false)
        {
            // 输出关闭失败属于控制链/外部设备故障，必须保持安全断电重试；
            // 只有卡钳过流且具备独立、实时电源证据时才锁存为卡钳硬件故障。
            return overCurrent && freshIndependentPowerEvidence
                ? FaultClassification.HardwareConfirmed
                : FaultClassification.SoftwareTransient;
        }

        private static bool IsStartupPositioningOutputControlFailure(
            StartupPositioningResult result)
        {
            return result?.Code?.IndexOf(
                       "OutputOffCommandFailed",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   result?.Reason?.IndexOf(
                       "OutputOffCommandFailed",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsStartupPositioningOverCurrent(StartupPositioningResult result)
        {
            return result != null &&
                   (result.Code?.IndexOf("OverCurrent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    result.Reason?.IndexOf("OverCurrent", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool HasFreshStartupPowerEvidence(int channel)
        {
            if (_powerSupply == null || channel <= 0) return false;
            var groupId = GetElectricalGroupId(channel);
            return groupId > 0 && _powerSupply.HasFreshPowerFaultEvidence(groupId);
        }

        private void ExportStartupPositioningSnapshot(
            StartupPositioningResult result,
            ControlFault fault)
        {
            var baseDir = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName, "AlarmSnapshots");
            Directory.CreateDirectory(baseDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var snapshotDir = Path.Combine(
                baseDir,
                $"{stamp}-EPB{result.Channel:D2}-StartupPositioning");
            Directory.CreateDirectory(snapshotDir);

            var samples = result.Samples ?? Array.Empty<StartupPositioningSample>();
            using (var writer = new StreamWriter(
                       Path.Combine(snapshotDir, "startup-current.csv"),
                       false,
                       new UTF8Encoding(false)))
            {
                writer.WriteLine("utc,elapsed_ms,stage,current_a,slope_a_per_ms,decision");
                foreach (var sample in samples)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(sample.Utc.ToString("O", CultureInfo.InvariantCulture)),
                        sample.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                        Csv(sample.Stage.ToString()),
                        sample.CurrentA.ToString("R", CultureInfo.InvariantCulture),
                        sample.SlopeAperMs.ToString("R", CultureInfo.InvariantCulture),
                        Csv(sample.Decision)
                    }));
                }
            }

            try
            {
                _acq?.ExportFastCurrentEvidence(
                    snapshotDir,
                    result.Channel,
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{result.Channel}] 启动定位快速原始证据导出失败：{ex.Message}", "AI");
            }

            _runIdByChannel.TryGetValue(result.Channel, out var runId);
            var doEvents = _doControlTrace.Snapshot(DateTime.UtcNow, runId)
                .Where(x => x.Channel == result.Channel)
                .ToArray();
            WriteDoTimeline(Path.Combine(snapshotDir, "do-timeline.csv"), doEvents);

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"schemaVersion\": 1,");
            json.AppendLine($"  \"capturedUtc\": \"{DateTime.UtcNow:O}\",");
            json.AppendLine($"  \"correlationId\": \"{fault.CorrelationId:N}\",");
            json.AppendLine($"  \"runId\": \"{runId:N}\",");
            json.AppendLine($"  \"channel\": {result.Channel},");
            json.AppendLine($"  \"stage\": \"{Json(result.Stage.ToString())}\",");
            json.AppendLine($"  \"code\": \"{Json(result.Code)}\",");
            json.AppendLine($"  \"reason\": \"{Json(result.Reason)}\",");
            json.AppendLine($"  \"peakCurrentA\": {result.PeakCurrentA.ToString("R", CultureInfo.InvariantCulture)},");
            json.AppendLine($"  \"lastSlopeAperMs\": {result.LastSlopeAperMs.ToString("R", CultureInfo.InvariantCulture)},");
            json.AppendLine($"  \"elapsedMs\": {result.ElapsedMs},");
            json.AppendLine($"  \"forwardProgramProgressDeadlineMs\": {result.ForwardProgramProgressDeadlineMs},");
            json.AppendLine($"  \"forwardProjectLimitMs\": {result.ForwardProjectLimitMs},");
            json.AppendLine($"  \"forwardLearnedProgressDeadlineMs\": {result.ForwardLearnedProgressDeadlineMs},");
            json.AppendLine($"  \"forwardEffectiveProgressDeadlineMs\": {result.ForwardEffectiveProgressDeadlineMs},");
            json.AppendLine($"  \"forwardAbsoluteOnTimeMs\": {result.ForwardAbsoluteOnTimeMs},");
            json.AppendLine($"  \"reverseProgramProgressDeadlineMs\": {result.ReverseProgramProgressDeadlineMs},");
            json.AppendLine($"  \"reverseLearnedProgressDeadlineMs\": {result.ReverseLearnedProgressDeadlineMs},");
            json.AppendLine($"  \"reverseEffectiveProgressDeadlineMs\": {result.ReverseEffectiveProgressDeadlineMs},");
            json.AppendLine($"  \"reverseAbsoluteOnTimeMs\": {result.ReverseAbsoluteOnTimeMs},");
            json.AppendLine($"  \"reverseReleaseThresholdA\": {result.ReverseReleaseThresholdA.ToString("R", CultureInfo.InvariantCulture)},");
            json.AppendLine($"  \"sampleCount\": {samples.Count},");
            json.AppendLine($"  \"doEventCount\": {doEvents.Length}");
            json.AppendLine("}");
            File.WriteAllText(
                Path.Combine(snapshotDir, "startup-metadata.json"),
                json.ToString(),
                new UTF8Encoding(false));

            _log?.Warn(
                $"启动定位报警快照已导出：EPB[{result.Channel}] -> {snapshotDir}",
                "落盘");
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string Json(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
