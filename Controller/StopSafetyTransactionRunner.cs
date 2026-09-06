using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// Clock boundary used by the stop transaction.  Production uses the
    /// system clock; seam tests can advance a deterministic clock without
    /// sleeping for the 45 second escape deadline.
    /// </summary>
    public interface IStopSafetyClock
    {
        DateTime UtcNow { get; }
        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
    }

    public sealed class SystemStopSafetyClock : IStopSafetyClock
    {
        public DateTime UtcNow => DateTime.UtcNow;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Result returned by one physical stop stage.  A stage result is the only
    /// way a port can advance the material-progress clock; changing Detail is
    /// deliberately not evidence.
    /// </summary>
    public sealed class StopSafetyPortResult
    {
        public bool Succeeded { get; set; } = true;
        public bool MaterialProgress { get; set; }
        public string EvidenceSource { get; set; } = string.Empty;
        public long EvidenceVersion { get; set; }
        // These are edge observations, not defaults.  A stage that has no
        // new physical evidence must leave the previous edge untouched; a
        // false value from a later bookkeeping stage must never erase a
        // previously verified true edge.
        public bool? PhysicalOffSubmitted { get; set; }
        public bool? PowerDisableStarted { get; set; }
        public bool? PhysicalSafe { get; set; }
        public string Detail { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public StopSafetyResult Result { get; set; }

        public static StopSafetyPortResult Success(
            string detail = null,
            bool materialProgress = false,
            string evidenceSource = null,
            long evidenceVersion = 0,
            bool? physicalOffSubmitted = null,
            bool? powerDisableStarted = null,
            bool? physicalSafe = null)
        {
            return new StopSafetyPortResult
            {
                Succeeded = true,
                Detail = detail ?? string.Empty,
                MaterialProgress = materialProgress,
                EvidenceSource = evidenceSource ?? string.Empty,
                EvidenceVersion = evidenceVersion,
                PhysicalOffSubmitted = physicalOffSubmitted,
                PowerDisableStarted = powerDisableStarted,
                PhysicalSafe = physicalSafe
            };
        }

        public static StopSafetyPortResult Failure(
            string error,
            bool? physicalOffSubmitted = null,
            bool? powerDisableStarted = null,
            bool? physicalSafe = null)
        {
            return new StopSafetyPortResult
            {
                Succeeded = false,
                Error = error ?? "Stop safety stage failed",
                PhysicalOffSubmitted = physicalOffSubmitted,
                PowerDisableStarted = powerDisableStarted,
                PhysicalSafe = physicalSafe
            };
        }
    }

    /// <summary>
    /// Hardware/control boundary for the transaction runner.  Existing EPB
    /// hardware algorithms are adapted to this port; the runner owns ordering,
    /// lease state, deadlines and terminal publication.
    /// </summary>
    public interface IStopSafetyExecutionPort
    {
        Task<StopSafetyPortResult> ExecuteStageAsync(
            StopSafetyStage stage,
            StopSafetyTransactionContext transaction,
            CancellationToken safetyCancellationToken);

        /// <summary>
        /// Idempotent emergency path.  It must only issue safety-direction
        /// commands and must not wait for a caller cancellation token.
        /// </summary>
        void EnterSafeIdleOnce(StopSafetyTransactionContext transaction, string reason);
    }

    public sealed class StopSafetyTransactionContext
    {
        internal StopSafetyTransactionContext(
            StopContext stopContext,
            Guid transactionId,
            long generation,
            Guid runId,
            long runEpoch,
            DateTime startedUtc,
            DateTime hardDeadlineUtc,
            CancellationToken safetyCancellationToken)
        {
            StopContext = stopContext;
            TransactionId = transactionId;
            Generation = generation;
            RunId = runId;
            RunEpoch = runEpoch;
            StartedUtc = startedUtc;
            HardDeadlineUtc = hardDeadlineUtc;
            SafetyCancellationToken = safetyCancellationToken;
        }

        public StopContext StopContext { get; }
        public Guid TransactionId { get; }
        public long Generation { get; }
        public Guid RunId { get; }
        public long RunEpoch { get; }
        public DateTime StartedUtc { get; }
        public DateTime HardDeadlineUtc { get; }
        public CancellationToken SafetyCancellationToken { get; }
    }

    public sealed class StopSafetyTransactionOptions
    {
        private static readonly StopSafetyStage[] DefaultStages =
        {
            StopSafetyStage.AdmitAndSubmitSafety,
            StopSafetyStage.FreezeActiveWork,
            StopSafetyStage.RevokeExecutionAuthorization,
            StopSafetyStage.SubmitPhysicalOff,
            StopSafetyStage.StartPowerDisable,
            StopSafetyStage.ClearTimerAndRunner,
            StopSafetyStage.ClearRecoveryOwners,
            StopSafetyStage.ReleaseHydraulics,
            StopSafetyStage.StopAcquisition,
            StopSafetyStage.ClosePersistenceBoundary,
            StopSafetyStage.VerifyLogicalQuiescence
        };

        public TimeSpan HardDeadline { get; set; } = TimeSpan.FromSeconds(45);
        public int StageNoProgressGraceMs { get; set; } = 5000;
        public IReadOnlyList<StopSafetyStage> Stages { get; set; } = DefaultStages;

        public Func<StopSafetyStage, TimeSpan> StageDeadline { get; set; } =
            stage =>
            {
                switch (stage)
                {
                    case StopSafetyStage.ReleaseHydraulics:
                    case StopSafetyStage.ClosePersistenceBoundary:
                        return TimeSpan.FromSeconds(15);
                    case StopSafetyStage.StartPowerDisable:
                    case StopSafetyStage.ClearTimerAndRunner:
                    case StopSafetyStage.ClearRecoveryOwners:
                    case StopSafetyStage.StopAcquisition:
                    case StopSafetyStage.VerifyLogicalQuiescence:
                        return TimeSpan.FromSeconds(5);
                    default:
                        return TimeSpan.FromSeconds(2);
                }
            };

        internal StopSafetyStage[] GetValidatedStages()
        {
            var stages = (Stages ?? DefaultStages)
                .Where(stage => stage != StopSafetyStage.None &&
                                stage != StopSafetyStage.Completed &&
                                stage != StopSafetyStage.TimedOut)
                .ToArray();
            if (stages.Length == 0) return DefaultStages.ToArray();
            for (var index = 1; index < stages.Length; index++)
            {
                if ((int)stages[index] <= (int)stages[index - 1])
                    throw new ArgumentException(
                        "Stop safety stages must be strictly increasing.", nameof(Stages));
            }
            return stages;
        }
    }

    /// <summary>
    /// Lease owned by exactly one stop transaction.  A caller cancellation
    /// never expires this lease; only the transaction hard deadline or a
    /// terminal result can do so.
    /// </summary>
    internal sealed class StopSafetyTransactionLease
    {
        private int _state; // 0 active, 1 expired, 2 terminal

        internal StopSafetyTransactionLease(
            Guid transactionId,
            long generation,
            Guid runId,
            long runEpoch,
            DateTime startedUtc,
            DateTime hardDeadlineUtc,
            CancellationTokenSource cancellation)
        {
            TransactionId = transactionId;
            Generation = generation;
            RunId = runId;
            RunEpoch = runEpoch;
            StartedUtc = startedUtc;
            HardDeadlineUtc = hardDeadlineUtc;
            Cancellation = cancellation;
        }

        internal Guid TransactionId { get; }
        internal long Generation { get; }
        internal Guid RunId { get; }
        internal long RunEpoch { get; }
        internal DateTime StartedUtc { get; }
        internal DateTime HardDeadlineUtc { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal bool Active => Volatile.Read(ref _state) == 0;
        internal bool Expired => Volatile.Read(ref _state) == 1;
        internal bool Terminal => Volatile.Read(ref _state) == 2;

        internal bool TryExpire()
        {
            return Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        }

        internal bool TryComplete()
        {
            return Interlocked.CompareExchange(ref _state, 2, 0) == 0;
        }

        internal bool TryTerminal()
        {
            return Interlocked.CompareExchange(ref _state, 2, 0) == 0 ||
                   Volatile.Read(ref _state) == 1 &&
                   Interlocked.CompareExchange(ref _state, 2, 1) == 1;
        }
    }

    /// <summary>
    /// One production stop transaction coordinator.  It is intentionally
    /// independent of EpbManager so the exact deadline/race behavior can be
    /// tested with only a fake execution port and a virtual clock.
    /// </summary>
    public sealed class StopSafetyTransactionRunner
    {
        private readonly object _gate = new object();
        private readonly IStopSafetyExecutionPort _port;
        private readonly IStopSafetyClock _clock;
        private readonly StopSafetyTransactionOptions _options;
        private readonly Action<StopSafetyProgressSnapshot> _publishProgress;
        private readonly Func<Guid> _runIdProvider;
        private readonly Func<long> _runEpochProvider;
        private readonly Func<long> _generationProvider;
        private readonly Dictionary<string, long> _materialEvidence =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private StopSafetyTransactionLease _lease;
        private Task<StopSafetyResult> _activeTask;
        // Kept only for the deadline race where the process hard timer wins
        // just before RunStagesAsync can replace its parent task with the
        // currently stuck stage.  It lets the stage retain the one orphan
        // slot instead of being cleared when the parent returns its timeout.
        private Task _transactionCoreTask;
        private Task _orphanCore;
        private StopSafetyResult _cachedTerminalResult;
        private StopSafetyResult _cachedTimedOut;
        private StopSafetyResult _lastTerminalResult;
        private StopSafetyProgressSnapshot _progress = new StopSafetyProgressSnapshot();
        private int _safeIdleIssued;
        private long _nextGeneration;

        public StopSafetyTransactionRunner(
            IStopSafetyExecutionPort port,
            IStopSafetyClock clock = null,
            StopSafetyTransactionOptions options = null,
            Action<StopSafetyProgressSnapshot> publishProgress = null,
            Func<Guid> runIdProvider = null,
            Func<long> runEpochProvider = null,
            Func<long> generationProvider = null)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _clock = clock ?? new SystemStopSafetyClock();
            _options = options ?? new StopSafetyTransactionOptions();
            _options.GetValidatedStages();
            _publishProgress = publishProgress;
            _runIdProvider = runIdProvider ?? (() => Guid.Empty);
            _runEpochProvider = runEpochProvider ?? (() => 0L);
            _generationProvider = generationProvider;
        }

        public bool HasActiveTransaction
        {
            get
            {
                lock (_gate) return _lease != null && _lease.Active;
            }
        }

        public bool HasOrphanCore
        {
            get
            {
                lock (_gate) return _orphanCore != null && !_orphanCore.IsCompleted;
            }
        }

        /// <summary>
        /// Number of distinct SafeIdle issue edges emitted by this process
        /// scoped runner.  It is a monotonic diagnostic counter; the runner's
        /// interlocked gate still guarantees that the physical action itself
        /// is issued at most once for an incident.
        /// </summary>
        public int SafeIdleIssueCount => Volatile.Read(ref _safeIdleIssued);

        public StopSafetyProgressSnapshot CaptureProgress()
        {
            lock (_gate) return _progress.Clone();
        }

        /// <summary>
        /// Production hardware code reports a stage through this ledger rather
        /// than maintaining a parallel progress clock.  A stale/late report is
        /// rejected by the lease and can never overwrite a terminal snapshot.
        /// </summary>
        public bool TryAdvanceProgress(
            StopSafetyStage stage,
            string detail,
            bool? active = null,
            bool? offSubmitted = null,
            bool? powerStarted = null,
            bool? physicalSafe = null)
        {
            return PublishStage(
                _lease,
                stage,
                detail,
                offSubmitted,
                powerStarted,
                physicalSafe,
                active);
        }

        /// <summary>Accepts only monotonic source/version evidence.</summary>
        public bool TryRecordMaterialProgress(
            string source,
            long evidenceVersion,
            string detail = null)
        {
            StopSafetyTransactionLease lease;
            lock (_gate) lease = _lease;
            if (lease == null) return false;
            return TryRecordMaterialProgress(lease, source, evidenceVersion, detail);
        }

        /// <summary>
        /// Starts or joins the current physical transaction. Historical
        /// terminal results are audit evidence only; a later request starts a
        /// new idempotent generation.
        /// </summary>
        public async Task<StopSafetyResult> StopAsync(
            StopContext context,
            CancellationToken callerCancellationToken = default(CancellationToken))
        {
            var receipt = await StopWithReceiptAsync(context, callerCancellationToken)
                .ConfigureAwait(false);
            return receipt.Result;
        }

        public async Task<StopRequestReceipt> StopWithReceiptAsync(
            StopContext context,
            CancellationToken callerCancellationToken = default(CancellationToken))
        {
            context = context ?? StopContext.Legacy(nameof(StopWithReceiptAsync));
            var requestedUtc = _clock.UtcNow;
            StopSafetyResult previous;
            bool joined;
            lock (_gate)
            {
                previous = _lastTerminalResult?.Clone();
                joined = _activeTask != null && !_activeTask.IsCompleted;
            }
            var result = await GetOrStartStopTask(context, callerCancellationToken)
                .ConfigureAwait(false);
            return new StopRequestReceipt
            {
                RequestId = Guid.NewGuid(),
                Source = context.Source,
                RequestedUtc = requestedUtc,
                StartedUtc = result?.StartedUtc ?? requestedUtc,
                CompletedUtc = _clock.UtcNow,
                PhysicalTransactionId = result?.SafetyTransactionId ?? Guid.Empty,
                JoinedActiveTransaction = joined,
                PreviousTransactionId = previous?.SafetyTransactionId ?? Guid.Empty,
                PreviousOutcome = previous?.Outcome ?? StopSafetyOutcome.Unknown,
                Result = result?.Clone()
            };
        }

        private Task<StopSafetyResult> GetOrStartStopTask(
            StopContext context,
            CancellationToken callerCancellationToken)
        {
            context = context ?? StopContext.Legacy(nameof(StopAsync));
            lock (_gate)
            {
                if (_activeTask != null && !_activeTask.IsCompleted)
                    return _activeTask;

                var generation = _generationProvider == null
                    ? ++_nextGeneration
                    : _generationProvider();
                if (generation <= 0)
                    generation = ++_nextGeneration;
                var transactionId = Guid.NewGuid();
                var startedUtc = _clock.UtcNow;
                var runId = _runIdProvider();
                var runEpoch = _runEpochProvider();
                var cts = new CancellationTokenSource();
                var lease = new StopSafetyTransactionLease(
                    transactionId,
                    generation,
                    runId,
                    runEpoch,
                    startedUtc,
                    startedUtc + _options.HardDeadline,
                    cts);
                _lease = lease;
                _safeIdleIssued = 0;
                _cachedTerminalResult = null;
                _cachedTimedOut = null;
                _materialEvidence.Clear();
                _progress = new StopSafetyProgressSnapshot
                {
                    TransactionId = transactionId,
                    RunId = runId,
                    RunEpoch = runEpoch,
                    Generation = generation,
                    ProgressVersion = 1,
                    StartedUtc = startedUtc,
                    StageStartedUtc = startedUtc,
                    HardDeadlineUtc = lease.HardDeadlineUtc,
                    StageNoProgressGraceMs = _options.StageNoProgressGraceMs,
                    LastMaterialProgressUtc = startedUtc,
                    Active = true,
                    Detail = "Stop safety transaction reserved"
                };
                PublishProgressLocked();
                // Cancellation is intentionally not linked to the caller token.
                // The token is accepted for API symmetry and for callers that
                // only want to cancel their await.
                _activeTask = RunTransactionAsync(context, lease);
                return _activeTask;
            }
        }

        private async Task<StopSafetyResult> RunTransactionAsync(
            StopContext context,
            StopSafetyTransactionLease lease)
        {
            var transaction = new StopSafetyTransactionContext(
                context,
                lease.TransactionId,
                lease.Generation,
                CaptureProgress().RunId,
                CaptureProgress().RunEpoch,
                lease.StartedUtc,
                lease.HardDeadlineUtc,
                lease.Cancellation.Token);
            // The first hardware stage may contain a synchronous DAQ/DO
            // prefix before its first await.  Keep that prefix off the caller
            // and the UI/StopAll gate; the lease still becomes authoritative
            // before this worker is scheduled.
            var coreTask = Task.Factory.StartNew(
                    () => RunStagesAsync(transaction, lease),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();
            lock (_gate) _transactionCoreTask = coreTask;
            var deadlineTask = _clock.Delay(_options.HardDeadline, CancellationToken.None);
            var completed = await Task.WhenAny(coreTask, deadlineTask).ConfigureAwait(false);

            // If both completed in the same scheduling turn, a completed core
            // wins only while the lease is still active; otherwise the deadline
            // CAS owns the terminal state.
            if (completed == coreTask && lease.Active)
            {
                try
                {
                    var result = await coreTask.ConfigureAwait(false);
                    if (lease.TryComplete())
                        return CompleteTransaction(lease, result);
                }
            catch (Exception ex)
            {
                return EnterStageFailureTerminal(
                    transaction,
                    StopSafetyStage.None,
                    lease,
                    "StageFailure:None:" + (ex.Message ?? "unexpected runner exception"),
                    null);
            }
            }

            if (!lease.TryExpire())
            {
                // Another path already completed the lease.  Its task remains
                // authoritative; observing the core here avoids an unobserved
                // exception without changing the published terminal state.
                try { return await coreTask.ConfigureAwait(false); }
                catch { return BuildTimeoutResult(transaction, "Stop lease was terminal"); }
            }

            var timeout = BuildTimeoutResult(
                transaction,
                "Stop safety hard deadline exceeded; takeover required");
            // Publish the immutable terminal fact before running any safety
            // hardware.  A stuck NI/PSU call must not prevent watchdog/UI
            // observers from seeing the takeover decision.
            PublishTerminal(lease, StopSafetyStage.TimedOut, timeout.StageError, timeout);
            lock (_gate)
            {
                _cachedTimedOut = timeout.Clone();
                _cachedTerminalResult = timeout.Clone();
                if (!coreTask.IsCompleted && _orphanCore == null)
                    _orphanCore = coreTask;
            }
            ScheduleCancellation(lease);
            ScheduleSafeIdleOnce(transaction, timeout.StageError);
            ObserveOrphan(coreTask, lease);
            return timeout;
        }

        private async Task<StopSafetyResult> RunStagesAsync(
            StopSafetyTransactionContext transaction,
            StopSafetyTransactionLease lease)
        {
            StopSafetyResult latest = null;
            foreach (var stage in _options.GetValidatedStages())
            {
                if (!lease.Active)
                    return BuildTimeoutResult(transaction, "Stop lease expired before stage " + stage);
                PublishStage(lease, stage, "Entering " + stage);
                StopSafetyPortResult stageResult;
                try
                {
                    // Every port invocation, including the synchronous
                    // Freeze prefix, is supervised by its own stage deadline
                    // in addition to the process-wide hard deadline.  This
                    // prevents a stuck NI/CTS detach from hiding the sticky
                    // takeover heartbeat until 45 seconds elapse.
                    var stageDeadline = _options.StageDeadline == null
                        ? TimeSpan.Zero
                        : _options.StageDeadline(stage);
                    // Register the deadline before scheduling the port call.
                    // This closes the tiny race where a synchronous prefix
                    // could start (and a virtual/test clock could advance)
                    // before its watchdog delay had an entry.
                    var stageDeadlineTask = stageDeadline > TimeSpan.Zero
                        ? _clock.Delay(stageDeadline, CancellationToken.None)
                        : null;
                    // Start the port call on an independent scheduler after
                    // registering its deadline.  ExecuteStageAsync is allowed
                    // to contain a synchronous DAQ/NI prefix; invoking it
                    // inline would make the deadline ineffective for that
                    // prefix.
                    var stageTask = Task.Factory.StartNew(
                            () => _port.ExecuteStageAsync(
                                stage,
                                transaction,
                                lease.Cancellation.Token),
                            CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach,
                            TaskScheduler.Default)
                        .Unwrap();
                    if (stageDeadline > TimeSpan.Zero)
                    {
                        var stageCompleted = await Task.WhenAny(
                                stageTask,
                                stageDeadlineTask)
                            .ConfigureAwait(false);
                        if (stageCompleted != stageTask)
                        {
                            // A stage timeout is a process-level stop decision,
                            // not an ordinary stage failure.  Expire the lease
                            // before returning so RunTransactionAsync cannot
                            // publish a normal Completed result, retain one
                            // cached takeover result for every re-entry, and
                            // register at most one non-cancellable stage task as
                            // the orphan.  Late completions are observed but
                            // can never write progress over the terminal
                            // snapshot.
                            return EnterStageDeadlineTimeout(
                                transaction,
                                stage,
                                stageTask,
                                lease);
                        }
                    }
                    stageResult = await stageTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return EnterStageFailureTerminal(
                        transaction,
                        stage,
                        lease,
                        "StageFailure:" + stage + ":" + (ex.Message ?? "stage threw"),
                        null);
                }

                // A late stage is never allowed to publish progress over an
                // expired/terminal lease.
                if (!lease.Active)
                    return BuildTimeoutResult(transaction, "Late stage result ignored: " + stage);
                if (stageResult == null)
                    stageResult = StopSafetyPortResult.Failure("Stage returned null: " + stage);
                if (!stageResult.Succeeded)
                {
                    // A failed stage may still have produced a one-way
                    // physical edge (for example PSU Disable was submitted
                    // and then its immediate acknowledgement failed).  Apply
                    // only nullable evidence before publishing the terminal
                    // failure; never synthesize false edges.
                    PublishStage(lease, stage, stageResult.Detail ?? stageResult.Error ?? string.Empty,
                        stageResult.PhysicalOffSubmitted,
                        stageResult.PowerDisableStarted,
                        stageResult.PhysicalSafe);
                    return EnterStageFailureTerminal(
                        transaction,
                        stage,
                        lease,
                        "StageFailure:" + stage + ":" +
                            (stageResult.Error ?? "Stop stage failed"),
                        null);
                }
                latest = stageResult.Result ?? latest;
                PublishMaterialIfAccepted(lease, stageResult);
                PublishStage(lease, stage, stageResult.Detail ?? string.Empty,
                    stageResult.PhysicalOffSubmitted,
                    stageResult.PowerDisableStarted,
                    stageResult.PhysicalSafe);
            }

            latest = latest ?? new StopSafetyResult();
            // The legacy production port returns the detailed physical
            // evidence without assigning an outcome.  Normalize it here, in
            // the one transaction owner, instead of allowing a wrapper to
            // turn an unconfirmed stop into CompletedSafe.
            var fullyConfirmed = latest.PhysicalSafetyConfirmed &&
                                 latest.PersistenceBoundaryConfirmed &&
                                 latest.LogicalQuiescenceConfirmed &&
                                 !latest.DataContinuityCompromised;
            var timedOut = latest.TimedOut || latest.LastStage == StopSafetyStage.TimedOut;
            latest.LastStage = timedOut ? StopSafetyStage.TimedOut : StopSafetyStage.Completed;
            latest.TimedOut = timedOut;
            latest.RequiresProcessRestart = timedOut || !fullyConfirmed;
            latest.Outcome = timedOut
                ? StopSafetyOutcome.SafeButRestartRequired
                : fullyConfirmed
                    ? StopSafetyOutcome.CompletedSafe
                    : latest.PhysicalSafetyConfirmed
                        ? StopSafetyOutcome.SafeButRestartRequired
                        : StopSafetyOutcome.PhysicalSafetyUnconfirmed;
            latest.PhysicalOffSubmitted = true;
            latest.PowerDisableStarted = true;
            latest.StartedUtc = transaction.StartedUtc;
            latest.CompletedUtc = _clock.UtcNow;
            latest.CorrelationId = transaction.StopContext.CorrelationId ??
                                   transaction.TransactionId.ToString("N");
            latest.Source = transaction.StopContext.Source;
            latest.RunId = transaction.RunId;
            StampSafetyIdentity(latest, lease);
            PublishTerminal(
                lease,
                timedOut ? StopSafetyStage.TimedOut : StopSafetyStage.Completed,
                timedOut
                    ? "Stop safety core returned a hard-timeout result"
                    : latest.RequiresProcessRestart
                        ? "Stop safety completed with restart required"
                        : "Stop safety transaction completed",
                latest);
            return latest;
        }

        private void ObserveLateStage(
            Task<StopSafetyPortResult> stageTask,
            StopSafetyStage stage,
            StopSafetyTransactionLease lease)
        {
            if (stageTask == null || stageTask.IsCompleted) return;
            _ = stageTask.ContinueWith(
                completed =>
                {
                    try { _ = completed.Exception; }
                    catch { }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private StopSafetyResult EnterStageDeadlineTimeout(
            StopSafetyTransactionContext transaction,
            StopSafetyStage stage,
            Task<StopSafetyPortResult> stageTask,
            StopSafetyTransactionLease lease)
        {
            var reason = "StageDeadlineExceeded:" + stage;
            var timeout = BuildTimeoutResult(transaction, reason);
            timeout.LastStage = StopSafetyStage.TimedOut;
            timeout.StageError = reason;

            if (!lease.TryExpire())
            {
                StopSafetyResult cachedResult = null;
                lock (_gate)
                {
                    if (_cachedTimedOut != null)
                    {
                        // The process deadline may win the same virtual-clock
                        // tick just before this stage's own deadline callback
                        // enters.  Preserve the actual stuck stage as the
                        // single orphan rather than clearing the parent core
                        // when it returns the already-cached timeout.
                        if (stageTask != null && !stageTask.IsCompleted &&
                            (_orphanCore == null ||
                             ReferenceEquals(_orphanCore, _transactionCoreTask) ||
                             _orphanCore.IsCompleted))
                            _orphanCore = stageTask;
                        cachedResult = (_cachedTerminalResult ?? _cachedTimedOut)
                            .Clone(reused: true);
                    }
                }
                if (cachedResult != null)
                {
                    ObserveOrphan(stageTask, lease);
                    return cachedResult;
                }
                return timeout;
            }

            // Keep the terminal heartbeat/cache path free of synchronous
            // hardware calls.  SafeIdle is still exactly-once, but executes
            // asynchronously after the authoritative terminal is visible.
            PublishTerminal(lease, StopSafetyStage.TimedOut, reason, timeout);
            lock (_gate)
            {
                if (_cachedTimedOut == null)
                    _cachedTimedOut = timeout.Clone();
                if (_cachedTerminalResult == null)
                    _cachedTerminalResult = timeout.Clone();
                if (stageTask != null && !stageTask.IsCompleted &&
                    (_orphanCore == null ||
                     ReferenceEquals(_orphanCore, _transactionCoreTask) ||
                     _orphanCore.IsCompleted))
                    _orphanCore = stageTask;
            }
            ScheduleCancellation(lease);
            ScheduleSafeIdleOnce(transaction, reason);
            // Observe even an already-completed task so a racing fault cannot
            // become unobserved; ObserveOrphan clears the single slot only
            // when the exact task is complete.
            ObserveOrphan(stageTask, lease);
            return timeout;
        }

        private StopSafetyResult CompleteTransaction(
            StopSafetyTransactionLease lease,
            StopSafetyResult result)
        {
            result = result ?? new StopSafetyResult();
            StampSafetyIdentity(result, lease);
            result.LastStage = StopSafetyStage.Completed;
            result.CompletedUtc = result.CompletedUtc == default(DateTime)
                ? _clock.UtcNow
                : result.CompletedUtc;
            result.CorrelationId = string.IsNullOrWhiteSpace(result.CorrelationId)
                ? lease.TransactionId.ToString("N")
                : result.CorrelationId;
            PublishTerminal(lease, StopSafetyStage.Completed,
                "Stop safety transaction completed", result);
            return result;
        }

        private static void StampSafetyIdentity(
            StopSafetyResult result,
            StopSafetyTransactionLease lease)
        {
            if (result == null || lease == null) return;
            result.SafetyTransactionId = lease.TransactionId;
            result.RunId = lease.RunId;
            result.RunEpoch = lease.RunEpoch;
            result.SafetyBoundaryGeneration = lease.Generation;
        }

        private StopSafetyResult EnterStageFailureTerminal(
            StopSafetyTransactionContext transaction,
            StopSafetyStage stage,
            StopSafetyTransactionLease lease,
            string reason,
            Task stageTask)
        {
            var failure = BuildFailureResult(
                transaction,
                stage,
                new InvalidOperationException(reason ?? "Stop stage failed"));
            failure.StageError = reason ?? failure.StageError;
            failure.LastStage = stage;
            failure.TimedOut = false;
            failure.RequiresProcessRestart = true;

            // Make the failure terminal before any emergency hardware call.
            // Stop/FinalExit re-entry sees the cached result and cannot create
            // a second runner core or issue a second SafeIdle sequence.
            lease.TryTerminal();
            PublishTerminal(lease, stage, failure.StageError, failure);
            lock (_gate)
            {
                if (_cachedTerminalResult == null)
                    _cachedTerminalResult = failure.Clone();
                if (stageTask != null && !stageTask.IsCompleted &&
                    (_orphanCore == null || _orphanCore.IsCompleted))
                    _orphanCore = stageTask;
            }
            ScheduleSafeIdleOnce(transaction, failure.StageError);
            ObserveOrphan(stageTask, lease);
            return failure;
        }

        private void PublishMaterialIfAccepted(
            StopSafetyTransactionLease lease,
            StopSafetyPortResult result)
        {
            if (!result.MaterialProgress ||
                string.IsNullOrWhiteSpace(result.EvidenceSource) ||
                result.EvidenceVersion <= 0)
                return;
            lock (_gate)
            {
                if (!lease.Active ||
                    (_materialEvidence.TryGetValue(result.EvidenceSource, out var previous) &&
                     result.EvidenceVersion <= previous))
                    return;
                _materialEvidence[result.EvidenceSource] = result.EvidenceVersion;
                _progress.MaterialEvidenceVersion = Math.Max(
                    _progress.MaterialEvidenceVersion,
                    result.EvidenceVersion);
                _progress.LastMaterialProgressUtc = _clock.UtcNow <
                    _progress.LastMaterialProgressUtc
                    ? _progress.LastMaterialProgressUtc
                    : _clock.UtcNow;
                _progress.ProgressVersion++;
                PublishProgressLocked();
            }
        }

        private bool PublishStage(
            StopSafetyTransactionLease lease,
            StopSafetyStage stage,
            string detail,
            bool? offSubmitted = null,
            bool? powerStarted = null,
            bool? physicalSafe = null,
            bool? active = null)
        {
            lock (_gate)
            {
                if (lease == null || !lease.Active || _progress.Generation != lease.Generation)
                    return false;
                var previousStage = _progress.Stage;
                if ((int)stage < (int)previousStage) return false;
                var changed = previousStage != stage;
                var edge = (offSubmitted.HasValue &&
                            offSubmitted.Value != _progress.PhysicalOffSubmitted) ||
                           (powerStarted.HasValue &&
                            powerStarted.Value != _progress.PowerDisableStarted) ||
                           (physicalSafe.HasValue &&
                            physicalSafe.Value != _progress.PhysicalSafe) ||
                           (active.HasValue && active.Value != _progress.Active);
                var now = _clock.UtcNow;
                if (now < _progress.LastMaterialProgressUtc)
                    now = _progress.LastMaterialProgressUtc;
                if (changed)
                {
                    _progress.Stage = stage;
                    _progress.StageStartedUtc = now;
                    var stageDeadline = _options.StageDeadline == null
                        ? TimeSpan.Zero
                        : _options.StageDeadline(stage);
                    _progress.StageHardDeadlineUtc = now + stageDeadline;
                    _progress.StageNoProgressGraceMs = _options.StageNoProgressGraceMs;
                }
                if (changed || edge)
                {
                    _progress.ProgressVersion = Math.Max(1, _progress.ProgressVersion + 1);
                    _progress.LastMaterialProgressUtc = now;
                }
                if (offSubmitted.HasValue)
                    _progress.PhysicalOffSubmitted = offSubmitted.Value;
                if (powerStarted.HasValue)
                    _progress.PowerDisableStarted = powerStarted.Value;
                if (physicalSafe.HasValue)
                    _progress.PhysicalSafe = physicalSafe.Value;
                if (active.HasValue)
                    _progress.Active = active.Value;
                if (detail != null) _progress.Detail = detail;
                PublishProgressLocked();
                return true;
            }
        }

        private bool TryRecordMaterialProgress(
            StopSafetyTransactionLease lease,
            string source,
            long evidenceVersion,
            string detail)
        {
            if (string.IsNullOrWhiteSpace(source) || evidenceVersion <= 0)
                return false;
            lock (_gate)
            {
                if (!lease.Active ||
                    (_materialEvidence.TryGetValue(source, out var previous) &&
                     evidenceVersion <= previous) ||
                    !_progress.Active ||
                    _progress.Stage == StopSafetyStage.None ||
                    _progress.Stage == StopSafetyStage.Completed ||
                    _progress.Stage == StopSafetyStage.TimedOut)
                    return false;
                _materialEvidence[source] = evidenceVersion;
                var now = _clock.UtcNow;
                if (now < _progress.LastMaterialProgressUtc)
                    now = _progress.LastMaterialProgressUtc;
                _progress.LastMaterialProgressUtc = now;
                _progress.MaterialEvidenceVersion = Math.Max(
                    _progress.MaterialEvidenceVersion,
                    evidenceVersion);
                _progress.ProgressVersion = Math.Max(1, _progress.ProgressVersion + 1);
                if (detail != null) _progress.Detail = detail;
                PublishProgressLocked();
                return true;
            }
        }

        private void PublishTerminal(
            StopSafetyTransactionLease lease,
            StopSafetyStage stage,
            string reason,
            StopSafetyResult result)
        {
            lock (_gate)
            {
                if (_progress.Generation != lease.Generation ||
                    _progress.TransactionId != lease.TransactionId)
                    return;
                _progress.Stage = stage;
                _progress.Active = false;
                _progress.TimedOut = result?.TimedOut == true || stage == StopSafetyStage.TimedOut;
                _progress.TakeoverRequired = result?.RequiresProcessRestart == true ||
                                             _progress.TimedOut;
                _progress.TerminalReason = reason ?? string.Empty;
                _progress.Detail = _progress.TerminalReason;
                _progress.ProgressVersion = Math.Max(1, _progress.ProgressVersion + 1);
                _progress.LastMaterialProgressUtc = _clock.UtcNow;
                if (result != null)
                    _lastTerminalResult = result.Clone();
                PublishProgressLocked();
            }
            if (stage == StopSafetyStage.Completed)
                lease.TryComplete();
            if (stage == StopSafetyStage.Completed)
            {
                lock (_gate)
                {
                    if (_transactionCoreTask == null ||
                        _transactionCoreTask.IsCompleted)
                        _transactionCoreTask = null;
                }
            }
        }

        private void PublishProgressLocked()
        {
            _publishProgress?.Invoke(_progress.Clone());
        }

        private void IssueSafeIdleOnce(
            StopSafetyTransactionContext transaction,
            string reason)
        {
            if (Interlocked.Exchange(ref _safeIdleIssued, 1) != 0) return;
            try { _port.EnterSafeIdleOnce(transaction, reason ?? "Stop safety takeover"); }
            catch { /* fail-closed: the lease remains terminal/takeover-required */ }
        }

        private void ScheduleSafeIdleOnce(
            StopSafetyTransactionContext transaction,
            string reason)
        {
            if (Interlocked.Exchange(ref _safeIdleIssued, 1) != 0) return;
            try
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        _port.EnterSafeIdleOnce(
                            transaction,
                            reason ?? "Stop safety takeover");
                    }
                    catch
                    {
                        // Terminal publication is already durable; a failed
                        // emergency action remains fail-closed and is visible
                        // through the takeover result.
                    }
                });
            }
            catch
            {
                // If scheduling itself fails, retain the once bit and leave
                // the process in the already-published takeover state.
            }
        }

        private static void ScheduleCancellation(
            StopSafetyTransactionLease lease)
        {
            if (lease == null) return;
            try
            {
                _ = Task.Run(() =>
                {
                    try { lease.Cancellation.Cancel(); }
                    catch { }
                });
            }
            catch
            {
                // The lease is already terminal; failure to schedule the
                // advisory cancellation cannot hide the published takeover.
            }
        }

        private void ObserveOrphan(
            Task coreTask,
            StopSafetyTransactionLease lease)
        {
            if (coreTask == null) return;
            _ = coreTask.ContinueWith(
                completed =>
                {
                    try { _ = completed.Exception; }
                    catch { }
                    lock (_gate)
                    {
                        if (ReferenceEquals(_orphanCore, completed))
                            _orphanCore = null;
                        if (_transactionCoreTask != null &&
                            _transactionCoreTask.IsCompleted)
                            _transactionCoreTask = null;
                    }
                    try { lease.Cancellation.Dispose(); }
                    catch { }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private StopSafetyResult BuildFailureResult(
            StopSafetyTransactionContext transaction,
            StopSafetyStage stage,
            Exception error)
        {
            return new StopSafetyResult
            {
                Outcome = StopSafetyOutcome.PhysicalSafetyUnconfirmed,
                LastStage = stage,
                RequiresProcessRestart = true,
                TimedOut = false,
                PhysicalOffSubmitted = false,
                PowerDisableStarted = false,
                StageError = error?.Message ?? "Stop stage failed",
                Source = transaction.StopContext.Source,
                CorrelationId = transaction.StopContext.CorrelationId ??
                                transaction.TransactionId.ToString("N"),
                SafetyTransactionId = transaction.TransactionId,
                RunId = transaction.RunId,
                RunEpoch = transaction.RunEpoch,
                SafetyBoundaryGeneration = transaction.Generation,
                StartedUtc = transaction.StartedUtc,
                CompletedUtc = _clock.UtcNow
            };
        }

        private StopSafetyResult BuildTimeoutResult(
            StopSafetyTransactionContext transaction,
            string reason)
        {
            return new StopSafetyResult
            {
                Outcome = StopSafetyOutcome.SafeButRestartRequired,
                LastStage = StopSafetyStage.TimedOut,
                TimedOut = true,
                RequiresProcessRestart = true,
                PhysicalOffSubmitted = true,
                PowerDisableStarted = true,
                StageError = reason ?? "Stop safety timeout",
                Source = transaction.StopContext.Source,
                CorrelationId = transaction.StopContext.CorrelationId ??
                                transaction.TransactionId.ToString("N"),
                SafetyTransactionId = transaction.TransactionId,
                RunId = transaction.RunId,
                RunEpoch = transaction.RunEpoch,
                SafetyBoundaryGeneration = transaction.Generation,
                StartedUtc = transaction.StartedUtc,
                CompletedUtc = _clock.UtcNow
            };
        }

        private StopSafetyResult BuildTimeoutResultLocked(string reason)
        {
            var transaction = new StopSafetyTransactionContext(
                StopContext.Legacy(nameof(StopSafetyTransactionRunner)),
                _progress.TransactionId == Guid.Empty ? Guid.NewGuid() : _progress.TransactionId,
                _progress.Generation,
                _progress.RunId,
                _progress.RunEpoch,
                _progress.StartedUtc,
                _progress.HardDeadlineUtc,
                CancellationToken.None);
            return BuildTimeoutResult(transaction, reason);
        }
    }
}
