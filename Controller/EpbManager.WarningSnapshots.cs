using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Alarm;
using DataOperation;
using IO.NI;

namespace Controller
{
    public sealed partial class EpbManager
    {
        public event Action<AdaptiveWarningEvent> ChannelWarningEvidenceRaised;
        public event Action<string> SnapshotExportFailed;
        public event Action<WarningSnapshotStorageStatus> WarningSnapshotStorageChanged;

        private readonly ConcurrentDictionary<string, WarningSnapshotRequest> _pendingWarningSnapshots = new();
        private readonly ConcurrentDictionary<string, byte> _warningSnapshotJobs = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<WarningSnapshotLink>> _warningChains = new();
        private readonly ConcurrentDictionary<Guid, string> _daqIncidentDirectories = new();
        private int _warningSnapshotFreeSpaceWarningActive;

        private void OnRunnerWarningEvidenceRaised(AdaptiveWarningEvent warning)
        {
            if (warning == null) return;
            try { ChannelWarningEvidenceRaised?.Invoke(warning); } catch { }
            var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
            if (!cfg.Enabled) return;
            // 正式圈为正数，学习圈为负数；只有 0/不存在才表示尚未进入任何圈。
            if (!_currentCycleNumberByChannel.TryGetValue(warning.Channel, out var cycleNumber) || cycleNumber == 0)
            {
                ReportSnapshotFailure($"EPB[{warning.Channel}] 预警发生时没有有效圈号，未保存软预警快照。");
                return;
            }
            if (!(Recorder is ICycleEvidenceExporter))
            {
                ReportSnapshotFailure("当前圈记录器不支持 ICycleEvidenceExporter，已拒绝静默丢失预警证据。");
                return;
            }

            var request = new WarningSnapshotRequest
            {
                TestRunId = _activeBatchId,
                Channel = warning.Channel,
                CycleNumber = cycleNumber,
                Warning = warning
            };
            if (_pendingWarningSnapshots.TryAdd(request.IdempotencyKey, request))
            {
                // 在控制回调只登记不可变请求与确定性路径；实际文件导出仍在封圈后的后台线程。
                // 这样硬报警紧随其后时 warning-chain 不依赖后台IO完成时序。
                var chainKey = GetWarningChainKey(request.Channel, warning.Code);
                var queue = _warningChains.GetOrAdd(chainKey, _ => new ConcurrentQueue<WarningSnapshotLink>());
                queue.Enqueue(new WarningSnapshotLink
                {
                    CycleNumber = request.CycleNumber,
                    Streak = warning.Streak,
                    ConfirmThreshold = warning.ConfirmThreshold,
                    RelativePath = MakeRelativePath(
                        Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName),
                        GetWarningSnapshotDirectory(request, cfg))
                });
            }
        }

        private void CompleteCycleAndScheduleEvidence(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            int finalSampleCount,
            DateTime endUtc)
        {
            finalSampleCount = FinalizeCyclePersistence(
                recorder,
                channel,
                cycleNumber,
                endUtc,
                finalSampleCount);
            recorder?.CompleteCycle(channel, cycleNumber, finalSampleCount, endUtc);
            if (recorder == null) return;

            foreach (var item in _pendingWarningSnapshots.ToArray())
            {
                var request = item.Value;
                if (request.Channel != channel || request.CycleNumber != cycleNumber) continue;
                if (!_pendingWarningSnapshots.TryRemove(item.Key, out request)) continue;
                QueueWarningSnapshot(request);
            }

            QueueRollingHistoricalSnapshot(channel, cycleNumber);
        }

