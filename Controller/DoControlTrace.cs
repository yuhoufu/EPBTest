using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Config;
using Controller.Adaptive;
using DataOperation;
using IO.NI;

namespace Controller
{
    internal sealed class PowerSupplyEnergizationPermitException : InvalidOperationException
    {
        public PowerSupplyEnergizationPermitException(int channel, int groupId, string reason)
            : base($"PowerSupplyEnergizationPermitMissing Channel={channel} Group={groupId} Reason={reason}") { }
    }

    internal sealed class ChannelExecutionPermitException : InvalidOperationException
    {
        public ChannelExecutionPermitException(int channel, ChannelRuntimeState state, string reason)
            : base($"ChannelExecutionPermitMissing Channel={channel} State={state} Reason={reason}") { }
    }

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
        public double CommandElapsedMs { get; set; }
        public double BranchCurrentA { get; set; }
    }

    internal sealed class TerminalOffSafetyEvidence
    {
        public DateTime CommandUtc { get; set; }
        public DateTime? DecisionUtc { get; set; }
        public DateTime? DoWriteStartedUtc { get; set; }
        public DateTime? DoWriteCompletedUtc { get; set; }
        public DateTime? CurrentClearedUtc { get; set; }
        public string Reason { get; set; }
        public bool CommandSucceeded { get; set; }
        public double CommandElapsedMs { get; set; }
        public DateTime? VerificationUtc { get; set; }
        public double? VerificationCurrentA { get; set; }
        public double? VerificationThresholdA { get; set; }
        public int? VerificationWaitMs { get; set; }
        public bool? ElectricalCurrentCleared { get; set; }
        public Guid CommandId { get; set; }
        public bool CallerTimedOut { get; set; }
        public bool LateHardwareSuccess { get; set; }
        public DateTime? HardwareCompletedUtc { get; set; }
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
        private readonly ConcurrentDictionary<int, TerminalOffSafetyEvidence> _terminalOffSafetyEvidence =
            new();
        private readonly ConcurrentDictionary<int, Guid> _runIdByChannel = new();
        private readonly ConcurrentDictionary<int, ChannelStaggerAssignment> _staggerAssignmentByChannel = new();
        private readonly ConcurrentDictionary<int, DateTime> _electricalPhaseDueByChannel = new();
        private readonly ConcurrentDictionary<Guid, ElectricalStaggerPlan> _staggerPlansByRun = new();
        private readonly ConcurrentQueue<Guid> _staggerPlanOrder = new();

        internal void RecordTerminalOffCommand(
            int channel,
            string reason,
            bool commandSucceeded,
            double commandElapsedMs,
            EpbDoTimingObservation timing = null)
        {
            var decisionUtc = AsOptionalUtc(timing?.DecisionUtc ?? default);
            var doWriteStartedUtc = AsOptionalUtc(timing?.DoWriteStartedUtc ?? default);
            var doWriteCompletedUtc = AsOptionalUtc(timing?.DoWriteCompletedUtc ?? default);
            var currentClearedUtc = timing?.CurrentClearedUtc.HasValue == true
                ? AsOptionalUtc(timing.CurrentClearedUtc.Value)
                : null;
            _terminalOffSafetyEvidence.AddOrUpdate(
                channel,
                _ => new TerminalOffSafetyEvidence
                {
                    CommandUtc = DateTime.UtcNow,
                    Reason = reason ?? string.Empty,
                    CommandSucceeded = commandSucceeded,
                    CommandElapsedMs = commandElapsedMs,
                    DecisionUtc = decisionUtc,
                    DoWriteStartedUtc = doWriteStartedUtc,
                    DoWriteCompletedUtc = doWriteCompletedUtc,
                    CurrentClearedUtc = currentClearedUtc
                },
                (_, existing) =>
                {
                    existing.CommandUtc = DateTime.UtcNow;
                    existing.Reason = reason ?? string.Empty;
                    existing.CommandSucceeded = commandSucceeded || existing.LateHardwareSuccess;
                    existing.CommandElapsedMs = commandElapsedMs;
                    if (timing != null)
                    {
                        if (decisionUtc.HasValue)
                        {
                            // 新决策开启一条新的因果时间线，必须清除上一圈尚未被本次
                            // 物理完成/电流验证覆盖的时刻，禁止跨圈拼接证据。
                            existing.DecisionUtc = decisionUtc;
                            existing.DoWriteStartedUtc = doWriteStartedUtc;
                            existing.DoWriteCompletedUtc = doWriteCompletedUtc;
                            existing.CurrentClearedUtc = currentClearedUtc;
                        }
                        else
                        {
                            if (doWriteStartedUtc.HasValue)
                                existing.DoWriteStartedUtc = doWriteStartedUtc;
                            if (doWriteCompletedUtc.HasValue)
                                existing.DoWriteCompletedUtc = doWriteCompletedUtc;
                            if (currentClearedUtc.HasValue)
                                existing.CurrentClearedUtc = currentClearedUtc;
                        }
                    }
                    return existing;
                });
        }

        private void RecordLateTerminalOffCompletion(HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null || telemetry.Channel < 1 || telemetry.Channel > 12) return;
            _terminalOffSafetyEvidence.AddOrUpdate(
                telemetry.Channel,
                _ => new TerminalOffSafetyEvidence
                {
                    CommandUtc = telemetry.HardwareCompletedUtc,
                    Reason = "HighPriorityOffLateCompletion",
                    CommandSucceeded = telemetry.Result,
                    CommandElapsedMs = telemetry.TotalMs,
                    CommandId = telemetry.CommandId,
                    CallerTimedOut = telemetry.CallerTimedOut,
                    LateHardwareSuccess = telemetry.LateHardwareSuccess,
                    HardwareCompletedUtc = telemetry.HardwareCompletedUtc,
                    DoWriteStartedUtc = EstimateDoWriteStartedUtc(telemetry),
                    DoWriteCompletedUtc = telemetry.HardwareCompletedUtc
                },
                (_, existing) =>
                {
                    existing.CommandId = telemetry.CommandId;
                    existing.CallerTimedOut = existing.CallerTimedOut || telemetry.CallerTimedOut;
                    existing.LateHardwareSuccess =
                        existing.LateHardwareSuccess || telemetry.LateHardwareSuccess;
                    existing.HardwareCompletedUtc = telemetry.HardwareCompletedUtc;
                    existing.DoWriteStartedUtc = EstimateDoWriteStartedUtc(telemetry);
                    existing.DoWriteCompletedUtc = telemetry.HardwareCompletedUtc;
                    if (telemetry.Result) existing.CommandSucceeded = true;
                    return existing;
                });
        }

        /// <summary>
        /// 自适应100ms监督截止已经提交终态后的迟到硬件证据。这里只更新证据对象，
        /// 不发布运行状态、不解除联锁，也不触发电流验证。
        /// </summary>
        internal void RecordAdaptiveTerminalOffLateEvidence(
            HighPriorityDoTelemetry telemetry,
            string reason)
        {
            if (telemetry == null || telemetry.Channel < 1 || telemetry.Channel > 12) return;
            _terminalOffSafetyEvidence.AddOrUpdate(
                telemetry.Channel,
                _ => new TerminalOffSafetyEvidence
                {
                    CommandUtc = telemetry.HardwareCompletedUtc,
                    Reason = "AdaptiveTerminalOffHardwareTimeoutLateCompletion " +
                             (reason ?? string.Empty),
                    CommandSucceeded = telemetry.Result,
                    CommandElapsedMs = telemetry.TotalMs,
                    CommandId = telemetry.CommandId,
                    CallerTimedOut = true,
                    LateHardwareSuccess = telemetry.Result,
                    HardwareCompletedUtc = telemetry.HardwareCompletedUtc,
                    DoWriteStartedUtc = EstimateDoWriteStartedUtc(telemetry),
                    DoWriteCompletedUtc = telemetry.HardwareCompletedUtc
                },
                (_, existing) =>
                {
                    existing.Reason =
                        "AdaptiveTerminalOffHardwareTimeoutLateCompletion " +
                        (reason ?? string.Empty);
                    existing.CommandId = telemetry.CommandId;
                    existing.CallerTimedOut = true;
                    existing.LateHardwareSuccess = telemetry.Result;
                    existing.HardwareCompletedUtc = telemetry.HardwareCompletedUtc;
                    existing.DoWriteStartedUtc = EstimateDoWriteStartedUtc(telemetry);
                    existing.DoWriteCompletedUtc = telemetry.HardwareCompletedUtc;
                    if (telemetry.Result) existing.CommandSucceeded = true;
                    return existing;
                });
        }

        internal void RecordTerminalOffCurrentVerification(
            int channel,
            double currentA,
            double thresholdA,
            int waitMs,
            bool cleared,
            DateTime? verificationUtc = null)
        {
            var observedUtc = verificationUtc.HasValue
                ? verificationUtc.Value.ToUniversalTime()
                : DateTime.UtcNow;
            _terminalOffSafetyEvidence.AddOrUpdate(
                channel,
                _ => new TerminalOffSafetyEvidence
                {
                    CommandUtc = DateTime.UtcNow,
                    Reason = "TerminalOffCommandEvidenceMissing",
                    VerificationUtc = observedUtc,
                    VerificationCurrentA = currentA,
                    VerificationThresholdA = thresholdA,
                    VerificationWaitMs = waitMs,
                    ElectricalCurrentCleared = cleared,
                    CurrentClearedUtc = cleared ? observedUtc : (DateTime?)null
                },
                (_, existing) =>
                {
                    return new TerminalOffSafetyEvidence
                    {
                        CommandUtc = existing.CommandUtc,
                        DecisionUtc = existing.DecisionUtc,
                        DoWriteStartedUtc = existing.DoWriteStartedUtc,
                        DoWriteCompletedUtc = existing.DoWriteCompletedUtc,
                        CurrentClearedUtc = cleared
                            ? observedUtc
                            : existing.CurrentClearedUtc,
                        Reason = existing.Reason,
                        CommandSucceeded = existing.CommandSucceeded,
                        CommandElapsedMs = existing.CommandElapsedMs,
                        VerificationUtc = observedUtc,
                        VerificationCurrentA = currentA,
                        VerificationThresholdA = thresholdA,
                        VerificationWaitMs = waitMs,
                        ElectricalCurrentCleared = cleared,
                        CommandId = existing.CommandId,
                        CallerTimedOut = existing.CallerTimedOut,
                        LateHardwareSuccess = existing.LateHardwareSuccess,
                        HardwareCompletedUtc = existing.HardwareCompletedUtc
                    };
                });
        }

        private static DateTime EstimateDoWriteStartedUtc(HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null || telemetry.HardwareCompletedUtc == default)
                return DateTime.MinValue;
            return telemetry.HardwareCompletedUtc.ToUniversalTime().AddMilliseconds(
                -Math.Max(0, telemetry.NiWriteMs));
        }

        private static DateTime? AsOptionalUtc(DateTime value)
        {
            if (value == default || value == DateTime.MinValue || value == DateTime.MaxValue)
                return null;
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

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
            EnsureChannelExecutionPermit(channel, stage);
            EnsurePowerSupplyEnergizationPermit(channel);
            return ExecuteDoCommand(channel, stage, EpbDoCommand.Forward, () => _do.SetEpbForward(channel));
        }

        internal bool CommandEpbReverse(int channel, string stage)
        {
            EnsureChannelExecutionPermit(channel, stage);
            EnsurePowerSupplyEnergizationPermit(channel);
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

        /// <summary>
        /// DAQ 快速控制链专用：只把 OFF 放入所属设备最高优先级 worker，
        /// 不读取电流、不生成追踪对象、不写日志，也不等待 NI 物理写完成。
        /// </summary>
        internal bool TrySubmitEpbOffHighPriority(
            int channel,
            Action<HighPriorityDoTelemetry> completion,
            out Guid commandId)
        {
            return _do.TrySubmitEpbOffHighPriority(channel, completion, out commandId);
        }

        /// <summary>
        /// 终态 OFF 在 DAQ 控制线程上未获有界队列接纳时，立即登记组级失效安全任务。
        /// 后台协调仍通过 DoController 的专用最高优先级 worker 执行 NI 写，
        /// 本方法没有用 Task.Run 直接包装任何 NI 写入。
        /// </summary>
        internal void QueueElectricalGroupEmergencyShutdownFromDaqControl(
            int channel,
            string reason)
        {
            ObserveBackgroundTask(
                System.Threading.Tasks.Task.Run(() =>
                    RequestElectricalGroupEmergencyShutdown(channel, reason)),
                "AdaptiveTerminalOffAdmissionEmergency",
                channel);
        }

        /// <summary>
        /// DAQ/设备事故同步安全处理专用。只执行DO与带电位图更新，不读取电流、
        /// 不创建追踪对象，也不写日志；调用方必须在全部受影响通道断电后再做诊断。
        /// </summary>
        internal bool CommandEpbOffSafetyImmediate(int channel)
        {
            var result = _do.SetEpbOffHighPriority(channel);
            if (result) SetChannelEnergized(channel, false);
            return result;
        }

        private void EnsureChannelExecutionPermit(int channel, string stage)
        {
            if (System.Threading.Volatile.Read(ref _energizationRevoked) != 0)
                throw new InvalidOperationException(
                    $"停止安全栅栏已生效，拒绝 EPB{channel:D2} 上电命令。Stage={stage}");
            if (!IsChannelEnabled(channel))
                throw new ChannelExecutionPermitException(
                    channel,
                    ChannelRuntimeState.NotEnabled,
                    $"Disabled Stage={stage}");
            var state = _channelRuntimeStateStore.Get(channel)?.State ??
                        ChannelRuntimeState.NotEnabled;
            if (!ChannelRuntimeAllowsEnergization(state))
                throw new ChannelExecutionPermitException(channel, state, stage);
        }

        internal static bool ChannelRuntimeAllowsEnergization(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.Starting ||
                   state == ChannelRuntimeState.Learning ||
                   state == ChannelRuntimeState.Running ||
                   state == ChannelRuntimeState.WarningRunning ||
                   state == ChannelRuntimeState.Recovering ||
                   state == ChannelRuntimeState.ResumeChecking ||
                   state == ChannelRuntimeState.Qualification ||
                   state == ChannelRuntimeState.PausePending;
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
            var commandStartedTicks = 0L;
            var commandCompletedTicks = 0L;
            try
            {
                commandStartedTicks = Stopwatch.GetTimestamp();
                if (command == EpbDoCommand.Forward || command == EpbDoCommand.Reverse)
                    BaselineDaqLivenessBeforeEnergization(channel);
                result = execute();
                commandCompletedTicks = Stopwatch.GetTimestamp();
                if (result)
                    SetChannelEnergized(
                        channel,
                        command == EpbDoCommand.Forward || command == EpbDoCommand.Reverse);
                return result;
            }
            finally
            {
                if (commandStartedTicks > 0 && commandCompletedTicks == 0)
                    commandCompletedTicks = Stopwatch.GetTimestamp();
                var commandElapsedMs = commandStartedTicks > 0
                    ? (commandCompletedTicks - commandStartedTicks) * 1000.0 / Stopwatch.Frequency
                    : 0;
                try
                {
                    _watchdogDoCommandSequence.AddOrUpdate(channel, 1, (_, value) => value + 1);
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
                        CommandElapsedMs = commandElapsedMs,
                        BranchCurrentA = current
                    };
                    _doControlTrace.Add(traceEvent);
                    var loggedResult = result;
                    ObserveBackgroundTask(System.Threading.Tasks.Task.Run(() =>
                    {
                        var currentText = double.IsNaN(current)
                            ? "NaN"
                            : current.ToString("F6", CultureInfo.InvariantCulture);
                        var message =
                            $"DO命令 UTC={utc:O} MonoTicks={ticks} Run={runId:N} Cycle={cycleNumber} " +
                            $"Group={traceEvent.ElectricalGroupId} EPB={channel} Phase={traceEvent.PlannedPhaseMs}ms " +
                            $"PhaseDueUtc={(hasPhaseDue ? phaseDueUtc.ToString("O") : "Unknown")} " +
                            $"PhaseDeviationMs={(traceEvent.ElectricalPhaseStartDeviationMs?.ToString("F3", CultureInfo.InvariantCulture) ?? "Unknown")} " +
                            $"Stage={traceEvent.Stage} Command={command} DoCommandResult={loggedResult} " +
                            $"CommandElapsedMs={commandElapsedMs:F3} " +
                            $"CurrentA={currentText} PhysicalOffStatus=NotMeasured";
                        if (loggedResult)
                            _log.Info(message, "EPB-DO");
                        else
                            _log.Warn(message, "EPB-DO");
                    }), "DoControlTraceLog", channel);
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
            AlarmCycleSnapshotEvidence snapshotEvidence,
            DateTime alarmUtc)
        {
            _runIdByChannel.TryGetValue(alarmChannel, out var runId);
            _staggerAssignmentByChannel.TryGetValue(alarmChannel, out var alarmAssignment);
            _staggerPlansByRun.TryGetValue(runId, out var plan);
            var events = _doControlTrace.Snapshot(alarmUtc, runId);
            var adaptiveEvents = _adaptiveDecisionTrace.Snapshot(
                alarmUtc,
                runId,
                alarmChannel);
            var latestAdaptiveDecision = SelectAlarmDecision(adaptiveEvents, alarmUtc);
            _terminalOffSafetyEvidence.TryGetValue(alarmChannel, out var terminalOffEvidence);

            TryWriteControlEvidence(
                "alarm-metadata.json",
                () => WriteAlarmMetadata(
                    Path.Combine(snapshotDir, "alarm-metadata.json"),
                    alarmChannel,
                    alarmCycleNumber,
                    reason,
                    snapshotEvidence,
                    alarmUtc,
                    runId,
                    alarmAssignment,
                    plan,
                    events,
                    latestAdaptiveDecision,
                    terminalOffEvidence));
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
            TryWriteControlEvidence(
                "adaptive-decision-timeline.csv",
                () => AdaptiveDecisionTraceBuffer.ExportCsv(
                    Path.Combine(snapshotDir, "adaptive-decision-timeline.csv"),
                    adaptiveEvents));
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

        internal static AdaptiveDecisionTraceEvent SelectAlarmDecision(
            IEnumerable<AdaptiveDecisionTraceEvent> events,
            DateTime alarmUtc)
        {
            var eligible = (events ?? Enumerable.Empty<AdaptiveDecisionTraceEvent>())
                .Where(x => x != null && x.SampleUtc <= alarmUtc)
                .OrderBy(x => x.SampleUtc)
                .ThenBy(x => x.MonotonicTicks)
                .ToArray();
            return eligible.LastOrDefault(
                       x => string.Equals(x.Action, "HardFault", StringComparison.OrdinalIgnoreCase)) ??
                   eligible.LastOrDefault();
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
            IReadOnlyList<DoControlTraceEvent> events,
            AdaptiveDecisionTraceEvent latestAdaptiveDecision = null,
            TerminalOffSafetyEvidence terminalOffEvidence = null)
        {
            WriteAlarmMetadata(
                path,
                alarmChannel,
                alarmCycleNumber,
                reason,
                new AlarmCycleSnapshotEvidence { IsValid = csvAndBinComplete },
                alarmUtc,
                runId,
                assignment,
                plan,
                events,
                latestAdaptiveDecision,
                terminalOffEvidence);
        }

        internal static void WriteAlarmMetadata(
            string path,
            int alarmChannel,
            int alarmCycleNumber,
            string reason,
            AlarmCycleSnapshotEvidence snapshotEvidence,
            DateTime alarmUtc,
            Guid runId,
            ChannelStaggerAssignment assignment,
            ElectricalStaggerPlan plan,
            IReadOnlyList<DoControlTraceEvent> events,
            AdaptiveDecisionTraceEvent latestAdaptiveDecision = null,
            TerminalOffSafetyEvidence terminalOffEvidence = null)
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
            snapshotEvidence ??= new AlarmCycleSnapshotEvidence
            {
                ValidationError = "SnapshotEvidenceMissing"
            };
            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"schemaVersion\": 5,");
            json.AppendLine($"  \"alarmUtc\": \"{alarmUtc:O}\",");
            json.AppendLine($"  \"runId\": \"{runId:N}\",");
            json.AppendLine($"  \"alarmChannel\": {alarmChannel},");
            json.AppendLine($"  \"alarmCycleNumber\": {alarmCycleNumber},");
            json.AppendLine($"  \"electricalGroupId\": {assignment?.ElectricalGroupId ?? 0},");
            json.AppendLine($"  \"selectedIndexInGroup\": {assignment?.SelectedIndexInGroup ?? 0},");
            json.AppendLine($"  \"staggerMs\": {assignment?.StaggerMs ?? 0},");
            json.AppendLine($"  \"plannedPhaseMs\": {assignment?.PhaseMs ?? 0},");
            json.AppendLine($"  \"reason\": \"{EscapeJson(reason)}\",");
            var legacyCsvAndBinComplete = snapshotEvidence.IsValid &&
                                          (string.IsNullOrWhiteSpace(snapshotEvidence.StorageFormat) ||
                                           string.Equals(snapshotEvidence.StorageFormat,
                                               StorageFormatLevel.CsvAndBin.ToString(),
                                               StringComparison.OrdinalIgnoreCase));
            json.AppendLine(
                $"  \"alarmCycleCsvAndBinComplete\": {legacyCsvAndBinComplete.ToString().ToLowerInvariant()},");
            json.AppendLine(
                $"  \"alarmCycleEvidenceComplete\": {snapshotEvidence.IsValid.ToString().ToLowerInvariant()},");
            var storageFormat = snapshotEvidence.StorageFormat ?? "Unknown";
            json.AppendLine(
                $"  \"alarmCycleStorageFormat\": \"{EscapeJson(storageFormat)}\",");
            json.AppendLine($"  \"evidenceSampleCount\": {snapshotEvidence.SampleCount},");
            json.AppendLine(
                $"  \"evidenceFirstSampleUtc\": {JsonDate(snapshotEvidence.FirstSampleUtc)},");
            json.AppendLine(
                $"  \"evidenceLastSampleUtc\": {JsonDate(snapshotEvidence.LastSampleUtc)},");
            json.AppendLine(
                $"  \"evidenceValidationError\": \"{EscapeJson(snapshotEvidence.ValidationError)}\",");
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
            AppendTerminalOffSafetyJson(json, terminalOffEvidence);
            AppendAdaptiveDecisionJson(json, latestAdaptiveDecision);
            json.AppendLine("  \"doCommandResultMeaning\": \"软件DO方法返回值；不代表继电器触点或负载端物理通断确认\",");
            json.AppendLine("  \"physicalOffStatus\": \"NotMeasured\",");
            json.AppendLine("  \"physicalPowerState\": \"NotMeasured\"");
            json.AppendLine("}");
            File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
        }

        private static void AppendTerminalOffSafetyJson(
            StringBuilder json,
            TerminalOffSafetyEvidence item)
        {
            json.Append("  \"terminalOffSafety\": ");
            if (item == null)
            {
                json.AppendLine("null,");
                return;
            }

            var verificationCurrent = item.VerificationCurrentA.HasValue &&
                                      !double.IsNaN(item.VerificationCurrentA.Value) &&
                                      !double.IsInfinity(item.VerificationCurrentA.Value)
                ? item.VerificationCurrentA.Value.ToString("F6", CultureInfo.InvariantCulture)
                : "null";
            var verificationThreshold = item.VerificationThresholdA.HasValue
                ? item.VerificationThresholdA.Value.ToString("F6", CultureInfo.InvariantCulture)
                : "null";
            var currentCleared = item.ElectricalCurrentCleared.HasValue
                ? item.ElectricalCurrentCleared.Value.ToString().ToLowerInvariant()
                : "null";
            json.Append("{");
            json.Append($"\"commandUtc\": \"{item.CommandUtc:O}\", ");
            json.Append($"\"decisionUtc\": {JsonDate(item.DecisionUtc)}, ");
            json.Append($"\"doWriteStartedUtc\": {JsonDate(item.DoWriteStartedUtc)}, ");
            json.Append($"\"doWriteCompletedUtc\": {JsonDate(item.DoWriteCompletedUtc)}, ");
            json.Append($"\"currentClearedUtc\": {JsonDate(item.CurrentClearedUtc)}, ");
            json.Append($"\"reason\": \"{EscapeJson(item.Reason)}\", ");
            json.Append($"\"commandSucceeded\": {item.CommandSucceeded.ToString().ToLowerInvariant()}, ");
            json.Append(
                $"\"commandElapsedMs\": {item.CommandElapsedMs.ToString("F3", CultureInfo.InvariantCulture)}, ");
            json.Append(
                $"\"verificationUtc\": {(item.VerificationUtc.HasValue ? $"\"{item.VerificationUtc.Value:O}\"" : "null")}, ");
            json.Append($"\"verificationCurrentA\": {verificationCurrent}, ");
            json.Append($"\"verificationThresholdA\": {verificationThreshold}, ");
            json.Append($"\"verificationWaitMs\": {item.VerificationWaitMs?.ToString() ?? "null"}, ");
            json.Append($"\"electricalCurrentCleared\": {currentCleared}, ");
            json.Append("\"physicalOffStatus\": \"NotMeasured\"");
            json.AppendLine("},");
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
                json.Append(
                    $"\"commandElapsedMs\": {item.CommandElapsedMs.ToString("F3", CultureInfo.InvariantCulture)}, ");
                json.Append($"\"branchCurrentA\": {current}");
                json.Append("}");
            }

            if (trailingComma) json.Append(',');
            json.AppendLine();
        }

        private static void AppendAdaptiveDecisionJson(
            StringBuilder json,
            AdaptiveDecisionTraceEvent item)
        {
            json.Append("  \"adaptiveDecision\": ");
            if (item == null)
            {
                json.AppendLine("null,");
                return;
            }

            json.Append("{");
            json.Append($"\"sampleUtc\": \"{item.SampleUtc:O}\", ");
            json.Append($"\"monotonicTicks\": {item.MonotonicTicks}, ");
            json.Append($"\"direction\": \"{EscapeJson(item.Direction)}\", ");
            json.Append($"\"stage\": \"{item.Stage}\", ");
            json.Append($"\"elapsedMs\": {item.ElapsedMs}, ");
            json.Append($"\"currentA\": {JsonNumber(item.CurrentA)}, ");
            json.Append($"\"windowSamples\": {item.WindowSampleCount}, ");
            json.Append($"\"windowSpanMs\": {item.WindowSpanMs}, ");
            json.Append($"\"medianA\": {JsonNumber(item.WindowMedianA)}, ");
            json.Append($"\"madA\": {JsonNumber(item.WindowMadA)}, ");
            json.Append($"\"p10A\": {JsonNumber(item.WindowP10A)}, ");
            json.Append($"\"p90A\": {JsonNumber(item.WindowP90A)}, ");
            json.Append($"\"releaseThresholdA\": {JsonNumber(item.ReleaseThresholdA)}, ");
            json.Append($"\"allowedSpreadA\": {JsonNumber(item.AllowedSpreadA)}, ");
            json.Append($"\"candidateElapsedMs\": {item.ReleaseCandidateElapsedMs}, ");
            json.Append($"\"windowQualified\": {item.WindowQualified.ToString().ToLowerInvariant()}, ");
            json.Append($"\"action\": \"{EscapeJson(item.Action)}\", ");
            json.Append($"\"reason\": \"{EscapeJson(item.Reason)}\"");
            json.AppendLine("},");
        }

        private static string JsonNumber(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "null"
                : value.ToString("F6", CultureInfo.InvariantCulture);
        }

        private static string JsonDate(DateTime? value)
        {
            return value.HasValue ? $"\"{value.Value.ToUniversalTime():O}\"" : "null";
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
                "Utc,MonotonicTicks,MonotonicElapsedMs,RunId,Cycle,ElectricalGroup,Channel,PlannedPhaseMs,ElectricalPhaseDueUtc,ElectricalPhaseStartDeviationMs,Stage,Command,DoCommandResult,BranchCurrentA,PhysicalPowerState,CommandElapsedMs");
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
                csv.Append(',').Append(item.CommandElapsedMs.ToString("F3", CultureInfo.InvariantCulture));
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
