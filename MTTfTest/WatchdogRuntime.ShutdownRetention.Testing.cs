using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;

namespace MTEmbTest
{
    internal enum RuntimeShutdownRetentionTestTransportMode
    {
        Complete = 0,
        Throw = 1,
        ReturnNull = 2,
        Incomplete = 3
    }

    internal enum RuntimeShutdownRetentionTestPipelineMode
    {
        Real = 0,
        Throw = 1
    }

    /// <summary>
    /// Production retention seam.  The coordinator, ownership CAS adapter
    /// and callback-pipeline adapter are the production implementations.  A
    /// test may control only the external Engine receipt, mark result and
    /// journal I/O boundaries.
    /// </summary>
    internal sealed class RuntimeShutdownRetentionProductionTestSession : IDisposable
    {
        private sealed class RetentionPostTarget : IWatchdogCallbackPostTarget, IDisposable
        {
            private sealed class Pending
            {
                internal readonly Func<System.Threading.Tasks.Task> Callback;
                internal readonly TaskCompletionSource<bool> Completion =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                internal Pending(Func<System.Threading.Tasks.Task> callback)
                {
                    Callback = callback;
                }
            }

            private readonly object _gate = new object();
            private readonly List<Pending> _pending = new List<Pending>();
            private int _closed;
            private long _sequence;
            private int _calls;

            internal bool HoldCompletions { get; set; }
            internal int Calls => Volatile.Read(ref _calls);

            public WatchdogPostReceipt TryPost(Func<System.Threading.Tasks.Task> callback)
            {
                if (callback == null) return WatchdogPostReceipt.Rejected("CallbackMissing");
                if (Volatile.Read(ref _closed) != 0)
                    return WatchdogPostReceipt.Rejected("TargetClosed");
                Interlocked.Increment(ref _calls);
                var pending = new Pending(callback);
                if (HoldCompletions)
                {
                    lock (_gate) _pending.Add(pending);
                }
                else
                {
                    RunPending(pending);
                }
                return new WatchdogPostReceipt(
                    true, pending.Completion.Task, string.Empty,
                    Interlocked.Increment(ref _sequence));
            }

            internal int ReleaseAll()
            {
                Pending[] pending;
                lock (_gate)
                {
                    pending = _pending.ToArray();
                    _pending.Clear();
                }
                foreach (var item in pending) RunPending(item);
                return pending.Length;
            }

