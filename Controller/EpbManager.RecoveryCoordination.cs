using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;

namespace Controller
{
    internal readonly struct UnattendedRunChainTransition
    {
        internal UnattendedRunChainTransition(
            string rootRunId,
            string parentRunId,
            string currentRunId,
            int restartGeneration,
            bool sameRun,
            bool recoveryContinuation,
            bool processRestartContinuation)
        {
            RootRunId = rootRunId ?? string.Empty;
            ParentRunId = parentRunId ?? string.Empty;
            CurrentRunId = currentRunId ?? string.Empty;
            RestartGeneration = Math.Max(0, restartGeneration);
            SameRun = sameRun;
            RecoveryContinuation = recoveryContinuation;
            ProcessRestartContinuation = processRestartContinuation;
        }

        internal string RootRunId { get; }
        internal string ParentRunId { get; }
        internal string CurrentRunId { get; }
        internal int RestartGeneration { get; }
        internal bool SameRun { get; }
        internal bool RecoveryContinuation { get; }
        internal bool ProcessRestartContinuation { get; }
        internal string RecoveryMode => SameRun
            ? "SameRun"
            : !RecoveryContinuation
                ? "ManualAuthorization"
                : ProcessRestartContinuation
                    ? "ProcessRestart"
                    : "InProcess";
        internal bool NewAuthorizationChain => !SameRun && !RecoveryContinuation;
    }

    internal enum DaqRecoveryParticipantState
    {
        Registered = 0,
        Ready = 1,
        Withdrawn = 2,
        Terminal = 3
    }

    internal sealed class DaqRecoveryParticipantToken : IDisposable
    {
        private readonly DaqRecoveryBatchBarrier _barrier;
        private int _disposed;

        internal DaqRecoveryParticipantToken(DaqRecoveryBatchBarrier barrier, string device)
        {
            _barrier = barrier ?? throw new ArgumentNullException(nameof(barrier));
            Device = device ?? string.Empty;
        }

        internal string Device { get; }

        internal Task<bool> ReadyAndWaitAsync(CancellationToken token)
            => _barrier.ReadyAndWaitAsync(Device, token);

        internal bool Withdraw(string reason)
            => _barrier.Withdraw(Device, reason);

