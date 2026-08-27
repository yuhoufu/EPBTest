using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;

namespace Controller
{
    /// <summary>
    /// Immutable, public-to-tests projection of the retained terminal record.
    /// The live dictionary remains private to EpbManager; this projection is
    /// only an observation seam and never participates in closure decisions.
    /// </summary>
    internal sealed class DaqTerminalClosureEvidence
    {
        internal DaqTerminalClosureEvidence(
            string device,
            Guid runId,
            Guid correlationId,
            long runEpoch,
            DaqRecoveryTerminal terminal,
            DaqRecoveryPhase progressStage,
            long progressVersion,
            DateTime startedUtc,
            DateTime stageStartedUtc,
            DateTime hardDeadlineUtc,
            DateTime retainedUntilUtc,
            bool terminalClosureBlocked,
            string terminalFailureReason,
            IEnumerable<int> affectedChannels)
        {
            Device = device ?? string.Empty;
            RunId = runId;
            CorrelationId = correlationId;
            RunEpoch = runEpoch;
            Terminal = terminal;
            ProgressStage = progressStage;
            ProgressVersion = progressVersion;
            StartedUtc = startedUtc;
            StageStartedUtc = stageStartedUtc;
            HardDeadlineUtc = hardDeadlineUtc;
            RetainedUntilUtc = retainedUntilUtc;
            TerminalClosureBlocked = terminalClosureBlocked;
            TerminalFailureReason = terminalFailureReason ?? string.Empty;
            AffectedChannels = Array.AsReadOnly(
                (affectedChannels ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray());
        }

        internal string Device { get; }
        internal Guid RunId { get; }
        internal Guid CorrelationId { get; }
        internal long RunEpoch { get; }
        internal DaqRecoveryTerminal Terminal { get; }
        internal DaqRecoveryPhase ProgressStage { get; }
        internal long ProgressVersion { get; }
        internal DateTime StartedUtc { get; }
        internal DateTime StageStartedUtc { get; }
        internal DateTime HardDeadlineUtc { get; }
        internal DateTime RetainedUntilUtc { get; }
        internal bool TerminalClosureBlocked { get; }
        internal string TerminalFailureReason { get; }
        internal IReadOnlyList<int> AffectedChannels { get; }
    }

    public sealed partial class EpbManager
    {
        /// <summary>
        /// Production DAQ terminal seam used by deterministic integration
        /// tests.  It owns the real DaqAutoRecoveryContext and inserts it into
        /// the same per-device registry consumed by normal recovery.  The
        /// handle only exposes observations/entry points; terminal identity,
        /// safe-state verification and context removal remain in EpbManager.
        /// </summary>
        internal sealed class DaqTerminalClosureHandle
        {
            private readonly EpbManager _manager;
            private readonly DaqAutoRecoveryContext _context;

            internal DaqTerminalClosureHandle(
                EpbManager manager,
                object context)
            {
                _manager = manager;
                _context = (DaqAutoRecoveryContext)context;
            }

            internal Guid RunId => _context.RunId;
            internal long RunEpoch => _context.RunEpoch;
            internal Guid CorrelationId => _context.CorrelationId;
            internal Guid TransactionId => _context.RecoveryTransactionId;
            internal DaqRecoveryTransaction Transaction => _context.Transaction;
            internal bool RegistryLeaseActive => _context.RegistryLease?.IsActive == true;
            internal bool RegistryLeaseBound => _context.RegistryLease?.IsBound == true;
            internal bool IsRegistered =>
                _manager._daqAutoRecovery.TryGetValue(
                    _context.Device,
                    out var active) && ReferenceEquals(active, _context);
            internal bool IsCompletionCompleted => _context.Completion.Task.IsCompleted;
            internal DaqRecoveryResult CompletionResult =>
                _context.Completion.Task.IsCompleted
                    ? _context.Completion.Task.Result
                    : null;
            internal Task TerminalRetryTask =>
                _context.TerminalSupervisor.RetryTask;
            internal ChannelRuntimeStateChangedEvent GetState(int channel) =>
                _manager._channelRuntimeStateStore.Get(channel);

            internal DaqTerminalClosureEvidence CaptureRetainedTerminalEvidence()
                => _manager.CaptureDaqTerminalClosureEvidence(_context);

            internal WatchdogRecoverySnapshot CaptureWatchdogSnapshot()
                => _manager.CaptureWatchdogRecoverySnapshot();

            internal WatchdogRecoveryAggregateSnapshot CaptureAggregate()
                => _manager.CaptureWatchdogRecoveryAggregateSnapshot();

            internal ChannelRuntimeStateChangedEvent[] CaptureHeartbeatChannelProjection(
                WatchdogRecoveryAggregateSnapshot aggregate)
                => RecoveryHeartbeatAggregateSource.CaptureChannelStates(aggregate);

            internal void PublishAuthoritativeStateForRecoverySeam(
                ChannelRuntimeState state)
            {
                lock (_manager._recoveryContractGate)
                {
                    foreach (var channel in _context.AffectedChannels ?? Array.Empty<int>())
                    {
                        _manager._channelRuntimeStateStore.Publish(
                            new ChannelRuntimeStateChangedEvent
                            {
                                Channel = channel,
                                State = state,
                                ReasonCode = "ProductionTerminalClosureAuthorityTest",
                                ReasonText = "权威状态源故障注入/恢复。",
                                AffectedChannels = _context.AffectedChannels,
                                TimestampUtc = DateTime.UtcNow,
                                CorrelationId = _context.CorrelationId,
                                RunId = _context.RunId,
                                RunEpoch = _context.RunEpoch,
                                Enabled = true,
                                RecoveryOwnerKind = RecoveryOwnerKind.None,
                                RecoveryOwnerId = Guid.Empty,
                                RecoveryOwnerGeneration = 0,
                                RecoveryTargetPhase = RecoveryTargetPhase.None
                            },
                            allowTerminalReset: true,
                            allowSystemFaultReset: true);
                    }
                    _manager.PublishRecoveryAggregateOwnershipSourceLocked();
                }
            }

            internal bool TryFinalizeFailClosed(string reason)
                => _manager.TryFinalizeDaqTerminalRetryFailClosed(_context, reason);

            internal void CompleteCancelledClosureForRecoverySeam(string reason)
                => _manager.CompleteCancelledRecovery(_context, reason);

            internal bool PublishSafeTerminal(
                ChannelRuntimeState state = ChannelRuntimeState.StartBlocked)
                => _manager.PublishDaqSafeTerminalStates(
                    _context,
                    "DaqTerminalClosureHandle",
                    state);

            internal void PreCancelWorker()
            {
                try { _context.Cancellation.Cancel(); }
                catch { }
            }

            internal void AdvanceGlobalRunEpoch()
            {
                Interlocked.Increment(ref _manager._runEpoch);
            }
        }

