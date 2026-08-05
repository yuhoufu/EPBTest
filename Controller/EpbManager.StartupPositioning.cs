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
            var overCurrent = result.Code?.IndexOf("OverCurrent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              result.Reason?.IndexOf("OverCurrent", StringComparison.OrdinalIgnoreCase) >= 0;
            var groupId = GetElectricalGroupId(result.Channel);
            var classification = overCurrent &&
                                 (_powerSupply == null || groupId <= 0 ||
                                  !_powerSupply.HasFreshPowerFaultEvidence(groupId))
                ? FaultClassification.SystemFault
                : FaultClassification.HardwareConfirmed;
            var fault = new ControlFault(
                string.IsNullOrWhiteSpace(result.Code) ? "StartupPositioningFailed" : result.Code,
                reason,
                FaultScope.Channel,
                new[] { result.Channel },
                null,
                DateTime.UtcNow,
                Guid.NewGuid(),
                classification);

            if (classification == FaultClassification.SystemFault)
            {
                try { CommandEpbOffSafetyImmediate(result.Channel); } catch { }
                PublishChannelRuntimeState(
                    result.Channel,
                    ChannelRuntimeState.SystemFault,
                    fault.Code,
                    "启动定位过流缺少新鲜PSU独立证据；已安全断电但不触发硬件报警。" + reason,
                    affectedChannels: fault.AffectedChannels,
                    correlationId: fault.CorrelationId);
                _log?.Error(
                    $"EPB[{result.Channel}] 启动定位单源过流归为系统故障。" +
                    $"CorrelationId={fault.CorrelationId:N} {reason}",
                    "报警");
                FlushPersistentLog();
                try { ControlFaultRaised?.Invoke(fault); } catch { }
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
            try { ControlFaultRaised?.Invoke(fault); } catch { }
            try { ChannelAlarmRaised?.Invoke(result.Channel, reason); } catch { }
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
                try { SnapshotExportFailed?.Invoke(message); } catch { }
            }
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
