using System;
using System.Threading;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    /// <summary>
    /// Outcome of the exact Engine session-closing mark.  NoEngineSession is
    /// still a valid, observable result; IdentityMismatch is the only result
    /// that forbids entering the Engine shutdown port.
    /// </summary>
    internal enum RuntimeShutdownMarkOutcome
    {
        Marked = 0,
        NoEngineSession = 1,
        IdentityMismatch = 2,
        TombstonePersistenceFailed = 3
    }

    internal enum RuntimeShutdownIntent
    {
        SessionClose = 0,
        ApplicationExit = 1,
        WatchdogTakeoverExit = 2,
        WatchdogRecoveryExit = 3
    }

    internal sealed class RuntimeSessionCloseFenceReceipt
    {
        internal RuntimeTransportSessionContext Context { get; set; }
        internal WatchdogClosingTombstone Tombstone { get; set; }
        internal RuntimeShutdownMarkOutcome MarkOutcome { get; set; }
        internal bool TombstoneDurable { get; set; }
        internal string Error { get; set; } = string.Empty;
        internal bool IsIrreversible => TombstoneDurable &&
                                        Tombstone?.SessionLease == Context?.SessionLease;
    }

    /// <summary>
    /// Ten observable retention phases.  None is the empty state and the
    /// remaining values are monotonic transaction states.
    /// </summary>
    internal enum RuntimeShutdownRetentionPhase
    {
        None = 0,
        OwnershipReserved = 1,
        SessionClosing = 2,
        EngineShutdown = 3,
        PipelineShutdown = 4,
        JournalFlush = 5,
        JournalDispose = 6,
        TerminalCandidate = 7,
        RetainedFailure = 8,
        Terminal = 9
    }

    internal interface IRuntimeTransportShutdownPort
    {
        ShutdownReceipt ShutdownWithReceipt();
    }

    internal interface IRuntimeShutdownOwnershipPort
    {
        RuntimeTransportSessionContext CaptureActive();
        bool TryDetachActive(RuntimeTransportSessionContext context);
    }

    internal interface IRuntimeShutdownSessionPort
    {
        RuntimeShutdownMarkOutcome TryMarkSessionClosing(RuntimeTransportSessionContext context);
        bool TryCompleteSessionClosing(
            RuntimeTransportSessionContext context,
            string terminalReason);
    }

    internal interface IRuntimeShutdownPipelinePort
    {
        RuntimeShutdownReceipt Close(RuntimeTransportSessionContext context, ShutdownReceipt engineReceipt);
    }

    internal interface IRuntimeShutdownJournalPort
    {
        void Record(RuntimeTransportSessionContext context, string eventType, string detail);
        bool Flush(RuntimeTransportSessionContext context);
        bool Dispose(RuntimeTransportSessionContext context);
    }

    internal sealed class WatchdogRuntimeTransportShutdownPort : IRuntimeTransportShutdownPort
    {
        private readonly WatchdogClientTransportEngine _engine;

        internal WatchdogRuntimeTransportShutdownPort(WatchdogClientTransportEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public ShutdownReceipt ShutdownWithReceipt()
        {
            // The sidecar is a separate process.  Close the protocol session
            // before detaching the transport so it can leave its pipe/read
            // loop and terminate deterministically.  The marker is best
            // effort: the Engine receipt remains the authoritative shutdown
            // result and must still be collected if the pipe is already gone.
            try
            {
                WatchdogRuntime.SendApplicationClosingForShutdown(
                    _engine,
                    WatchdogRuntime.CurrentShutdownIntent);
            }
            catch { }
            return _engine.ShutdownWithReceipt();
        }
    }

    internal sealed class WatchdogRuntimeShutdownOwnershipPort : IRuntimeShutdownOwnershipPort
    {
        public RuntimeTransportSessionContext CaptureActive()
        {
            return WatchdogRuntime.CaptureShutdownActiveContext();
        }

        public bool TryDetachActive(RuntimeTransportSessionContext context)
        {
            return WatchdogRuntime.TryDetachShutdownActiveContext(context);
        }
    }

    internal sealed class WatchdogRuntimeShutdownSessionPort : IRuntimeShutdownSessionPort
    {
        public RuntimeShutdownMarkOutcome TryMarkSessionClosing(RuntimeTransportSessionContext context)
        {
            return WatchdogRuntime.BeginSessionCloseExact(
                context,
                WatchdogRuntime.CurrentShutdownIntent.ToString(),
                Guid.Empty,
                Guid.Empty,
                0,
                0).MarkOutcome;
        }

        public bool TryCompleteSessionClosing(
            RuntimeTransportSessionContext context,
            string terminalReason)
        {
            return WatchdogRuntime.CompleteSessionCloseTombstone(
                context,
                terminalReason);
        }
    }

    internal sealed class WatchdogRuntimeShutdownPipelinePort : IRuntimeShutdownPipelinePort
    {
        public RuntimeShutdownReceipt Close(RuntimeTransportSessionContext context, ShutdownReceipt engineReceipt)
        {
            return WatchdogRuntime.CloseCallbackPipelineForContext(context, engineReceipt);
        }

        /// <summary>
        /// Production close with an explicit observation budget.  The
        /// default two-argument path remains the normal five-second Runtime
        /// behavior; this overload only exposes the existing production
        /// cleanup boundary for deterministic deadline tests.
        /// </summary>
        internal RuntimeShutdownReceipt Close(
            RuntimeTransportSessionContext context,
            ShutdownReceipt engineReceipt,
            TimeSpan budget)
        {
            return WatchdogRuntime.CleanupCallbackPipelineResources(
                context, budget, string.Empty, engineReceipt);
        }
    }

    internal sealed class WatchdogRuntimeShutdownJournalPort : IRuntimeShutdownJournalPort
    {
        public void Record(RuntimeTransportSessionContext context, string eventType, string detail)
        {
            WatchdogRuntime.RecordClientEvent(context, eventType, detail);
        }

        public bool Flush(RuntimeTransportSessionContext context)
        {
            if (context == null || context.Journal == null) return true;
            return context.Journal.Flush(TimeSpan.FromSeconds(1));
        }

        public bool Dispose(RuntimeTransportSessionContext context)
        {
            if (context == null || context.Journal == null) return true;
            context.Journal.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Immutable process-level retention owner.  A non-terminal owner is the
    /// sole authority for the frozen context and may only be retried by the
    /// coordinator that owns it.
    /// </summary>
    internal sealed class RuntimeShutdownRetentionOwner
    {
        internal RuntimeTransportSessionContext Context { get; }
        internal RuntimeValidatedAttachIdentity AttachIdentity { get; }
        internal string SessionId { get; }
        internal long SessionGeneration { get; }
        internal long SessionLease { get; }
        internal long ClosingAttempt { get; }
        internal long Attempts => ClosingAttempt;
        internal long Version { get; }
        internal ShutdownReceipt EngineReceipt { get; }
        internal RuntimeShutdownReceipt RuntimeReceipt { get; }
        internal bool EngineShutdownStarted { get; }
        internal bool SkipEngineShutdown { get; }
        internal RuntimeShutdownMarkOutcome MarkOutcome { get; }
        internal string FailureKind { get; }
        internal bool JournalFlushCompleted { get; }
        internal bool JournalDisposed { get; }
        internal RuntimeShutdownRetentionPhase Phase { get; }

        internal bool IsTerminal => Phase == RuntimeShutdownRetentionPhase.Terminal &&
            RuntimeReceipt != null && RuntimeReceipt.IsTerminal &&
            JournalFlushCompleted && JournalDisposed && !RuntimeReceipt.Retained;

        internal RuntimeShutdownRetentionOwner(
            RuntimeTransportSessionContext context,
            RuntimeValidatedAttachIdentity attachIdentity,
            long closingAttempt,
            long version,
            ShutdownReceipt engineReceipt,
            RuntimeShutdownReceipt runtimeReceipt,
            bool engineShutdownStarted,
            bool skipEngineShutdown,
            bool journalFlushCompleted,
            bool journalDisposed,
            RuntimeShutdownMarkOutcome markOutcome = RuntimeShutdownMarkOutcome.Marked,
            string failureKind = null,
            RuntimeShutdownRetentionPhase phase = RuntimeShutdownRetentionPhase.None)
        {
            Context = context;
            AttachIdentity = attachIdentity;
            SessionId = context?.SessionId ?? runtimeReceipt?.SessionId ?? string.Empty;
            SessionGeneration = context?.SessionGeneration ?? runtimeReceipt?.SessionGeneration ?? 0;
            SessionLease = context?.SessionLease ?? runtimeReceipt?.SessionLease ?? 0;
            ClosingAttempt = Math.Max(0, closingAttempt);
            Version = Math.Max(0, version);
            EngineReceipt = engineReceipt;
            RuntimeReceipt = runtimeReceipt;
            EngineShutdownStarted = engineShutdownStarted;
            SkipEngineShutdown = skipEngineShutdown;
            MarkOutcome = markOutcome;
            FailureKind = failureKind ?? string.Empty;
            JournalFlushCompleted = journalFlushCompleted;
            JournalDisposed = journalDisposed;
            Phase = phase;
        }

        internal RuntimeShutdownRetentionOwner With(
            long closingAttempt,
            long version,
            ShutdownReceipt engineReceipt,
            RuntimeShutdownReceipt runtimeReceipt,
            bool engineShutdownStarted,
            bool skipEngineShutdown,
            bool journalFlushCompleted,
            bool journalDisposed,
            RuntimeShutdownMarkOutcome? markOutcome = null,
            string failureKind = null,
            RuntimeShutdownRetentionPhase? phase = null)
        {
            return new RuntimeShutdownRetentionOwner(
                Context, AttachIdentity, closingAttempt, version,
                engineReceipt, runtimeReceipt, engineShutdownStarted,
                skipEngineShutdown, journalFlushCompleted, journalDisposed,
                markOutcome ?? MarkOutcome, failureKind ?? FailureKind,
                phase ?? Phase);
        }
    }

    internal static partial class WatchdogRuntime
    {
        private static int _shutdownIntentForCurrentShutdown;

        internal static bool IsProcessExitExpectedForCurrentShutdown =>
            CurrentShutdownIntent != RuntimeShutdownIntent.SessionClose;

        internal static RuntimeShutdownIntent CurrentShutdownIntent =>
            (RuntimeShutdownIntent)Volatile.Read(ref _shutdownIntentForCurrentShutdown);

        private static readonly Lazy<RuntimeShutdownRetentionCoordinator> ShutdownRetentionCoordinatorHolder =
            new Lazy<RuntimeShutdownRetentionCoordinator>(
                () => new RuntimeShutdownRetentionCoordinator(
                    new WatchdogRuntimeTransportShutdownPort(TransportEngine),
                    new WatchdogRuntimeShutdownOwnershipPort(),
                    new WatchdogRuntimeShutdownSessionPort(),
                    new WatchdogRuntimeShutdownPipelinePort(),
                    new WatchdogRuntimeShutdownJournalPort()),
                LazyThreadSafetyMode.ExecutionAndPublication);

        internal static RuntimeShutdownRetentionCoordinator ShutdownRetentionCoordinator =>
            ShutdownRetentionCoordinatorHolder.Value;

        internal static RuntimeShutdownRetentionOwner CaptureRetainedShutdown()
        {
            return ShutdownRetentionCoordinator.Capture();
        }

        internal static RuntimeShutdownReceipt ShutdownRuntimeWithReceipt()
        {
            return ShutdownRuntimeWithReceipt(false);
        }

        internal static RuntimeShutdownReceipt ShutdownRuntimeWithReceipt(
            bool processExitExpected)
        {
            return ShutdownRuntimeWithReceipt(
                processExitExpected
                    ? RuntimeShutdownIntent.ApplicationExit
                    : RuntimeShutdownIntent.SessionClose);
        }

        internal static RuntimeShutdownReceipt ShutdownRuntimeWithReceipt(
            RuntimeShutdownIntent shutdownIntent)
        {
            SessionLifecycleGate.Wait();
            var previous = Interlocked.Exchange(
                ref _shutdownIntentForCurrentShutdown,
                (int)shutdownIntent);
            try { return ShutdownRuntimeWithReceiptNoGate(); }
            finally
            {
                Interlocked.Exchange(ref _shutdownIntentForCurrentShutdown, previous);
                SessionLifecycleGate.Release();
            }
        }

        private static RuntimeShutdownReceipt ShutdownRuntimeWithReceiptNoGate()
        {
            return ShutdownRetentionCoordinator.ShutdownOrRetry();
        }

        internal static RuntimeTransportSessionContext CaptureShutdownActiveContext()
        {
            lock (Gate) return _activeContext;
        }

        internal static bool TryDetachShutdownActiveContext(RuntimeTransportSessionContext context)
        {
            if (context == null) return false;
            lock (Gate)
            {
                if (!ReferenceEquals(_activeContext, context)) return false;
                _activeContext = null;
                return true;
            }
        }

        /// <summary>
        /// Shared production installation core used by normal, recovery and
        /// emergency entry points as well as the deterministic production
        /// seam.  The coordinator gate is checked before Runtime Gate so a
        /// retained owner can never be bypassed by installing a replacement.
        /// </summary>
        internal static bool InstallContextWithRetentionGate(
            RuntimeTransportSessionContext context,
            RuntimeShutdownRetentionCoordinator coordinator,
            out string rejection)
        {
            rejection = string.Empty;
            if (context == null)
            {
                rejection = "Runtime context is null.";
                return false;
            }
            coordinator = coordinator ?? ShutdownRetentionCoordinator;
            if (!coordinator.EnsurePreviousTerminal())
            {
                rejection = "PreviousRuntimeShutdownIncomplete";
                return false;
            }
            lock (Gate)
            {
                if (_activeContext != null && !ReferenceEquals(_activeContext, context))
                {
                    rejection = "Runtime active context already exists.";
                    return false;
                }
                _activeContext = context;
            }
            RecordRetentionInstallGateClassification(context.JournalMode);
            return true;
        }

        internal static bool IsExactEngineTerminal(
            RuntimeTransportSessionContext context, ShutdownReceipt receipt)
        {
            return IsExactEngineTerminal(context?.SessionLease ?? 0, receipt);
        }

        internal static bool IsExactEngineTerminal(long expectedSessionLease, ShutdownReceipt receipt)
        {
            if (receipt == null || !receipt.AllResourcesReleased) return false;
            if (expectedSessionLease > 0)
            {
                return receipt.SessionLease == expectedSessionLease &&
                    receipt.SessionDetached && receipt.TransportDetached;
            }
            return receipt.TransportDetached &&
                (receipt.SessionLease == 0 || receipt.SessionDetached);
        }

        private static bool IsShutdownReady(RuntimeShutdownReceipt receipt)
        {
            return receipt != null && receipt.IsTerminal &&
                IsShutdownReady(receipt.EngineReceipt);
        }

        /// <summary>
        /// Compatibility boundary for old Engine-only callers.  It delegates
        /// to the same process-wide coordinator and never owns a second
        /// retention algorithm.
        /// </summary>
        private static ShutdownReceipt ShutdownCoreNoGate()
        {
            return ShutdownRuntimeWithReceiptNoGate()?.EngineReceipt;
        }
    }
}