        /// <summary>
        /// Register one already-created production safety transaction as a
        /// live DAQ recovery owner.  This is intentionally the same context
        /// and dictionary used by BeginDaqAutoRecoveryBodyAsync; it exists so
        /// production terminal closure can be integration-tested without
        /// starting NI acquisition hardware.
        /// </summary>
        internal DaqTerminalClosureHandle BeginDaqTerminalClosureProduction(
            string device,
            Guid runId,
            long runEpoch,
            Guid correlationId,
            DaqRecoveryTransaction transaction)
        {
            if (string.IsNullOrWhiteSpace(device))
                throw new ArgumentException("DAQ设备不能为空。", nameof(device));
            if (runId == Guid.Empty || runEpoch <= 0 || correlationId == Guid.Empty)
                throw new ArgumentException("DAQ终态身份不完整。", nameof(runId));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            var now = DateTime.UtcNow;
            var context = new DaqAutoRecoveryContext
            {
                Device = device,
                RunId = runId,
                RunEpoch = runEpoch,
                CorrelationId = correlationId,
                RecoveryTransactionId = transaction.Identity,
                StartedUtc = now,
                CutoffUtc = now,
                AffectedChannels = transaction.Channels.ToArray(),
                PreviouslyRunningChannels = transaction.Channels.ToArray(),
                PreviouslyActiveChannels = transaction.Channels.ToArray(),
                TriggerCode = "ProductionTerminalClosureSeam",
                TriggerReason = "ProductionTerminalClosureSeam",
                ProgressStage = DaqRecoveryPhase.SafeIdle,
                StageStartedUtc = now,
                HardDeadlineUtc = now.AddSeconds(45),
                Transaction = transaction
            };

            // Use the real Controller authority for terminal publication.  A
            // caller may provide only hardware/receipt ports; the live
            // context must still prove the channel left Recovering and advance
            // its phase through EpbManager's production gates.
            transaction.BindControllerTerminalCallbacks(
                phase =>
                {
                    if (!AdvanceDaqRecoveryStage(
                            context,
                            phase,
                            "ProductionTerminalClosureSeam"))
                        throw new InvalidOperationException(
                            "DAQ终态生产seam阶段发布被Controller phase gate拒绝。");
                },
                missing =>
                {
                    var requested = missing ?? Array.Empty<int>();
                    return PublishDaqSafeTerminalStates(
                               context,
                               "ProductionTerminalClosureSeam",
                               ChannelRuntimeState.StartBlocked,
                               requested)
                        ? requested
                        : Array.Empty<int>();
                });

            // The context is already on the safety branch for this seam.  The
            // transaction itself still performs its normal single-shot OFF /
            // power actions; no synthetic CancellationToken is introduced.
            context.Phase.MarkSafeIdle();
            transaction.RequestSafeIdle("ProductionTerminalClosureSeam");

            context.RegistryLease = _recoveryTaskRegistry.Reserve(
                "DaqTerminalRecovery",
                context.RunEpoch,
                context.AffectedChannels);
            if (!context.RegistryLease.TryBind(context.Completion.Task))
            {
                context.RegistryLease.CompleteAfterTerminal();
                throw new InvalidOperationException(
                    "DAQ终态生产seam无法绑定真实context完成Task。");
            }

            lock (_recoveryContractGate)
            {
                if (!_daqAutoRecovery.TryAdd(device, context))
                {
                    context.RegistryLease.CompleteAfterTerminal();
                    throw new InvalidOperationException(
                        "DAQ终态生产seam无法注册重复设备owner。");
                }

                // Seed the same authoritative Recovering identity that the
                // normal context publishes after acquiring its lease.  No
                // cleanup path is duplicated here; subsequent transitions go
                // through PublishDaqSafeTerminalStates and the real context.
                foreach (var channel in context.AffectedChannels)
                {
                    _channelRuntimeStateStore.Publish(
                        new ChannelRuntimeStateChangedEvent
                        {
                            Channel = channel,
                            State = ChannelRuntimeState.Recovering,
                            ReasonCode = "ProductionTerminalClosureSeam",
                            ReasonText = "生产终态回归上下文。",
                            AffectedChannels = context.AffectedChannels,
                            TimestampUtc = now,
                            CorrelationId = context.CorrelationId,
                            RunId = context.RunId,
                            RunEpoch = context.RunEpoch,
                            Enabled = true,
                            RecoveryOwnerKind = RecoveryOwnerKind.DaqRecovery,
                            RecoveryOwnerId = context.CorrelationId,
                            RecoveryOwnerGeneration = context.RunEpoch,
                            RecoveryTargetPhase = RecoveryTargetPhase.Formal
                        },
                        allowTerminalReset: true,
                        allowSystemFaultReset: true);
                }
                PublishRecoveryAggregateOwnershipSourceLocked();
                CaptureLogicalQuiescenceSnapshotLocked();
            }

            return new DaqTerminalClosureHandle(this, context);
        }

        /// <summary>
        /// Single production entry point for DAQ terminal closure retries.
        /// The long-running recovery/ownership cancellation token is not a
        /// terminal-lifecycle token: ownership preemption may cancel it while
        /// the same incident still has to publish its SafeIdle/Terminal
        /// receipt and release its lease.  Keep that policy here so every
        /// EpbManager path (normal, cancelled and infrastructure SafeIdle)
        /// uses the exact same supervisor invocation.
        /// </summary>
        internal static bool ScheduleDaqTerminalRetryProduction(
            DaqRecoveryTerminalSupervisor supervisor,
            Func<bool> tryComplete,
            Func<bool> isCurrent,
            Action onCompleted,
            Action<string> onExhausted,
            string exhaustionReason,
            int maxAttempts = 8,
            int initialDelayMs = 100,
            int maxDelayMs = 2000,
            Action onFinished = null)
        {
            if (supervisor == null) return false;
            // Deliberately use a closure-independent token.  Passing
            // context.Cancellation.Token here would make ownership
            // preemption abandon the very terminal closure that must release
            // that ownership.
            return supervisor.TryCompleteOrSchedule(
                tryComplete,
                isCurrent,
                onCompleted,
                onExhausted,
                CancellationToken.None,
                exhaustionReason,
                maxAttempts,
                initialDelayMs,
                maxDelayMs,
                onFinished);
        }

        internal static bool ShouldAutoRecoverExternalEquipmentFault(FaultScope scope)
        {
            // 只有卡钳本体的通道级、已定义硬件故障允许保持锁存停机。
            // DAQ、液压、电源等外部设备即使硬件暂时离线，也应保持安全断电并持续探测，
            // 条件恢复后自动续测。
            return scope != FaultScope.Channel;
        }

        private bool IsCurrentRecovery(DaqAutoRecoveryContext context)
        {
            if (context == null || context.Cancellation.IsCancellationRequested) return false;
            if (context.RunEpoch != Interlocked.Read(ref _runEpoch)) return false;
            return _daqAutoRecovery.TryGetValue(context.Device, out var active) &&
                   ReferenceEquals(active, context) &&
                   context.Terminal.Current == DaqRecoveryTerminal.None;
        }