        internal bool Terminal(string reason)
            => _barrier.Terminal(Device, reason);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _barrier.Terminal(Device, "ParticipantDisposedWithoutExplicitTerminal");
        }
    }

    internal sealed class DaqRecoveryBatchBarrier
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DaqRecoveryParticipantState> _participants =
            new Dictionary<string, DaqRecoveryParticipantState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _reasons =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource<bool> _released =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sealed;

        internal DaqRecoveryBatchBarrier(Guid runId, long runEpoch)
        {
            RunId = runId;
            RunEpoch = runEpoch;
        }

        // Compatibility constructor used by existing deterministic tests: the
        // supplied devices are treated as already-admitted production contexts.
        internal DaqRecoveryBatchBarrier(Guid runId, long runEpoch, IEnumerable<string> devices)
            : this(runId, runEpoch)
        {
            foreach (var device in devices ?? Array.Empty<string>()) Register(device);
            Seal();
        }

        internal Guid RunId { get; }
        internal long RunEpoch { get; }

        internal DaqRecoveryParticipantToken Register(string device)
        {
            var normalized = (device ?? string.Empty).Trim();
            if (normalized.Length == 0) return null;
            lock (_gate)
            {
                if (_sealed || _participants.ContainsKey(normalized)) return null;
                _participants[normalized] = DaqRecoveryParticipantState.Registered;
                return new DaqRecoveryParticipantToken(this, normalized);
            }
        }

        internal void RecordUnadmitted(string device, string reason)
        {
            var normalized = (device ?? string.Empty).Trim();
            if (normalized.Length == 0) return;
            lock (_gate)
                _reasons[normalized] = "NoRunnableContext:" + (reason ?? string.Empty);
        }

        internal void Seal()
        {
            lock (_gate)
            {
                _sealed = true;
                TryReleaseLocked();
            }
        }

        internal Task<bool> SignalReadyAndWaitAsync(string device, CancellationToken token)
            => ReadyAndWaitAsync(device, token);

        internal async Task<bool> ReadyAndWaitAsync(string device, CancellationToken token)
        {
            lock (_gate)
            {
                if (!_participants.TryGetValue(device ?? string.Empty, out var state) ||
                    (state != DaqRecoveryParticipantState.Registered &&
                     state != DaqRecoveryParticipantState.Ready))
                    return false;
                _participants[device] = DaqRecoveryParticipantState.Ready;
                TryReleaseLocked();
            }

            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetCanceled()))
            {
                var completed = await Task.WhenAny(_released.Task, cancelled.Task)
                    .ConfigureAwait(false);
                if (completed != _released.Task)
                    token.ThrowIfCancellationRequested();
                return await _released.Task.ConfigureAwait(false);
            }
        }

        internal bool Withdraw(string device, string reason)
        {
            lock (_gate)
            {
                if (!_participants.TryGetValue(device ?? string.Empty, out var state)) return false;
                if (state == DaqRecoveryParticipantState.Terminal ||
                    state == DaqRecoveryParticipantState.Withdrawn)
                    return true;
                _participants[device] = DaqRecoveryParticipantState.Withdrawn;
                _reasons[device] = reason ?? "Withdrawn";
                TryReleaseLocked();
                return true;
            }
        }

        internal bool Terminal(string device, string reason)
        {
            lock (_gate)
            {
                if (!_participants.TryGetValue(device ?? string.Empty, out var state)) return false;
                if (state == DaqRecoveryParticipantState.Terminal)
                    return IsTerminalLocked();
                var failedBeforeReady = state == DaqRecoveryParticipantState.Registered;
                _participants[device] = DaqRecoveryParticipantState.Terminal;
                var terminalReason = reason ?? "Terminal";
                if (state == DaqRecoveryParticipantState.Withdrawn &&
                    _reasons.TryGetValue(device, out var withdrawReason) &&
                    !string.IsNullOrWhiteSpace(withdrawReason))
                    _reasons[device] = withdrawReason + ";Terminal=" + terminalReason;
                else
                    _reasons[device] = terminalReason;
                if (failedBeforeReady) _released.TrySetResult(false);
                TryReleaseLocked();
                return IsTerminalLocked();
            }
        }

        internal bool MarkTerminal(string device) => Terminal(device, "RecoveryTerminal");

        internal string CaptureDiagnostic()
        {
            lock (_gate)
            {
                var participants = string.Join(",", _participants
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value));
                var reasons = string.Join(",", _reasons
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value));
                return $"Sealed={_sealed};Participants=[{participants}];Reasons=[{reasons}]";
            }
        }

        private void TryReleaseLocked()
        {
            if (!_sealed) return;
            if (_participants.Count == 0 || _participants.Values.All(state =>
                    state == DaqRecoveryParticipantState.Ready ||
                    state == DaqRecoveryParticipantState.Withdrawn ||
                    state == DaqRecoveryParticipantState.Terminal))
                _released.TrySetResult(true);
        }

        private bool IsTerminalLocked()
            => _sealed && _participants.Values.All(state =>
                state == DaqRecoveryParticipantState.Withdrawn ||
                state == DaqRecoveryParticipantState.Terminal);
    }

    /// <summary>
    ///     Ensures that repeated software-recovery failures open at most one batch-recycle
    ///     circuit per RunId.  A new formal run resets the gate; competing recovery owners
    ///     that observe an already-open gate must stop retrying instead of creating another
    ///     self-maintenance loop.
    /// </summary>
    internal sealed class SoftwareRecoveryEscalationGate
    {
        private readonly ConcurrentDictionary<Guid, byte> _openedRuns = new();

        internal bool TryOpen(Guid runId)
        {
            return runId != Guid.Empty && _openedRuns.TryAdd(runId, 0);
        }

        internal bool IsOpen(Guid runId)
        {
            return runId != Guid.Empty && _openedRuns.ContainsKey(runId);
        }

        internal void Reset()
        {
            _openedRuns.Clear();
        }
    }

    public sealed partial class EpbManager
    {
        private const int RecoveryOwnershipTakeoverTimeoutMs = 10_000;
        private const int RecoveryStageTimeoutMs = 15_000;
        private const int RecoveryMechanicalReleaseTimeoutMs = 20_000;
        internal const int RecoveryGroupHardDeadlineMs = 60_000;
        // 仅作为阶段无进度诊断；DaqAutoRecoveryContext 的60秒
        // HardDeadlineUtc 才是唯一允许取消恢复上下文的总截止。
        internal const int PowerDisableHardDeadlineMs = 10_000;
        internal const int SoftwareRecoveryEscalationAttempts = 3;
        public const int UnattendedProcessRestartBudget = 3;

        internal static UnattendedRunChainTransition SelectUnattendedRunChainTransition(
            string existingCurrentRunId,
            string existingRootRunId,
            string existingParentRunId,
            int existingRestartGeneration,
            bool armed,
            bool recoveryChainPendingStart,
            bool inProcessRecoveryPending,
            string incomingRunId)
        {
            var incoming = Guid.TryParse(incomingRunId, out var incomingGuid) &&
                           incomingGuid != Guid.Empty
                ? incomingGuid.ToString("N")
                : string.Empty;
            var existingCurrent = Guid.TryParse(existingCurrentRunId, out var currentGuid) &&
                                  currentGuid != Guid.Empty
                ? currentGuid.ToString("N")
                : string.Empty;
            var existingRoot = Guid.TryParse(existingRootRunId, out var rootGuid) &&
                               rootGuid != Guid.Empty
                ? rootGuid.ToString("N")
                : string.Empty;
            var existingParent = Guid.TryParse(existingParentRunId, out var parentGuid) &&
                                 parentGuid != Guid.Empty
                ? parentGuid.ToString("N")
                : string.Empty;

            // A multi-channel batch publishes Starting once per channel. Those repeated
            // notifications are one execution, not new authorization chains, and must not
            // reset the root RunId or the ten-minute process-restart budget.
            if (incoming.Length > 0 &&
                string.Equals(existingCurrent, incoming, StringComparison.OrdinalIgnoreCase))
                return new UnattendedRunChainTransition(
                    existingRoot.Length > 0 ? existingRoot : incoming,
                    existingParent,
                    incoming,
                    existingRestartGeneration,
                    sameRun: true,
                    recoveryContinuation: false,
                    processRestartContinuation: false);

            // The child process has consumed the one-time nonce but has not yet created its
            // new in-process RunId. Preserve the first manually authorized RunId as the chain
            // root, link the old execution as the parent, and carry the restart budget forward.
            if (armed && (recoveryChainPendingStart || inProcessRecoveryPending) &&
                incoming.Length > 0 &&
                existingCurrent.Length > 0 && existingRoot.Length > 0)
                return new UnattendedRunChainTransition(
                    existingRoot,
                    existingCurrent,
                    incoming,
                    checked(Math.Max(0, existingRestartGeneration) + 1),
                    sameRun: false,
                    recoveryContinuation: true,
                    processRestartContinuation: recoveryChainPendingStart);

            // A new manual authorization starts a fresh chain and a fresh restart budget.
            return new UnattendedRunChainTransition(
                incoming,
                string.Empty,
                incoming,
                0,
                sameRun: false,
                recoveryContinuation: false,
                processRestartContinuation: false);
        }

        internal static bool ShouldRestartAfterRecoveryStartupFailure(
            bool recoveryBatchCommitted)
            => !recoveryBatchCommitted;

        internal static bool CanCommitUnattendedRecovery(
            bool armed,
            bool processRecoveryPending,
            bool inProcessRecoveryPending,
            bool revocationObserved)
            => armed &&
               (processRecoveryPending || inProcessRecoveryPending) &&
               !revocationObserved;

        private readonly HydraulicRecoveryOwnershipCoordinator _recoveryOwnership =
            new HydraulicRecoveryOwnershipCoordinator();
        private readonly ConcurrentDictionary<long, byte> _affectedGroupResetInProgress = new();
        private readonly ConcurrentDictionary<long, byte> _activeCycleLimitRecoveries = new();
        // 调度锁和重试次数必须使用同一个 (runEpoch, hydraulicGroup) 身份。若这里只按
        // hydraulicGroup 加锁，旧 run 尚未退出的 finally 会先吞掉新 run 的调度，再把
        // 共享键删除，导致新 run 永久没有恢复任务。
        private readonly ConcurrentDictionary<long, byte> _isolatedInfrastructureRecoveryScheduled = new();
        // key = (runEpoch << 8) | hydraulicGroup。旧 run 的迟到任务不能继承或污染
        // 新 run 的恢复次数；BeginBatchSession 仍会主动清空历史键。
        private readonly ConcurrentDictionary<long, int> _isolatedInfrastructureRecoveryAttempts = new();
        private readonly SoftwareRecoveryEscalationGate _softwareRecoveryEscalation = new();
        private readonly ConcurrentDictionary<Guid, DaqRecoveryBatchBarrier>
            _daqRecoveryBatchBarriers = new();
        private readonly ConcurrentDictionary<Guid, Guid> _daqRecoveryBatchAliases = new();

        private void RegisterDaqRecoveryBatchBarrier(
            Guid correlationId,
            IEnumerable<string> devices,
            Guid runId,
            long runEpoch,
            IEnumerable<Guid> correlationAliases = null)
        {
            if (correlationId == Guid.Empty) return;
            var barrier = new DaqRecoveryBatchBarrier(runId, runEpoch);
            var aliases = (correlationAliases ?? Array.Empty<Guid>())
                .Append(correlationId)
                .Where(value => value != Guid.Empty)
                .Distinct()
                .ToArray();
            var existingBarrier = aliases
                .Select(alias => _daqRecoveryBatchBarriers.TryGetValue(
                    alias,
                    out var found)
                    ? found
                    : null)
                .FirstOrDefault(found => found != null);
            if (existingBarrier != null)
                barrier = existingBarrier;
            foreach (var alias in aliases)
            {
                _daqRecoveryBatchBarriers[alias] = barrier;
                _daqRecoveryBatchAliases[alias] = correlationId;
            }
        }

        private void RegisterDaqRecoveryBatchParticipant(
            Guid correlationId,
            string device,
            DaqAutoRecoveryContext context)
        {
            if (context == null || correlationId == Guid.Empty ||
                !_daqRecoveryBatchBarriers.TryGetValue(correlationId, out var barrier))
                return;
            if (barrier.RunId != context.RunId || barrier.RunEpoch != context.RunEpoch)
            {
                barrier.RecordUnadmitted(device, "RunIdentityMismatch");
                return;
            }
            context.BatchBarrierParticipant = barrier.Register(device);
            if (context.BatchBarrierParticipant == null)
                barrier.RecordUnadmitted(device, "RegistrationClosedOrDuplicate");
        }

        private void SealDaqRecoveryBatchBarrier(
            Guid correlationId,
            IEnumerable<string> incidentDevices)
        {
            if (correlationId == Guid.Empty ||
                !_daqRecoveryBatchBarriers.TryGetValue(correlationId, out var barrier))
                return;
            foreach (var device in (incidentDevices ?? Array.Empty<string>())
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_daqAutoRecovery.TryGetValue(device, out var context) ||
                    context == null || context.BatchBarrierParticipant == null)
                    barrier.RecordUnadmitted(device, "NoRunnableContext");
            }
            barrier.Seal();
            _log?.Info(
                $"DAQ恢复屏障已封口 CorrelationId={correlationId:N};{barrier.CaptureDiagnostic()}",
                "AI");
        }

        private async Task<bool> WaitForDaqRecoveryBatchBarrierAsync(
            DaqAutoRecoveryContext context)
        {
            if (context == null || context.BatchBarrierParticipant == null)
                return true;
            if (!_daqRecoveryBatchBarriers.TryGetValue(context.CorrelationId, out var barrier))
                return false;
            if (barrier.RunId != context.RunId || barrier.RunEpoch != context.RunEpoch)
                return false;
            return await context.BatchBarrierParticipant.ReadyAndWaitAsync(
                    context.Cancellation.Token)
                .ConfigureAwait(false);
        }

        private void MarkDaqRecoveryBatchTerminal(DaqAutoRecoveryContext context)
        {
            if (context == null || context.BatchBarrierParticipant == null ||
                !_daqRecoveryBatchBarriers.TryGetValue(context.CorrelationId, out var barrier))
                return;
            if (!context.BatchBarrierParticipant.Terminal("RecoveryTerminal")) return;
            foreach (var alias in _daqRecoveryBatchBarriers
                         .Where(pair => ReferenceEquals(pair.Value, barrier))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                ((ICollection<KeyValuePair<Guid, DaqRecoveryBatchBarrier>>)_daqRecoveryBatchBarriers)
                    .Remove(new KeyValuePair<Guid, DaqRecoveryBatchBarrier>(alias, barrier));
                _daqRecoveryBatchAliases.TryRemove(alias, out _);
            }
        }

        internal static bool ShouldEscalateSoftwareRecovery(int attempt)
        {
            return attempt >= SoftwareRecoveryEscalationAttempts;
        }

        internal static bool IsSoftwareRecoveryHardDeadlineElapsed(
            long startedTicks,
            long nowTicks,
            long stopwatchFrequency,
            int hardDeadlineMs = RecoveryGroupHardDeadlineMs)
        {
            if (startedTicks <= 0 || nowTicks < startedTicks || stopwatchFrequency <= 0)
                return false;
            var elapsedTicks = nowTicks - startedTicks;
            var requiredTicks = Math.Max(1, hardDeadlineMs) / 1000.0 * stopwatchFrequency;
            return elapsedTicks >= requiredTicks;
        }

        public static int SelectUnattendedProcessRestartRetryDelayMs(int completedAttempts)
        {
            // 第一次交接失败后快速复核安全/耐久状态；第二次失败后给磁盘、DAQ
            // 及操作系统资源更长的收敛时间。第三次已经耗尽既有10分钟预算，
            // 调用方不得再进入此退避分支。
            return completedAttempts <= 1 ? 5_000 : 15_000;
        }

        public static bool ShouldScheduleUnattendedProcessRestartRetry(
            bool nonceReleased,
            bool armed,
            string checkpointRunId,
            string expectedRunId,
            int attemptsInWindow)
        {
            return nonceReleased &&
                   armed &&
                   attemptsInWindow >= 0 &&
                   AreSameNonEmptyRunIds(checkpointRunId, expectedRunId);
        }

        public static bool ShouldRepeatProcessRestartSafetyTeardown(bool handoffReady)
        {
            // 一旦采集/持久化已经关闭且硬件句柄已释放，本进程只能继续尝试创建
            // 恢复子进程。再次访问已Dispose的DAQ/持久化对象会把一次可恢复的
            // Process.Start失败放大成永久恢复失败。
            return !handoffReady;
        }

        internal static bool IsDeterministicProcessingGap(string faultCode)
        {
            return string.Equals(
                       faultCode,
                       "BackgroundWorkerFault",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       faultCode,
                       "BackgroundQueueFull",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       faultCode,
                       "DaqDataContinuityGap",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       faultCode,
                       "RawPersistencePermanentFault",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       faultCode,
                       "DaqPersistenceWriteStall",
                       StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDaqInfrastructureRecoveryCode(string faultCode)
        {
            if (string.IsNullOrWhiteSpace(faultCode)) return false;
            return faultCode.StartsWith("Daq", StringComparison.OrdinalIgnoreCase) ||
                   faultCode.StartsWith("RecoveryBoundary", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       faultCode,
                       "RawPersistencePermanentFault",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       faultCode,
                       "BackgroundQueueFull",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       faultCode,
                       "BackgroundWorkerFault",
                       StringComparison.OrdinalIgnoreCase);
        }

        internal static bool RequiresImmediateProcessRecycle(string faultCode)
            => string.Equals(
                faultCode,
                "DaqPersistenceWriteStall",
                StringComparison.OrdinalIgnoreCase);

        internal static bool ShouldKeepSoftwareRecoveryLocal(
            string stage,
            string faultCode = null)
        {
            // 已接纳批次在工程处理或写盘所有权移交阶段形成空洞，是确定性连续性破坏，
            // 不是等待同一进程自行恢复的调度抖动。它仍属于软件/数据链故障而非硬件
            // 报警，但达到有界阈值后必须进入整批/进程连续性恢复。
            if (IsDeterministicProcessingGap(faultCode)) return false;
            return string.Equals(stage, "DaqSelfMaintenance", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stage, "IsolatedInfrastructureRecovery", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(stage, "IsolatedInfrastructureRecoveryException", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldPublishUnattendedBatchRecycle(
            int attempt,
            string stage,
            string faultCode)
        {
            // 同步存储调用已超过看门狗时限时，本进程无法取消该内核I/O，也不能在
            // 同一批次上并行重写。第一次确认 stall 就请求安全整批/进程交接；
            // 交接仍须满足断能与耐久边界，未满足时保持安全停止。
            if (RequiresImmediateProcessRecycle(faultCode)) return true;
            if (!ShouldEscalateSoftwareRecovery(attempt)) return false;

            // Local means "prefer an in-process repair before the threshold", never
            // "retry forever". V2.12.0.29 combined the old local-stage predicate with the
            // threshold using &&, so DaqCallbackStale could retry hundreds of times without
            // opening the unattended recycle circuit.
            return true;
        }

        internal static bool ShouldEnterSoftwareRecoveryCircuitOpen(
            int attempt,
            string stage,
            string faultCode)
            => ShouldPublishUnattendedBatchRecycle(attempt, stage, faultCode);

        internal static bool MustBlockDaqRecoveryCommitForContinuityGap(bool hasPermanentGap)
        {
            return hasPermanentGap;
        }

        internal static long GetInfrastructureRecoveryAttemptKey(long runEpoch, int hydraulicGroupId)
        {
            return unchecked((runEpoch << 8) | (uint)(hydraulicGroupId & 0xff));
        }

        internal static long GetAffectedGroupResetKey(long runEpoch, int hydraulicGroupId)
        {
            return GetInfrastructureRecoveryAttemptKey(runEpoch, hydraulicGroupId);
        }

        internal static bool IsAffectedGroupResetRunCurrent(
            Guid expectedRunId,
            long expectedRunEpoch,
            Guid currentRunId,
            long currentRunEpoch)
        {
            return expectedRunId != Guid.Empty &&
                   expectedRunId == currentRunId &&
                   expectedRunEpoch == currentRunEpoch;
        }

        private bool IsAffectedGroupResetRunCurrent(Guid expectedRunId, long expectedRunEpoch)
        {
            return IsAffectedGroupResetRunCurrent(
                expectedRunId,
                expectedRunEpoch,
                _activeBatchId,
                Interlocked.Read(ref _runEpoch));
        }

        internal static int[] MergeInfrastructureRecoveryCohort(
            int hydraulicGroupId,
            params IEnumerable<int>[] sources)
        {
            return (sources ?? Array.Empty<IEnumerable<int>>())
                .Where(source => source != null)
                .SelectMany(source => source)
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => GetHydraulicGroupForChannel(channel) == hydraulicGroupId)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        internal static ControlFault CreateSoftwareRecoveryCircuitFault(
            Guid runId,
            IEnumerable<int> affectedChannels,
            string detail,
            Guid correlationId = default)
        {
            if (runId == Guid.Empty)
                throw new ArgumentException("软件恢复熔断必须携带非空 RunId。", nameof(runId));
            var channels = (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            return new ControlFault(
                "SoftwareRecoveryCircuitOpen",
                detail ?? string.Empty,
                FaultScope.Global,
                channels,
                null,
                DateTime.UtcNow,
                correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
                FaultClassification.SystemFault,
                FaultRecoveryPolicy.UnattendedBatchRecycle,
                runId);
        }

        /// <summary>
        ///     Repeated infrastructure recovery failure is a software lifecycle failure, not
        ///     proof that every affected caliper is broken.  Stop the local retry loop and ask
        ///     the unattended coordinator for one complete StopAll/in-process rebuild.  The
        ///     existing coordinator escalates again to a process restart when the in-process
        ///     rebuild cannot establish all safety and durability invariants.
        /// </summary>
        private bool TryEscalateSoftwareRecoveryCircuitOpen(
            string stage,
            string reason,
            IEnumerable<int> affectedChannels,
            Guid runId,
            long runEpoch,
            int attempt,
            string faultCode = null)
        {
            if (runId == Guid.Empty ||
                runId != _activeBatchId ||
                runEpoch != Interlocked.Read(ref _runEpoch))
                return false;

            // Acquisition/communication loss is not proof of a permanent hardware fault.
            // Exhausted local attempts retain operator intent through the batch/Host cooldown path.
            // 可重放的DAQ/外部基础设施抖动不是“所有卡钳都坏”的证据，前两次优先在
            // 受影响组内修复；第3次仍未闭环即说明本进程恢复生命周期失效，必须转入
            // 统一StopAll/整批回收。该升级仍是软件故障，不会伪装成卡钳永久报警。
            // DaqPersistenceWriteStall is already a confirmed, non-cancellable synchronous
            // storage stall.  It must reach the same real circuit-opening branch on attempt 1;
            // gating this method with the generic three-attempt threshold would make the
            // immediate policy helper dead code in production.
            if (!ShouldEnterSoftwareRecoveryCircuitOpen(attempt, stage, faultCode))
            {
                if (attempt == SoftwareRecoveryEscalationAttempts || attempt % 10 == 0)
                    _log.Warn(
                        $"局部基础设施自愈已连续{attempt}次失败，尚未达到有界回收阈值；" +
                        $"Stage={stage} FaultCode={faultCode ?? "Unknown"} " +
                        $"Channels=[{string.Join(",", affectedChannels ?? Array.Empty<int>())}] " +
                        $"Error={reason}",
                        "EPB");
                return false;
            }

            // Another group/Timer may already have opened the same batch circuit.  Returning
            // true is intentional: every local owner must stop retrying once one owner has
            // requested the common batch recycle.
            if (!_softwareRecoveryEscalation.TryOpen(runId))
            {
                var alreadyOpen = _softwareRecoveryEscalation.IsOpen(runId);
                if (alreadyOpen)
                    CompleteDaqContextsForSoftwareCircuit(
                        runId,
                        runEpoch,
                        "SoftwareRecoveryCircuitOpenSafeIdle");
                return alreadyOpen;
            }

            var channels = (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var detail =
                $"Stage={stage ?? "Unknown"} Attempt={attempt} " +
                $"FaultCode={faultCode ?? "Unknown"} " +
                $"Channels=[{string.Join(",", channels)}] Error={reason ?? "Unknown"}";

            var fault = CreateSoftwareRecoveryCircuitFault(runId, channels, detail);
            Dictionary<int, string> rejectedOff = null;
            ExecuteNonBlockingSafetyIsolationOrder(
                () => FreezeAndCancelSafetyChannels(
                    channels,
                    "SoftwareRecoveryCircuitOpen",
                    cancelStopTokens: true),
                () => rejectedOff = SubmitEpbOffHighPriorityBatch(
                    channels,
                    "SoftwareRecoveryCircuitOpenOffAdmissionRejected",
                    "SoftwareRecoveryCircuitOpenOffSubmissionException"),
                null,
                () =>
                {
                    // 整批/进程回收观察者优先于任何逐通道诊断。即使 UI 状态发布
                    // 变慢，恢复协调器也已经拿到明确的 SystemFault 终态。
                    _adaptiveLifecyclePort.PublishSystemFault(
                        fault,
                        runId,
                        runEpoch);
                    foreach (var channel in channels)
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.SystemFault,
                            "SoftwareRecoveryCircuitOpen",
                            "局部软件自愈连续失败，已停止重复自维护并转入整批安全重建。" + detail,
                            affectedChannels: channels,
                            correlationId: runId,
                            runIdOverride: runId);
                    _log.Error(
                        "局部软件自愈达到有界阈值，停止重复自维护并升级为一次整批安全重建。" +
                        detail,
                        "EPB");
                    NonCriticalObserver.Invoke(
                        ControlFaultRaised,
                        fault,
                        ex => _log?.Warn(
                            $"软件恢复熔断观察者异常，已隔离：{ex.Message}",
                            "EPB"));
                },
                () => ScheduleRejectedOffFallbacks(
                    rejectedOff,
                    "SoftwareRecoveryCircuitOpenImmediateOffFallback"));
            // A batch circuit is a single SafeIdle decision.  Close every
            // active DAQ context for this run after SystemFault/SafeIdle has
            // been published; late watchdog ticks then see no recoverable
            // incident and cannot request another process relaunch.
            CompleteDaqContextsForSoftwareCircuit(
                runId,
                runEpoch,
                "SoftwareRecoveryCircuitOpenSafeIdle");
            return true;
        }

        private bool CompleteDaqInfrastructureCircuitAsSafeIdle(
            string stage,
            string reason,
            IEnumerable<int> affectedChannels,
            Guid runId,
            long runEpoch,
            string faultCode)
        {
            var channels = (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var key = $"{runId:N}:{runEpoch}:" + string.Join(",", channels);
            if (!_daqSafeIdleCircuitKeys.TryAdd(key, 0))
            {
                CompleteDaqContextsForSoftwareCircuit(
                    runId,
                    runEpoch,
                    "DaqInfrastructureCircuitAlreadySafeIdle");
                return true;
            }

            Dictionary<int, string> rejected = null;
            var correlationId = Guid.Empty;
            DaqRecoveryTransaction transaction = null;
            DaqRecoveryTerminalSupervisor terminalSupervisor = null;
            try
            {
                lock (_recoveryContractGate)
                {
                    var active = _activeRecoveryContracts.Values
                        .Where(incident => incident?.Contract != null &&
                                           incident.Contract.RunId == runId &&
                                           incident.Contract.RunEpoch == runEpoch &&
                                           (incident.Contract.Channels ?? Array.Empty<int>())
                                               .Intersect(channels)
                                               .Any())
                        .OrderBy(incident => incident.Contract.StartedUtc)
                        .FirstOrDefault();
                    correlationId = active?.Contract.IncidentId ?? Guid.Empty;
                }
                if (correlationId == Guid.Empty)
                    correlationId = runId;

                transaction = new DaqRecoveryTransaction(
                    channels,
                    new DaqRecoveryTransaction.Port
                    {
                        // This is the production DAQ circuit-open path.  It
                        // still uses the real bounded DO worker; the local
                        // phase publisher is intentionally silent because
                        // there is no live DaqAutoRecoveryContext to own a
                        // heartbeat stage.
                        TrySubmitOff = channel =>
                        {
                            Guid commandId;
                            var accepted = TrySubmitEpbOffHighPriority(
                                channel,
                                null,
                                out commandId);
                            return new DaqRecoveryTransaction.OffReceipt(
                                channel,
                                commandId,
                                accepted,
                                false,
                                accepted ? string.Empty : "DaqInfrastructureOffRejected");
                        },
                        PublishPhase = _ => { },
                        // This path's surrounding safety-order helper owns
                        // the fallback submission after all routes have had
                        // their admission attempt.  Do not submit a second
                        // command from the transaction callback.
                        SubmitSafetyOff = _ => { },
                        DisablePower = _ => StartElectricalGroupSafetyDisables(
                            channels,
                            $"DAQ永久失联安全断电 RunId={runId:N}",
                            "DaqInfrastructureCircuitPowerDisable"),
                        PublishTerminal = missing =>
                            (missing ?? Array.Empty<int>()).ToArray(),
                        PublishCheckpoint = version =>
                            new DaqRecoveryTransaction.CheckpointReceipt(
                                version,
                                true,
                                "DaqInfrastructureSafeIdle"),
                        // SafeIdle/cancelled infrastructure incidents still
                        // require an independent terminal aggregate receipt
                        // before their surrounding context/lease is closed.
                        PublishTerminalCheckpoint = version =>
                        {
                            var now = DateTime.UtcNow;
                            var terminal = _recoveryAggregateStore.PublishDaqTerminal(
                                DaqRecoveryPhase.Terminal.ToString(),
                                Math.Max(1, version + 1),
                                now,
                                now,
                                "DaqInfrastructureSafeIdleTerminal",
                                runId,
                                runEpoch);
                            return new DaqRecoveryTransaction.CheckpointReceipt(
                                terminal.Version,
                                terminal.Stable && terminal.Version > version,
                                terminal.Detail);
                        }
                    });
                terminalSupervisor = new DaqRecoveryTerminalSupervisor();

                ExecuteNonBlockingSafetyIsolationOrder(
                    () => FreezeAndCancelSafetyChannels(
                        channels,
                        "DaqInfrastructureCircuitOpen",
                        cancelStopTokens: true),
                    () =>
                    {
                        transaction.SubmitOffBatch();
                        rejected = transaction.OffReceipts
                            .Where(receipt => !receipt.Accepted)
                            .GroupBy(receipt => receipt.Channel)
                            .ToDictionary(
                                group => group.Key,
                                group => group.Last().Failure);
                    },
                    () => transaction.RequestSafeIdle(
                        $"DaqInfrastructureCircuitSafeIdle:{faultCode}"),
                    () =>
                    {
                        // Publish terminal states while the DAQ context and any
                        // legacy contract are still visible.  No SystemFault
                        // callback is emitted from this branch.
                        foreach (var channel in channels)
                        {
                            _adaptiveLifecyclePort.PublishSafeIdle(
                                channel,
                                $"DaqInfrastructureCircuitSafeIdle:{faultCode}",
                                runId,
                                runEpoch);
                            PublishChannelRuntimeState(
                                channel,
                                ChannelRuntimeState.StartBlocked,
                                "DaqInfrastructureCircuitSafeIdle",
                                $"DAQ基础设施恢复熔断，已进入唯一SafeIdle。" +
                                $" Stage={stage}; Code={faultCode}; Reason={reason}",
                                affectedChannels: channels,
                                correlationId: correlationId,
                                allowTerminalReset: true,
                                allowSystemFaultReset: true,
                                runIdOverride: runId);
                        }
                        ScheduleDaqTerminalRetryProduction(
                            terminalSupervisor,
                            () => transaction.TryTerminal(),
                            () => transaction.CurrentOutcome !=
                                  DaqRecoveryTransaction.Outcome.Terminal,
                            () => { },
                            retryReason => _log?.Error(
                                $"DAQ基础设施SafeIdle终态aggregate凭证持续失败；" +
                                $"RunId={runId:N}; Reason={retryReason}",
                                "AI"),
                            "DaqInfrastructureTerminalRetryDeadline;SafeIdleOnly");
                    },
                    () => ScheduleRejectedOffFallbacks(
                        rejected,
                        "DaqInfrastructureCircuitImmediateOffFallback"));
            }
            catch (Exception ex)
            {
                _log?.Error(
                    $"DAQ基础设施SafeIdle安全顺序异常；保持单次安全决策。" +
                    $"RunId={runId:N}; Error={ex.Message}",
                    "AI",
                    ex);
            }
            finally
            {
                CompleteDaqContextsForSoftwareCircuit(
                    runId,
                    runEpoch,
                    "DaqInfrastructureCircuitSafeIdle");
            }
            return true;
        }

        private void CompleteDaqContextsForSoftwareCircuit(
            Guid runId,
            long runEpoch,
            string reason)
        {
            var contexts = _daqAutoRecovery.Values
                .Where(context => context != null &&
                                  context.RunId == runId &&
                                  context.RunEpoch == runEpoch &&
                                  context.Terminal.Current == DaqRecoveryTerminal.None)
                .ToArray();
            foreach (var context in contexts)
                CompleteCancelledRecovery(context, reason);
        }

        private bool TryEscalatePermanentDataContinuityGap(
            string phase,
            IEnumerable<int> affectedChannels,
            Guid runId,
            long runEpoch,
            Guid sourceCorrelationId,
            DaqAutoRecoveryContext recoveryContext = null)
        {
            var channels = (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            // 恢复上下文本身持有权威Device。通道映射可能正在重建或已被故障清空，
            // 不能仅靠反向映射决定是否存在永久gap，否则最终拒绝可能漏过提交门禁。
            var devices = channels
                .Select(channel => _acq.GetDeviceForEpbChannel(channel))
                .Concat(new[] { recoveryContext?.Device })
                .Where(device => !string.IsNullOrWhiteSpace(device))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var gaps = new List<string>();
            foreach (var device in devices)
            {
                if (_acq.TryGetDataContinuityGap(
                        device,
                        out var firstGap,
                        out var lastObserved))
                    gaps.Add(
                        $"{device}:FirstGap={firstGap},LastObserved={lastObserved}," +
                        $"AbandonedTail={Math.Max(0, lastObserved - firstGap + 1)}");
            }

            if (!MustBlockDaqRecoveryCommitForContinuityGap(gaps.Count > 0)) return false;

            // 先提交当前DAQ恢复为Cancelled，永久空洞后绝不能再由Recovered事件或
            // Completer迟到进入Rejoin。恢复事故只进入一次SafeIdle/Terminal；
            // DAQ故障不得升级为整批回收或主进程重启。
            if (recoveryContext != null)
                CompleteCancelledRecovery(recoveryContext, "PermanentDataContinuityGap");
            var detail =
                $"Phase={phase ?? "Unknown"}; SourceCorrelationId={sourceCorrelationId:N}; " +
                $"Channels=[{string.Join(",", channels)}]; {string.Join("; ", gaps)}";
            _log.Error(
                "DAQ已形成不可重放数据空洞，立即禁止同进程恢复并保持SafeIdle。" + detail,
                "AI");
            return true;
        }

        private static int GetHydraulicGroupForChannel(int channel)
        {
            return channel >= 1 && channel <= 6 ? 1 :
                channel >= 7 && channel <= 12 ? 2 : 0;
        }

        internal static int[] SelectInfrastructureRecoveryEligibleChannels(
            IEnumerable<int> channels,
            Func<int, bool> isEnabled,
            Func<int, bool> isAlarmStopped,
            Func<int, bool> isPaused,
            Func<int, ChannelRuntimeState> getRuntimeState)
        {
            return (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => isEnabled?.Invoke(channel) ?? true)
                .Where(channel => !(isAlarmStopped?.Invoke(channel) ?? false))
                .Where(channel => !(isPaused?.Invoke(channel) ?? false))
                .Where(channel =>
                {
                    var state = getRuntimeState?.Invoke(channel) ??
                                ChannelRuntimeState.Recovering;
                    // channels 是在破坏 Timer/Runner 前冻结的恢复事务 cohort。
                    // 运行、内部 Paused 或状态迟到均不能把兄弟通道从事务中删除；
                    // 这里只排除有独立耐久事实的终态，人工暂停另由 isPaused 排除。
                    return state != ChannelRuntimeState.NotEnabled &&
                           state != ChannelRuntimeState.AlarmStopped &&
                           state != ChannelRuntimeState.InterlockStopped &&
                           state != ChannelRuntimeState.ManualStopped &&
                           state != ChannelRuntimeState.Completed &&
                           state != ChannelRuntimeState.StartBlocked;
                })
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        private void ScheduleIsolatedInfrastructureRecovery(
            IEnumerable<int> affectedChannels,
            string reason,
            Guid correlationId,
            string faultCode = null,
            long recoveryStartedTicks = 0)
        {
            if (recoveryStartedTicks <= 0)
                recoveryStartedTicks = Stopwatch.GetTimestamp();
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            // 故障码必须在第一次调度时冻结并跨异常/重登记原样传递；若只保留人类可读
            // reason，QueueFull/Worker/RawPermanent 会在第3次仍被误判为可无限局部抖动。
            var frozenFaultCode = string.IsNullOrWhiteSpace(faultCode) ? reason : faultCode;
            var sessionToken = _batchSessionCts?.Token ?? CancellationToken.None;
            foreach (var group in (affectedChannels ?? Array.Empty<int>())
                         .Where(channel => channel >= 1 && channel <= 12)
                         .Distinct()
                         .GroupBy(GetHydraulicGroupForChannel))
            {
                var hydraulicGroupId = group.Key;
                var recoveryKey = GetInfrastructureRecoveryAttemptKey(runEpoch, hydraulicGroupId);
                if (hydraulicGroupId <= 0 ||
                    !_isolatedInfrastructureRecoveryScheduled.TryAdd(recoveryKey, 0))
                    continue;
                // 在首轮破坏 timer/runner/participant 运行对象之前冻结完整同组 cohort。
                // 后续重试必须继续携带兄弟通道，不能从已被清场的实时字典重新推导。
                var requested = MergeInfrastructureRecoveryCohort(
                        hydraulicGroupId,
                        group,
                        _timers.Keys,
                        _runners.Keys,
                        _hydraulicParticipants.Keys)
                    .Where(IsChannelEnabled)
                    .Where(channel => !IsAlarmStopRequested(channel))
                    .Where(channel => !_channelPausedUtc.ContainsKey(channel))
                    .Where(channel => !_manualStopRequestedChannels.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                if (requested.Length == 0)
                {
                    _isolatedInfrastructureRecoveryScheduled.TryRemove(
                        recoveryKey,
                        out _);
                    continue;
                }
                var attemptKey = recoveryKey;
                var recoveryCorrelationId = correlationId == Guid.Empty
                    ? Guid.NewGuid()
                    : correlationId;
                RecoveryIncidentHandle recoveryIncident = null;
                Func<Task> BuildRecoveryWorker()
                {
                    return async () =>
                    {
                        var reschedule = false;
                        try
                        {
                            while (runEpoch == Interlocked.Read(ref _runEpoch) &&
                                   (runId == Guid.Empty || runId == _activeBatchId))
                            {
                                var eligible = SelectInfrastructureRecoveryEligibleChannels(
                                    requested,
                                    IsChannelEnabled,
                                    IsAlarmStopRequested,
                                    channel => _channelPausedUtc.ContainsKey(channel) ||
                                               _manualStopRequestedChannels.ContainsKey(channel),
                                    channel => _channelRuntimeStateStore.Get(channel)?.State ??
                                               ChannelRuntimeState.NotEnabled);
                                if (eligible.Length == 0) return;

                                if (IsSoftwareRecoveryHardDeadlineElapsed(
                                        recoveryStartedTicks,
                                        Stopwatch.GetTimestamp(),
                                        Stopwatch.Frequency))
                                {
                                    var deadlineAttempt = _isolatedInfrastructureRecoveryAttempts
                                        .TryGetValue(attemptKey, out var currentAttempt)
                                        ? currentAttempt
                                        : 0;
                                    TryEscalateSoftwareRecoveryCircuitOpen(
                                        "IsolatedInfrastructureRecoveryHardDeadline",
                                        $"Reason={reason}; Hydraulic={hydraulicGroupId}; " +
                                        $"HardDeadlineMs={RecoveryGroupHardDeadlineMs}",
                                        eligible,
                                        runId,
                                        runEpoch,
                                        Math.Max(SoftwareRecoveryEscalationAttempts, deadlineAttempt),
                                        frozenFaultCode);
                                    return;
                                }

                                var attempt = _isolatedInfrastructureRecoveryAttempts.AddOrUpdate(
                                    attemptKey,
                                    1,
                                    (_, current) => current + 1);
                                await Task.Delay(
                                        SelectTimerRecoveryRetryDelayMs(attempt),
                                        sessionToken)
                                    .ConfigureAwait(false);
                                if (IsBatchSessionActive &&
                                    CurrentBatchPauseState != BatchPauseState.Running)
                                    continue;

                                await ExecuteAffectedGroupResetAsync(
                                        eligible,
                                        $"InfrastructureSelfHealing:{reason}:Attempt={attempt}",
                                        recoveryCorrelationId,
                                        runId,
                                        runEpoch,
                                        RecoveryTargetPhase.Formal)
                                    .ConfigureAwait(false);

                                var stillRecovering = eligible.Any(channel =>
                                {
                                    var runtime = _channelRuntimeStateStore.Get(channel);
                                    return runtime != null &&
                                           (runtime.State == ChannelRuntimeState.Recovering ||
                                            runtime.State == ChannelRuntimeState.SystemFault);
                                });
                                if (!stillRecovering)
                                {
                                    _isolatedInfrastructureRecoveryAttempts.TryRemove(
                                        attemptKey,
                                        out _);
                                    return;
                                }

                                if (TryEscalateSoftwareRecoveryCircuitOpen(
                                        "IsolatedInfrastructureRecovery",
                                        $"Reason={reason}; Hydraulic={hydraulicGroupId}",
                                        eligible,
                                        runId,
                                        runEpoch,
                                        attempt,
                                        frozenFaultCode))
                                    return;
                            }
                        }
                        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
                        {
                            reschedule = false;
                        }
                        catch (Exception ex)
                        {
                            var runStillCurrent = runEpoch == Interlocked.Read(ref _runEpoch) &&
                                                  runId != Guid.Empty &&
                                                  runId == _activeBatchId;
                            var attempt = _isolatedInfrastructureRecoveryAttempts.TryGetValue(
                                attemptKey,
                                out var currentAttempt)
                                ? currentAttempt
                                : 1;
                            var eligible = SelectInfrastructureRecoveryEligibleChannels(
                                requested,
                                IsChannelEnabled,
                                IsAlarmStopRequested,
                                channel => _channelPausedUtc.ContainsKey(channel) ||
                                           _manualStopRequestedChannels.ContainsKey(channel),
                                channel => _channelRuntimeStateStore.Get(channel)?.State ??
                                           ChannelRuntimeState.NotEnabled);
                            if (runStillCurrent &&
                                TryEscalateSoftwareRecoveryCircuitOpen(
                                    "IsolatedInfrastructureRecoveryException",
                                    $"Reason={reason}; Hydraulic={hydraulicGroupId}; Error={ex.Message}",
                                    eligible.Length > 0 ? eligible : requested,
                                    runId,
                                    runEpoch,
                                    attempt,
                                    frozenFaultCode))
                            {
                                reschedule = false;
                            }
                            else
                            {
                                reschedule = runStillCurrent;
                                _log.Warn(
                                    $"外部设备自恢复调度第{attempt}次异常，将重新登记有界退避重试；" +
                                    $"健康组继续运行。Hydraulic={hydraulicGroupId} " +
                                    $"Reason={reason} Error={ex.Message}",
                                    "液压协调");
                            }
                        }
                        finally
                        {
                            // 只释放当前 run 的精确调度身份；旧任务不得删除新 run 的锁。
                            _isolatedInfrastructureRecoveryScheduled.TryRemove(recoveryKey, out _);
                            if (reschedule)
                                ScheduleIsolatedInfrastructureRecovery(
                                    requested,
                                    reason,
                                    recoveryCorrelationId,
                                    frozenFaultCode,
                                    recoveryStartedTicks);
                            recoveryIncident?.CompleteAfterTerminal(contract =>
                                CommitRecoveryIncidentStateForRelease(
                                    contract,
                                    "IsolatedInfrastructureRecoveryTerminal",
                                    "受影响组恢复执行体已结束；已完成终态复核。"));
                        }
                    };
                }

                var started = TryBeginRecoveryIncident(
                    "IsolatedInfrastructureRecovery",
                    runId,
                    runEpoch,
                    RecoveryOwnerKind.AffectedGroupRecovery,
                    RecoveryTargetPhase.Formal,
                    recoveryCorrelationId,
                    requested,
                    _ => BuildRecoveryWorker(),
                    contract =>
                    {
                        foreach (var channel in contract.Channels ?? Array.Empty<int>())
                            PublishRecoveryIncidentState(
                                channel,
                                ChannelRuntimeState.Recovering,
                                string.IsNullOrWhiteSpace(frozenFaultCode)
                                    ? "IsolatedInfrastructureRecovery"
                                    : frozenFaultCode,
                                reason ?? "受影响组恢复已建立真实执行任务。",
                                affectedChannels: contract.Channels,
                                correlationId: contract.IncidentId,
                                recoveryOwnerKind: contract.OwnerKind,
                                recoveryTargetPhase: contract.TargetPhase,
                                recoveryOwnerId: contract.OwnerId,
                                recoveryOwnerGeneration: contract.RunEpoch);
                    },
                    out recoveryIncident);
                if (!started || recoveryIncident == null)
                {
                    _isolatedInfrastructureRecoveryScheduled.TryRemove(
                        recoveryKey,
                        out _);
                    continue;
                }
                try
                {
                    _taskSupervisor.Observe(
                        recoveryIncident.WorkerTask,
                        "IsolatedInfrastructureRecovery",
                        _activeBatchId,
                        requested.FirstOrDefault());
                    if (!recoveryIncident.Start())
                        recoveryIncident.CompleteAfterTerminal(contract =>
                            PublishRecoverySafeTerminal(
                                contract,
                                "IsolatedInfrastructureRecoveryStartRejected",
                                "受影响组恢复启动许可被拒绝，已保持安全终态。"));
                }
                catch (Exception observeError)
                {
                    recoveryIncident.CompleteAfterTerminal(contract =>
                    {
                        try { CommandEpbOffHighPriority(contract.Channels.FirstOrDefault(), "IsolatedInfrastructureRecoveryObserveFailed"); }
                        catch { }
                        PublishRecoverySafeTerminal(
                            contract,
                            "IsolatedInfrastructureRecoveryObserveFailed",
                            $"受影响组恢复任务登记失败，已保持安全终态：{observeError.Message}");
                    });
                    _isolatedInfrastructureRecoveryScheduled.TryRemove(
                        recoveryKey,
                        out _);
                }
            }
        }

        private async Task<HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease[]>
            AcquireRecoveryOwnershipsAsync(
                string ownerId,
                RecoveryOwnerPriority priority,
                IEnumerable<int> channels,
                CancellationToken token,
                int takeoverTimeoutMs = RecoveryOwnershipTakeoverTimeoutMs)
        {
            var leases = new List<HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease>();
            try
            {
                foreach (var groupId in (channels ?? Array.Empty<int>())
                             .Select(GetHydraulicGroupForChannel)
                             .Where(groupId => groupId > 0)
                             .Distinct()
                             .OrderBy(groupId => groupId))
                {
                    leases.Add(await _recoveryOwnership.AcquireAsync(
                            groupId,
                            ownerId,
                            priority,
                            takeoverTimeoutMs,
                            token)
                        .ConfigureAwait(false));
                }
                return leases.ToArray();
            }
            catch
            {
                foreach (var lease in leases) lease.Dispose();
                throw;
            }
        }

        private async Task AcquireDaqRecoveryOwnershipsAsync(DaqAutoRecoveryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            context.ValidationDetail = "RecoveryOwnership";
            var batchCorrelation = ResolveDaqRecoveryBatchCorrelation(
                context.CorrelationId,
                _daqRecoveryBatchAliases);
            var leases = await AcquireRecoveryOwnershipsAsync(
                    $"DAQ_BATCH:{batchCorrelation:N}",
                    RecoveryOwnerPriority.Daq,
                    context.AffectedChannels,
                    context.Cancellation.Token)
                .ConfigureAwait(false);
            context.Ownerships = leases;
            if (Volatile.Read(ref context.OwnershipReleased) != 0)
            {
                foreach (var lease in leases) lease.Dispose();
                return;
            }
            context.OwnershipCancellationRegistrations = leases
                .Select(lease => lease.Token.Register(() =>
                {
                    try { context.Cancellation.Cancel(); }
                    catch { }
                    // 所有权抢占必须有一个确定的退出提交点。仅取消令牌不够：
                    // 自维护任务可能正处在下一次定时重试之前，没有活动 await 负责释放 lease。
                    ObserveBackgroundTask(
                        Task.Run(() => CompleteCancelledRecovery(
                            context,
                            "RecoveryOwnershipPreempted")),
                        "DaqRecoveryOwnershipPreempted");
                }))
                .ToArray();
        }

        internal static Guid ResolveDaqRecoveryBatchCorrelation(
            Guid individualCorrelation,
            IReadOnlyDictionary<Guid, Guid> aliases)
        {
            if (individualCorrelation == Guid.Empty) return Guid.Empty;
            return aliases != null &&
                   aliases.TryGetValue(individualCorrelation, out var batchCorrelation) &&
                   batchCorrelation != Guid.Empty
                ? batchCorrelation
                : individualCorrelation;
        }

        private void ReleaseDaqRecoveryOwnerships(DaqAutoRecoveryContext context)
        {
            if (context == null || Interlocked.Exchange(ref context.OwnershipReleased, 1) != 0)
                return;
            foreach (var registration in context.OwnershipCancellationRegistrations ??
                         Array.Empty<CancellationTokenRegistration>())
            {
                try { registration.Dispose(); }
                catch { }
            }
            foreach (var lease in context.Ownerships ??
                         Array.Empty<HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease>())
            {
                try { lease.Dispose(); }
                catch { }
            }
        }

        private void StartAffectedGroupRecoveryDeadline(DaqAutoRecoveryContext context)
        {
            if (context == null) return;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(RecoveryGroupHardDeadlineMs, context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;

                    var channels = (context.PreviouslyRunningChannels ?? context.AffectedChannels ??
                                    Array.Empty<int>())
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray();
                    _log.Error(
                        $"DAQ恢复超过{RecoveryGroupHardDeadlineMs}ms未提交，" +
                        $"停止局部重试并仅进入一次SafeIdle/Terminal。Device={context.Device} " +
                        $"Channels=[{string.Join(",", channels)}] " +
                        $"Phase={context.ValidationPhase} CorrelationId={context.CorrelationId:N}",
                        "AI");

                    CompleteCancelledRecovery(
                        context,
                        "DaqRecoveryHardDeadlineSafeIdle");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.Error(
                        $"DAQ受影响组自动清场失败 Device={context.Device}: {ex.Message}",
                        "AI",
                        ex);
                    PublishIsolatedSoftwareFault(
                        "AffectedGroupResetFailed",
                        ex.Message,
                        context.AffectedChannels,
                        context.CorrelationId);
                }
            }), "AffectedGroupRecoveryDeadline");
        }

        private async Task ExecuteAffectedGroupResetAsync(
            int[] requestedChannels,
            string reason,
            Guid correlationId,
            Guid expectedRunId,
            long expectedRunEpoch,
            RecoveryTargetPhase targetPhase)
        {
            var requested = (requestedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var runStillCurrent = IsAffectedGroupResetRunCurrent(
                expectedRunId,
                expectedRunEpoch);
            var ownersStillCurrent = requested.Length > 0 && requested.All(channel =>
            {
                var state = _channelRuntimeStateStore.Get(channel);
                return state != null &&
                       state.State == ChannelRuntimeState.Recovering &&
                       state.RunId == expectedRunId &&
                       state.RunEpoch == expectedRunEpoch &&
                       state.RecoveryTargetPhase == RecoveryTargetPhase.Formal &&
                       RecoveryOwnershipPolicy.IsOwnerCurrent(state) &&
                       state.RecoveryOwnerGeneration == expectedRunEpoch;
            });
            if (!CanExecuteAffectedGroupReset(
                    targetPhase,
                    IsFormalPhaseCommitted,
                    runStillCurrent,
                    ownersStillCurrent))
            {
                _log?.Error(
                    $"AffectedGroupResetRejected Target={targetPhase} " +
                    $"FormalCommitted={IsFormalPhaseCommitted} Reason={reason} " +
                    $"RunCurrent={runStillCurrent} OwnerCurrent={ownersStillCurrent} " +
                    $"Run={expectedRunId:N}/{expectedRunEpoch}; " +
                    "入口零副作用拒绝，交回原阶段owner处理。",
                    "液压协调");
                return;
            }
            foreach (var requestedGroup in requested.GroupBy(GetHydraulicGroupForChannel))
            {
                var hydraulicGroupId = requestedGroup.Key;
                var resetKey = GetAffectedGroupResetKey(expectedRunEpoch, hydraulicGroupId);
                if (hydraulicGroupId <= 0 ||
                    !IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                    !_affectedGroupResetInProgress.TryAdd(resetKey, 0))
                    continue;

                // Stop→Start 等价清场的边界是共享液压组，而不是最初上报故障的单通道。
                // 将同组仍有运行对象/成员资格的兄弟通道一并清场，避免旧 lease 或旧代次
                // 在恢复通道重新加入后继续污染下一槽。
                var channels = requestedGroup
                    .Concat(_timers.Keys)
                    .Concat(_runners.Keys)
                    .Concat(_hydraulicParticipants.Keys)
                    .Where(channel => GetHydraulicGroupForChannel(channel) == hydraulicGroupId)
                    .Where(IsChannelEnabled)
                    .Where(channel => !IsAlarmStopRequested(channel))
                    .Where(channel => !_channelPausedUtc.ContainsKey(channel))
                    .Where(channel => !_manualStopRequestedChannels.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                if (channels.Length == 0)
                {
                    _affectedGroupResetInProgress.TryRemove(resetKey, out _);
                    continue;
                }
                var executionPermits = channels.ToDictionary(
                    channel => channel,
                    channel => _channelExecutionFence.Capture(channel));
                Func<bool> executionPermitsCurrent = () => executionPermits.All(pair =>
                    IsChannelExecutionPermitCurrent(pair.Key, pair.Value));
                HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease lease = null;
                try
                {
                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                        !executionPermitsCurrent())
                        return;
                    using var executionFenceCts = CancellationTokenSource.CreateLinkedTokenSource(
                        executionPermits.Values
                            .Select(permit => permit.RevocationToken)
                            .ToArray());
                    lease = await _recoveryOwnership.AcquireAsync(
                            hydraulicGroupId,
                            $"GROUP-RESET:{hydraulicGroupId}:{correlationId:N}",
                            RecoveryOwnerPriority.AffectedGroupReset,
                            RecoveryOwnershipTakeoverTimeoutMs,
                            executionFenceCts.Token)
                        .ConfigureAwait(false);
                    using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(
                        executionFenceCts.Token,
                        lease.Token);

                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                        !executionPermitsCurrent())
                        return;
                    if (TryEscalatePermanentDataContinuityGap(
                            "AffectedGroupResetEntry",
                            channels,
                            expectedRunId,
                            expectedRunEpoch,
                            correlationId))
                        return;

                    var cutoffUtc = DateTime.UtcNow;
                    var cutoffCycles = CaptureSoftwareRecoveryCycles(channels);
                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                        !executionPermitsCurrent())
                        return;
                    if (!TrySealSoftwareRecoveryCycleWindows(
                            cutoffCycles,
                            cutoffUtc,
                            reason,
                            () => IsAffectedGroupResetRunCurrent(
                                      expectedRunId,
                                      expectedRunEpoch) &&
                                  executionPermitsCurrent()))
                    {
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        throw new SoftwareSelfHealingRetryException(
                            "受影响组截止时间窗封闭失败；保持断能并持续清场重试。");
                    }

                    // 先完成与人工Stop相同的内存清场和安全断电。此时即使后续预检失败，
                    // 通道也已经处于明确隔离态，不会继续显示一个永不结束的旧恢复。
                    foreach (var channel in channels)
                    {
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                            !executionPermitsCurrent())
                            return;
                            PublishRecoveryIncidentState(
                                channel,
                                ChannelRuntimeState.Recovering,
                                "AffectedGroupReset",
                            "恢复超过硬期限，正在执行受影响组Stop→Start等价清场。",
                            affectedChannels: channels,
                            correlationId: correlationId,
                            allowTerminalReset: false,
                            allowSystemFaultReset: true,
                            recoveryOwnerKind: RecoveryOwnerKind.AffectedGroupRecovery,
                            recoveryTargetPhase: RecoveryTargetPhase.Formal,
                            recoveryOwnerId: correlationId,
                            recoveryOwnerGeneration: expectedRunEpoch);
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        try { CancelCyclePauseCts(channel); } catch { }
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        try { CancelStopCts(channel); } catch { }
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        RemoveTimerRuntime(channel, "AffectedGroupReset");
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        RemoveRunnerRuntime(channel, "AffectedGroupReset");
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        UnmarkHydraulicParticipant(channel);
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        try { CommandEpbOffHighPriority(channel, "AffectedGroupReset"); } catch { }
                    }

                    // 先完成电源断能，再恢复“只读采样能力”。压力安全门不能使用陈旧值，
                    // 但也不能要求一个已停止的DAQ先提供新鲜压力才允许修复DAQ。
                    if (_powerSupply != null)
                    {
                        foreach (var electricalGroupId in channels
                                     .Select(GetElectricalGroupId)
                                     .Where(id => id > 0)
                                     .Distinct())
                        {
                            if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                            await RecoveryStageDeadline.RunAsync(
                                    "AffectedGroupPowerDisable",
                                    RecoveryStageTimeoutMs,
                                    ct => _powerSupply.DisableGroupAsync(
                                        electricalGroupId,
                                        "AffectedGroupPressureSensingRearm:" + reason,
                                        ct),
                                    recoveryCts.Token)
                                .ConfigureAwait(false);
                            if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        }
                    }

                    var devices = channels
                        .Select(channel => _acq.GetDeviceForEpbChannel(channel))
                        .Where(device => !string.IsNullOrWhiteSpace(device))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    foreach (var device in devices)
                    {
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        IO.NI.DaqRecoveryResult[] ready = null;
                        await RecoveryStageDeadline.RunAsync(
                                "AffectedGroupPressureSensingRearm",
                                RecoveryStageTimeoutMs,
                                async ct =>
                                {
                                    ready = await _acq.EnsureChannelsReadyAsync(
                                            channels.Where(channel => string.Equals(
                                                    _acq.GetDeviceForEpbChannel(channel),
                                                    device,
                                                    StringComparison.OrdinalIgnoreCase))
                                                .ToArray(),
                                            RecoveryStageTimeoutMs,
                                            _daqPersistenceRequiredFreshBatches,
                                            (int)_daqPersistenceResumeAgeMs,
                                            ct)
                                        .ConfigureAwait(false);
                                },
                                recoveryCts.Token)
                            .ConfigureAwait(false);
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        if (ready == null || ready.Any(status => !status.Recovered))
                            throw new InvalidOperationException(
                                $"AffectedGroupPressureSensingRearmFailed Device={device}");
                    }

                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                    await RecoveryStageDeadline.RunAsync(
                            "AffectedGroupForceRelease",
                            RecoveryMechanicalReleaseTimeoutMs,
                            _ => _hydCoordinator.ForceReleaseAsync(
                                hydraulicGroupId,
                                "AffectedGroupReset:" + reason),
                            recoveryCts.Token)
                        .ConfigureAwait(false);
                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;

                    if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                            cutoffCycles,
                            cutoffUtc,
                            reason,
                            _daqPersistenceRecoveryTimeoutMs,
                            recoveryCts.Token,
                            () => IsAffectedGroupResetRunCurrent(
                                      expectedRunId,
                                      expectedRunEpoch) &&
                                  executionPermitsCurrent())
                        .ConfigureAwait(false))
                        throw new SoftwareSelfHealingRetryException(
                            "受影响组 Raw/耐久边界尚未闭合；保持断能并持续清场重试。");

                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                    if (TryEscalatePermanentDataContinuityGap(
                            "AffectedGroupResetBeforeRejoin",
                            channels,
                            expectedRunId,
                            expectedRunEpoch,
                            correlationId))
                        return;
                    foreach (var device in devices)
                    {
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        _persistence.AcceptGeneration(device, _acq.GetCurrentGeneration(device));
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch)) return;
                        _persistence.ResumeAdmission(
                            device,
                            _acq.GetLastAcceptedSequence(device));
                    }

                    if (_powerSupply != null)
                    {
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                            !executionPermitsCurrent())
                            return;
                        await RecoveryStageDeadline.RunAsync(
                                "AffectedGroupPowerEnable",
                                RecoveryStageTimeoutMs,
                                ct => _powerSupply.PrepareAndEnableAsync(channels, ct),
                                recoveryCts.Token)
                            .ConfigureAwait(false);
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                            !executionPermitsCurrent())
                            return;
                    }

                    var plan = GetCompatibleStaggerPlan(channels);
                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                        !executionPermitsCurrent())
                        return;
                    await RecoveryStageDeadline.RunAsync(
                            "AffectedGroupMechanicalRelease",
                            RecoveryMechanicalReleaseTimeoutMs,
                            ct => EnsureMotorReleasedBeforeFormalRejoinAsync(
                                channels,
                                plan,
                                reason,
                                ct),
                            recoveryCts.Token)
                        .ConfigureAwait(false);

                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                        !executionPermitsCurrent())
                        return;
                    ResetTransientFaultStateForRestart(channels, "AffectedGroupResetRejoin");
                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                        !executionPermitsCurrent())
                        return;
                    RejoinFormalChannelsAtSharedFutureSlot(
                        channels,
                        plan,
                        "AffectedGroupResetRecovered",
                        "受影响组已完成Stop→Start等价清场并从未来完整槽重新加入",
                        allowTerminalReset: false,
                        allowSystemFaultReset: true);
                    _log.Info(
                        $"受影响液压组{hydraulicGroupId}自动清场完成，" +
                        $"Channels=[{string.Join(",", channels)}] Reason={reason}",
                        "液压协调");
                }
                catch (Exception ex)
                {
                    if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                        !executionPermitsCurrent())
                    {
                        _log.Info(
                            $"忽略旧运行受影响组清场的迟到异常。" +
                            $"ExpectedRun={expectedRunId:N}/{expectedRunEpoch} " +
                            $"CurrentRun={_activeBatchId:N}/{Interlocked.Read(ref _runEpoch)} " +
                            $"Hydraulic={hydraulicGroupId} Error={ex.Message}",
                            "液压协调");
                        continue;
                    }
                    foreach (var channel in channels)
                    {
                        if (!IsAffectedGroupResetRunCurrent(expectedRunId, expectedRunEpoch) ||
                            !executionPermitsCurrent())
                            break;
                        try { CommandEpbOffHighPriority(channel, "AffectedGroupResetFailed"); }
                        catch { }
                    }
                    PublishIsolatedSoftwareFault(
                        "AffectedGroupResetFailed",
                        $"Hydraulic={hydraulicGroupId} Reason={reason} Error={ex.Message}",
                        channels,
                        correlationId);
                }
                finally
                {
                    lease?.Dispose();
                    _affectedGroupResetInProgress.TryRemove(resetKey, out _);
                }
            }
        }

        internal static bool CanExecuteAffectedGroupReset(
            RecoveryTargetPhase targetPhase,
            bool formalPhaseCommitted)
        {
            return CanExecuteAffectedGroupReset(
                targetPhase,
                formalPhaseCommitted,
                runStillCurrent: true,
                ownerGenerationCurrent: true);
        }

        internal static bool CanExecuteAffectedGroupReset(
            RecoveryTargetPhase targetPhase,
            bool formalPhaseCommitted,
            bool runStillCurrent,
            bool ownerGenerationCurrent)
        {
            return targetPhase == RecoveryTargetPhase.Formal &&
                   formalPhaseCommitted &&
                   runStillCurrent &&
                   ownerGenerationCurrent;
        }

        private async Task ExecuteAffectedGroupResetIncidentAsync(
            int[] requestedChannels,
            string reason,
            Guid correlationId,
            Guid expectedRunId,
            long expectedRunEpoch,
            RecoveryTargetPhase targetPhase)
        {
            var channels = (requestedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (channels.Length == 0 || expectedRunId == Guid.Empty ||
                expectedRunEpoch <= 0 ||
                expectedRunId != _activeBatchId ||
                expectedRunEpoch != Interlocked.Read(ref _runEpoch))
                return;
            var ownerId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId;
            RecoveryIncidentHandle incident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                    try
                    {
                        await ExecuteAffectedGroupResetAsync(
                                channels,
                                reason,
                                ownerId,
                                expectedRunId,
                                expectedRunEpoch,
                                targetPhase)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        incident?.CompleteAfterTerminal(contract =>
                            CommitRecoveryIncidentStateForRelease(
                                contract,
                                "AffectedGroupResetTerminal",
                                "受影响组清场执行体已结束；已完成终态复核。"));
                    }
                };
            }

            var started = TryBeginRecoveryIncident(
                "AffectedGroupReset",
                expectedRunId,
                expectedRunEpoch,
                RecoveryOwnerKind.AffectedGroupRecovery,
                targetPhase,
                ownerId,
                channels,
                _ => BuildRecoveryWorker(),
                contract =>
                {
                    foreach (var channel in contract.Channels ?? Array.Empty<int>())
                        PublishRecoveryIncidentState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "AffectedGroupReset",
                            reason ?? "受影响组清场已建立真实执行任务。",
                            affectedChannels: contract.Channels,
                            correlationId: contract.IncidentId,
                            allowSystemFaultReset: true,
                            recoveryOwnerKind: contract.OwnerKind,
                            recoveryTargetPhase: contract.TargetPhase,
                            recoveryOwnerId: contract.OwnerId,
                            recoveryOwnerGeneration: contract.RunEpoch);
                },
                out incident);
            if (!started || incident == null) return;
            try
            {
                _taskSupervisor.Observe(
                    incident.WorkerTask,
                    "AffectedGroupReset",
                    _activeBatchId,
                    channels.FirstOrDefault());
                if (!incident.Start())
                {
                    incident.CompleteAfterTerminal(contract =>
                        PublishRecoverySafeTerminal(
                            contract,
                            "AffectedGroupResetStartRejected",
                            "受影响组清场启动许可被拒绝，已保持安全终态。"));
                    return;
                }
                await incident.WorkerTask.ConfigureAwait(false);
            }
            catch
            {
                incident?.CompleteAfterTerminal(contract =>
                    CommitRecoveryIncidentStateForRelease(
                        contract,
                        "AffectedGroupResetWorkerFailed",
                        "受影响组清场执行体异常，已完成终态复核。"));
                throw;
            }
        }

        private async Task HandleActiveCycleDataLimitExceededAsync(
            DaqPersistenceStateChanged update)
        {
            if (update == null) return;
            var channel = update.EpbId;
            if (channel < 1 || channel > 12)
            {
                await HandleActiveCycleDataLimitExceededBodyAsync(update)
                    .ConfigureAwait(false);
                return;
            }
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var recoveryCorrelation = update.CorrelationId == Guid.Empty
                ? Guid.NewGuid()
                : update.CorrelationId;
            var lifecycleKey = ((long)channel << 32) |
                               (uint)update.CycleNumber;
            if (_activeCycleLimitRecoveries.ContainsKey(lifecycleKey)) return;

            RecoveryIncidentHandle incident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                    try
                    {
                        await HandleActiveCycleDataLimitExceededBodyAsync(
                                update,
                                recoveryCorrelation)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        incident?.CompleteAfterTerminal(contract =>
                            CommitRecoveryIncidentStateForRelease(
                                contract,
                                "ActiveCycleLimitRecoveryTerminal",
                                "活动圈上限恢复执行体已结束；已完成终态复核。"));
                    }
                };
            }

            var started = TryBeginRecoveryIncident(
                "ActiveCycleDataLimitRecovery",
                runId,
                runEpoch,
                RecoveryOwnerKind.HydraulicGroupRecovery,
                RecoveryTargetPhase.Formal,
                recoveryCorrelation,
                new[] { channel },
                _ => BuildRecoveryWorker(),
                contract =>
                {
                    foreach (var affectedChannel in contract.Channels ?? Array.Empty<int>())
                        PublishRecoveryIncidentState(
                            affectedChannel,
                            ChannelRuntimeState.Recovering,
                            "ActiveCycleDataLimitExceeded",
                            $"活动圈数据达到上限，正在作废 EPB={affectedChannel} " +
                            $"Cycle={update.CycleNumber} Limit={update.RecordLimit}并重置液压代次。",
                            affectedChannels: contract.Channels,
                            correlationId: contract.IncidentId,
                            recoveryOwnerKind: contract.OwnerKind,
                            recoveryTargetPhase: contract.TargetPhase,
                            recoveryOwnerId: contract.OwnerId,
                            recoveryOwnerGeneration: contract.RunEpoch);
                },
                out incident);
            if (!started || incident == null) return;
            try
            {
                _taskSupervisor.Observe(
                    incident.WorkerTask,
                    "ActiveCycleDataLimitRecovery",
                    _activeBatchId,
                    channel);
                if (!incident.Start())
                {
                    incident.CompleteAfterTerminal(contract =>
                        PublishRecoverySafeTerminal(
                            contract,
                            "ActiveCycleLimitRecoveryStartRejected",
                            "活动圈上限恢复启动许可被拒绝，已保持安全终态。"));
                    return;
                }
                await incident.WorkerTask.ConfigureAwait(false);
            }
            catch
            {
                incident?.CompleteAfterTerminal(contract =>
                    CommitRecoveryIncidentStateForRelease(
                        contract,
                        "ActiveCycleLimitRecoveryWorkerFailed",
                        "活动圈上限恢复执行体异常，已完成终态复核。"));
                throw;
            }
        }

        private async Task HandleActiveCycleDataLimitExceededBodyAsync(
            DaqPersistenceStateChanged update,
            Guid recoveryCorrelation = default)
        {
            if (update == null) return;
            var channel = update.EpbId;
            if (channel < 1 || channel > 12)
            {
                PublishIsolatedSoftwareFault(
                    "ActiveCycleIdentityMissing",
                    $"活动圈达到上限但缺少有效EPB标识。Device={update.Device} " +
                    $"Cycle={update.CycleNumber} Limit={update.RecordLimit}",
                    GetDaqGroupChannels(update.Device),
                    recoveryCorrelation == Guid.Empty
                        ? update.CorrelationId
                        : recoveryCorrelation);
                return;
            }

            var expectedRunEpoch = Interlocked.Read(ref _runEpoch);
            if (recoveryCorrelation == Guid.Empty)
                recoveryCorrelation = update.CorrelationId == Guid.Empty
                    ? Guid.NewGuid()
                    : update.CorrelationId;
            var lifecycleKey = ((long)channel << 32) |
                               (uint)update.CycleNumber;
            if (!_activeCycleLimitRecoveries.TryAdd(lifecycleKey, 0)) return;

            var hydraulicGroupId = GetHydraulicGroupForChannel(channel);
            HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease lease = null;
            try
            {
                lease = await _recoveryOwnership.AcquireAsync(
                        hydraulicGroupId,
                        $"ACTIVE-CYCLE:{channel}:{update.CycleNumber}:{update.CorrelationId:N}",
                        RecoveryOwnerPriority.Hydraulic,
                        RecoveryOwnershipTakeoverTimeoutMs,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (expectedRunEpoch != Interlocked.Read(ref _runEpoch)) return;
                if (!_currentCycleNumberByChannel.TryGetValue(channel, out var currentCycle) ||
                    currentCycle != update.CycleNumber)
                {
                    _log.Warn(
                        $"忽略已过期的活动圈上限事件。EPB={channel} " +
                        $"EventCycle={update.CycleNumber} CurrentCycle={currentCycle} " +
                        $"Limit={update.RecordLimit}",
                        "落盘");
                    return;
                }
                    PublishRecoveryIncidentState(
                        channel,
                        ChannelRuntimeState.Recovering,
                        "ActiveCycleDataLimitExceeded",
                    $"活动圈数据达到上限，正在作废 EPB={channel} " +
                    $"Cycle={update.CycleNumber} Limit={update.RecordLimit} 并重置液压代次。",
                    affectedChannels: new[] { channel },
                    correlationId: recoveryCorrelation,
                    recoveryOwnerKind: RecoveryOwnerKind.HydraulicGroupRecovery,
                    recoveryTargetPhase: RecoveryTargetPhase.Formal,
                    recoveryOwnerId: recoveryCorrelation,
                    recoveryOwnerGeneration: expectedRunEpoch);
                if (_timers.TryGetValue(channel, out var timer))
                    timer.Pause("ActiveCycleDataLimitExceeded");
                CancelCyclePauseCts(channel);
                try { CommandEpbOffHighPriority(channel, "ActiveCycleDataLimitExceeded"); }
                catch { }
                UnmarkHydraulicParticipant(channel);
                var cutoffCycles = new Dictionary<int, int>
                {
                    [channel] = update.CycleNumber
                };
                var cutoffReason =
                    $"ActiveCycleDataLimitExceeded EPB={channel} " +
                    $"Cycle={update.CycleNumber} Limit={update.RecordLimit}";
                TrySealSoftwareRecoveryCycleWindows(
                    cutoffCycles,
                    update.TimestampUtc,
                    cutoffReason);
                if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                        cutoffCycles,
                        update.TimestampUtc,
                        cutoffReason,
                        _daqPersistenceRecoveryTimeoutMs,
                        lease.Token)
                    .ConfigureAwait(false))
                    throw new InvalidOperationException(
                        $"ActiveCycleDurableCleanupFailed EPB={channel} " +
                        $"Cycle={update.CycleNumber} Limit={update.RecordLimit}");

                await RecoveryStageDeadline.RunAsync(
                        "ActiveCycleHydraulicGenerationReset",
                        RecoveryMechanicalReleaseTimeoutMs,
                        _ => _hydCoordinator.ForceReleaseAsync(
                            hydraulicGroupId,
                            $"ActiveCycleDataLimitExceeded:EPB={channel}:Cycle={update.CycleNumber}"),
                        lease.Token)
                    .ConfigureAwait(false);

                var freshness = _acq.GetDaqFreshnessSnapshot(
                    update.Device,
                    _daqPersistenceResumeAgeMs);
                if (freshness == null || !freshness.IsFresh)
                {
                    // 活动圈上限只负责圈生命周期清理。仅有独立的DAQ陈旧证据时，
                    // 才以真实DAQ故障码进入设备恢复，绝不拿容量异常冒充DAQ故障。
                    _log.Warn(
                        $"活动圈已作废且液压代次已重置；另有DAQ陈旧证据，" +
                        $"转入DaqSampleStale恢复。EPB={channel} Device={update.Device}",
                        "AI");
                    ObserveNonRecoveryLifecycleTask(BeginDaqAutoRecoveryAsync(
                        update.Device,
                        "DaqSampleStale",
                        $"ActiveCycle cleanup completed; CallbackAge={freshness?.CallbackAgeMs:F1}ms " +
                        $"ControlAge={freshness?.ControlProcessedAgeMs:F1}ms",
                        recoveryCorrelation,
                        restartDaq: true,
                        eventUtc: DateTime.UtcNow),
                        "BeginDaqAutoRecoveryAfterCycleCleanup",
                        channel);
                    return;
                }

                var plan = GetCompatibleStaggerPlan(new[] { channel });
                await RecoveryStageDeadline.RunAsync(
                        "ActiveCycleMechanicalRelease",
                        RecoveryMechanicalReleaseTimeoutMs,
                        ct => EnsureMotorReleasedBeforeFormalRejoinAsync(
                            new[] { channel },
                            plan,
                            "ActiveCycleDataLimitExceeded",
                            ct),
                        lease.Token)
                    .ConfigureAwait(false);
                if (expectedRunEpoch != Interlocked.Read(ref _runEpoch)) return;

                ResetTransientFaultStateForRestart(
                    new[] { channel },
                    "ActiveCycleDataLimitExceededRejoin");
                RejoinFormalChannelsAtSharedFutureSlot(
                    new[] { channel },
                    plan,
                    "ActiveCycleLifecycleRecovered",
                    $"EPB={channel} Cycle={update.CycleNumber} 已作废；" +
                    "DAQ保持原代次，液压代次重置后从未来完整槽重试",
                    allowTerminalReset: false);
                _log.Warn(
                    $"ActiveCycleDataLimitExceeded 已按圈生命周期故障处理，未重建DAQ。" +
                    $"EPB={channel} Cycle={update.CycleNumber} Limit={update.RecordLimit} " +
                    $"Device={update.Device} DaqFresh=true",
                    "落盘");
            }
            catch (Exception ex)
            {
                try { CommandEpbOffHighPriority(channel, "ActiveCycleRecoveryFailed"); }
                catch { }
                PublishIsolatedSoftwareFault(
                    "ActiveCycleRecoveryFailed",
                    $"EPB={channel} Cycle={update.CycleNumber} Limit={update.RecordLimit} " +
                    $"Error={ex.Message}",
                    new[] { channel },
                    recoveryCorrelation);
            }
            finally
            {
                lease?.Dispose();
                _activeCycleLimitRecoveries.TryRemove(lifecycleKey, out _);
            }
        }
    }
}
