using System;
using System.Threading;
using MTTFTest.Watchdog.Client;

namespace MTEmbTest
{
    /// <summary>
    /// The process-wide shutdown transaction.  Runtime owns exactly one
    /// instance of this coordinator; every retry runs through the same
    /// execution gate and the same retained owner.
    /// </summary>
    internal sealed class RuntimeShutdownRetentionCoordinator
    {
        private readonly object _executionGate = new object();
        private readonly IRuntimeTransportShutdownPort _transport;
        private readonly IRuntimeShutdownOwnershipPort _ownership;
        private readonly IRuntimeShutdownSessionPort _session;
        private readonly IRuntimeShutdownPipelinePort _pipeline;
        private readonly IRuntimeShutdownJournalPort _journal;
        private RuntimeShutdownRetentionOwner _owner;
        // Terminal evidence is retained as an immutable value, not as a
        // non-retained owner.  This prevents repeated shutdown callers from
        // re-entering the Engine after the transaction has completed while
        // keeping the owner slot empty.
        private RuntimeShutdownReceipt _terminalReceipt;
        private long _version;
        private bool _executionActive;

        internal Action<RuntimeShutdownRetentionOwner> StagePublishedObserver { get; set; }

        internal RuntimeShutdownRetentionCoordinator(
            IRuntimeTransportShutdownPort transport,
            IRuntimeShutdownOwnershipPort ownership,
            IRuntimeShutdownSessionPort session,
            IRuntimeShutdownPipelinePort pipeline,
            IRuntimeShutdownJournalPort journal)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        }

        internal RuntimeShutdownRetentionOwner Capture()
        {
            lock (_executionGate) return _owner;
        }

        internal RuntimeShutdownReceipt LatestReceipt()
        {
            lock (_executionGate) return _owner?.RuntimeReceipt ?? _terminalReceipt;
        }

        internal bool EnsurePreviousTerminal()
        {
            lock (_executionGate)
            {
                WaitForExecutionIdleLocked();
                if (_owner == null) return true;
                if (!_owner.IsTerminal) return false;
                _owner = null;
                return true;
            }
        }

        internal RuntimeShutdownReceipt ShutdownOrRetry()
        {
            lock (_executionGate)
            {
                WaitForExecutionIdleLocked();
                _executionActive = true;
            }
            try
            {
                // Hardware, Engine, pipeline and journal ports are always
                // invoked outside _executionGate.  The active flag keeps a
                // single transaction owner while allowing callbacks and
                // other snapshots to acquire the short state lock.
                return ShutdownOrRetryCore();
            }
            finally
            {
                lock (_executionGate)
                {
                    _executionActive = false;
                    Monitor.PulseAll(_executionGate);
                }
            }
        }

        private void WaitForExecutionIdleLocked()
        {
            while (_executionActive)
                Monitor.Wait(_executionGate);
        }

