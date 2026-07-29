using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Controller
{
    internal enum EpbDoCommand
    {
        Forward,
        Reverse,
        Off,
        OffHighPriority
    }

    internal sealed class DoControlTraceEvent
    {
        public DateTime Utc { get; set; }
        public long MonotonicTicks { get; set; }
        public double MonotonicElapsedMs { get; set; }
        public Guid RunId { get; set; }
        public int CycleNumber { get; set; }
        public int ElectricalGroupId { get; set; }
        public int Channel { get; set; }
        public int PlannedPhaseMs { get; set; }
        public DateTime? ElectricalPhaseDueUtc { get; set; }
        public double? ElectricalPhaseStartDeviationMs { get; set; }
        public string Stage { get; set; }
        public EpbDoCommand Command { get; set; }
        public bool DoCommandResult { get; set; }
        public double BranchCurrentA { get; set; }
    }

    /// <summary>
    /// 保存最近60秒、最多10000条DO命令。这里记录的是软件命令与返回值，
    /// 不表示继电器触点或负载端已经完成物理通断。
    /// </summary>
    internal sealed class DoControlTraceBuffer
    {
        private const int MaxEvents = 10000;
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
        private readonly object _gate = new();
        private readonly Queue<DoControlTraceEvent> _events = new();

        public void Add(DoControlTraceEvent item)
        {
            if (item == null) return;

            lock (_gate)
            {
                _events.Enqueue(item);
                Trim(item.Utc);
            }
        }

        public IReadOnlyList<DoControlTraceEvent> Snapshot(DateTime nowUtc, Guid runId)
        {
            lock (_gate)
            {
                Trim(nowUtc);
                return _events
                    .Where(x => runId == Guid.Empty || x.RunId == runId)
                    .ToArray();
            }
        }

        private void Trim(DateTime nowUtc)
        {
            var cutoff = nowUtc - MaxAge;
            while (_events.Count > 0 &&
                   (_events.Count > MaxEvents || _events.Peek().Utc < cutoff))
                _events.Dequeue();
        }
    }

    public sealed partial class EpbManager
    {
        private const int MaxRetainedStaggerPlans = 64;
        private readonly DoControlTraceBuffer _doControlTrace = new();
        private readonly ConcurrentDictionary<int, Guid> _runIdByChannel = new();
        private readonly ConcurrentDictionary<int, ChannelStaggerAssignment> _staggerAssignmentByChannel = new();
        private readonly ConcurrentDictionary<int, DateTime> _electricalPhaseDueByChannel = new();
        private readonly ConcurrentDictionary<Guid, ElectricalStaggerPlan> _staggerPlansByRun = new();
        private readonly ConcurrentQueue<Guid> _staggerPlanOrder = new();

        private void RegisterRunContext(Guid runId, ElectricalStaggerPlan plan)
        {
            if (runId == Guid.Empty) throw new ArgumentException("运行ID不能为空。", nameof(runId));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            if (_staggerPlansByRun.TryAdd(runId, plan))
                _staggerPlanOrder.Enqueue(runId);

            foreach (var assignment in plan.Assignments.Values)
            {
                _runIdByChannel[assignment.Channel] = runId;
                _staggerAssignmentByChannel[assignment.Channel] = assignment;
            }

            while (_staggerPlansByRun.Count > MaxRetainedStaggerPlans &&
                   _staggerPlanOrder.TryDequeue(out var expiredRunId))
                _staggerPlansByRun.TryRemove(expiredRunId, out _);
        }

        private void MarkElectricalPhaseDue(int channel, DateTime dueUtc)
        {
            _electricalPhaseDueByChannel[channel] = dueUtc.Kind == DateTimeKind.Utc
                ? dueUtc
                : dueUtc.ToUniversalTime();
        }

        internal bool CommandEpbForward(int channel, string stage)
        {
            return ExecuteDoCommand(channel, stage, EpbDoCommand.Forward, () => _do.SetEpbForward(channel));
        }

        internal bool CommandEpbReverse(int channel, string stage)
        {
            return ExecuteDoCommand(channel, stage, EpbDoCommand.Reverse, () => _do.SetEpbReverse(channel));
        }

        internal bool CommandEpbOff(int channel, string stage)
        {
            return ExecuteDoCommand(channel, stage, EpbDoCommand.Off, () => _do.SetEpbOff(channel));
        }

        internal bool CommandEpbOffHighPriority(int channel, string stage)
        {
            return ExecuteDoCommand(
                channel,
                stage,
                EpbDoCommand.OffHighPriority,
                () => _do.SetEpbOffHighPriority(channel));
        }

        private bool ExecuteDoCommand(
            int channel,
            string stage,
            EpbDoCommand command,
            Func<bool> execute)
        {
            var utc = DateTime.UtcNow;
            var ticks = Stopwatch.GetTimestamp();
            var current = double.NaN;
            try { current = _readCurrent(channel); }
            catch { /* 电流读取失败不应阻止安全DO命令 */ }

            var result = false;
            try
            {
                result = execute();
                return result;
            }
            finally
            {
                try
                {
                    _runIdByChannel.TryGetValue(channel, out var runId);
                    _staggerAssignmentByChannel.TryGetValue(channel, out var assignment);
                    _currentCycleNumberByChannel.TryGetValue(channel, out var cycleNumber);
                    _electricalPhaseDueByChannel.TryGetValue(channel, out var phaseDueUtc);
                    var hasPhaseDue = phaseDueUtc != default;

                    var traceEvent = new DoControlTraceEvent
                    {
                        Utc = utc,
                        MonotonicTicks = ticks,
                        MonotonicElapsedMs =
                            (ticks - _wallBaseTicks) * 1000.0 / Stopwatch.Frequency,
                        RunId = runId,
                        CycleNumber = cycleNumber,
                        ElectricalGroupId = assignment?.ElectricalGroupId ?? 0,
                        Channel = channel,
                        PlannedPhaseMs = assignment?.PhaseMs ?? 0,
                        ElectricalPhaseDueUtc = hasPhaseDue ? phaseDueUtc : (DateTime?)null,
                        ElectricalPhaseStartDeviationMs =
                            hasPhaseDue ? (utc - phaseDueUtc).TotalMilliseconds : (double?)null,
                        Stage = string.IsNullOrWhiteSpace(stage) ? "Unknown" : stage,
                        Command = command,
                        DoCommandResult = result,
                        BranchCurrentA = current
                    };
                    _doControlTrace.Add(traceEvent);

                    var currentText = double.IsNaN(current)
                        ? "NaN"
                        : current.ToString("F6", CultureInfo.InvariantCulture);
                    var message =
                        $"DO命令 UTC={utc:O} MonoTicks={ticks} Run={runId:N} Cycle={cycleNumber} " +
                        $"Group={traceEvent.ElectricalGroupId} EPB={channel} Phase={traceEvent.PlannedPhaseMs}ms " +
                        $"PhaseDueUtc={(hasPhaseDue ? phaseDueUtc.ToString("O") : "Unknown")} " +
                        $"PhaseDeviationMs={(traceEvent.ElectricalPhaseStartDeviationMs?.ToString("F3", CultureInfo.InvariantCulture) ?? "Unknown")} " +
                        $"Stage={traceEvent.Stage} Command={command} DoCommandResult={result} " +
                        $"CurrentA={currentText} PhysicalOffStatus=NotMeasured";
                    if (result)
                        _log.Info(message, "EPB-DO");
                    else
                        _log.Warn(message, "EPB-DO");
                }
                catch
                {
                    // 追踪与日志是辅助证据，不得改变原始DO方法的返回/异常语义。
                }
            }
        }

        private void ExportControlEvidence(
            string snapshotDir,
            int alarmChannel,
            int alarmCycleNumber,
            string reason,
            bool csvAndBinComplete,
            DateTime alarmUtc)
        {
            _runIdByChannel.TryGetValue(alarmChannel, out var runId);
            _staggerAssignmentByChannel.TryGetValue(alarmChannel, out var alarmAssignment);
            _staggerPlansByRun.TryGetValue(runId, out var plan);
            var events = _doControlTrace.Snapshot(alarmUtc, runId);

            TryWriteControlEvidence(
                "alarm-metadata.json",
                () => WriteAlarmMetadata(
                    Path.Combine(snapshotDir, "alarm-metadata.json"),
                    alarmChannel,
                    alarmCycleNumber,
                    reason,
                    csvAndBinComplete,
                    alarmUtc,
                    runId,
                    alarmAssignment,
                    plan,
                    events));
            TryWriteControlEvidence(
                "electrical-stagger-plan.json",
                () => WriteStaggerPlan(
                    Path.Combine(snapshotDir, "electrical-stagger-plan.json"),
                    runId,
                    plan));
            TryWriteControlEvidence(
                "do-control-timeline.csv",
                () => WriteDoTimeline(
                    Path.Combine(snapshotDir, "do-control-timeline.csv"),
                    events));
        }

        private void TryWriteControlEvidence(string fileName, Action write)
        {
            try
            {
                write();
            }
            catch (Exception ex)
            {
                _log.Warn($"报警辅助证据 {fileName} 写入失败：{ex.Message}", "落盘");
            }
        }

        internal static void WriteAlarmMetadata(
            string path,
            int alarmChannel,
            int alarmCycleNumber,
            string reason,
            bool csvAndBinComplete,
            DateTime alarmUtc,
            Guid runId,
            ChannelStaggerAssignment assignment,
            ElectricalStaggerPlan plan,
            IReadOnlyList<DoControlTraceEvent> events)
        {
            var groupAssignments = plan?.Assignments.Values
                .Where(x => assignment != null && x.ElectricalGroupId == assignment.ElectricalGroupId)
                .OrderBy(x => x.SelectedIndexInGroup)
                .ToArray() ?? Array.Empty<ChannelStaggerAssignment>();
            var channelEvents = (events ?? Array.Empty<DoControlTraceEvent>())
                .Where(x => x.Channel == alarmChannel)
                .OrderBy(x => x.Utc)
                .ThenBy(x => x.MonotonicTicks)
                .ToArray();
            var lastForward = channelEvents.LastOrDefault(x => x.Command == EpbDoCommand.Forward);
            var lastReverse = channelEvents.LastOrDefault(x => x.Command == EpbDoCommand.Reverse);
            var lastOff = channelEvents.LastOrDefault(
                x => x.Command == EpbDoCommand.Off || x.Command == EpbDoCommand.OffHighPriority);

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"schemaVersion\": 1,");
            json.AppendLine($"  \"alarmUtc\": \"{alarmUtc:O}\",");
            json.AppendLine($"  \"runId\": \"{runId:N}\",");
            json.AppendLine($"  \"alarmChannel\": {alarmChannel},");
            json.AppendLine($"  \"alarmCycleNumber\": {alarmCycleNumber},");
            json.AppendLine($"  \"electricalGroupId\": {assignment?.ElectricalGroupId ?? 0},");
            json.AppendLine($"  \"selectedIndexInGroup\": {assignment?.SelectedIndexInGroup ?? 0},");
            json.AppendLine($"  \"staggerMs\": {assignment?.StaggerMs ?? 0},");
            json.AppendLine($"  \"plannedPhaseMs\": {assignment?.PhaseMs ?? 0},");
            json.AppendLine($"  \"reason\": \"{EscapeJson(reason)}\",");
            json.AppendLine($"  \"alarmCycleCsvAndBinComplete\": {csvAndBinComplete.ToString().ToLowerInvariant()},");
            json.AppendLine(
                $"  \"selectedChannelsInElectricalGroup\": [{string.Join(", ", groupAssignments.Select(x => x.Channel))}],");
            json.AppendLine("  \"electricalGroupPlan\": [");
            for (var i = 0; i < groupAssignments.Length; i++)
            {
                var x = groupAssignments[i];
                json.Append(
                    $"    {{\"channel\": {x.Channel}, \"selectedIndexInGroup\": {x.SelectedIndexInGroup}, \"phaseMs\": {x.PhaseMs}}}");
                json.AppendLine(i == groupAssignments.Length - 1 ? string.Empty : ",");
            }
            json.AppendLine("  ],");
            json.AppendLine("  \"lastCommands\": {");
            AppendCommandJson(json, "forward", lastForward, true);
            AppendCommandJson(json, "reverse", lastReverse, true);
            AppendCommandJson(json, "off", lastOff, false);
            json.AppendLine("  },");
            json.AppendLine("  \"doCommandResultMeaning\": \"软件DO方法返回值；不代表继电器触点或负载端物理通断确认\",");
            json.AppendLine("  \"physicalOffStatus\": \"NotMeasured\",");
            json.AppendLine("  \"physicalPowerState\": \"NotMeasured\"");
            json.AppendLine("}");
            File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
        }

        private static void AppendCommandJson(
            StringBuilder json,
            string propertyName,
            DoControlTraceEvent item,
            bool trailingComma)
        {
            json.Append($"    \"{propertyName}\": ");
            if (item == null)
            {
                json.Append("null");
            }
            else
            {
                var current = double.IsNaN(item.BranchCurrentA)
                    ? "null"
                    : item.BranchCurrentA.ToString("F6", CultureInfo.InvariantCulture);
                json.Append("{");
                json.Append($"\"utc\": \"{item.Utc:O}\", ");
                json.Append($"\"monotonicTicks\": {item.MonotonicTicks}, ");
                json.Append($"\"stage\": \"{EscapeJson(item.Stage)}\", ");
                json.Append($"\"command\": \"{item.Command}\", ");
                json.Append($"\"doCommandResult\": {item.DoCommandResult.ToString().ToLowerInvariant()}, ");
                json.Append($"\"branchCurrentA\": {current}");
                json.Append("}");
            }

            if (trailingComma) json.Append(',');
            json.AppendLine();
        }

        internal static void WriteStaggerPlan(string path, Guid runId, ElectricalStaggerPlan plan)
        {
            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"schemaVersion\": 1,");
            json.AppendLine($"  \"runId\": \"{runId:N}\",");
            json.AppendLine($"  \"planAvailable\": {(plan != null).ToString().ToLowerInvariant()},");
            json.AppendLine($"  \"createdUtc\": \"{(plan == null ? string.Empty : plan.CreatedUtc.ToString("O"))}\",");
            json.AppendLine($"  \"periodMs\": {plan?.PeriodMs ?? 0},");
            json.AppendLine("  \"assignments\": [");

            var assignments = plan?.Assignments.Values
                .OrderBy(x => x.ElectricalGroupId)
                .ThenBy(x => x.SelectedIndexInGroup)
                .ToArray() ?? Array.Empty<ChannelStaggerAssignment>();
            for (var i = 0; i < assignments.Length; i++)
            {
                var x = assignments[i];
                json.Append("    {");
                json.Append($"\"channel\": {x.Channel}, ");
                json.Append($"\"electricalGroupId\": {x.ElectricalGroupId}, ");
                json.Append($"\"selectedIndexInGroup\": {x.SelectedIndexInGroup}, ");
                json.Append($"\"staggerMs\": {x.StaggerMs}, ");
                json.Append($"\"phaseMs\": {x.PhaseMs}");
                json.Append(i == assignments.Length - 1 ? "}" : "},");
                json.AppendLine();
            }

            json.AppendLine("  ]");
            json.AppendLine("}");
            File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
        }

        internal static void WriteDoTimeline(string path, IEnumerable<DoControlTraceEvent> events)
        {
            var csv = new StringBuilder();
            csv.AppendLine(
                "Utc,MonotonicTicks,MonotonicElapsedMs,RunId,Cycle,ElectricalGroup,Channel,PlannedPhaseMs,ElectricalPhaseDueUtc,ElectricalPhaseStartDeviationMs,Stage,Command,DoCommandResult,BranchCurrentA,PhysicalPowerState");
            foreach (var item in (events ?? Array.Empty<DoControlTraceEvent>())
                         .OrderBy(x => x.Utc)
                         .ThenBy(x => x.MonotonicTicks))
            {
                csv.Append(item.Utc.ToString("O", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(item.MonotonicTicks).Append(',');
                csv.Append(item.MonotonicElapsedMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',');
                csv.Append(item.RunId.ToString("N")).Append(',');
                csv.Append(item.CycleNumber).Append(',');
                csv.Append(item.ElectricalGroupId).Append(',');
                csv.Append(item.Channel).Append(',');
                csv.Append(item.PlannedPhaseMs).Append(',');
                csv.Append(item.ElectricalPhaseDueUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)
                    .Append(',');
                csv.Append(item.ElectricalPhaseStartDeviationMs?.ToString("F3", CultureInfo.InvariantCulture) ??
                           string.Empty).Append(',');
                csv.Append(EscapeCsv(item.Stage)).Append(',');
                csv.Append(item.Command).Append(',');
                csv.Append(item.DoCommandResult ? "true" : "false").Append(',');
                csv.Append(double.IsNaN(item.BranchCurrentA)
                    ? string.Empty
                    : item.BranchCurrentA.ToString("F6", CultureInfo.InvariantCulture));
                csv.Append(",NotMeasured");
                csv.AppendLine();
            }

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