        /// <summary>
        /// Terminal closure is a safety transaction owned by the incident,
        /// not by the long-running rebuild cancellation token.  Ownership
        /// preemption cancels <see cref="DaqAutoRecoveryContext.Cancellation"/>
        /// immediately; the same context must nevertheless be allowed to
        /// publish its already-started SafeIdle/Terminal receipt and release
        /// its owner.  Keep the identity/terminal checks, but deliberately do
        /// not treat that worker cancellation as a reason to abandon closure.
        /// </summary>
        private bool IsCurrentRecoveryForTerminal(DaqAutoRecoveryContext context)
        {
            if (context == null || context.RecoveryTransactionId == Guid.Empty ||
                context.Transaction == null ||
                context.Transaction.Identity != context.RecoveryTransactionId)
                return false;

            // Do not compare context.RunEpoch with the mutable global epoch.
            // CancelAll/RunEpochChanged revokes new work, but the exact
            // context/transaction still owns its already-started terminal
            // safety closure.  Reference identity in the per-device registry,
            // plus the frozen transaction identity, prevents an old owner
            // from ever closing a replacement context.
            return _daqAutoRecovery.TryGetValue(context.Device, out var active) &&
                   ReferenceEquals(active, context) &&
                   active.RecoveryTransactionId == context.RecoveryTransactionId &&
                   context.Terminal.Current == DaqRecoveryTerminal.None;
        }

        /// <summary>
        /// Only these latched states are valid evidence that a DAQ recovery
        /// owner has left the channel lifecycle.  Running/Learning/
        /// ResumeChecking are normal lifecycle states, not a safety terminal;
        /// accepting one here would allow a stale owner to disappear while a
        /// runner can still issue commands.
        /// </summary>
        internal static bool IsExactDaqSafeTerminalState(
            ChannelRuntimeStateChangedEvent state,
            Guid runId,
            long runEpoch,
            Guid correlationId)
        {
            if (state == null || runId == Guid.Empty || runEpoch <= 0 ||
                correlationId == Guid.Empty ||
                state.RunId != runId || state.RunEpoch != runEpoch ||
                state.CorrelationId != correlationId)
                return false;

            var exactSafeState = state.State == ChannelRuntimeState.StartBlocked ||
                                 state.State == ChannelRuntimeState.AlarmStopped ||
                                 state.State == ChannelRuntimeState.InterlockStopped ||
                                 state.State == ChannelRuntimeState.SystemFault;
            return exactSafeState &&
                   state.RecoveryOwnerKind == RecoveryOwnerKind.None &&
                   state.RecoveryTargetPhase == RecoveryTargetPhase.None &&
                   state.RecoveryOwnerId == Guid.Empty &&
                   state.RecoveryOwnerGeneration == 0;
        }

        private static bool IsDaqRecoveryOwnerState(
            ChannelRuntimeStateChangedEvent state,
            DaqAutoRecoveryContext context)
        {
            return state != null && context != null &&
                   state.State == ChannelRuntimeState.Recovering &&
                   state.RunId == context.RunId &&
                   state.RunEpoch == context.RunEpoch &&
                   state.CorrelationId == context.CorrelationId &&
                   state.RecoveryOwnerKind == RecoveryOwnerKind.DaqRecovery &&
                   state.RecoveryOwnerId == context.CorrelationId &&
                   state.RecoveryOwnerGeneration == context.RunEpoch &&
                   state.RecoveryTargetPhase != RecoveryTargetPhase.None;
        }