        private RuntimeShutdownReceipt ShutdownOrRetryCore()
        {
            RuntimeShutdownRetentionOwner retained;
            lock (_executionGate)
            {
                retained = _owner;
                if (retained != null && retained.IsTerminal)
                {
                    _owner = null;
                    retained = null;
                }
            }

            var active = _ownership.CaptureActive();
            lock (_executionGate)
            {
                if (active == null && _terminalReceipt != null)
                    return _terminalReceipt;
                if (active != null) _terminalReceipt = null;
            }
            if (retained != null && active != null &&
                !ReferenceEquals(retained.Context, active))
                return CreatePreviousRuntimeShutdownIncomplete(retained);
            if (retained != null && active != null &&
                retained.Phase == RuntimeShutdownRetentionPhase.RetainedFailure &&
                (retained.FailureKind ?? string.Empty).IndexOf(
                    "FinalStopSafetyResultNotDurable",
                    StringComparison.Ordinal) >= 0)
            {
                if (!_session.IsFinalSafetyResultDurable(active))
                    return retained.RuntimeReceipt;
                if (!_ownership.TryDetachActive(active))
                    return CreatePreviousRuntimeShutdownIncomplete(active, retained.Version);
                var resumed = retained.With(
                    retained.ClosingAttempt,
                    NextVersion(retained.Version),
                    retained.EngineReceipt,
                    retained.RuntimeReceipt,
                    retained.EngineShutdownStarted,
                    retained.SkipEngineShutdown,
                    retained.JournalFlushCompleted,
                    retained.JournalDisposed,
                    retained.MarkOutcome,
                    string.Empty,
                    RuntimeShutdownRetentionPhase.SessionClosing);
                if (!TryPublishOwnerStage(retained, resumed))
                    return CaptureRetainedReceiptOr(
                        retained.RuntimeReceipt,
                        retained.EngineReceipt);
                return ExecuteRetainedShutdown(resumed);
            }
            if (retained != null) return ExecuteRetainedShutdown(retained);

            if (active == null)
            {
                var engineReceipt = _transport.ShutdownWithReceipt();
                var noWork = CreateNoWorkReceipt(engineReceipt);
                lock (_executionGate) _terminalReceipt = noWork;
                return noWork;
            }

            Volatile.Write(ref active.State.SessionClosing, 1);
            active.TrySetPipelineState(RuntimeCallbackPipelineState.Closing);
            active.IngressGate.BeginClosing();
            var markOutcome = _session.TryMarkSessionClosing(active);
            var ownerVersion = NextVersion(0);
            if (markOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch ||
                markOutcome == RuntimeShutdownMarkOutcome.TombstonePersistenceFailed)
            {
                var failed = markOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch
                    ? CreateIdentityMismatchReceipt(active, ownerVersion)
                    : CreateStageFailureReceipt(
                        active,
                        ownerVersion,
                        null,
                        "ClosingTombstonePersistence",
                        null);
                var failedOwner = new RuntimeShutdownRetentionOwner(
                    active, active.ValidatedAttachIdentity, active.ClosingAttempt,
                    ownerVersion, null, failed, false, true, false, false,
                    markOutcome,
                    markOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch
                        ? "ShutdownIdentityMismatch"
                        : "ClosingTombstonePersistenceFailed",
                    RuntimeShutdownRetentionPhase.RetainedFailure);
                lock (_executionGate) _owner = failedOwner;
                NotifyStagePublished(failedOwner);
                return failed;
            }

            var owner = new RuntimeShutdownRetentionOwner(
                active, active.ValidatedAttachIdentity, active.ClosingAttempt,
                ownerVersion, null, null, false, false, false, false,
                markOutcome, string.Empty, RuntimeShutdownRetentionPhase.OwnershipReserved);
            lock (_executionGate) _owner = owner;
            NotifyStagePublished(owner);
            // The exact final StopSafetyResult must be durable before Engine,
            // callback pipeline or journal ownership can be detached. A close
            // fence with no StopSafety transaction represents a no-controller
            // session and is explicitly accepted by the production port.
            if (!_session.IsFinalSafetyResultDurable(active))
            {
                var blocked = CreateStageFailureReceipt(
                    active,
                    NextVersion(owner.Version),
                    null,
                    "FinalStopSafetyResultNotDurable",
                    null);
                var blockedOwner = owner.With(
                    owner.ClosingAttempt,
                    blocked.RetentionVersion,
                    null,
                    blocked,
                    false,
                    false,
                    false,
                    false,
                    owner.MarkOutcome,
                    blocked.TerminalReason,
                    RuntimeShutdownRetentionPhase.RetainedFailure);
                return PublishStageOrReturn(owner, blockedOwner, blocked);
            }
            if (!_ownership.TryDetachActive(active))
                return CreatePreviousRuntimeShutdownIncomplete(active, ownerVersion);
            var closingOwner = owner.With(
                owner.ClosingAttempt, NextVersion(owner.Version), null, null,
                false, false, false, false, owner.MarkOutcome, owner.FailureKind,
                RuntimeShutdownRetentionPhase.SessionClosing);
            if (!TryPublishOwnerStage(owner, closingOwner))
                return CaptureRetainedReceiptOr(null, null);
            return ExecuteRetainedShutdown(closingOwner);
        }