        private int FinalizeCyclePersistence(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime endUtc,
            int fallbackCount)
        {
            if (recorder == null) return fallbackCount;
            try
            {
                if (recorder is IBatchedEpbCycleRecorder batched)
                    batched.SealCycleWindow(channel, cycleNumber, endUtc);
                var device = _acq.GetDeviceForEpbChannel(channel);
                if (!string.IsNullOrWhiteSpace(device))
                {
                    var boundary = _acq.GetLastProducedSequence(device);
                    var deadline = Stopwatch.GetTimestamp() +
                                   (long)(_daqPersistenceRecoveryTimeoutMs / 1000.0 * Stopwatch.Frequency);
                    while (_acq.GetLastDiskPublishedSequence(device) < boundary &&
                           Stopwatch.GetTimestamp() < deadline)
                        Thread.Sleep(2);
                    var remainingMs = (int)Math.Max(
                        1,
                        (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);
                    _persistence.WaitForPersistedAsync(
                            device,
                            boundary,
                            remainingMs,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                return recorder.GetCurrentCycleSampleCount(channel);
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"EPB[{channel}] 圈封存屏障等待失败 Cycle={cycleNumber}: {ex.Message}",
                    "落盘");
                return recorder.GetCurrentCycleSampleCount(channel);
            }
        }

        private void AbortCycleAfterPersistence(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime endUtc,
            string status)
        {
            if (recorder == null) return;
            var finalN = FinalizeCyclePersistence(
                recorder,
                channel,
                cycleNumber,
                endUtc,
                recorder.GetCurrentCycleSampleCount(channel));
            recorder.AbortCycle(channel, cycleNumber, finalN, endUtc, status);
        }

        private async Task ExportDaqIncidentSnapshotAsync(
            DaqAutoRecoveryContext context,
            string reason,
            string result)
        {
            if (context == null) return;
            // 在调用线程立即冻结；后台线程不得再读取会继续变化的诊断环或恢复上下文。
            var capturedUtc = DateTime.UtcNow;
            var diagnostics = _acq.CaptureDiagnostics(
                new[] { context.Device },
                TimeSpan.FromSeconds(60));
            var queue = _persistence.GetSnapshot(context.Device);
            var runEpoch = context.RunEpoch;
            var recoveryEpoch = context.RecoveryEpoch;
            var runId = _activeBatchId;
            var beforeClock = context.BeforeClock ?? new DaqFreshnessSnapshot();
            var afterClock = context.AfterClock ?? _acq.GetDaqFreshnessSnapshot(context.Device, 100);
            var generation = _acq.GetCurrentGeneration(context.Device);
            var previousGeneration = context.PreviousGeneration;
            var recoveredGeneration = context.RecoveredGeneration;
            var firstVerifiedSequence = context.FirstVerifiedSequence;
            var lastVerifiedSequence = context.LastVerifiedSequence;
            var recoveryAttempt = context.RecoveryAttempt;
            var affectedChannels = (context.AffectedChannels ?? Array.Empty<int>()).ToArray();
            var powerStates = _powerSupply == null
                ? Array.Empty<PowerSupplyRuntimeState>()
                : _cfg.Test.Groups
                    .Where(group => group.Members.Any(affectedChannels.Contains))
                    .Select(group => _powerSupply.GetRuntimeState(group.Id))
                    .ToArray();
            var powerStatesJson = string.Join(",", powerStates.Select(state => "{" +
                $"\"group\":{state.ElectricalGroupId}," +
                $"\"operationEpoch\":{state.OperationEpoch}," +
                $"\"expectedOn\":{state.ExpectedOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"planned\":{state.PlannedTransition.ToString().ToLowerInvariant()}," +
                $"\"active\":{state.Active.ToString().ToLowerInvariant()}," +
                $"\"telemetryOn\":{state.TelemetryOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"protection\":{state.ProtectionTripped.ToString().ToLowerInvariant()}," +
                $"\"telemetryUtc\":\"{state.TelemetryUtc:O}\"" + "}"));
            var sequence = Interlocked.Increment(ref context.SnapshotSequence);
            await Task.Run(() =>
            {
                try
                {
                    var root = Path.Combine(
                        _cfg.Test.StoreDir,
                        _cfg.Test.TestName,
                        "IncidentSnapshots");
                    Directory.CreateDirectory(root);
                    var directory = _daqIncidentDirectories.GetOrAdd(
                        context.CorrelationId,
                        _ => Path.Combine(
                            root,
                            $"{context.StartedUtc.ToLocalTime():yyyyMMdd_HHmmss_fff}-" +
                            $"{context.Device}-{context.CorrelationId:N}"));
                    Directory.CreateDirectory(directory);
                    var safePhase = string.Concat((result ?? "phase")
                        .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));
                    var phaseDirectory = Path.Combine(
                        directory,
                        $"{safePhase}-{sequence:D3}-{capturedUtc:HHmmss_fff}");
                    Directory.CreateDirectory(phaseDirectory);
                    var requiredFresh = string.Equals(
                        context.TriggerCode,
                        "DaqClockModelInvalid",
                        StringComparison.OrdinalIgnoreCase)
                        ? _daqClockRecoveryFreshBatches
                        : _daqPersistenceRequiredFreshBatches;
                    var incidentJson =
                        "{\n" +
                        $"  \"device\": \"{JsonEscape(context.Device)}\",\n" +
                        $"  \"correlationId\": \"{context.CorrelationId:N}\",\n" +
                        $"  \"runId\": \"{runId:N}\",\n" +
                        $"  \"runEpoch\": {runEpoch},\n" +
                        $"  \"recoveryEpoch\": {recoveryEpoch},\n" +
                        $"  \"faultCode\": \"{JsonEscape(context.TriggerCode)}\",\n" +
                        $"  \"reason\": \"{JsonEscape(reason)}\",\n" +
                        $"  \"generation\": {generation},\n" +
                        $"  \"previousGeneration\": {previousGeneration},\n" +
                        $"  \"recoveredGeneration\": {recoveredGeneration},\n" +
                        $"  \"firstVerifiedSequence\": {firstVerifiedSequence},\n" +
                        $"  \"lastVerifiedSequence\": {lastVerifiedSequence},\n" +
                        $"  \"recoveryAttempt\": {recoveryAttempt},\n" +
                        $"  \"clockRecoveryMaxAttempts\": {_daqClockRecoveryMaxAttempts},\n" +
                        $"  \"clockRecoveryWindowMinutes\": {_daqClockRecoveryWindowMinutes},\n" +
                        $"  \"beforeClockState\": \"{beforeClock.ClockState}\",\n" +
                        $"  \"beforeSampleRateHz\": {beforeClock.EffectiveSampleRateHz.ToString("F6", CultureInfo.InvariantCulture)},\n" +
                        $"  \"beforeSkewPpm\": {beforeClock.EstimatedSkewPpm.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"beforeResidualMs\": {beforeClock.ClockResidualMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"afterClockState\": \"{afterClock.ClockState}\",\n" +
                        $"  \"afterSampleRateHz\": {afterClock.EffectiveSampleRateHz.ToString("F6", CultureInfo.InvariantCulture)},\n" +
                        $"  \"afterSkewPpm\": {afterClock.EstimatedSkewPpm.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"afterResidualMs\": {afterClock.ClockResidualMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"queueDepth\": {queue.QueueDepth},\n" +
                        $"  \"oldestBatchAgeMs\": {queue.OldestBatchAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"affectedChannels\": [{string.Join(",", affectedChannels)}],\n" +
                        $"  \"powerStates\": [{powerStatesJson}],\n" +
                        "  \"processingQueueCapacity\": 64,\n" +
                        $"  \"persistenceQueueCapacity\": {_daqPersistenceQueueCapacity},\n" +
                        $"  \"pauseDepth\": {_daqPersistencePauseDepth},\n" +
                        $"  \"resumeDepth\": {_daqPersistenceResumeDepth},\n" +
                        $"  \"pauseAgeMs\": {_daqPersistencePauseAgeMs.ToString("F0", CultureInfo.InvariantCulture)},\n" +
                        $"  \"resumeAgeMs\": {_daqPersistenceResumeAgeMs.ToString("F0", CultureInfo.InvariantCulture)},\n" +
                        $"  \"recoveryTimeoutMs\": {_daqPersistenceRecoveryTimeoutMs},\n" +
                        $"  \"requiredFreshBatches\": {requiredFresh},\n" +
                        $"  \"suppressedBatches\": {queue.SuppressedBatchCount},\n" +
                        $"  \"discardedGenerationBatches\": {queue.DiscardedGenerationBatchCount},\n" +
                        "  \"validBatchesDroppedByClockModel\": 0,\n" +
                        $"  \"result\": \"{JsonEscape(result)}\",\n" +
                        $"  \"capturedUtc\": \"{capturedUtc:O}\"\n" +
                        "}\n";
                    using (var stream = new FileStream(
                               Path.Combine(phaseDirectory, "incident.json"),
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(incidentJson);
                    diagnostics.WriteTo(phaseDirectory);
                    var recorder = Recorder;
                    if (recorder != null)
                    {
                        foreach (var channel in affectedChannels)
                        {
                            var sub = Path.Combine(phaseDirectory, $"EPB{channel:D2}");
                            Directory.CreateDirectory(sub);
                            try { recorder.FlushRecentTo(channel, 10, sub, includeRunningCycle: true); }
                            catch (Exception ex) { _log.Warn($"Incident EPB[{channel}] 证据导出失败：{ex.Message}", "落盘"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"DAQ IncidentSnapshot 导出失败：{ex.Message}", "落盘", ex);
                }
            }).ConfigureAwait(false);
        }

        private async Task ExportDaqHardFaultIncidentSnapshotAsync(
            DaqIncidentContext initialContext,
            DaqDeviceFault evidence)
        {
            if (initialContext == null) return;
            // 触发线程立即冻结所有可变证据；后台只消费冻结副本。
            var context = initialContext.Clone();
            var capturedUtc = DateTime.UtcNow;
            var control = _acq.GetControlSnapshot(context.Device);
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var powerStates = _powerSupply == null
                ? Array.Empty<PowerSupplyRuntimeState>()
                : _cfg.Test.Groups
                    .Where(group => group.Members.Any((context.AffectedChannels ?? Array.Empty<int>()).Contains))
                    .Select(group => _powerSupply.GetRuntimeState(group.Id))
                    .ToArray();
            var powerStatesJson = string.Join(",", powerStates.Select(state => "{" +
                $"\"group\":{state.ElectricalGroupId}," +
                $"\"operationEpoch\":{state.OperationEpoch}," +
                $"\"expectedOn\":{state.ExpectedOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"planned\":{state.PlannedTransition.ToString().ToLowerInvariant()}," +
                $"\"telemetryOn\":{state.TelemetryOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"protection\":{state.ProtectionTripped.ToString().ToLowerInvariant()}" + "}"));
            var diagnostics = _acq.CaptureDiagnostics(
                new[] { context.Device },
                TimeSpan.FromSeconds(60));
            await Task.Run(() =>
            {
                string intendedDirectory = null;
                try
                {
                    var root = Path.Combine(
                        _cfg.Test.StoreDir,
                        _cfg.Test.TestName,
                        "IncidentSnapshots");
                    Directory.CreateDirectory(root);
                    intendedDirectory = _daqIncidentDirectories.GetOrAdd(
                        context.CorrelationId,
                        _ => Path.Combine(
                            root,
                            $"{context.FirstSeenUtc.ToLocalTime():yyyyMMdd_HHmmss_fff}-" +
                            $"{context.Device}-{context.CorrelationId:N}"));
                    Directory.CreateDirectory(intendedDirectory);
                    var phaseDirectory = Path.Combine(
                        intendedDirectory,
                        $"90-terminal-{capturedUtc:HHmmss_fff}-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(phaseDirectory);

                    var derivedCodes = context.DerivedCodes
                        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                        .Select(code => $"\"{JsonEscape(code)}\"");
                    var incidentJson = "{\n" +
                        $"  \"runId\": \"{context.RunId:N}\",\n" +
                        $"  \"device\": \"{JsonEscape(context.Device)}\",\n" +
                        $"  \"generation\": {context.Generation},\n" +
                        $"  \"correlationId\": \"{context.CorrelationId:N}\",\n" +
                        $"  \"runEpoch\": {runEpoch},\n" +
                        $"  \"primaryFault\": \"{JsonEscape(context.PrimaryCode)}\",\n" +
                        $"  \"primaryReason\": \"{JsonEscape(context.PrimaryReason)}\",\n" +
                        $"  \"primaryChannel\": {context.PrimaryChannel},\n" +
                        $"  \"affectedChannels\": [{string.Join(",", context.AffectedChannels ?? Array.Empty<int>())}],\n" +
                        $"  \"powerStates\": [{powerStatesJson}],\n" +
                        $"  \"derivedActions\": [{string.Join(",", derivedCodes)}],\n" +
                        $"  \"firstSeenUtc\": \"{context.FirstSeenUtc:O}\",\n" +
                        $"  \"lastSeenUtc\": \"{context.LastSeenUtc:O}\",\n" +
                        $"  \"controlQueueType\": \"{JsonEscape(control?.QueueType ?? evidence?.QueueType ?? "PreallocatedSpscRing")}\",\n" +
                        $"  \"controlQueueCapacity\": {control?.QueueCapacity ?? evidence?.QueueCapacity ?? 0},\n" +
                        $"  \"controlQueueDepth\": {control?.QueueDepth ?? evidence?.QueueDepth ?? 0},\n" +
                        $"  \"oldestControlBatchAgeMs\": {(control?.OldestBatchAgeMs ?? evidence?.OldestBatchAgeMs ?? 0).ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"lastControlProcessingMs\": {(control?.LastBatchProcessMs ?? 0).ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"subscriberMaxMs\": {(control?.SubscriberMaxMs ?? 0).ToString("F3", CultureInfo.InvariantCulture)},\n" +
                        $"  \"lastProcessedSampleUtc\": \"{(control?.ProcessedSampleUtc ?? evidence?.LastProcessedSampleUtc ?? default):O}\",\n" +
                        $"  \"capturedUtc\": \"{capturedUtc:O}\"\n" +
                        "}\n";
                    using (var stream = new FileStream(
                               Path.Combine(phaseDirectory, "incident.json"),
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(incidentJson);
                    diagnostics.WriteTo(phaseDirectory);
                    RuntimeBuildIdentity.Capture().WriteJson(
                        Path.Combine(phaseDirectory, "build-identity.json"));
                    _log.Info(
                        $"DAQ硬故障事故快照已保存：{phaseDirectory} " +
                        $"CorrelationId={context.CorrelationId:N}",
                        "落盘");
                }
                catch (Exception ex)
                {
                    var fallbackDirectory = TryWriteDaqSnapshotFailure(
                        initialContext,
                        intendedDirectory,
                        ex);
                    _log.Error(
                        $"DAQ硬故障事故快照导出失败。Target={intendedDirectory ?? "unknown"} " +
                        $"Fallback={fallbackDirectory} Error={ex.Message}",
                        "落盘",
                        ex);
                }
            }).ConfigureAwait(false);
        }

        private string TryWriteDaqSnapshotFailure(
            DaqIncidentContext context,
            string intendedDirectory,
            Exception error)
        {
            var candidates = new[]
            {
                intendedDirectory,
                Path.Combine(
                    Path.GetTempPath(),
                    "EPBTest",
                    "IncidentSnapshots",
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}-{context.Device}-{context.CorrelationId:N}"),
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "IncidentSnapshots-Fallback",
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}-{context.Device}-{context.CorrelationId:N}")
            };
            foreach (var directory in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var failureJson = "{\n" +
                        $"  \"device\": \"{JsonEscape(context.Device)}\",\n" +
                        $"  \"runId\": \"{context.RunId:N}\",\n" +
                        $"  \"generation\": {context.Generation},\n" +
                        $"  \"correlationId\": \"{context.CorrelationId:N}\",\n" +
                        $"  \"intendedDirectory\": \"{JsonEscape(intendedDirectory)}\",\n" +
                        $"  \"fallbackDirectory\": \"{JsonEscape(directory)}\",\n" +
                        $"  \"error\": \"{JsonEscape(error?.ToString())}\",\n" +
                        $"  \"timestampUtc\": \"{DateTime.UtcNow:O}\"\n" +
                        "}\n";
                    using (var stream = new FileStream(
                               Path.Combine(directory, $"snapshot-failure-{Guid.NewGuid():N}.json"),
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(failureJson);
                    return directory;
                }
                catch { }
            }
            return "unavailable";
        }

        private void QueueWarningSnapshot(WarningSnapshotRequest request)
        {
            if (request == null || !_warningSnapshotJobs.TryAdd(request.IdempotencyKey, 0)) return;
            _ = Task.Run(() => ExportWarningSnapshot(request));
        }

        private void ExportWarningSnapshot(WarningSnapshotRequest request)
        {
            try
            {
                var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
                var exporter = Recorder as ICycleEvidenceExporter
                    ?? throw new InvalidOperationException("Recorder does not implement ICycleEvidenceExporter.");
                var warning = request.Warning;
                var directory = GetWarningSnapshotDirectory(request, cfg);
                Directory.CreateDirectory(directory);
                PublishWarningSnapshotStorageStatus();

                var evidence = exporter.ExportCompletedCycleTo(
                    request.Channel,
                    request.CycleNumber,
                    directory,
                    cfg.SaveCsv,
                    cfg.SaveBin);
                var hashes = new ConcurrentDictionary<string, string>();
                foreach (var path in new[] { evidence.CsvPath, evidence.BinPath }.Where(File.Exists))
                    hashes[Path.GetFileName(path)] = ComputeSha256(path);

                var chainKey = GetWarningChainKey(request.Channel, warning.Code);
                var linkedAlarmPath = _warningChains.TryGetValue(chainKey, out var registeredLinks)
                    ? registeredLinks.FirstOrDefault(x =>
                        x.CycleNumber == request.CycleNumber && x.Streak == warning.Streak)
                        ?.LinkedAlarmRelativePath ?? string.Empty
                    : string.Empty;

                File.WriteAllText(
                    Path.Combine(directory, "warning-metadata.json"),
                    BuildWarningMetadataJson(request, evidence, hashes, linkedAlarmPath),
                    new UTF8Encoding(false));
                File.WriteAllLines(
                    Path.Combine(directory, "checksums.sha256"),
                    hashes.OrderBy(x => x.Key).Select(x => $"{x.Value}  {x.Key}"),
                    new UTF8Encoding(false));

                _log.Info($"软预警完整单圈快照已保存：{directory}", "落盘");
                EnforceSoftWarningQuota(request.TestRunId, cfg);
                PublishWarningSnapshotStorageStatus();
            }
            catch (Exception ex)
            {
                ReportSnapshotFailure(
                    $"WarningSnapshotExportFailed Key={request.IdempotencyKey} Error={ex.Message}",
                    ex);
            }
        }

        private void QueueRollingHistoricalSnapshot(int channel, int cycleNumber)
        {
            if (!(Recorder is ICycleEvidenceExporter exporter)) return;
            _ = Task.Run(() =>
            {
                try
                {
                    var root = Path.Combine(
                        _cfg.Test.StoreDir,
                        _cfg.Test.TestName,
                        "HistoricalSnapshots",
                        $"EPB{channel:D2}");
                    var dir = Path.Combine(root, $"Cycle_{cycleNumber:D6}");
                    exporter.ExportCompletedCycleTo(channel, cycleNumber, dir, false, true);
                    var snapshotCount = AlarmConfig?.WarningSnapshots?.HardAlarmLastNCycles
                                        ?? AlarmConfig?.Behavior?.SnapshotLastNCycles
                                        ?? 10;
                    var keep = Math.Max(3, snapshotCount + 2);
                    foreach (var old in new DirectoryInfo(root).EnumerateDirectories("Cycle_*")
                                 .OrderByDescending(x => x.Name).Skip(keep))
                    {
                        try { old.Delete(true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    ReportSnapshotFailure(
                        $"HistoricalSnapshotExportFailed EPB={channel} Cycle={cycleNumber} Error={ex.Message}",
                        ex);
                }
            });
        }

        private string GetWarningSnapshotDirectory(
            WarningSnapshotRequest request,
            WarningSnapshotConfig cfg)
        {
            var rootName = string.IsNullOrWhiteSpace(cfg?.RootDirectory)
                ? "WarningSnapshots"
                : cfg.RootDirectory.Trim();
            var warning = request.Warning;
            var folder =
                $"{warning.OccurredUtc.ToLocalTime():yyyyMMdd_HHmmssfff}-" +
                $"Cycle{request.CycleNumber:D6}-Streak{warning.Streak}of{warning.ConfirmThreshold}";
            return Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                rootName,
                $"EPB{request.Channel:D2}",
                warning.Code.ToString(),
                folder);
        }

        private void WriteWarningChain(string alarmSnapshotDirectory, int channel, string reason, int terminalCycle)
        {
            AdaptiveWarningCode? code = null;
            if (reason?.IndexOf("ForwardPeakOvershoot", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.ForwardPeakOvershootWarning;
            else if (reason?.IndexOf("PeakEvidenceMismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.PeakEvidenceMismatchWarning;
            else if (reason?.IndexOf("PeakEvidenceLag", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.PeakEvidenceLagWarning;
            else if (reason?.IndexOf("ForwardCurrentRiseStall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     reason?.IndexOf("ForwardCurrentRiseStalled", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.ForwardCurrentRiseStallWarning;
            if (!code.HasValue) return;

            var links = Array.Empty<WarningSnapshotLink>();
            if (_warningChains.TryGetValue(GetWarningChainKey(channel, code.Value), out var queue))
            {
                var candidates = queue.Where(x => x.CycleNumber < terminalCycle)
                    .OrderByDescending(x => x.CycleNumber)
                    .ToArray();
                var threshold = candidates.FirstOrDefault()?.ConfirmThreshold ?? 0;
                var selected = new System.Collections.Generic.List<WarningSnapshotLink>();
                for (var expected = threshold - 1; expected >= 1; expected--)
                {
                    var match = candidates.FirstOrDefault(x => x.Streak == expected);
                    if (match == null) break;
                    selected.Add(match);
                    candidates = candidates.Where(x => x.CycleNumber < match.CycleNumber).ToArray();
                }
                if (selected.Count == Math.Max(0, threshold - 1))
                    links = selected.OrderBy(x => x.Streak).ToArray();
            }
            if (links.Length == 0) return;
            var testRoot = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName);
            var linkedAlarmRelativePath = MakeRelativePath(testRoot, alarmSnapshotDirectory);
            foreach (var link in links)
            {
                link.LinkedAlarmRelativePath = linkedAlarmRelativePath;
                TryUpdateWarningMetadataLink(testRoot, link);
            }
            var sb = new StringBuilder();
            sb.Append("{\n  \"WarningCode\": \"").Append(code.Value).Append("\",")
                .Append("\n  \"HardAlarmTerminalCycle\": ").Append(terminalCycle).Append(',')
                .Append("\n  \"Links\": [");
            for (var i = 0; i < links.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("\n    {\"CycleNumber\": ").Append(links[i].CycleNumber)
                    .Append(", \"Streak\": ").Append(links[i].Streak)
                    .Append(", \"ConfirmThreshold\": ").Append(links[i].ConfirmThreshold)
                    .Append(", \"Path\": \"").Append(JsonEscape(links[i].RelativePath)).Append("\"}");
            }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(
                Path.Combine(alarmSnapshotDirectory, "warning-chain.json"),
                sb.ToString(),
                new UTF8Encoding(false));
        }

        private static void WriteAlarmSnapshotManifest(
            string snapshotDirectory,
            int alarmChannel,
            int alarmCycle,
            int requestedCycles,
            string reason,
            int[] affectedChannels)
        {
            var files = Directory.EnumerateFiles(snapshotDirectory, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path)
                .ToArray();
            var alarmFolder = $"EPB{alarmChannel:D2}_ALARM";
            var alarmCycleToken = $"_{alarmCycle:D6}.";
            var alarmCycles = files
                .Where(path => path.IndexOf(alarmFolder, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var sb = new StringBuilder();
            sb.Append("{\n  \"AlarmChannel\": ").Append(alarmChannel).Append(',')
                .Append("\n  \"AlarmCycle\": ").Append(alarmCycle).Append(',')
                .Append("\n  \"Reason\": \"").Append(JsonEscape(reason)).Append("\",")
                .Append("\n  \"AffectedChannels\": [")
                .Append(string.Join(",", affectedChannels ?? new[] { alarmChannel })).Append("],")
                .Append("\n  \"GroupCoverage\": \"Primary alarm cycle is sealed for AlarmChannel; ")
                .Append("sibling channels are exported as recent/running auxiliary evidence\",")
                .Append("\n  \"RequestedCycles\": ").Append(requestedCycles).Append(',')
                .Append("\n  \"ExportedCycles\": ").Append(alarmCycles).Append(',')
                .Append("\n  \"ShortfallReason\": \"")
                .Append(alarmCycles < requestedCycles
                    ? $"Only {alarmCycles} completed/alarm cycles were available"
                    : string.Empty)
                .Append("\",")
                .Append("\n  \"Files\": [");
            for (var i = 0; i < files.Length; i++)
            {
                var relative = MakeRelativePath(snapshotDirectory, files[i]);
                var inAlarmFolder = relative.IndexOf(alarmFolder, StringComparison.OrdinalIgnoreCase) >= 0;
                var role = inAlarmFolder
                    ? relative.IndexOf(alarmCycleToken, StringComparison.OrdinalIgnoreCase) >= 0
                        ? alarmCycle < 0 ? "LearningCycle" : "AlarmCycle"
                        : "HistoricalCompletedCycle"
                    : "AuxiliaryRunningPartial";
                if (i > 0) sb.Append(',');
                sb.Append("\n    {\"Path\": \"").Append(JsonEscape(relative))
                    .Append("\", \"Role\": \"").Append(role)
                    .Append("\", \"IsComplete\": ").Append(role == "AuxiliaryRunningPartial" ? "false" : "true")
                    .Append(", \"Sha256\": \"").Append(ComputeSha256(files[i])).Append("\"}");
            }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(
                Path.Combine(snapshotDirectory, "snapshot-manifest.json"),
                sb.ToString(),
                new UTF8Encoding(false));
        }

        private static string BuildWarningMetadataJson(
            WarningSnapshotRequest request,
            CycleSnapshotEvidence evidence,
            ConcurrentDictionary<string, string> hashes,
            string linkedAlarmSnapshotPath)
        {
            var w = request.Warning;
            var files = string.Join(",\n", hashes.OrderBy(x => x.Key).Select(x =>
                $"    \"{JsonEscape(x.Key)}\": \"{x.Value}\""));
            return "{\n" +
                   $"  \"TestRunId\": \"{request.TestRunId:N}\",\n" +
                   $"  \"Channel\": {request.Channel},\n" +
                   $"  \"CycleNumber\": {request.CycleNumber},\n" +
                   $"  \"WarningCode\": \"{w.Code}\",\n" +
                   $"  \"OccurredUtc\": \"{w.OccurredUtc:O}\",\n" +
                   $"  \"PeakCurrentA\": {w.PeakCurrentA.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"TargetCurrentA\": {w.TargetCurrentA.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"PeakErrorA\": {w.PeakErrorA.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"SlopeAperMs\": {w.SlopeAperMs.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"WindowSpanMs\": {w.WindowSpanMs},\n" +
                   $"  \"Streak\": {w.Streak},\n" +
                   $"  \"ConfirmThreshold\": {w.ConfirmThreshold},\n" +
                   $"  \"FirstSampleUtc\": \"{evidence.FirstSampleUtc:O}\",\n" +
                   $"  \"LastSampleUtc\": \"{evidence.LastSampleUtc:O}\",\n" +
                   $"  \"SampleCount\": {evidence.SampleCount},\n" +
                   $"  \"IsCompleteCycle\": {(evidence.IsCompleteCycle ? "true" : "false")},\n" +
                   $"  \"LinkedAlarmSnapshotPath\": \"{JsonEscape(linkedAlarmSnapshotPath)}\",\n" +
                   "  \"FileSha256\": {\n" + files + "\n  }\n}\n";
        }

        private static void TryUpdateWarningMetadataLink(string testRoot, WarningSnapshotLink link)
        {
            try
            {
                var metadataPath = Path.Combine(testRoot, link.RelativePath, "warning-metadata.json");
                if (!File.Exists(metadataPath)) return;
                var json = File.ReadAllText(metadataPath);
                const string marker = "\"LinkedAlarmSnapshotPath\": \"\"";
                if (json.IndexOf(marker, StringComparison.Ordinal) < 0) return;
                json = json.Replace(
                    marker,
                    $"\"LinkedAlarmSnapshotPath\": \"{JsonEscape(link.LinkedAlarmRelativePath)}\"");
                var temporary = metadataPath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Replace(temporary, metadataPath, null);
            }
            catch
            {
                // 链文件本身仍是主关联证据；元数据补链失败由后续清单校验发现。
            }
        }

        private void ReportSnapshotFailure(string message, Exception exception = null)
        {
            _log.Error(message, "落盘", exception);
            try
            {
                var path = Path.Combine(
                    _cfg.Test.StoreDir,
                    _cfg.Test.TestName,
                    "snapshot-export-errors.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(
                    path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { }
            try { SnapshotExportFailed?.Invoke(message); } catch { }
        }

        public WarningSnapshotStorageStatus GetWarningSnapshotStorageStatus()
        {
            var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
            var root = Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                string.IsNullOrWhiteSpace(cfg.RootDirectory) ? "WarningSnapshots" : cfg.RootDirectory.Trim());
            var used = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(x => x.IndexOf(Path.DirectorySeparatorChar + "Archive" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0)
                    .Sum(x => { try { return new FileInfo(x).Length; } catch { return 0L; } })
                : 0L;
            long free = 0;
            try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))).AvailableFreeSpace; } catch { }
            var snapshots = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "warning-metadata.json", SearchOption.AllDirectories).Count()
                : 0;
            var average = snapshots > 0 ? Math.Max(1L, used / snapshots) : 0L;
            return new WarningSnapshotStorageStatus
            {
                RootDirectory = root,
                UsedBytes = used,
                FreeBytes = free,
                EstimatedAdditionalCycles = average > 0 ? free / average : 0,
                IsBelowFreeSpaceWarning = free > 0 &&
                    free < Math.Max(0L, cfg.DiskFreeWarningMb) * 1024L * 1024L
            };
        }

        private void PublishWarningSnapshotStorageStatus()
        {
            try
            {
                var status = GetWarningSnapshotStorageStatus();
                WarningSnapshotStorageChanged?.Invoke(status);
                if (status.IsBelowFreeSpaceWarning)
                {
                    if (System.Threading.Interlocked.Exchange(
                            ref _warningSnapshotFreeSpaceWarningActive, 1) == 0)
                        _log.Warn(
                            $"WarningSnapshots 磁盘余量低：Free={status.FreeBytes / 1024d / 1024d:F0}MB " +
                            $"Root={status.RootDirectory}",
                            "落盘");
                }
                else
                {
                    System.Threading.Interlocked.Exchange(
                        ref _warningSnapshotFreeSpaceWarningActive, 0);
                }
            }
            catch { }
        }

        private void EnforceSoftWarningQuota(Guid currentRunId, WarningSnapshotConfig cfg)
        {
            if (cfg == null || cfg.SoftWarningQuotaMb <= 0 || currentRunId == Guid.Empty) return;
            var root = Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                string.IsNullOrWhiteSpace(cfg.RootDirectory) ? "WarningSnapshots" : cfg.RootDirectory.Trim());
            if (!Directory.Exists(root)) return;
            var quotaBytes = cfg.SoftWarningQuotaMb * 1024L * 1024L;
            var currentToken = $"\"TestRunId\": \"{currentRunId:N}\"";
            var candidates = Directory.EnumerateFiles(root, "warning-metadata.json", SearchOption.AllDirectories)
                .Where(x => x.IndexOf(Path.DirectorySeparatorChar + "Archive" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) < 0)
                .Select(Path.GetDirectoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(dir =>
                {
                    try { return !File.ReadAllText(Path.Combine(dir, "warning-metadata.json")).Contains(currentToken); }
                    catch { return false; }
                })
                .OrderBy(dir => new DirectoryInfo(dir).CreationTimeUtc)
                .ToArray();

            foreach (var directory in candidates)
            {
                if (GetWarningSnapshotStorageStatus().UsedBytes <= quotaBytes) break;
                ArchiveVerifiedSoftWarning(directory, root);
            }
        }

        private void ArchiveVerifiedSoftWarning(string directory, string root)
        {
            var archiveRoot = Path.Combine(root, "Archive");
            Directory.CreateDirectory(archiveRoot);
            var baseName = Path.GetFileName(directory) + "-" + Guid.NewGuid().ToString("N");
            var temporary = Path.Combine(archiveRoot, baseName + ".zip.tmp");
            var completed = Path.Combine(archiveRoot, baseName + ".zip");
            ZipFile.CreateFromDirectory(directory, temporary, CompressionLevel.Optimal, false);
            using (var archive = ZipFile.OpenRead(temporary))
            {
                if (archive.Entries.Count == 0)
                    throw new InvalidDataException("软预警归档为空，拒绝删除源目录。");
            }
            File.Move(temporary, completed);
            File.WriteAllText(completed + ".sha256", ComputeSha256(completed), new UTF8Encoding(false));
            Directory.Delete(directory, true);
            _log.Info($"旧批次软预警已校验归档：{completed}", "落盘");
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string GetWarningChainKey(int channel, AdaptiveWarningCode code) => $"{channel}:{code}";
        private static string JsonEscape(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        private static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private sealed class WarningSnapshotLink
        {
            public int CycleNumber;
            public int Streak;
            public int ConfirmThreshold;
            public string RelativePath;
            public string LinkedAlarmRelativePath;
        }
    }
}