        /// <summary>
        /// Publish the affected group into one safe terminal state while the
        /// DAQ recovery context/owner is still present.  The state-store
        /// writes run under the same recovery contract gate, so a heartbeat
        /// cannot observe an owner-less Recovering channel between the first
        /// terminal publication and context/lease cleanup.  The context is
        /// intentionally retained until every affected channel has a terminal
        /// record (or the authoritative store fallback has accepted it).
        /// </summary>
        private bool PublishDaqSafeTerminalStates(
            DaqAutoRecoveryContext context,
            string reason,
            ChannelRuntimeState terminalState = ChannelRuntimeState.StartBlocked,
            IEnumerable<int> requestedChannels = null)
        {
            if (context == null) return false;
            var channels = (requestedChannels ?? context.AffectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (channels.Length == 0) return true;

            var allPublished = true;
            lock (_recoveryContractGate)
            {
                foreach (var channel in channels)
                {
                    var channelPublished = false;
                    var before = _channelRuntimeStateStore.Get(channel);

                    // A terminal closure may only consume the exact frozen
                    // owner identity.  Never overwrite another run/owner, and
                    // never convert a normal lifecycle state (Running,
                    // Learning, ResumeChecking, ...) into a safety terminal
                    // as a side effect of cleanup.
                    if (before != null &&
                        (before.RunId != context.RunId ||
                         before.RunEpoch != context.RunEpoch ||
                         (before.CorrelationId != Guid.Empty &&
                          before.CorrelationId != context.CorrelationId) ||
                         (!IsDaqRecoveryOwnerState(before, context) &&
                          !IsExactDaqSafeTerminalState(
                              before,
                              context.RunId,
                              context.RunEpoch,
                              context.CorrelationId))))
                    {
                        allPublished = false;
                        _log?.Error(
                            $"DAQ终态权威释放拒绝非安全/串代状态 EPB={channel}; " +
                            $"State={before.State}; Run={before.RunId:N}/{before.RunEpoch}; " +
                            $"Context={context.RunId:N}/{context.RunEpoch}; " +
                            $"CorrelationId={context.CorrelationId:N}",
                            "AI");
                        continue;
                    }
                    try
                    {
                        PublishChannelRuntimeState(
                            channel,
                            terminalState,
                            "DaqRecoverySafeTerminal",
                            reason ?? "DAQ恢复已安全停止。",
                            affectedChannels: channels,
                            correlationId: context.CorrelationId,
                            allowTerminalReset: true,
                            allowSystemFaultReset: true,
                            runIdOverride: context.RunId,
                            runEpochOverride: context.RunEpoch);
                        channelPublished = true;
                    }
                    catch (Exception ex)
                    {
                        _log?.Error(
                            $"DAQ恢复安全终态发布异常 EPB={channel} " +
                            $"Device={context.Device}; Error={ex.Message}",
                            "AI",
                            ex);
                    }

                    var state = _channelRuntimeStateStore.Get(channel);
                    var terminal = IsExactDaqSafeTerminalState(
                        state,
                        context.RunId,
                        context.RunEpoch,
                        context.CorrelationId);
                    // The authoritative state store, not the wrapper return
                    // path, is the evidence.  An observer may throw after the
                    // store accepted the terminal event.
                    channelPublished = terminal;
                    if (!terminal)
                    {
                        // Keep a deterministic store-level safety fallback even
                        // when a fault-injected publisher/observer throws.  It
                        // carries no owner, but the context remains the owner
                        // until this verification succeeds.
                        try
                        {
                            _channelRuntimeStateStore.Publish(
                                new ChannelRuntimeStateChangedEvent
                                {
                                    Channel = channel,
                                    State = terminalState,
                                    ReasonCode = "DaqRecoverySafeTerminalFallback",
                                    ReasonText = reason ?? "DAQ恢复已安全停止。",
                                    AffectedChannels = channels,
                                    TimestampUtc = DateTime.UtcNow,
                                    CorrelationId = context.CorrelationId,
                                    RunId = context.RunId,
                                    RunEpoch = context.RunEpoch,
                                    Enabled = IsChannelEnabled(channel),
                                    Energized = IsChannelEnergized(channel)
                                },
                                allowTerminalReset: true,
                                allowSystemFaultReset: true);
                        }
                        catch (Exception fallbackError)
                        {
                            _log?.Error(
                                $"DAQ恢复安全终态兜底发布失败 EPB={channel} " +
                                $"Device={context.Device}; Error={fallbackError.Message}",
                                "AI",
                                fallbackError);
                        }
                        var fallbackState = _channelRuntimeStateStore.Get(channel);
                        channelPublished = IsExactDaqSafeTerminalState(
                            fallbackState,
                            context.RunId,
                            context.RunEpoch,
                            context.CorrelationId);
                    }
                    allPublished = allPublished && channelPublished;
                }
            }
            return allPublished;
        }

        private void CompleteCancelledRecovery(DaqAutoRecoveryContext context, string reason)
        {
            if (context == null) return;
            // Safety actions and their single-shot incident decision are owned
            // by the DAQ transaction.  Request this before taking the manager
            // commit gate so an OFF/power callback cannot be serialized behind
            // terminal-state publication.
            context.Transaction?.RequestSafeIdle(reason ?? "DAQ恢复已安全停止。");
            var terminalPending = false;
            lock (_daqRecoveryCommitGate)
            {
                // SafeIdle is an explicit safety branch from whatever normal
                // stage was reached.  Publish it through the same controller
                // gate, then publish Terminal; never force the phase integer
                // directly or let a late callback resurrect the incident.
                AdvanceDaqRecoveryStage(
                    context,
                    DaqRecoveryPhase.SafeIdle,
                    reason ?? "DAQ恢复已安全停止。");
                if (context.Transaction == null)
                {
                    if (!PublishDaqSafeTerminalStates(context, reason))
                    {
                        // Do not remove a DAQ owner while any affected
                        // channel is still Recovering.
                        _log?.Error(
                            $"DAQ恢复安全终态尚未覆盖整组，保留context/owner。" +
                            $"Device={context.Device}; CorrelationId={context.CorrelationId:N}",
                            "AI");
                        terminalPending = true;
                    }
                    var legacyTerminalPublished = !terminalPending &&
                        AdvanceDaqRecoveryStage(
                            context,
                            DaqRecoveryPhase.Terminal,
                            "DAQ恢复事故已进入终态；等待稳定终态aggregate凭证.");
                    var legacyTerminalReceipt = legacyTerminalPublished
                        ? PublishDaqAggregateTerminal(context)
                        : null;
                    if (!terminalPending &&
                        (!legacyTerminalPublished ||
                         legacyTerminalReceipt == null ||
                         !legacyTerminalReceipt.Stable ||
                         legacyTerminalReceipt.Version <= 0))
                    {
                        // Even legacy contexts without a transaction must
                        // obtain the same independent terminal aggregate
                        // receipt before their terminal gate is committed.
                        terminalPending = true;
                    }
                }
                else if (!context.Transaction.TryTerminal())
                {
                    // The transaction publishes only its missing terminal
                    // routes and retains the incident until all are
                    // authoritative.  A stable terminal aggregate receipt is
                    // part of that barrier for SafeIdle/cancelled outcomes.
                    _log?.Error(
                        $"DAQ事务终态receipt尚未覆盖整组，保留context/owner。" +
                        $"Device={context.Device}; CorrelationId={context.CorrelationId:N}",
                        "AI");
                    terminalPending = true;
                }
                if (!terminalPending)
                {
                    if (!context.Terminal.TryCommit(DaqRecoveryTerminal.Cancelled)) return;
                    if (context.Transaction != null)
                        AdvanceDaqRecoveryStage(
                            context,
                            DaqRecoveryPhase.Terminal,
                            "DAQ恢复事故已进入终态。");
                    RetainDaqRecoveryTerminalSnapshot(context);
                    MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
                    TryRemoveExactDaqRecoveryContext(context);
                }
            }
            if (terminalPending)
            {
                ScheduleDaqCancelledTerminalRetry(context, reason);
                return;
            }
            CompleteDaqRecoveryRegistryLease(context);
            MarkDaqRecoveryBatchTerminal(context);
            ReleaseDaqRecoveryOwnerships(context);
            try { context.Cancellation.Cancel(); } catch { }
            var result = new DaqRecoveryResult
            {
                Device = context.Device ?? string.Empty,
                Recovered = false,
                PreviousGeneration = context.PreviousGeneration,
                RecoveredGeneration = context.RecoveredGeneration,
                FailureReason = reason?.IndexOf(
                                    "ManualUi",
                                    StringComparison.OrdinalIgnoreCase) >= 0
                    ? "人工停止，DAQ自动恢复已取消"
                    : reason ?? "RecoveryCancelled",
                FailureKind = reason?.IndexOf(
                                  "ManualUi",
                                  StringComparison.OrdinalIgnoreCase) >= 0
                    ? "ManualCancelled"
                    : "Cancelled",
                Classification = FaultClassification.SoftwareTransient
            };
            context.Completion.TrySetResult(result);
            LogDaqRecoveryFieldMetric(context, "Cancelled", reason);
            NonCriticalObserver.Invoke(
                DaqRecoveryStateChanged,
                result,
                ex => _log?.Warn($"DAQ取消恢复观察者异常，已隔离：{ex.Message}", "AI"));
        }

        /// <summary>
        /// Close a terminal-receipt retry that has exhausted its bounded
        /// attempts.  At that point the incident is deliberately fail-closed:
        /// every affected channel must first be observable outside
        /// Recovering, then the incident gate/context/lease are closed with a
        /// retained <c>TerminalClosureBlocked</c> snapshot.  This avoids both
        /// an owner-less Recovering window and a permanently leaked context;
        /// it is never reported as a successful recovery and never requests a
        /// batch/process restart.
        /// </summary>
        private bool TryFinalizeDaqTerminalRetryFailClosed(
            DaqAutoRecoveryContext context,
            string reason)
        {
            if (context == null || !IsCurrentRecoveryForTerminal(context))
                return false;

            var detail = string.IsNullOrWhiteSpace(reason)
                ? "DaqTerminalRetryExhausted;SafeIdleOnly"
                : reason;
            var closed = false;
            lock (_daqRecoveryCommitGate)
            {
                if (!IsCurrentRecoveryForTerminal(context)) return false;

                context.TerminalFailureReason = detail;
                Interlocked.Exchange(ref context.TerminalClosureBlocked, 1);
                context.Transaction?.RequestSafeIdle(detail);

                // If the transaction had already published Terminal before
                // its independent aggregate receipt failed, leave that
                // monotonic phase intact.  Otherwise publish the incident's
                // SafeIdle phase before touching the channel/context barrier.
                if (context.Phase.Current != DaqRecoveryPhase.Terminal &&
                    context.Phase.Current != DaqRecoveryPhase.SafeIdle)
                    AdvanceDaqRecoveryStage(context, DaqRecoveryPhase.SafeIdle, detail);

                if (!PublishDaqSafeTerminalStates(
                        context,
                        "DAQ终态凭证耗尽，已安全闭锁：" + detail,
                        ChannelRuntimeState.StartBlocked))
                {
                    // Retaining the owner is the only safe fallback when the
                    // authoritative channel store itself cannot prove that
                    // Recovering has ended.
                    _log?.Error(
                        $"DAQ终态凭证耗尽且安全终态复核失败，保留owner/context以防孤儿Recovering。" +
                        $"Device={context.Device}; CorrelationId={context.CorrelationId:N};" +
                        $"Reason={detail}",
                        "AI");
                    return false;
                }

                // The terminal aggregate receipt is unavailable by contract,
                // so use Cancelled as the explicit fail-closed outcome rather
                // than claiming Recovered.  The retained snapshot carries
                // the stable reason and blocked marker for the watchdog.
                if (!context.Terminal.TryCommit(DaqRecoveryTerminal.Cancelled))
                    return false;
                RetainDaqRecoveryTerminalSnapshot(context);
                MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
                TryRemoveExactDaqRecoveryContext(context);
                CompleteDaqRecoveryRegistryLease(context);
                // Re-publish after the lease is closed so the retained
                // frozen incident is visible without an obsolete active lease.
                lock (_recoveryContractGate)
                    PublishRecoveryOperationalSourcesLocked();
                closed = true;
            }

            if (!closed) return false;
            MarkDaqRecoveryBatchTerminal(context);
            ReleaseDaqRecoveryOwnerships(context);
            try { context.Cancellation.Cancel(); } catch { }
            context.Completion.TrySetResult(new DaqRecoveryResult
            {
                Device = context.Device ?? string.Empty,
                Recovered = false,
                FailureReason = detail,
                FailureKind = "TerminalClosureBlocked",
                Classification = FaultClassification.SoftwareTransient
            });
            LogDaqRecoveryFieldMetric(context, "SafeIdle", detail);
            return true;
        }

        private void CompleteDaqRecoveryRegistryLease(DaqAutoRecoveryContext context)
        {
            if (context?.RegistryLease == null) return;
            try { context.RegistryLease.CompleteAfterTerminal(); }
            catch (Exception ex)
            {
                // Terminal cleanup is already fail-closed; a registry
                // bookkeeping observer must never reopen the incident or
                // throw into the hardware safety path.
                _log?.Error(
                    $"DAQ终态registry lease清理异常 Device={context.Device}; " +
                    $"CorrelationId={context.CorrelationId:N}; Error={ex.Message}",
                    "AI",
                    ex);
            }
        }

        /// <summary>
        /// SafeIdle/cancelled terminal closure uses the same production
        /// supervisor as normal Recovered closure.  The context, contract and
        /// lease remain live until DaqRecoveryTransaction has both published
        /// all terminal channel states and obtained the independent stable
        /// terminal aggregate receipt.
        /// </summary>
        private void ScheduleDaqCancelledTerminalRetry(
            DaqAutoRecoveryContext context,
            string reason)
        {
            if (context == null || !IsCurrentRecoveryForTerminal(context)) return;
            if (Interlocked.CompareExchange(ref context.TerminalRetryScheduled, 1, 0) != 0)
                return;

            ScheduleDaqTerminalRetryProduction(
                context.TerminalSupervisor,
                () =>
                {
                    lock (_daqRecoveryCommitGate)
                    {
                        if (!IsCurrentRecoveryForTerminal(context)) return false;
                        context.Transaction?.RequestSafeIdle(reason ?? "DaqRecoveryCancelled");
                        AdvanceDaqRecoveryStage(
                            context,
                            DaqRecoveryPhase.SafeIdle,
                            reason ?? "DAQ恢复事故已安全停止。");
                        if (context.Transaction != null)
                            return context.Transaction.TryTerminal();
                        if (!PublishDaqSafeTerminalStates(context, reason))
                            return false;
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.Terminal,
                                "DAQ恢复事故已进入终态；等待稳定终态aggregate凭证."))
                            return false;
                        var receipt = PublishDaqAggregateTerminal(context);
                        return receipt != null && receipt.Stable && receipt.Version > 0;
                    }
                },
                () => IsCurrentRecoveryForTerminal(context),
                () =>
                {
                    var committed = false;
                    lock (_daqRecoveryCommitGate)
                    {
                        if (!IsCurrentRecoveryForTerminal(context) ||
                            (context.Transaction != null &&
                             context.Transaction.CurrentOutcome !=
                                 DaqRecoveryTransaction.Outcome.Terminal))
                            return;
                        if (!context.Terminal.TryCommit(DaqRecoveryTerminal.Cancelled))
                            return;
                        AdvanceDaqRecoveryStage(
                            context,
                            DaqRecoveryPhase.Terminal,
                            "DAQ恢复事故已进入终态。");
                        RetainDaqRecoveryTerminalSnapshot(context);
                        MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
                        TryRemoveExactDaqRecoveryContext(context);
                        CompleteDaqRecoveryRegistryLease(context);
                        // Publish once more after lease completion so the
                        // retained blocked incident is observable without a
                        // stale active registry lease in the heartbeat source.
                        lock (_recoveryContractGate)
                            PublishRecoveryOperationalSourcesLocked();
                        committed = true;
                    }
                    if (!committed) return;
                    Interlocked.Exchange(ref context.TerminalRetryScheduled, 0);
                    MarkDaqRecoveryBatchTerminal(context);
                    ReleaseDaqRecoveryOwnerships(context);
                    try { context.Cancellation.Cancel(); } catch { }
                    var result = new DaqRecoveryResult
                    {
                        Device = context.Device ?? string.Empty,
                        Recovered = false,
                        FailureReason = reason ?? "RecoveryCancelled",
                        FailureKind = "Cancelled",
                        Classification = FaultClassification.SoftwareTransient
                    };
                    context.Completion.TrySetResult(result);
                    LogDaqRecoveryFieldMetric(context, "Cancelled", reason);
                    NonCriticalObserver.Invoke(
                        DaqRecoveryStateChanged,
                        result,
                        ex => _log?.Warn($"DAQ取消恢复观察者异常，已隔离：{ex.Message}", "AI"));
                },
                exhaustedReason =>
                {
                    Interlocked.Exchange(ref context.TerminalRetryScheduled, 0);
                    if (!TryFinalizeDaqTerminalRetryFailClosed(
                            context,
                            exhaustedReason ?? "DaqCancelledTerminalRetryExhausted;SafeIdleOnly"))
                        _log?.Error(
                            $"DAQ取消/SafeIdle终态aggregate凭证持续失败，安全终态尚未完成复核，保留owner。" +
                            $"Device={context.Device}; CorrelationId={context.CorrelationId:N};" +
                            $"Reason={exhaustedReason}",
                            "AI");
                },
                "DaqCancelledTerminalRetryDeadline;SafeIdleOnly",
                onFinished: () => Interlocked.Exchange(
                    ref context.TerminalRetryScheduled,
                    0));
        }