        private RuntimeShutdownReceipt ExecuteRetainedShutdown(
            RuntimeShutdownRetentionOwner owner)
        {
            if (owner == null) return CreateNoWorkReceipt(null);
            var context = owner.Context;
            var engineReceipt = owner.EngineReceipt;
            var engineStarted = owner.EngineShutdownStarted;
            var skipEngineShutdown = owner.SkipEngineShutdown;
            var currentOwner = owner;

            // An Engine identity mismatch is sticky.  It must not be hidden
            // by a later pipeline close and must never close another session.
            if (currentOwner.MarkOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch)
                return currentOwner.RuntimeReceipt ??
                    CreateIdentityMismatchReceipt(context, currentOwner.Version);
            if (currentOwner.MarkOutcome == RuntimeShutdownMarkOutcome.TombstonePersistenceFailed)
            {
                var retryOutcome = _session.TryMarkSessionClosing(context);
                if (retryOutcome == RuntimeShutdownMarkOutcome.TombstonePersistenceFailed ||
                    retryOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch)
                    return currentOwner.RuntimeReceipt ?? CreateStageFailureReceipt(
                        context,
                        currentOwner.Version,
                        currentOwner.EngineReceipt,
                        "ClosingTombstonePersistence",
                        null);
                if (!_ownership.TryDetachActive(context))
                    return CreatePreviousRuntimeShutdownIncomplete(context, currentOwner.Version);
                var recoveredOwner = currentOwner.With(
                    currentOwner.ClosingAttempt,
                    NextVersion(currentOwner.Version),
                    currentOwner.EngineReceipt,
                    currentOwner.RuntimeReceipt,
                    currentOwner.EngineShutdownStarted,
                    currentOwner.SkipEngineShutdown,
                    currentOwner.JournalFlushCompleted,
                    currentOwner.JournalDisposed,
                    retryOutcome,
                    string.Empty,
                    RuntimeShutdownRetentionPhase.SessionClosing);
                if (!TryPublishOwnerStage(currentOwner, recoveredOwner))
                    return CaptureRetainedReceiptOr(
                        currentOwner.RuntimeReceipt,
                        currentOwner.EngineReceipt);
                currentOwner = recoveredOwner;
                owner = recoveredOwner;
            }

            // Phase 1: obtain a candidate Engine receipt.  A null/throwing
            // candidate leaves the previous incomplete receipt intact.
            if (!skipEngineShutdown &&
                !WatchdogRuntime.IsExactEngineTerminal(context, engineReceipt))
            {
                var previousEngineReceipt = engineReceipt;
                ShutdownReceipt candidate = null;
                try
                {
                    engineStarted = true;
                    candidate = _transport.ShutdownWithReceipt();
                    if (candidate == null)
                        throw new InvalidOperationException("Engine shutdown returned no receipt.");
                    engineReceipt = candidate;
                }
                catch (Exception ex)
                {
                    var failedVersion = NextVersion(currentOwner.Version);
                    var failedReceipt = CreateStageFailureReceipt(
                        context, failedVersion, previousEngineReceipt, "EngineShutdown", ex);
                    var failedOwner = currentOwner.With(
                        currentOwner.ClosingAttempt, failedVersion, previousEngineReceipt,
                        failedReceipt, engineStarted, skipEngineShutdown,
                        currentOwner.JournalFlushCompleted, currentOwner.JournalDisposed,
                        currentOwner.MarkOutcome, failedReceipt.TerminalReason,
                        RuntimeShutdownRetentionPhase.RetainedFailure);
                    return PublishStageOrReturn(currentOwner, failedOwner, failedReceipt);
                }
            }

            var engineVersion = NextVersion(currentOwner.Version);
            var engineOwner = currentOwner.With(
                currentOwner.ClosingAttempt, engineVersion, engineReceipt,
                currentOwner.RuntimeReceipt, engineStarted, skipEngineShutdown,
                currentOwner.JournalFlushCompleted, currentOwner.JournalDisposed,
                currentOwner.MarkOutcome, currentOwner.FailureKind,
                RuntimeShutdownRetentionPhase.EngineShutdown);
            if (!TryPublishOwnerStage(currentOwner, engineOwner))
                return CaptureRetainedReceiptOr(currentOwner.RuntimeReceipt, engineReceipt);
            currentOwner = engineOwner;

            // No later phase may publish evidence while the exact Engine
            // terminal predicate is false.  The owner remains retryable.
            if (!WatchdogRuntime.IsExactEngineTerminal(context, engineReceipt))
            {
                var incompleteVersion = NextVersion(currentOwner.Version);
                var incompleteReceipt = CreateStageFailureReceipt(
                    context, incompleteVersion, engineReceipt,
                    "EngineReceiptIncomplete", null);
                var incompleteOwner = currentOwner.With(
                    currentOwner.ClosingAttempt, incompleteVersion, engineReceipt,
                    incompleteReceipt, engineStarted, skipEngineShutdown,
                    currentOwner.JournalFlushCompleted, currentOwner.JournalDisposed,
                    currentOwner.MarkOutcome, incompleteReceipt.TerminalReason,
                    RuntimeShutdownRetentionPhase.RetainedFailure);
                if (!TryPublishOwnerStage(currentOwner, incompleteOwner))
                    return CaptureRetainedReceiptOr(incompleteReceipt, engineReceipt);
                return incompleteReceipt;
            }

            // Phase 2: close the callback/pipeline once, reusing a terminal
            // pipeline receipt during journal-only retries.
            RuntimeShutdownReceipt runtimeReceipt = currentOwner.RuntimeReceipt;
            if (runtimeReceipt == null || !runtimeReceipt.PipelineTerminal)
            {
                try
                {
                    runtimeReceipt = _pipeline.Close(context, engineReceipt);
                    if (runtimeReceipt == null)
                        throw new InvalidOperationException("Runtime pipeline shutdown returned no receipt.");
                }
                catch (Exception ex)
                {
                    var failedVersion = NextVersion(currentOwner.Version);
                    var failedReceipt = CreateStageFailureReceipt(
                        context, failedVersion, engineReceipt, "RuntimePipelineShutdown", ex);
                    var failedOwner = currentOwner.With(
                        currentOwner.ClosingAttempt, failedVersion, engineReceipt,
                        failedReceipt, engineStarted, skipEngineShutdown,
                        currentOwner.JournalFlushCompleted, currentOwner.JournalDisposed,
                        currentOwner.MarkOutcome, failedReceipt.TerminalReason,
                        RuntimeShutdownRetentionPhase.RetainedFailure);
                    return PublishStageOrReturn(currentOwner, failedOwner, failedReceipt);
                }
            }

            var pipelineVersion = NextVersion(currentOwner.Version);
            var pipelineOwner = currentOwner.With(
                runtimeReceipt?.ClosingAttempt ?? currentOwner.ClosingAttempt,
                pipelineVersion, engineReceipt,
                runtimeReceipt, engineStarted, skipEngineShutdown,
                currentOwner.JournalFlushCompleted, currentOwner.JournalDisposed,
                currentOwner.MarkOutcome, currentOwner.FailureKind,
                RuntimeShutdownRetentionPhase.PipelineShutdown);
            if (!TryPublishOwnerStage(currentOwner, pipelineOwner))
                return CaptureRetainedReceiptOr(runtimeReceipt, engineReceipt);
            currentOwner = pipelineOwner;

            var flushCompleted = currentOwner.JournalFlushCompleted;
            var journalDisposed = currentOwner.JournalDisposed;

            // Phase 3/4: journal flush and dispose.  A real journal starts
            // with both flags false; a no-journal receipt already carries
            // explicit true flags from the pipeline boundary.
            if (runtimeReceipt != null && runtimeReceipt.PipelineTerminal &&
                WatchdogRuntime.IsExactEngineTerminal(context, engineReceipt) && context != null)
            {
                if (!flushCompleted)
                {
                    try
                    {
                        _journal.Record(context, "TransportShutdownReceipt",
                            string.Format(
                                "SessionDetached={0};AllResourcesReleased={1};AllWorkersTerminal={2};" +
                                "OwnedHandlesBefore={3};OwnedHandlesAfter={4};" +
                                "LaunchReservationTerminal={5};RetainedLaunchTerminal={6}",
                                engineReceipt.SessionDetached,
                                engineReceipt.AllResourcesReleased,
                                engineReceipt.AllWorkersTerminal,
                                engineReceipt.WorkerTermination?.OwnedHandleCountBefore ?? 0,
                                engineReceipt.WorkerTermination?.OwnedHandleCountAfter ?? 0,
                                engineReceipt.LaunchReservationTerminal,
                                engineReceipt.RetainedLaunchTerminal));
                        flushCompleted = _journal.Flush(context);
                    }
                    catch (Exception ex)
                    {
                        try { _journal.Record(context, "JournalFlushDeferred", ex.Message); }
                        catch { }
                        flushCompleted = false;
                    }
                }

                var flushVersion = NextVersion(currentOwner.Version);
                var flushReceipt = runtimeReceipt.WithRetention(
                    flushVersion, true, flushCompleted, journalDisposed,
                    runtimeReceipt.PreviousRuntimeShutdownIncomplete);
                var flushOwner = currentOwner.With(
                    currentOwner.ClosingAttempt, flushVersion, engineReceipt, flushReceipt,
                    engineStarted, skipEngineShutdown, flushCompleted, journalDisposed,
                    currentOwner.MarkOutcome, currentOwner.FailureKind,
                    RuntimeShutdownRetentionPhase.JournalFlush);
                if (!TryPublishOwnerStage(currentOwner, flushOwner))
                    return CaptureRetainedReceiptOr(flushReceipt, engineReceipt);
                currentOwner = flushOwner;
                runtimeReceipt = flushReceipt;

                if (flushCompleted && !journalDisposed)
                {
                    try { journalDisposed = _journal.Dispose(context); }
                    catch (Exception ex)
                    {
                        try { _journal.Record(context, "JournalDisposeDeferred", ex.Message); }
                        catch { }
                        journalDisposed = false;
                    }
                }
            }

            var disposeVersion = NextVersion(currentOwner.Version);
            var disposeReceipt = runtimeReceipt?.WithRetention(
                disposeVersion, true, flushCompleted, journalDisposed,
                runtimeReceipt?.PreviousRuntimeShutdownIncomplete == true);
            var disposeOwner = currentOwner.With(
                currentOwner.ClosingAttempt, disposeVersion, engineReceipt,
                disposeReceipt, engineStarted, skipEngineShutdown,
                flushCompleted, journalDisposed, currentOwner.MarkOutcome,
                currentOwner.FailureKind, RuntimeShutdownRetentionPhase.JournalDispose);
            if (!TryPublishOwnerStage(currentOwner, disposeOwner))
                return CaptureRetainedReceiptOr(disposeReceipt, engineReceipt);
            currentOwner = disposeOwner;
            runtimeReceipt = disposeReceipt;

            // Publish an observable candidate before deciding whether the
            // complete Runtime owner can be released.  This phase keeps a
            // stable retained evidence point if the final publication loses
            // its owner CAS.
            var candidateVersion = NextVersion(currentOwner.Version);
            var candidateReceipt = runtimeReceipt?.WithRetention(
                candidateVersion, true, flushCompleted, journalDisposed,
                runtimeReceipt?.PreviousRuntimeShutdownIncomplete == true);
            var candidateOwner = currentOwner.With(
                currentOwner.ClosingAttempt, candidateVersion, engineReceipt,
                candidateReceipt, engineStarted, skipEngineShutdown,
                flushCompleted, journalDisposed, currentOwner.MarkOutcome,
                currentOwner.FailureKind, RuntimeShutdownRetentionPhase.TerminalCandidate);
            if (!TryPublishOwnerStage(currentOwner, candidateOwner))
                return CaptureRetainedReceiptOr(candidateReceipt, engineReceipt);
            currentOwner = candidateOwner;
            runtimeReceipt = candidateReceipt;

            var finalVersion = NextVersion(currentOwner.Version);
            var closingTerminalPersisted =
                _session.TryCompleteSessionClosing(
                    context,
                    "RuntimeShutdownTerminal");
            var terminal = runtimeReceipt != null && runtimeReceipt.PipelineTerminal &&
                WatchdogRuntime.IsExactEngineTerminal(context, engineReceipt) &&
                flushCompleted && journalDisposed && closingTerminalPersisted;
            var finalReceipt = runtimeReceipt?.WithRetention(
                finalVersion, !terminal, flushCompleted, journalDisposed,
                runtimeReceipt?.PreviousRuntimeShutdownIncomplete == true,
                sessionClosingPersisted: closingTerminalPersisted);
            if (terminal)
            {
                lock (_executionGate)
                {
                    // A terminal receipt is returned directly.  Do not
                    // publish a Retained=false owner, even briefly; clear
                    // only the exact candidate that completed this attempt.
                    if (!ReferenceEquals(_owner, currentOwner))
                        return CaptureRetainedReceiptOr(finalReceipt, engineReceipt);
                    _owner = null;
                    _terminalReceipt = finalReceipt;
                }
                return finalReceipt ?? CreateNoWorkReceipt(engineReceipt);
            }

            var retainedFinalOwner = currentOwner.With(
                finalReceipt?.ClosingAttempt ?? currentOwner.ClosingAttempt,
                finalVersion, engineReceipt, finalReceipt, engineStarted,
                skipEngineShutdown, flushCompleted, journalDisposed,
                currentOwner.MarkOutcome, currentOwner.FailureKind,
                finalReceipt?.Disposition == RuntimeShutdownDisposition.DetachedRetained
                    ? RuntimeShutdownRetentionPhase.DetachedRetained
                    : RuntimeShutdownRetentionPhase.RetainedFailure);
            if (finalReceipt?.Disposition == RuntimeShutdownDisposition.DetachedRetained)
            {
                try
                {
                    _journal.Record(
                        context,
                        "DetachedRetained",
                        string.Format(
                            "Session={0};Attempt={1};Reason={2};Flush={3};Dispose={4};" +
                            "PipelineTerminal={5};SafeExitAllowed={6};SessionClosingPersisted={7}",
                            finalReceipt.SessionId,
                            finalReceipt.ClosingAttempt,
                            finalReceipt.TerminalReason,
                            finalReceipt.JournalFlushCompleted,
                            finalReceipt.JournalDisposed,
                            finalReceipt.PipelineTerminal,
                            finalReceipt.SafeExitAllowed,
                            finalReceipt.SessionClosingPersisted));
                }
                catch { }
            }
            if (!TryPublishOwnerStage(currentOwner, retainedFinalOwner))
                return CaptureRetainedReceiptOr(finalReceipt, engineReceipt);
            return finalReceipt ?? CreateNoWorkReceipt(engineReceipt);
        }

