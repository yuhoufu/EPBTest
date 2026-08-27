using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    internal sealed class StopPersistenceBoundaryResult
    {
        internal string Device { get; set; } = string.Empty;
        internal long Boundary { get; set; }
        internal long FinalBoundary { get; set; }
        internal bool BoundaryStable { get; set; }
        internal long Published { get; set; }
        internal long Persisted { get; set; }
        internal int QueueDepth { get; set; }
        internal DaqPersistenceState PersistenceState { get; set; }
        internal bool RequireRecoveredState { get; set; }
        internal bool DurabilityBlocked { get; set; }
        internal long DiscardedGenerationBatchCount { get; set; }
        internal long OverCapacityDroppedBatchCount { get; set; }
        internal long SuppressAfterSequence { get; set; }
        internal long SuppressThroughSequence { get; set; }
        internal long LastTerminallyHandledSequence { get; set; }
        internal long PendingHeadSequence { get; set; }
        internal long InFlightSequence { get; set; }
        internal bool RawPipelineDrained { get; set; }
        internal bool PersistenceQueueDrained { get; set; }
        internal bool Closed { get; set; }
    }

    public sealed partial class EpbManager
    {
        private const int FieldMetricIntervalMs = 2000;
        private long _lastFieldMetricTicks;

        private static string Metric(double value)
            => value.ToString("F3", CultureInfo.InvariantCulture);

        private static string MetricToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "none";
            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Trim())
                builder.Append(char.IsWhiteSpace(character) || character == '=' ? '_' : character);
            return builder.ToString();
        }

        private void LogFieldSessionMetric(
            string phase,
            Guid runId,
            IEnumerable<int> channels,
            bool closed,
            string detail = null)
        {
            if (runId == Guid.Empty) return;
            var identity = RuntimeBuildIdentity.Capture();
            _log.Info(
                $"FieldMetric SESSION Phase={MetricToken(phase)} RunId={runId:N} " +
                $"Channels={string.Join(",", (channels ?? Enumerable.Empty<int>()).Distinct().OrderBy(x => x))} " +
                $"Closed={closed} Detail={MetricToken(detail)} " +
                $"ProductVersion={MetricToken(identity.ProductVersion)} " +
                $"AssemblyVersion={MetricToken(identity.AssemblyVersion)} " +
                $"ProcessId={identity.ProcessId} " +
                $"ExecutablePath={MetricToken(identity.ExecutablePath)} " +
                $"ExeSha256={MetricToken(identity.ExecutableSha256)} " +
                $"ConfigSha256={MetricToken(identity.ReleaseConfigSha256)} " +
                $"GitCommit={MetricToken(identity.GitCommit)} GitDirty={MetricToken(identity.GitDirty)} " +
                $"BuildUtc={MetricToken(identity.BuildUtc)} " +
                $"AsyncLogDropped={Config.ProjectLogHub.DroppedAsyncRecords} " +
                $"PackageVerified={identity.ReleasePackageVerified} " +
                $"PackageCode={MetricToken(identity.ReleasePackageCode)} " +
                $"PackageFiles={identity.ReleasePackageFileCount}",
                "FIELD");
        }

        private void LogFieldRuntimeStateMetric(ChannelRuntimeStateChangedEvent update)
        {
            if (update == null) return;
            var device = update.Channel <= 6 ? "Dev1" : "Dev2";
            _log.Info(
                $"FieldMetric STATE RunId={update.RunId:N} Device={device} " +
                $"Channel={update.Channel} State={update.State} " +
                $"Reason={MetricToken(update.ReasonCode)} Revision={update.Revision} " +
                $"CorrelationId={update.CorrelationId:N} RunEpoch={update.RunEpoch} " +
                $"Enabled={update.Enabled} Formal={update.FormalPhaseCommitted} " +
                $"Timer={update.TimerActive} Runner={update.RunnerActive} Energized={update.Energized}",
                "FIELD");
        }

        private void TryLogFieldRuntimeMetrics(bool force = false, string phase = "Running")
        {
            var nowTicks = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastFieldMetricTicks);
            if (!force && previous != 0 &&
                (nowTicks - previous) * 1000.0 / Stopwatch.Frequency < FieldMetricIntervalMs)
                return;
            if (!force &&
                Interlocked.CompareExchange(ref _lastFieldMetricTicks, nowTicks, previous) != previous)
                return;
            if (force) Interlocked.Exchange(ref _lastFieldMetricTicks, nowTicks);

            foreach (var device in new[] { "Dev1", "Dev2" })
            {
                try
                {
                    var control = _acq.GetControlSnapshot(device);
                    var pipeline = _acq.GetPipelineSnapshot(device);
                    var freshness = _acq.GetDaqFreshnessSnapshot(
                        device,
                        _daqLivenessWarnThresholdMs);
                    var persistence = _persistence.GetSnapshot(device);
                    var published = _acq.GetLastDiskPublishedSequence(device);
                    _log.Info(
                        $"FieldMetric DAQ Phase={phase} Device={device} " +
                        $"ControlDepth={control.QueueDepth} " +
                        $"ControlOldestMs={Metric(control.OldestBatchAgeMs)} " +
                        $"ControlProcessMs={Metric(control.LastBatchProcessMs)} " +
                        $"SubscriberMaxMs={Metric(control.SubscriberMaxMs)} " +
                        $"ProcessingDepth={pipeline.ProcessingQueueDepth} " +
                        $"ProcessingCapacity={pipeline.ProcessingQueueCapacity} " +
                        $"ProcessingOldestMs={Metric(pipeline.ProcessingOldestBatchAgeMs)} " +
                        $"ProcessingInFlight={pipeline.ProcessingInFlightSequence} " +
                        $"RawDepth={pipeline.RawQueueDepth} " +
                        $"RawCapacity={pipeline.RawQueueCapacity} " +
                        $"RawInFlight={pipeline.RawInFlightCount} " +
                        $"PersistenceState={persistence.State} " +
                        $"PersistenceDepth={persistence.QueueDepth} " +
                        $"PersistenceOldestMs={Metric(persistence.OldestBatchAgeMs)} " +
                        $"PersistenceHead={persistence.PendingHeadSequence} " +
                        $"PersistenceInFlight={persistence.InFlightSequence} " +
                        $"CallbackAgeMs={Metric(freshness.CallbackAgeMs)} " +
                        $"CallbackGapEvents={freshness.CallbackGapEventCount} " +
                        $"LastCallbackGapMs={Metric(freshness.LastCallbackGapIntervalMs)} " +
                        $"ControlProcessedAgeMs={Metric(freshness.ControlProcessedAgeMs)} " +
                        $"Produced={freshness.LastProducedSequence} " +
                        $"Processed={freshness.LastProcessedSequence} " +
                        $"Allocated={pipeline.LastAllocatedSequence} " +
                        $"Accepted={pipeline.LastAcceptedSequence} " +
                        $"Observed={pipeline.LastObservedSequence} " +
                        $"Published={published} RawTransferred={pipeline.LastRawTransferredSequence} " +
                        $"Persisted={persistence.Sequence} " +
                        $"TerminallyHandled={persistence.LastTerminallyHandledSequence} " +
                        $"SuppressBoundary={persistence.SuppressAfterSequence} " +
                        $"SuppressThrough={persistence.SuppressThroughSequence} " +
                        $"FirstPermanentGap={pipeline.FirstPermanentGapSequence} " +
                        $"PendingProcessingGap={pipeline.PendingProcessingGapSequence} " +
                        $"PendingRawGap={pipeline.PendingRawGapSequence} " +
                        $"Discontinuities={freshness.ControlDiscontinuityCount} " +
                        $"DurabilityBlocked={persistence.DurabilityBlocked} " +
                        $"Suppressed={persistence.SuppressedBatchCount} " +
                        $"SuppressedCumulative={persistence.CumulativeSuppressedBatchCount} " +
                         $"SuppressedFirst={persistence.FirstSuppressedSequence} " +
                         $"SuppressedLast={persistence.LastSuppressedSequence} " +
                         $"SuppressedRanges={persistence.SuppressedRangeCount} " +
                         $"SuppressedCumulativeFirst={persistence.CumulativeFirstSuppressedSequence} " +
                         $"SuppressedCumulativeLast={persistence.CumulativeLastSuppressedSequence} " +
                         $"SuppressedCumulativeRanges={persistence.CumulativeSuppressedRangeCount} " +
                         $"Discarded={persistence.DiscardedGenerationBatchCount} " +
                        $"OverCapacityDropped={persistence.OverCapacityDroppedBatchCount} " +
                        $"AsyncLogDropped={Config.ProjectLogHub.DroppedAsyncRecords}",
                        "FIELD");
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"FieldMetric DAQ采集失败已隔离：Device={device} {ex.Message}",
                        "FIELD");
                }
            }
        }

        private void LogHighPriorityOffFieldMetric(IO.NI.HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null) return;
            var device = telemetry.Channel <= 6 ? "Dev1" : "Dev2";
            _log.Info(
                $"FieldMetric DO_OFF Device={device} Channel={telemetry.Channel} " +
                $"CommandId={telemetry.CommandId:N} Result={telemetry.Result} " +
                $"Late={telemetry.LateHardwareSuccess} " +
                $"QueueWaitMs={Metric(telemetry.QueueWaitMs)} " +
                $"NIWriteMs={Metric(telemetry.NiWriteMs)} " +
                $"WorkerMs={Metric(telemetry.WorkerExecutionMs)} " +
                $"TotalMs={Metric(telemetry.TotalMs)}",
                "FIELD");
        }

        private void LogDaqRecoveryFieldMetric(
            DaqAutoRecoveryContext context,
            string result,
            string detail)
        {
            if (context == null) return;
            try
            {
                var persistence = _persistence.GetSnapshot(context.Device);
                var pipeline = _acq.GetPipelineSnapshot(context.Device);
                _log.Info(
                    $"FieldMetric DAQ_RECOVERY Result={MetricToken(result)} " +
                    $"Device={context.Device} Phase={MetricToken(context.ValidationPhase)} " +
                    $"RunId={context.RunId:N} RunEpoch={context.RunEpoch} " +
                    $"RecoveryEpoch={context.RecoveryEpoch} CorrelationId={context.CorrelationId:N} " +
                    $"Trigger={MetricToken(context.TriggerCode)} Attempt={context.FailureBackoff.Current} " +
                    $"RecoveryAttempt={context.RecoveryAttempt} " +
                    $"ElapsedMs={Math.Max(0, (DateTime.UtcNow - context.StartedUtc).TotalMilliseconds):F0} " +
                    $"HardDeadlineMs={RecoveryGroupHardDeadlineMs} " +
                    $"Cutoff={Interlocked.Read(ref context.CutoffPersistenceBoundary)} " +
                    $"Accepted={pipeline.LastAcceptedSequence} Published={pipeline.LastDiskPublishedSequence} " +
                    $"RawTransferred={pipeline.LastRawTransferredSequence} Persisted={persistence.Sequence} " +
                    $"TerminallyHandled={persistence.LastTerminallyHandledSequence} " +
                    $"Head={persistence.PendingHeadSequence} InFlight={persistence.InFlightSequence} " +
                    $"SuppressAfter={persistence.SuppressAfterSequence} " +
                    $"SuppressThrough={persistence.SuppressThroughSequence} " +
                    $"Detail={MetricToken(detail)}",
                    "FIELD");
            }
            catch (Exception ex)
            {
                _log.Warn($"FieldMetric DAQ_RECOVERY采集失败已隔离：{ex.Message}", "FIELD");
            }
        }

        internal static bool IsStopPersistenceBoundaryClosed(
            long boundary,
            long published,
            long persisted,
            int queueDepth,
            DaqPersistenceState state = DaqPersistenceState.Recovered,
            bool requireRecoveredState = true,
            bool durabilityBlocked = false,
            long discardedGenerationBatchCount = 0,
            long overCapacityDroppedBatchCount = 0)
            => boundary >= 0 && published >= boundary && persisted >= boundary && queueDepth == 0 &&
               !durabilityBlocked && discardedGenerationBatchCount == 0 &&
               overCapacityDroppedBatchCount == 0 &&
               (!requireRecoveredState || state == DaqPersistenceState.Recovered);

        internal static bool IsFrozenStopPersistenceBoundaryClosed(
            long boundary,
            long finalBoundary,
            bool rawPipelineDrained,
            long published,
            long persisted,
            int queueDepth,
            DaqPersistenceState state = DaqPersistenceState.Recovered,
            bool requireRecoveredState = true,
            bool durabilityBlocked = false,
            long discardedGenerationBatchCount = 0,
            long overCapacityDroppedBatchCount = 0,
            long suppressAfterSequence = 0,
            long suppressThroughSequence = 0,
            long lastTerminallyHandledSequence = 0,
            long pendingHeadSequence = 0,
            long inFlightSequence = 0)
        {
            if (!rawPipelineDrained || finalBoundary < boundary ||
                !IsStopPersistenceBoundaryClosed(
                   boundary,
                   published,
                   persisted,
                   queueDepth,
                   state,
                   requireRecoveredState,
                   durabilityBlocked,
                   discardedGenerationBatchCount,
                   overCapacityDroppedBatchCount))
                return false;
            var noUnresolvedPrefixOwner =
                (pendingHeadSequence <= 0 || pendingHeadSequence > finalBoundary) &&
                (inFlightSequence <= 0 || inFlightSequence > finalBoundary);
            if (!noUnresolvedPrefixOwner) return false;
            if (finalBoundary == boundary) return true;

            // StopAll 接管 DAQ 恢复时，boundary 是事故首次冻结的唯一正式耐久前缀；
            // 为重建新鲜压力而产生的后续批次属于明确排除的有限尾段。只有抑制窗口从
            // 同一边界开始、覆盖整个尾段，且发布与终态水位都越过尾端时才能闭合。
            // 不再要求 FinalBoundary == boundary，这个等式在只读 DAQ 恢复后不可达。
            var suppressionCoversTail = suppressAfterSequence == boundary &&
                                        (suppressThroughSequence == long.MaxValue ||
                                         suppressThroughSequence >= finalBoundary);
            return suppressionCoversTail &&
                   published >= finalBoundary &&
                   lastTerminallyHandledSequence >= finalBoundary &&
                   noUnresolvedPrefixOwner;
        }

        internal static bool ShouldStopAcquisitionBeforeFinalPersistence(StopSource source)
            // StopAll 一律先冻结DAQ生产者；同进程重新开始会在预检中建立
            // 新 generation。只有这样 Raw/SQLite 才能对同一最终边界做闭合证明。
            => true;

        internal static bool IsFinalExitStopSource(StopSource source)
            => source == StopSource.ApplicationClosing || source == StopSource.ProgramExit;

        internal static bool CanReuseStopResultForSource(
            StopSource completedSource,
            StopSource requestedSource)
            => !IsFinalExitStopSource(requestedSource) ||
               IsFinalExitStopSource(completedSource);

        internal static bool RequiresRecoveredPersistenceStateForStop(StopSource source)
            => !IsFinalExitStopSource(source);

        private async Task<StopPersistenceBoundaryResult[]> WaitForStopPersistenceBoundariesAsync(
            IReadOnlyDictionary<string, long> boundaries,
            bool requireRecoveredState)
        {
            // The transaction runner owns the only stop-stage deadline.  This
            // persistence helper intentionally has no competing 5/15-second
            // timeout; a slow device remains an orphan supervised by the
            // outer runner and cannot race its terminal publication.
            const int unboundedTimeoutMs = int.MaxValue;
            // DAQ 已在调用方停止。正式义务仍是 boundaries 中的冻结前缀，但还必须把
            // 截止后已接纳的抑制尾段推进到明确终态，避免仅验证前缀后遗留后台所有权。
            var finalBoundaries = boundaries.ToDictionary(
                pair => pair.Key,
                pair => Math.Max(pair.Value, _acq.GetLastProcessRecycleBoundary(pair.Key)),
                StringComparer.OrdinalIgnoreCase);
            var rawDrainMs = unboundedTimeoutMs;
            var rawPipelineDrained = await _acq.DrainBackgroundPipelinesToBoundariesAsync(
                    finalBoundaries.TryGetValue("Dev1", out var dev1Boundary) ? dev1Boundary : 0,
                    finalBoundaries.TryGetValue("Dev2", out var dev2Boundary) ? dev2Boundary : 0,
                    rawDrainMs,
                    CancellationToken.None)
                .ConfigureAwait(false);
            // boundaries 是DAQ停止后冻结的唯一身份。禁止在Raw只排到A后又将
            // SQLite边界扩大到B，否则A+1..B的Raw尚未移交也会被伪装成完整收口。
            foreach (var pair in boundaries)
            {
                var remainingMs = unboundedTimeoutMs;
                await _persistence.WaitForPersistedAsync(
                        pair.Key,
                        pair.Value,
                        remainingMs,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var remainingDrainMs = unboundedTimeoutMs;
            var persistenceQueueDrained = await _persistence.DrainAsync(remainingDrainMs)
                .ConfigureAwait(false);

            var results = boundaries.Select(pair =>
            {
                var persistence = _persistence.GetSnapshot(pair.Key);
                var published = _acq.GetLastDiskPublishedSequence(pair.Key);
                var finalBoundary = finalBoundaries[pair.Key];
                // Stop 后生产者不得再次前进。若旧 Run 的异步重建在冻结后偷偷启动了
                // DAQ，ObservedFinal 会大于本次排空目标，必须拒绝交接而不是遗漏新尾段。
                var observedFinalBoundary = _acq.GetLastProcessRecycleBoundary(pair.Key);
                var producerStayedStopped = observedFinalBoundary == finalBoundary;
                var boundaryStable = producerStayedStopped &&
                                     (finalBoundary == pair.Value ||
                                      persistence.SuppressAfterSequence == pair.Value);
                return new StopPersistenceBoundaryResult
                {
                    Device = pair.Key,
                    Boundary = pair.Value,
                    FinalBoundary = finalBoundary,
                    BoundaryStable = boundaryStable,
                    Published = published,
                    Persisted = persistence.Sequence,
                    QueueDepth = persistence.QueueDepth,
                    PersistenceState = persistence.State,
                    RequireRecoveredState = requireRecoveredState,
                    DurabilityBlocked = persistence.DurabilityBlocked,
                    DiscardedGenerationBatchCount = persistence.DiscardedGenerationBatchCount,
                    OverCapacityDroppedBatchCount = persistence.OverCapacityDroppedBatchCount,
                    SuppressAfterSequence = persistence.SuppressAfterSequence,
                    SuppressThroughSequence = persistence.SuppressThroughSequence,
                    LastTerminallyHandledSequence = persistence.LastTerminallyHandledSequence,
                    PendingHeadSequence = persistence.PendingHeadSequence,
                    InFlightSequence = persistence.InFlightSequence,
                    RawPipelineDrained = rawPipelineDrained,
                    PersistenceQueueDrained = persistenceQueueDrained,
                    Closed = producerStayedStopped && persistenceQueueDrained &&
                             IsFrozenStopPersistenceBoundaryClosed(
                        pair.Value,
                        finalBoundary,
                        rawPipelineDrained,
                        published,
                        persistence.Sequence,
                        persistence.QueueDepth,
                        persistence.State,
                        requireRecoveredState,
                        persistence.DurabilityBlocked,
                        persistence.DiscardedGenerationBatchCount,
                        persistence.OverCapacityDroppedBatchCount,
                        persistence.SuppressAfterSequence,
                        persistence.SuppressThroughSequence,
                        persistence.LastTerminallyHandledSequence,
                        persistence.PendingHeadSequence,
                        persistence.InFlightSequence)
                };
            }).ToArray();

            foreach (var result in results)
            {
                var message =
                    $"FieldMetric STOP_PERSISTENCE Device={result.Device} " +
                    $"RawDrained={result.RawPipelineDrained} " +
                    $"PersistenceDrained={result.PersistenceQueueDrained} " +
                    $"Boundary={result.Boundary} FinalBoundary={result.FinalBoundary} " +
                    $"BoundaryStable={result.BoundaryStable} Published={result.Published} " +
                    $"Persisted={result.Persisted} Depth={result.QueueDepth} " +
                    $"SuppressAfter={result.SuppressAfterSequence} " +
                    $"SuppressThrough={result.SuppressThroughSequence} " +
                    $"TerminallyHandled={result.LastTerminallyHandledSequence} " +
                    $"Head={result.PendingHeadSequence} InFlight={result.InFlightSequence} " +
                    $"State={result.PersistenceState} RequireRecovered={result.RequireRecoveredState} " +
                    $"DurabilityBlocked={result.DurabilityBlocked} " +
                    $"Discarded={result.DiscardedGenerationBatchCount} " +
                    $"OverCapacityDropped={result.OverCapacityDroppedBatchCount} " +
                    $"Closed={result.Closed}";
                if (result.Closed) _log.Info(message, "FIELD");
                else _log.Error(message, "FIELD");
            }
            return results;
        }
    }
}