        private bool TryRemoveExactDaqRecoveryContext(DaqAutoRecoveryContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Device)) return false;
            // ConcurrentDictionary.TryRemove(key) can remove a newer context if an old terminal
            // cleanup races with remove+replace. ICollection.Remove(KeyValuePair) compares both
            // key and reference value atomically, so a late old Run cannot delete a new Run.
            return ((ICollection<KeyValuePair<string, DaqAutoRecoveryContext>>)_daqAutoRecovery)
                .Remove(new KeyValuePair<string, DaqAutoRecoveryContext>(
                    context.Device,
                context));
        }

        private void RetainDaqRecoveryTerminalSnapshot(DaqAutoRecoveryContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Device)) return;
            DaqRecoveryTerminalSnapshot snapshot;
            lock (context.ProgressGate)
            {
                snapshot = new DaqRecoveryTerminalSnapshot
                {
                    Device = context.Device,
                    RunId = context.RunId,
                    CorrelationId = context.CorrelationId,
                    RunEpoch = context.RunEpoch,
                    Terminal = context.Terminal.Current,
                    ProgressStage = context.ProgressStage,
                    ProgressVersion = context.ProgressVersion,
                    StartedUtc = context.StartedUtc,
                    StageStartedUtc = context.StageStartedUtc,
                    HardDeadlineUtc = context.HardDeadlineUtc,
                    RetainedUntilUtc = DateTime.UtcNow.AddMilliseconds(
                        DaqRecoveryTerminalSnapshotRetentionMs),
                    TerminalClosureBlocked = Volatile.Read(
                        ref context.TerminalClosureBlocked) != 0,
                    TerminalFailureReason = context.TerminalFailureReason ?? string.Empty,
                    AffectedChannels = (context.AffectedChannels ?? Array.Empty<int>())
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray()
                };
            }
            _daqRecoveryTerminalSnapshots[BuildDaqRecoveryTerminalKey(
                context.Device,
                context.CorrelationId)] = snapshot;
        }

        private DaqTerminalClosureEvidence CaptureDaqTerminalClosureEvidence(
            DaqAutoRecoveryContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Device) ||
                context.CorrelationId == Guid.Empty)
                return null;
            if (!_daqRecoveryTerminalSnapshots.TryGetValue(
                    BuildDaqRecoveryTerminalKey(context.Device, context.CorrelationId),
                    out var snapshot) || snapshot == null ||
                snapshot.RetainedUntilUtc <= DateTime.UtcNow)
                return null;
            return new DaqTerminalClosureEvidence(
                snapshot.Device,
                snapshot.RunId,
                snapshot.CorrelationId,
                snapshot.RunEpoch,
                snapshot.Terminal,
                snapshot.ProgressStage,
                snapshot.ProgressVersion,
                snapshot.StartedUtc,
                snapshot.StageStartedUtc,
                snapshot.HardDeadlineUtc,
                snapshot.RetainedUntilUtc,
                snapshot.TerminalClosureBlocked,
                snapshot.TerminalFailureReason,
                snapshot.AffectedChannels);
        }

        private void RetainDaqRecoveryCommittedSnapshot(DaqAutoRecoveryContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Device)) return;
            DaqRecoveryTerminalSnapshot snapshot;
            lock (context.ProgressGate)
            {
                snapshot = new DaqRecoveryTerminalSnapshot
                {
                    Device = context.Device,
                    RunId = context.RunId,
                    CorrelationId = context.CorrelationId,
                    RunEpoch = context.RunEpoch,
                    Terminal = context.Terminal.Current,
                    ProgressStage = DaqRecoveryPhase.Committed,
                    ProgressVersion = context.ProgressVersion,
                    StartedUtc = context.StartedUtc,
                    StageStartedUtc = context.StageStartedUtc,
                    HardDeadlineUtc = context.HardDeadlineUtc,
                    RetainedUntilUtc = DateTime.UtcNow.AddMilliseconds(
                        DaqRecoveryTerminalSnapshotRetentionMs),
                    TerminalClosureBlocked = false,
                    TerminalFailureReason = string.Empty,
                    AffectedChannels = (context.AffectedChannels ?? Array.Empty<int>())
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray()
                };
            }
            _daqRecoveryCommittedSnapshots[BuildDaqRecoveryTerminalKey(
                context.Device,
                context.CorrelationId)] = snapshot;
        }

        private DaqRecoveryTerminalSnapshot CaptureRetainedDaqRecoveryCommittedSnapshot(
            string device,
            Guid runId,
            long runEpoch)
        {
            var now = DateTime.UtcNow;
            DaqRecoveryTerminalSnapshot selected = null;
            foreach (var item in _daqRecoveryCommittedSnapshots.ToArray())
            {
                var value = item.Value;
                if (value == null || value.RetainedUntilUtc <= now)
                {
                    _daqRecoveryCommittedSnapshots.TryRemove(item.Key, out _);
                    continue;
                }
                if ((!string.IsNullOrWhiteSpace(device) &&
                     !string.Equals(value.Device, device, StringComparison.OrdinalIgnoreCase)) ||
                    value.RunId != runId ||
                    value.RunEpoch != runEpoch)
                    continue;
                if (selected == null || value.StartedUtc > selected.StartedUtc)
                    selected = value;
            }
            return selected;
        }

        private DaqRecoveryTerminalSnapshot CaptureRetainedDaqRecoveryTerminalSnapshot(
            string device,
            Guid runId,
            long runEpoch,
            bool allowFrozenBlocked = false)
        {
            var now = DateTime.UtcNow;
            DaqRecoveryTerminalSnapshot selected = null;
            foreach (var item in _daqRecoveryTerminalSnapshots.ToArray())
            {
                var value = item.Value;
                if (value == null || value.RetainedUntilUtc <= now)
                {
                    _daqRecoveryTerminalSnapshots.TryRemove(item.Key, out _);
                    continue;
                }
                var frozenBlocked = allowFrozenBlocked && value.TerminalClosureBlocked;
                if ((!string.IsNullOrWhiteSpace(device) &&
                     !string.Equals(value.Device, device, StringComparison.OrdinalIgnoreCase)) ||
                    (!frozenBlocked && value.RunId != runId) ||
                    (!frozenBlocked && value.RunEpoch != runEpoch))
                    continue;
                if (frozenBlocked && _daqAutoRecovery.Values.Any(owner =>
                        owner != null &&
                        owner.Terminal.Current == DaqRecoveryTerminal.None &&
                        string.Equals(owner.Device, value.Device, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (selected == null || value.StartedUtc > selected.StartedUtc)
                    selected = value;
            }
            return selected;
        }

        private void MarkDaqRecoveryTerminal(Guid correlationId, string device)
        {
            if (correlationId == Guid.Empty) return;
            _daqRecoveryTerminalCorrelations[BuildDaqRecoveryTerminalKey(device, correlationId)] =
                DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(device))
            {
                var generation = 0L;
                try { generation = _acq.GetCurrentGeneration(device); }
                catch { }
                _daqIncidentGrace[device.Trim()] = new DaqIncidentGrace
                {
                    CorrelationId = correlationId,
                    Generation = generation,
                    ExpiresMonotonicTicks = Stopwatch.GetTimestamp() +
                        (long)(Stopwatch.Frequency * 1.2)
                };
            }
            _daqIncidentLatch.Complete(device, correlationId);
            if (_daqRecoveryTerminalCorrelations.Count <= 1024) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var item in _daqRecoveryTerminalCorrelations)
                if (item.Value < cutoff)
                    _daqRecoveryTerminalCorrelations.TryRemove(item.Key, out _);
        }

        private bool IsDaqRecoveryTerminalCorrelation(string device, Guid correlationId)
        {
            return correlationId != Guid.Empty &&
                   _daqRecoveryTerminalCorrelations.ContainsKey(
                       BuildDaqRecoveryTerminalKey(device, correlationId));
        }

        internal static string BuildDaqRecoveryTerminalKey(string device, Guid correlationId)
            => $"{(device ?? string.Empty).Trim().ToUpperInvariant()}:{correlationId:N}";

        private void StartDaqRecoveryWatchdog(DaqAutoRecoveryContext context)
        {
            if (context == null) return;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                try
                {
                    // 看门狗必须在任何同步断电、写盘封存或诊断动作之前启动。
                    // 这些步骤中的任意一个即使意外阻塞，也不能让通道永久停留在旧状态。
                    await Task.Delay(_daqPersistenceRecoveryTimeoutMs, context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;
                    // This watchdog is intentionally incident-local.  A stalled
                    // DAQ pipeline must enter one SafeIdle/Terminal outcome;
                    // escalating it to the process watchdog used to create the
                    // observed batch-recycle/restart loop.
                    CompleteCancelledRecovery(
                        context,
                        $"DaqRecoveryPipelineStalled after {_daqPersistenceRecoveryTimeoutMs}ms; " +
                        "SafeIdleOnly");
                }
                catch (OperationCanceledException) { }
            }), "DaqRecoveryWatchdog");
        }

        private void StartDaqPowerDisableDeadline(DaqAutoRecoveryContext context)
        {
            if (context == null || _powerSupply == null ||
                ((context.PowerDisableTasksByGroup == null ||
                  context.PowerDisableTasksByGroup.Count == 0) &&
                 (context.PowerDisableTasks == null ||
                  context.PowerDisableTasks.Length == 0)))
                return;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(
                            PowerDisableHardDeadlineMs,
                            context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;
                    // 截止阶段一旦提交，后续 Validation/Rejoin 中的 Active 或重新上电
                    // 都不再属于“OFF 未确认”；不能用过期的 5 秒切断计时器越级接管。
                    if (context.Phase.Current >= DaqRecoveryPhase.CutoffCompleted) return;

                    var pendingGroups = (context.PowerDisableTasksByGroup ??
                                         new Dictionary<int, Task>())
                        .Where(pair => pair.Key > 0 &&
                                       pair.Value != null &&
                                       !pair.Value.IsCompleted)
                        .Select(pair => pair.Key)
                        .ToHashSet();
                    // Backward-compatible safety for contexts built by an older call
                    // site that only filled Task[].  New cutoff contexts always carry
                    // the group map, so no group identity is invented here.
                    var pendingTaskCount = pendingGroups.Count > 0
                        ? pendingGroups.Count
                        : (context.PowerDisableTasks ?? Array.Empty<Task>())
                            .Count(task => task != null && !task.IsCompleted);
                    var energizedGroups = (context.PowerDisableTasksByGroup ??
                                           new Dictionary<int, Task>())
                        .Where(pair => pair.Key > 0)
                        .Select(pair => pair.Key)
                        .Where(id =>
                        {
                            var state = _powerSupply.GetRuntimeState(id);
                            return pendingGroups.Contains(id) ||
                                   state.ExpectedOutputEnabled ||
                                   state.TelemetryOutputEnabled;
                        })
                        .OrderBy(id => id)
                        .ToArray();
                    if (energizedGroups.Length == 0 && pendingTaskCount == 0) return;

                    if (Interlocked.Exchange(ref context.PowerDisableDeadlineLogged, 1) == 0)
                    {
                        _log.Error(
                            $"DaqPowerDisableDeadlineExceeded Device={context.Device} " +
                            $"RunId={context.RunId:N} RunEpoch={context.RunEpoch} " +
                            $"CorrelationId={context.CorrelationId:N} " +
                            $"PendingTasks={pendingTaskCount} " +
                            $"EnergizedGroups=[{string.Join(",", energizedGroups)}] " +
                            $"DeadlineMs={PowerDisableHardDeadlineMs}；" +
                            "仅执行一次整组SafeIdle/Terminal，禁止批次回收或主进程重启。",
                            "程控电源");
                    }

                    // A power-off deadline is an incident-local safety outcome.
                    // Never turn it into a batch recycle/process restart: the
                    // terminal gate below is the single idempotent owner of this
                    // incident's SafeIdle transition.
                    CompleteCancelledRecovery(
                        context,
                        $"DaqPowerDisableDeadline Device={context.Device}; " +
                        $"PendingTasks={pendingTaskCount}; " +
                        $"EnergizedGroups=[{string.Join(",", energizedGroups)}]");
                }
                catch (OperationCanceledException) { }
            }), "DaqPowerDisableDeadline");
        }

        private async Task CancelAllDaqRecoveriesAsync(string reason)
        {
            DaqAutoRecoveryContext[] contexts;
            lock (_daqRecoveryCommitGate)
            {
                Interlocked.Increment(ref _runEpoch);
                contexts = _daqAutoRecovery.Values.ToArray();
                foreach (var context in contexts)
                    CompleteCancelledRecovery(context, reason);
            }
            if (contexts.Length == 0) return;
            var completions = contexts.Select(x => (Task)x.Completion.Task).ToArray();
            var all = Task.WhenAll(completions);
            await Task.WhenAny(all, Task.Delay(2000)).ConfigureAwait(false);
        }

        /// <summary>
        /// The sole Controller publisher for DAQ recovery progress.  Phase,
        /// version, stage start and hard deadline are updated together; callers
        /// must not write ValidationPhase or invent a progress counter.
        /// </summary>
        private bool AdvanceDaqRecoveryStage(
            DaqAutoRecoveryContext context,
            DaqRecoveryPhase next,
            string reason,
            bool allowAlreadyCommitted = false)
        {
            if (context == null) return false;
            var changed = false;
            DateTime stageStarted;
            DateTime hardDeadline;
            long progressVersion;
            DaqRecoveryPhase effective;
            lock (context.ProgressGate)
            {
                var publishedBefore = context.ProgressStage;
                if ((int)next < (int)publishedBefore) return false;
                var gateCurrent = context.Phase.Current;
                // Physical confirmation can publish DoOffConfirmed from the
                // transaction callback before the Controller resumes after
                // ConfirmPhysicalFromController. Only that exact, fully
                // committed triple is an idempotent success; every other
                // duplicate, skip, regression or terminal transition remains
                // fail-closed.
                if (IsAlreadyCommittedDaqRecoveryStage(
                        allowAlreadyCommitted,
                        next,
                        gateCurrent,
                        publishedBefore,
                        context.Transaction?.Phase))
                    return true;
                if (gateCurrent == DaqRecoveryPhase.Terminal &&
                    next != DaqRecoveryPhase.Terminal)
                    return false;
                if (gateCurrent == DaqRecoveryPhase.SafeIdle &&
                    next != DaqRecoveryPhase.SafeIdle &&
                    next != DaqRecoveryPhase.Terminal)
                    return false;
                // The phase gate is authoritative.  In particular, do not
                // publish a progress stage when a late/out-of-order callback
                // was rejected by the gate: doing so used to advance the
                // watchdog version even though the real recovery phase had not
                // moved.
                var phaseAdvanced = next == DaqRecoveryPhase.SafeIdle
                    ? context.Phase.MarkSafeIdle()
                    : context.Phase.TryAdvance(next);
                if (!phaseAdvanced || context.Phase.Current != next)
                    return false;
                effective = next;
                changed = (int)effective > (int)publishedBefore ||
                          (context.ProgressVersion == 0 &&
                           effective == DaqRecoveryPhase.StaleDetected);
                if (context.StageStartedUtc == default)
                    context.StageStartedUtc = context.StartedUtc == default
                        ? DateTime.UtcNow
                        : context.StartedUtc;
                if (context.HardDeadlineUtc == default)
                    context.HardDeadlineUtc = (context.StartedUtc == default
                            ? DateTime.UtcNow
                            : context.StartedUtc)
                        .AddMilliseconds(Math.Max(1, RecoveryGroupHardDeadlineMs));
                if (changed)
                {
                    context.ProgressStage = effective;
                    // Stage timestamps are Controller-owned evidence.  Wall-clock
                    // corrections must not make a later stage appear to start before
                    // its predecessor; keep the value monotonic while preserving the
                    // UTC representation consumed by the watchdog.
                    var nowUtc = DateTime.UtcNow;
                    context.StageStartedUtc = nowUtc < context.StageStartedUtc
                        ? context.StageStartedUtc
                        : nowUtc;
                    context.ValidationPhase = effective.ToString();
                    context.ProgressVersion = Math.Max(
                        context.ProgressVersion + 1,
                        1);
                }
                else if (string.IsNullOrWhiteSpace(context.ValidationPhase))
                {
                    context.ValidationPhase = effective.ToString();
                }
                stageStarted = context.StageStartedUtc;
                hardDeadline = context.HardDeadlineUtc;
                progressVersion = context.ProgressVersion;
            }

            if (changed)
            {
                context.RegistryLease?.ReportProgress(effective.ToString());
                // The aggregate store is the sole committed DAQ source used
                // by watchdog capture; keep it in lockstep with the
                // Controller's authoritative phase/version publication.
                PublishDaqAggregateCheckpoint(context);
                LogDaqRecoveryFieldMetric(
                    context,
                    "Progress",
                    $"Stage={effective};ProgressVersion={progressVersion};" +
                     $"StageStartedUtc={stageStarted:O};HardDeadlineUtc={hardDeadline:O};" +
                     (reason ?? string.Empty));
            }

            // State observers receive the Controller-owned stage.  A duplicate
            // callback in the same stage is intentionally not another incident.
            if (changed)
            {
                DaqPersistenceStateChanged queue;
                try { queue = _persistence.GetSnapshot(context.Device); }
                catch { queue = default; }
                NonCriticalObserver.Invoke(
                    DaqPersistenceStateChanged,
                    new DaqPersistenceStateChanged
                    {
                        Device = context.Device,
                        State = DaqPersistenceState.Recovering,
                        Code = context.TriggerCode,
                        Reason = reason ?? effective.ToString(),
                        QueueDepth = queue?.QueueDepth ?? 0,
                        OldestBatchAgeMs = queue?.OldestBatchAgeMs ?? 0,
                        Generation = _acq.GetCurrentGeneration(context.Device),
                        TimestampUtc = DateTime.UtcNow,
                        CorrelationId = context.CorrelationId
                    },
                    ex => _log?.Warn($"DAQ恢复进度观察者异常，已隔离：{ex.Message}", "AI"));
            }
            return changed;
        }

        internal static bool IsAlreadyCommittedDaqRecoveryStage(
            bool explicitlyAllowed,
            DaqRecoveryPhase requested,
            DaqRecoveryPhase controllerPhase,
            DaqRecoveryPhase publishedPhase,
            DaqRecoveryPhase? transactionPhase)
        {
            return explicitlyAllowed &&
                   requested == DaqRecoveryPhase.DoOffConfirmed &&
                   controllerPhase == requested &&
                   publishedPhase == requested &&
                   transactionPhase.HasValue &&
                   transactionPhase.Value == requested;
        }

        private void PublishRecoveryProgress(DaqAutoRecoveryContext context, string reason)
        {
            AdvanceDaqRecoveryStage(
                context,
                context?.Phase.Current ?? DaqRecoveryPhase.StaleDetected,
                reason);
        }

        private async Task<HardwareEvidence[]> ConfirmDaqHardwareFailureAsync(
            DaqAutoRecoveryContext context,
            DaqRecoveryResult recoveryResult)
        {
            if (context == null || recoveryResult == null ||
                !string.Equals(recoveryResult.FailureKind, "DaqDeviceMissing", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<HardwareEvidence>();

            var evidence = new List<HardwareEvidence>(2);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation.Token))
            {
                timeout.CancelAfter(3000);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (!IsCurrentRecovery(context)) return Array.Empty<HardwareEvidence>();
                    DaqHardwareProbeResult probe;
                    try
                    {
                        probe = await _daqHardwareProbe.ProbeAsync(context.Device, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return Array.Empty<HardwareEvidence>();
                    }
                    evidence.Add(probe.ToEvidence());
                    if (!probe.IndependentFailureConfirmed) return evidence.ToArray();
                    if (attempt == 0)
                        await Task.Delay(500, timeout.Token).ConfigureAwait(false);
                }
            }
            return evidence.ToArray();
        }

        /// <summary>
        /// Isolates an infrastructure fault while keeping it recoverable. Healthy independent
        /// groups continue running; affected channels remain safely de-energized in Recovering
        /// and are handed to the affected-group restart loop instead of a permanent SystemFault.
        /// </summary>
        private void PublishIsolatedSoftwareFault(
            string code,
            string reason,
            int[] affectedChannels,
            Guid correlationId)
        {
            var channels = (affectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var fault = new ControlFault(
                code,
                reason,
                channels.Length == 1 ? FaultScope.Channel : FaultScope.DaqGroup,
                channels,
                null,
                DateTime.UtcNow,
                correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
                FaultClassification.SystemFault);
            Dictionary<int, string> rejectedOff = null;
            ExecuteNonBlockingSafetyIsolationOrder(
                () => FreezeAndCancelSafetyChannels(
                    channels,
                    $"IsolatedSoftwareFault:{code}",
                    cancelStopTokens: false),
                () => rejectedOff = SubmitEpbOffHighPriorityBatch(
                    channels,
                    "IsolatedSoftwareFaultOffAdmissionRejected",
                    "IsolatedSoftwareFaultOffSubmissionException"),
                () => StartElectricalGroupSafetyDisables(
                    channels,
                    $"隔离软件故障安全断电 Code={code} CorrelationId={fault.CorrelationId:N}",
                    "IsolatedSoftwareFaultPowerDisable"),
                () =>
                {
                    // 先登记局部恢复任务，再发布逐通道/UI诊断；慢观察者不能阻止
                    // 受影响组进入有界恢复协调。
                    ScheduleIsolatedInfrastructureRecovery(
                        channels,
                        code,
                        fault.CorrelationId,
                        code);
                    // ScheduleIsolatedInfrastructureRecovery owns the first
                    // Recovering publication.  Its transaction binds the
                    // actual reset worker before publishing this state; this
                    // method must not create a direct lifecycle gap here.
                    _log.Error(
                        $"隔离软件故障（健康设备继续运行，不触发硬件报警/全局重启） [{code}]：{reason}",
                        "AI");
                    NonCriticalObserver.Invoke(
                        ControlFaultRaised,
                        fault,
                        ex => _log?.Warn(
                            $"DAQ隔离故障观察者异常，已隔离：{ex.Message}",
                            "AI"));
                },
                () => ScheduleRejectedOffFallbacks(
                    rejectedOff,
                    "IsolatedSoftwareFaultImmediateOffFallback"));
        }
    }
}