        private bool TryPublishOwnerStage(
            RuntimeShutdownRetentionOwner expected,
            RuntimeShutdownRetentionOwner replacement)
        {
            var published = false;
            lock (_executionGate)
            {
                if (expected == null || replacement == null ||
                    !ReferenceEquals(_owner, expected) ||
                    !ReferenceEquals(expected.Context, replacement.Context))
                    return false;
                _owner = replacement;
                published = true;
            }
            if (published) NotifyStagePublished(replacement);
            return published;
        }

        private void NotifyStagePublished(RuntimeShutdownRetentionOwner owner)
        {
            var observer = StagePublishedObserver;
            if (observer == null || owner == null) return;
            try { observer(owner); } catch { }
        }

        private RuntimeShutdownReceipt PublishStageOrReturn(
            RuntimeShutdownRetentionOwner expected,
            RuntimeShutdownRetentionOwner replacement,
            RuntimeShutdownReceipt fallback)
        {
            if (TryPublishOwnerStage(expected, replacement)) return fallback;
            return CaptureRetainedReceiptOr(fallback, replacement?.EngineReceipt);
        }

        private RuntimeShutdownReceipt CaptureRetainedReceiptOr(
            RuntimeShutdownReceipt fallback,
            ShutdownReceipt engineReceipt)
        {
            lock (_executionGate)
                return _owner?.RuntimeReceipt ?? fallback ?? CreateNoWorkReceipt(engineReceipt);
        }