            private static void RunPending(Pending pending)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var task = pending.Callback();
                        if (task != null) await task.ConfigureAwait(false);
                        pending.Completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        pending.Completion.TrySetException(ex);
                    }
                });
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _closed, 1);
                ReleaseAll();
            }
        }

        private sealed class ControllableTransport : IRuntimeTransportShutdownPort
        {
            private readonly RuntimeTransportSessionContext _context;
            private int _calls;

            internal RuntimeShutdownRetentionTestTransportMode Mode { get; set; }
            internal int Calls => Volatile.Read(ref _calls);

            internal ControllableTransport(RuntimeTransportSessionContext context)
            {
                _context = context;
                Mode = RuntimeShutdownRetentionTestTransportMode.Complete;
            }

            public ShutdownReceipt ShutdownWithReceipt()
            {
                Interlocked.Increment(ref _calls);
                switch (Mode)
                {
                    case RuntimeShutdownRetentionTestTransportMode.Throw:
                        throw new InvalidOperationException("ControlledEngineFailure");
                    case RuntimeShutdownRetentionTestTransportMode.ReturnNull:
                        return null;
                    case RuntimeShutdownRetentionTestTransportMode.Incomplete:
                        return CreateReceipt(_context, false);
                    default:
                        return CreateReceipt(_context, true);
                }
            }
        }

        private sealed class ControllableMark : IRuntimeShutdownSessionPort
        {
            internal RuntimeShutdownMarkOutcome Outcome { get; set; }

            internal ControllableMark()
            {
                Outcome = RuntimeShutdownMarkOutcome.Marked;
            }

            public RuntimeShutdownMarkOutcome TryMarkSessionClosing(
                RuntimeTransportSessionContext context)
            {
                return Outcome;
            }
        }

        private sealed class ProductionPipelineDecorator : IRuntimeShutdownPipelinePort
        {
            private readonly IRuntimeShutdownPipelinePort _production =
                new WatchdogRuntimeShutdownPipelinePort();
            private readonly WatchdogRuntimeShutdownPipelinePort _productionWithBudget;
            private int _calls;
            private int _throwNext;

            internal int Calls => Volatile.Read(ref _calls);
            internal RuntimeShutdownRetentionTestPipelineMode Mode { get; set; }
            internal TimeSpan? CloseBudget { get; set; }

            internal ProductionPipelineDecorator()
            {
                _productionWithBudget = (WatchdogRuntimeShutdownPipelinePort)_production;
                Mode = RuntimeShutdownRetentionTestPipelineMode.Real;
            }

            internal void ThrowNext() => Interlocked.Exchange(ref _throwNext, 1);

            public RuntimeShutdownReceipt Close(
                RuntimeTransportSessionContext context, ShutdownReceipt engineReceipt)
            {
                Interlocked.Increment(ref _calls);
                if (Mode == RuntimeShutdownRetentionTestPipelineMode.Throw ||
                    Interlocked.Exchange(ref _throwNext, 0) != 0)
                    throw new InvalidOperationException("ControlledPipelineFailure");

                return CloseBudget.HasValue
                    ? _productionWithBudget.Close(context, engineReceipt, CloseBudget.Value)
                    : _production.Close(context, engineReceipt);
            }
        }

        private sealed class ControllableJournal : IRuntimeShutdownJournalPort
        {
            private int _recordCalls;
            private int _flushCalls;
            private int _disposeCalls;

            internal int FlushFailuresRemaining { get; set; }
            internal int DisposeFailuresRemaining { get; set; }
            internal bool ThrowOnFlush { get; set; }
            internal bool ThrowOnDispose { get; set; }
            internal int RecordCalls => Volatile.Read(ref _recordCalls);
            internal int FlushCalls => Volatile.Read(ref _flushCalls);
            internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

            public void Record(RuntimeTransportSessionContext context,
                string eventType, string detail)
            {
                Interlocked.Increment(ref _recordCalls);
            }

            public bool Flush(RuntimeTransportSessionContext context)
            {
                Interlocked.Increment(ref _flushCalls);
                if (ThrowOnFlush) throw new InvalidOperationException("ControlledJournalFlushFailure");
                if (FlushFailuresRemaining > 0)
                {
                    FlushFailuresRemaining--;
                    return false;
                }
                return true;
            }

            public bool Dispose(RuntimeTransportSessionContext context)
            {
                Interlocked.Increment(ref _disposeCalls);
                if (ThrowOnDispose) throw new InvalidOperationException("ControlledJournalDisposeFailure");
                if (DisposeFailuresRemaining > 0)
                {
                    DisposeFailuresRemaining--;
                    return false;
                }
                return true;
            }
        }

        private readonly RuntimeCallbackPipelineTestSession _pipelineSession;
        private readonly ControllableTransport _transport;
        private readonly ControllableMark _mark;
        private readonly ProductionPipelineDecorator _pipeline;
        private readonly ControllableJournal _journal;
        private readonly RuntimeShutdownRetentionCoordinator _coordinator;
        private readonly RetentionPostTarget _target;
        private readonly IDisposable _handlerLease;
        private readonly ConcurrentQueue<RuntimeShutdownRetentionOwner> _stageSnapshots =
            new ConcurrentQueue<RuntimeShutdownRetentionOwner>();
        private int _installed;
        private int _disposed;

        internal RuntimeShutdownRetentionProductionTestSession()
        {
            _pipelineSession = new RuntimeCallbackPipelineTestSession();
            _transport = new ControllableTransport(_pipelineSession.Context);
            _mark = new ControllableMark();
            _pipeline = new ProductionPipelineDecorator();
            _journal = new ControllableJournal();
            _target = new RetentionPostTarget();
            var targetLease = _pipelineSession.BindTarget("retention-production-safety", _target);
            if (targetLease == null || !targetLease.Accepted)
                throw new InvalidOperationException(
                    "production retention safety target bind failed: " +
                    (targetLease?.RejectionReason ?? "null"));
            _handlerLease = _pipelineSession.RegisterHandler(
                targetLease, "retention-production-handler",
                _ => System.Threading.Tasks.Task.CompletedTask);
            if (_handlerLease == null || !_pipelineSession.Attach())
                throw new InvalidOperationException("production retention pipeline Attach failed");
            _coordinator = new RuntimeShutdownRetentionCoordinator(
                _transport,
                new WatchdogRuntimeShutdownOwnershipPort(),
                _mark,
                _pipeline,
                _journal);
            _coordinator.StagePublishedObserver = owner => _stageSnapshots.Enqueue(owner);
            if (!WatchdogRuntime.InstallShutdownRetentionTestingContext(
                    _pipelineSession.Context, _coordinator))
                throw new InvalidOperationException("Unable to install production active context seam.");
            Volatile.Write(ref _installed, 1);
        }

        internal RuntimeTransportSessionContext Context => _pipelineSession.Context;
        internal RuntimeShutdownRetentionCoordinator Coordinator => _coordinator;
        internal RuntimeShutdownRetentionOwner CaptureOwner() => _coordinator.Capture();
        internal RuntimeShutdownRetentionOwner[] CaptureStageSnapshots() =>
            _stageSnapshots.ToArray();
        internal RuntimeShutdownReceipt LatestReceipt() => _coordinator.LatestReceipt();
        internal RuntimeShutdownReceipt ShutdownOrRetry() => _coordinator.ShutdownOrRetry();
        internal bool EnsurePreviousTerminal() => _coordinator.EnsurePreviousTerminal();
        internal bool TryInstallReplacement(RuntimeTransportSessionContext context) =>
            WatchdogRuntime.InstallShutdownRetentionTestingContext(context, _coordinator);
        internal int TransportCalls => _transport.Calls;
        internal int PipelineCalls => _pipeline.Calls;
        internal int JournalRecordCalls => _journal.RecordCalls;
        internal int JournalFlushCalls => _journal.FlushCalls;
        internal int JournalDisposeCalls => _journal.DisposeCalls;
        internal RuntimeShutdownRetentionTestTransportMode TransportMode
        {
            get => _transport.Mode;
            set => _transport.Mode = value;
        }
        internal RuntimeShutdownRetentionTestPipelineMode PipelineMode
        {
            get => _pipeline.Mode;
            set => _pipeline.Mode = value;
        }
        internal RuntimeShutdownMarkOutcome MarkOutcome
        {
            get => _mark.Outcome;
            set => _mark.Outcome = value;
        }
        internal int FlushFailuresRemaining
        {
            get => _journal.FlushFailuresRemaining;
            set => _journal.FlushFailuresRemaining = value;
        }
        internal int DisposeFailuresRemaining
        {
            get => _journal.DisposeFailuresRemaining;
            set => _journal.DisposeFailuresRemaining = value;
        }
        internal bool ThrowOnFlush
        {
            get => _journal.ThrowOnFlush;
            set => _journal.ThrowOnFlush = value;
        }
        internal bool ThrowOnDispose
        {
            get => _journal.ThrowOnDispose;
            set => _journal.ThrowOnDispose = value;
        }
        internal void ThrowNextPipeline() => _pipeline.ThrowNext();
        internal TimeSpan? PipelineCloseBudget
        {
            get => _pipeline.CloseBudget;
            set => _pipeline.CloseBudget = value;
        }
        internal bool HoldPipelineCompletions
        {
            get => _target.HoldCompletions;
            set => _target.HoldCompletions = value;
        }
        internal int PipelinePostCalls => _target.Calls;
        internal bool PublishStopRequest()
        {
            return _pipelineSession.PublishPipe(
                "RetentionProductionStop", "retention-production-correlation");
        }
        internal int ReleasePipelineCompletions() => _target.ReleaseAll();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Exchange(ref _installed, 0) != 0)
                WatchdogRuntime.RemoveShutdownRetentionTestingContext(_pipelineSession.Context);
            _pipelineSession.Dispose();
        }

        private static ShutdownReceipt CreateReceipt(
            RuntimeTransportSessionContext context, bool complete)
        {
            var termination = new WorkerTerminationState(
                context?.SessionLease ?? 0,
                DateTime.UtcNow.Ticks,
                complete, complete, complete, complete, complete, complete,
                complete, complete, complete, complete,
                complete ? 0 : 1, 0, complete);
            return new ShutdownReceipt(
                context?.SessionLease ?? 0,
                termination.CapturedUtcTicks,
                termination,
                true,
                true);
        }
    }

    internal static partial class WatchdogRuntime
    {
        private static long _retentionInstallGateCalls;
        private static long _retentionInstallGateCreateNew;
        private static long _retentionInstallGateRecovery;
        private static long _retentionInstallGateEmergency;

        internal struct RuntimeShutdownRetentionInstallGateCounters
        {
            internal long Calls { get; }
            internal long CreateNewInitial { get; }
            internal long OpenExistingRecovery { get; }
            internal long EmergencyRecoveryReject { get; }

            internal RuntimeShutdownRetentionInstallGateCounters(
                long calls, long createNewInitial, long openExistingRecovery,
                long emergencyRecoveryReject)
            {
                Calls = calls;
                CreateNewInitial = createNewInitial;
                OpenExistingRecovery = openExistingRecovery;
                EmergencyRecoveryReject = emergencyRecoveryReject;
            }
        }

        internal static RuntimeShutdownRetentionInstallGateCounters
            CaptureRetentionInstallGateCounters()
        {
            return new RuntimeShutdownRetentionInstallGateCounters(
                Interlocked.Read(ref _retentionInstallGateCalls),
                Interlocked.Read(ref _retentionInstallGateCreateNew),
                Interlocked.Read(ref _retentionInstallGateRecovery),
                Interlocked.Read(ref _retentionInstallGateEmergency));
        }

        internal static void RecordRetentionInstallGateClassification(
            RuntimeJournalMode mode)
        {
            Interlocked.Increment(ref _retentionInstallGateCalls);
            switch (mode)
            {
                case RuntimeJournalMode.OpenExistingRecovery:
                    Interlocked.Increment(ref _retentionInstallGateRecovery);
                    break;
                case RuntimeJournalMode.EmergencyRecoveryReject:
                    Interlocked.Increment(ref _retentionInstallGateEmergency);
                    break;
                default:
                    Interlocked.Increment(ref _retentionInstallGateCreateNew);
                    break;
            }
        }

        /// <summary>
        /// Isolated production-test install boundary.  It uses the same Gate
        /// and ownership CAS as InstallContext but never bypasses the
        /// coordinator in production code.
        /// </summary>
        internal static bool InstallShutdownRetentionTestingContext(
            RuntimeTransportSessionContext context)
        {
            return InstallShutdownRetentionTestingContext(
                context, ShutdownRetentionCoordinator);
        }

        internal static bool InstallShutdownRetentionTestingContext(
            RuntimeTransportSessionContext context,
            RuntimeShutdownRetentionCoordinator coordinator)
        {
            return InstallContextWithRetentionGate(context, coordinator, out _);
        }

        internal static bool RemoveShutdownRetentionTestingContext(
            RuntimeTransportSessionContext context)
        {
            if (context == null) return false;
            lock (Gate)
            {
                if (!ReferenceEquals(_activeContext, context)) return false;
                _activeContext = null;
                return true;
            }
        }
    }
}
