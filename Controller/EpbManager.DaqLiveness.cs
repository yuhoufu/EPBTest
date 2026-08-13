using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Config;
using IO.NI;

namespace Controller
{
    internal readonly struct DaqUnenergizedGapDecision
    {
        internal DaqUnenergizedGapDecision(bool emit, string code, string reason)
        {
            Emit = emit;
            Code = code ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        internal bool Emit { get; }
        internal string Code { get; }
        internal string Reason { get; }
    }

    /// <summary>
    /// Bounded, run-scoped gate for DAQ liveness transition logs.  The watchdog can
    /// observe one shared recovery on every timer tick while the recovery owner is
    /// still being created; only the first transition log is useful evidence.
    /// </summary>
    internal sealed class DaqLivenessLogTransitionGate
    {
        private const int MaximumEntries = 32;
        private readonly object _gate = new object();
        private readonly Queue<string> _order = new Queue<string>(MaximumEntries);
        private readonly HashSet<string> _emitted =
            new HashSet<string>(StringComparer.Ordinal);
        private Guid _runId;
        private long _runEpoch;

        internal int Count
        {
            get { lock (_gate) return _emitted.Count; }
        }

        internal void BeginSession(Guid runId, long runEpoch)
        {
            lock (_gate)
            {
                _runId = runId;
                _runEpoch = runEpoch;
                _order.Clear();
                _emitted.Clear();
            }
        }

        internal bool TryAccept(
            Guid runId,
            long runEpoch,
            Guid correlationId,
            IEnumerable<string> participants,
            string result,
            string transitionToken = null)
        {
            var key = BuildKey(
                runId,
                runEpoch,
                correlationId,
                participants,
                result,
                transitionToken);
            lock (_gate)
            {
                if (_runId != runId || _runEpoch != runEpoch)
                {
                    _runId = runId;
                    _runEpoch = runEpoch;
                    _order.Clear();
                    _emitted.Clear();
                }

                if (!_emitted.Add(key)) return false;
                _order.Enqueue(key);
                while (_order.Count > MaximumEntries)
                    _emitted.Remove(_order.Dequeue());
                return true;
            }
        }

        internal static string BuildKey(
            Guid runId,
            long runEpoch,
            Guid correlationId,
            IEnumerable<string> participants,
            string result,
            string transitionToken = null)
        {
            var devices = (participants ?? Enumerable.Empty<string>())
                .Where(device => !string.IsNullOrWhiteSpace(device))
                .Select(device => device.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(device => device, StringComparer.Ordinal)
                .ToArray();
            return $"{runId:N}|{runEpoch}|{correlationId:N}|" +
                   $"{(result ?? string.Empty).Trim().ToUpperInvariant()}|" +
                   $"{string.Join(",", devices)}|" +
                   $"{(transitionToken ?? string.Empty).Trim()}";
        }
    }

    internal readonly struct DaqLivenessDecision
    {
        internal DaqLivenessDecision(bool trip, string code, string reason)
        {
            Trip = trip;
            Code = code ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        internal bool Trip { get; }
        internal string Code { get; }
        internal string Reason { get; }
    }

    public sealed partial class EpbManager
    {
        internal static DaqLivenessDecision EvaluateDaqLiveness(
            bool batchActive,
            bool deviceEnergized,
            bool recoveryActive,
            DaqFreshnessSnapshot freshness,
            long observedGapEventCount,
            double staleThresholdMs)
        {
            if (!batchActive || !deviceEnergized || recoveryActive || freshness == null)
                return new DaqLivenessDecision(false, string.Empty, string.Empty);
            var threshold = Math.Max(50, staleThresholdMs);
            if (freshness.CallbackGapEventCount > observedGapEventCount &&
                freshness.LastCallbackGapIntervalMs > threshold)
                return new DaqLivenessDecision(
                    true,
                    "DaqCallbackGap",
                    $"独立DAQ存活监督发现带电期间发生{freshness.LastCallbackGapIntervalMs:F1}ms回调空窗；" +
                    $"GapEvent={freshness.CallbackGapEventCount} Generation={freshness.Generation} " +
                    $"Produced={freshness.LastProducedSequence} " +
                    $"Processed={freshness.LastProcessedSequence}");
            if (freshness.LastCallbackMonotonicTicks > 0 &&
                freshness.CallbackAgeMs <= threshold)
                return new DaqLivenessDecision(false, string.Empty, string.Empty);
            return new DaqLivenessDecision(
                true,
                "DaqCallbackStale",
                $"独立DAQ存活监督发现回调超过{threshold:F0}ms未更新；" +
                $"CallbackAge={freshness.CallbackAgeMs:F1}ms " +
                $"Generation={freshness.Generation} Produced={freshness.LastProducedSequence} " +
                    $"Processed={freshness.LastProcessedSequence}");
        }

        internal static DaqUnenergizedGapDecision EvaluateUnenergizedDaqGap(
            bool batchActive,
            bool deviceEnergized,
            bool recoveryActive,
            DaqFreshnessSnapshot freshness,
            bool hasObservedBaseline,
            long observedGapEventCount,
            double staleThresholdMs)
        {
            if (!batchActive || deviceEnergized || recoveryActive || freshness == null)
                return new DaqUnenergizedGapDecision(false, string.Empty, string.Empty);

            // A key's first observation establishes a baseline.  It may represent a
            // callback gap from before this batch (or before the watchdog was started)
            // and must not create an INFO event retroactively.
            if (!hasObservedBaseline ||
                freshness.CallbackGapEventCount <= observedGapEventCount)
                return new DaqUnenergizedGapDecision(false, string.Empty, string.Empty);

            var threshold = Math.Max(50, staleThresholdMs);
            if (freshness.LastCallbackGapIntervalMs <= threshold)
                return new DaqUnenergizedGapDecision(false, string.Empty, string.Empty);

            return new DaqUnenergizedGapDecision(
                true,
                "ObservedUnenergizedGap",
                $"未带电DAQ观察到超过{threshold:F0}ms的回调空窗；" +
                $"GapEvent={freshness.CallbackGapEventCount} " +
                $"GapIntervalMs={freshness.LastCallbackGapIntervalMs:F1} " +
                $"Generation={freshness.Generation} " +
                $"Produced={freshness.LastProducedSequence} " +
                $"Processed={freshness.LastProcessedSequence}");
        }

        internal static long SelectDeviceValue(string device, long dev1, long dev2)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? dev1
                : dev2;

        private void InspectDaqLiveness(object state)
        {
            if (!IsBatchSessionActive ||
                Interlocked.Exchange(ref _daqLivenessWatchdogBusy, 1) != 0)
                return;
            try
            {
                var incidents = new List<DaqDeviceFault>(2);
                var gapKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var gapCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var freshnessByDevice = new Dictionary<string, DaqFreshnessSnapshot>(
                    StringComparer.OrdinalIgnoreCase);
                var existingIncidents = new Dictionary<string, DaqIncidentContext>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var device in new[] { "Dev1", "Dev2" })
                {
                    var freshness = _acq.GetDaqFreshnessSnapshot(
                        device,
                        _daqLivenessStaleThresholdMs);
                    freshnessByDevice[device] = freshness;
                    var recoveryActive = _daqAutoRecovery.TryGetValue(device, out var recovery) &&
                                         recovery.Terminal.Current == DaqRecoveryTerminal.None;
                    var deviceEnergized = IsDaqDeviceControlActive(device);
                    if (_daqIncidentLatch.TryGet(
                            _activeBatchId,
                            device,
                            out var existingIncident))
                        existingIncidents[device] = existingIncident;
                    var gapKey = BuildDaqLivenessGapKey(device, freshness?.Generation ?? 0);
                    var hasObservedGapBaseline = _daqLivenessObservedGapEvents.TryGetValue(
                        gapKey,
                        out var observedGapEvents);
                    if (!hasObservedGapBaseline && !deviceEnergized && freshness != null)
                    {
                        // The first key observation is a baseline.  In particular, a
                        // callback gap that happened before the batch or before this
                        // generation was observed must not be reported retroactively.
                        observedGapEvents = freshness.CallbackGapEventCount;
                        _daqLivenessObservedGapEvents.TryAdd(gapKey, observedGapEvents);
                    }

                    var unenergizedGap = EvaluateUnenergizedDaqGap(
                        IsBatchSessionActive,
                        deviceEnergized,
                        recoveryActive,
                        freshness,
                        hasObservedGapBaseline,
                        observedGapEvents,
                        _daqLivenessStaleThresholdMs);
                    if (unenergizedGap.Emit)
                    {
                        _log.Info(
                            $"FieldMetric DAQ_LIVENESS Result={unenergizedGap.Code} " +
                            $"Device={device} {unenergizedGap.Reason} " +
                            $"RunId={_activeBatchId:N} " +
                            $"RunEpoch={Interlocked.Read(ref _runEpoch)} " +
                            $"ThresholdMs={_daqLivenessStaleThresholdMs:F0} " +
                            "Energized=false RecoveryActive=false",
                            "FIELD");
                        _daqLivenessObservedGapEvents.AddOrUpdate(
                            gapKey,
                            freshness.CallbackGapEventCount,
                            (_, current) => Math.Max(
                                current,
                                freshness.CallbackGapEventCount));
                    }

                    var decision = EvaluateDaqLiveness(
                        IsBatchSessionActive,
                        IsDaqDeviceControlActive(device),
                        recoveryActive,
                        freshness,
                        observedGapEvents,
                        _daqLivenessStaleThresholdMs);
                    if (!decision.Trip)
                    {
                        if (freshness != null)
                            _daqLivenessObservedGapEvents.AddOrUpdate(
                                gapKey,
                                freshness.CallbackGapEventCount,
                                (_, current) => Math.Max(
                                    current,
                                    freshness.CallbackGapEventCount));
                        if (!recoveryActive && freshness?.IsFresh == true)
                            _daqLivenessLatchedGeneration.TryRemove(device, out _);
                        continue;
                    }

                    var generation = Math.Max(1, freshness.Generation);
                    if (_daqLivenessLatchedGeneration.TryGetValue(device, out var latched) &&
                        latched == generation)
                        continue;
                    gapKeys[device] = gapKey;
                    gapCounts[device] = freshness?.CallbackGapEventCount ?? 0;
                    incidents.Add(new DaqDeviceFault
                    {
                        Device = device,
                        Code = decision.Code,
                        Reason = decision.Reason,
                        Generation = generation,
                        TimestampUtc = DateTime.UtcNow,
                        QueueKind = "LivenessSupervisor",
                        QueueType = "HostTimer",
                        Classification = FaultClassification.SoftwareTransient
                    });
                }

                var participantDevices = new HashSet<string>(
                    incidents.Select(incident => incident.Device),
                    StringComparer.OrdinalIgnoreCase);
                if (existingIncidents.Count >= 2)
                {
                    var dev1 = existingIncidents.TryGetValue("Dev1", out var first)
                        ? first
                        : null;
                    var dev2 = existingIncidents.TryGetValue("Dev2", out var second)
                        ? second
                        : null;
                    if (dev1 != null && dev2 != null &&
                        ShouldMergeDaqRecoveryEvents(
                            dev1.FirstSeenUtc,
                            dev2.FirstSeenUtc,
                            freshnessByDevice.TryGetValue("Dev1", out var firstFreshness)
                                ? firstFreshness?.LastCallbackGapMonotonicTicks ?? 0
                                : 0,
                            freshnessByDevice.TryGetValue("Dev2", out var secondFreshness)
                                ? secondFreshness?.LastCallbackGapMonotonicTicks ?? 0
                                : 0,
                            Stopwatch.Frequency))
                    {
                        participantDevices.Add("Dev1");
                        participantDevices.Add("Dev2");
                    }
                }
                foreach (var existing in existingIncidents)
                {
                    if (participantDevices.Contains(existing.Key)) continue;
                    var existingGapTicks = freshnessByDevice.TryGetValue(
                        existing.Key,
                        out var existingFreshness)
                        ? existingFreshness?.LastCallbackGapMonotonicTicks ?? 0
                        : 0;
                    var matchesTriggeredDevice = incidents.Any(incident =>
                    {
                        var incidentGapTicks = freshnessByDevice.TryGetValue(
                            incident.Device,
                            out var incidentFreshness)
                            ? incidentFreshness?.LastCallbackGapMonotonicTicks ?? 0
                            : 0;
                        return ShouldMergeDaqRecoveryEvents(
                            existing.Value.FirstSeenUtc,
                            incident.TimestampUtc,
                            existingGapTicks,
                            incidentGapTicks,
                            Stopwatch.Frequency);
                    });
                    if (matchesTriggeredDevice)
                        participantDevices.Add(existing.Key);
                }

                if (incidents.Count == 0 && participantDevices.Count <= 1) return;
                var batchCorrelation = existingIncidents
                    .Where(pair => participantDevices.Contains(pair.Key))
                    .OrderBy(pair => pair.Value.FirstSeenUtc)
                    .Select(pair => pair.Value.CorrelationId)
                    .FirstOrDefault();
                if (batchCorrelation == Guid.Empty)
                    batchCorrelation = Guid.NewGuid();
                var correlationAliases = existingIncidents
                    .Where(pair => participantDevices.Contains(pair.Key))
                    .Select(pair => pair.Value.CorrelationId)
                    .Append(batchCorrelation)
                    .Distinct()
                    .ToArray();
                RegisterDaqRecoveryBatchBarrier(
                    batchCorrelation,
                    participantDevices,
                    _activeBatchId,
                    Interlocked.Read(ref _runEpoch),
                    correlationAliases);
                var livenessResult = incidents.Count > 0 ? "Trip" : "BatchMerged";
                var transitionToken = livenessResult == "Trip"
                    ? string.Join(
                        ",",
                        incidents
                            .OrderBy(incident => incident.Device, StringComparer.OrdinalIgnoreCase)
                            .Select(incident =>
                                $"{incident.Device.Trim().ToUpperInvariant()}:{incident.Generation}"))
                    : null;
                if (_daqLivenessLogTransitions.TryAccept(
                        _activeBatchId,
                        Interlocked.Read(ref _runEpoch),
                        batchCorrelation,
                        participantDevices,
                        livenessResult,
                        transitionToken))
                    _log.Error(
                        $"FieldMetric DAQ_LIVENESS Result={livenessResult} " +
                        $"Devices={string.Join(",", participantDevices.OrderBy(x => x))} " +
                        $"TriggeredDevices={string.Join(",", incidents.Select(x => x.Device))} " +
                        $"BatchCorrelationId={batchCorrelation:N} RunId={_activeBatchId:N} " +
                        $"RunEpoch={Interlocked.Read(ref _runEpoch)} ThresholdMs={_daqLivenessStaleThresholdMs:F0}",
                        "FIELD");

                // 同一扫描中的设备直接通过统一事故入口提交，显式传递共享批次
                // CorrelationId。安全事件与 publication 事件仍由入口幂等合并；这里
                // 不再先用随机 correlation 提交、再用 batchCorrelation 补发，避免
                // 双DAQ恢复上下文被拆成两个事故身份。
                foreach (var incident in incidents)
                {
                    PublishDaqRecoveryIncident(
                        incident,
                        batchCorrelation,
                        fromSafetyEvent: true);
                    // 只有恢复上下文创建成功后才锁存代次并消费历史空窗；否则异常
                    // 会被外层隔离，下一次扫描仍可重试同一安全事件。
                    _daqLivenessLatchedGeneration[incident.Device] = incident.Generation;
                    if (gapKeys.TryGetValue(incident.Device, out var gapKey) &&
                        gapCounts.TryGetValue(incident.Device, out var gapCount))
                        _daqLivenessObservedGapEvents.AddOrUpdate(
                            gapKey,
                            gapCount,
                            (_, current) => Math.Max(current, gapCount));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"DAQ独立存活监督异常已隔离：{ex.Message}", "AI");
            }
            finally
            {
                Volatile.Write(ref _daqLivenessWatchdogBusy, 0);
            }
        }

        internal static bool ShouldMergeDaqRecoveryEvents(
            DateTime firstSeenUtc,
            DateTime secondSeenUtc,
            long firstGapTicks,
            long secondGapTicks,
            long stopwatchFrequency,
            double mergeWindowMs = 100)
        {
            var window = Math.Max(1, mergeWindowMs);
            if (firstGapTicks > 0 && secondGapTicks > 0 && stopwatchFrequency > 0)
                return Math.Abs(firstGapTicks - secondGapTicks) * 1000.0 /
                       stopwatchFrequency <= window;
            if (firstSeenUtc == default || secondSeenUtc == default) return false;
            return Math.Abs((firstSeenUtc.ToUniversalTime() -
                             secondSeenUtc.ToUniversalTime()).TotalMilliseconds) <= window;
        }

        private static string BuildDaqLivenessGapKey(string device, long generation)
            => (device ?? string.Empty).Trim().ToUpperInvariant() + ":" +
               Math.Max(0, generation);

        private void LogDaqLivenessRunBinding(Guid runId)
        {
            if (runId == Guid.Empty) return;
            _log.Info(
                $"FieldMetric DAQ_LIVENESS Result=Configured " +
                $"IntervalMs={_daqLivenessWatchdogIntervalMs} " +
                $"ThresholdMs={_daqLivenessStaleThresholdMs:F0} " +
                $"ProcessId={Process.GetCurrentProcess().Id} " +
                $"RunId={runId:N} RunEpoch={Interlocked.Read(ref _runEpoch)}",
                "FIELD");
        }

        /// <summary>
        /// 在发出第一条带电DO之前记录该DAQ代次已经发生的历史空窗。这样未带电时的
        /// 启动/调试抖动不会误伤；DO之后出现的事件即使回调先于监督器恢复，也会被发现。
        /// </summary>
        private void BaselineDaqLivenessBeforeEnergization(int channel)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (string.IsNullOrWhiteSpace(device) || IsDaqDeviceControlActive(device)) return;
            var freshness = _acq.GetDaqFreshnessSnapshot(device, _daqLivenessStaleThresholdMs);
            if (freshness == null) return;
            _daqLivenessObservedGapEvents[BuildDaqLivenessGapKey(
                device,
                freshness.Generation)] = freshness.CallbackGapEventCount;
        }
    }
}