        private long NextVersion(long previous)
        {
            return Math.Max(previous + 1, Interlocked.Increment(ref _version));
        }

        private static RuntimeShutdownReceipt CreateNoWorkReceipt(ShutdownReceipt engineReceipt)
        {
            return new RuntimeShutdownReceipt(
                0, engineReceipt, null, null, null,
                RuntimeCallbackPipelineState.Terminal,
                "PipelineNotCreatedNoWork",
                false, 0, string.Empty, 0, engineReceipt?.SessionLease ?? 0,
                false, true, true, false);
        }

        private static RuntimeShutdownReceipt CreateIdentityMismatchReceipt(
            RuntimeTransportSessionContext context, long version)
        {
            return new RuntimeShutdownReceipt(
                Math.Max(1, context?.ClosingAttempt ?? 1), null, null, null, null,
                RuntimeCallbackPipelineState.FailClosed,
                "ShutdownIdentityMismatch",
                false, version, context?.SessionId,
                context?.SessionGeneration ?? 0, context?.SessionLease ?? 0,
                true, false, false, true);
        }

        private static RuntimeShutdownReceipt CreateStageFailureReceipt(
            RuntimeTransportSessionContext context,
            long version,
            ShutdownReceipt engineReceipt,
            string stage,
            Exception error)
        {
            var suffix = error == null ? "Unknown" : error.GetType().Name;
            return new RuntimeShutdownReceipt(
                Math.Max(1, context?.ClosingAttempt ?? 1), engineReceipt,
                null, null, null, RuntimeCallbackPipelineState.FailClosed,
                string.Format("ShutdownStageFailure:{0}:{1}", stage, suffix),
                false, version, context?.SessionId,
                context?.SessionGeneration ?? 0, context?.SessionLease ?? 0,
                true, false, false, true);
        }

        private static RuntimeShutdownReceipt CreatePreviousRuntimeShutdownIncomplete(
            RuntimeShutdownRetentionOwner owner)
        {
            return owner?.RuntimeReceipt ?? CreatePreviousRuntimeShutdownIncomplete(
                owner?.Context, owner?.Version ?? 0);
        }

        private static RuntimeShutdownReceipt CreatePreviousRuntimeShutdownIncomplete(
            RuntimeTransportSessionContext context, long version)
        {
            return new RuntimeShutdownReceipt(
                Math.Max(1, context?.ClosingAttempt ?? 1), null, null, null, null,
                RuntimeCallbackPipelineState.FailClosed,
                "PreviousRuntimeShutdownIncomplete",
                false, version, context?.SessionId,
                context?.SessionGeneration ?? 0, context?.SessionLease ?? 0,
                true, false, false, true);
        }
    }
}
