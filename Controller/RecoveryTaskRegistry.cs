using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// 统一登记会影响重新开始的恢复任务。它不替代各恢复域自己的取消令牌，
    /// 但为 StopAll 提供一个共同的有界收口屏障，并用 RunEpoch 隔离迟到旧任务。
    /// </summary>
    internal sealed class RecoveryTaskRegistry
    {
        internal enum DrainResidueKind
        {
            ReservationOnly = 0,
            WorkerRunning = 1,
            WorkerCompletedWithoutTerminal = 2
        }

        internal sealed class DrainResidue
        {
            internal DrainResidue(
                long id,
                string operation,
                long runEpoch,
                IEnumerable<int> channels,
                DrainResidueKind kind)
            {
                Id = id;
                Operation = operation ?? string.Empty;
                RunEpoch = runEpoch;
                Channels = (channels ?? Array.Empty<int>()).ToArray();
                Kind = kind;
            }

            internal long Id { get; }
            internal string Operation { get; }
            internal long RunEpoch { get; }
            internal IReadOnlyList<int> Channels { get; }
            internal DrainResidueKind Kind { get; }
        }

        internal sealed class DrainResult
        {
            internal DrainResult(bool drained, bool cancelled, IEnumerable<DrainResidue> residues)
            {
                Drained = drained;
                Cancelled = cancelled;
                Residues = (residues ?? Array.Empty<DrainResidue>()).ToArray();
            }

            internal bool Drained { get; }
            internal bool Cancelled { get; }
            internal IReadOnlyList<DrainResidue> Residues { get; }
            internal string[] Operations => Residues
                .Select(item => item.Operation)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();
        }

        /// <summary>
        /// Immutable evidence of one lease at the instant an aggregate
        /// watchdog snapshot was captured.  The registry deliberately exposes
        /// a copy rather than its live Entry/Task objects: a heartbeat must not
        /// compose owner and task state by reading mutable dictionaries at
        /// different times.
        /// </summary>
        internal sealed class RecoveryTaskLeaseSnapshot
        {
            internal RecoveryTaskLeaseSnapshot(
                long id,
                string operation,
                long runEpoch,
                IEnumerable<int> channels,
                bool bound,
                bool terminal,
                Guid incidentId = default,
                Guid runId = default,
                Guid ownerId = default,
                RecoveryOwnerKind ownerKind = RecoveryOwnerKind.None,
                RecoveryTargetPhase targetPhase = RecoveryTargetPhase.None,
                long progressVersion = 0,
                DateTime lastProgressUtc = default,
                string progressStage = null)
            {
                Id = id;
                Operation = operation ?? string.Empty;
                RunEpoch = runEpoch;
                Channels = (channels ?? Array.Empty<int>()).ToArray();
                IsBound = bound;
                IsTerminal = terminal;
                IncidentId = incidentId;
                RunId = runId;
                OwnerId = ownerId;
                OwnerKind = ownerKind;
                TargetPhase = targetPhase;
                ProgressVersion = progressVersion;
                LastProgressUtc = lastProgressUtc;
                ProgressStage = progressStage ?? string.Empty;
            }

            internal long Id { get; }
            internal string Operation { get; }
            internal long RunEpoch { get; }
            internal IReadOnlyList<int> Channels { get; }
            internal bool IsBound { get; }
            internal bool IsTerminal { get; }
            internal Guid IncidentId { get; }
            internal Guid RunId { get; }
            internal Guid OwnerId { get; }
            internal RecoveryOwnerKind OwnerKind { get; }
            internal RecoveryTargetPhase TargetPhase { get; }
            internal long ProgressVersion { get; }
            internal DateTime LastProgressUtc { get; }
            internal string ProgressStage { get; }
        }

        private sealed class Entry
        {
            internal long Id;
            internal string Operation;
            internal long RunEpoch;
            internal int[] Channels;
            internal Guid IncidentId;
            internal Guid RunId;
            internal Guid OwnerId;
            internal RecoveryOwnerKind OwnerKind;
            internal RecoveryTargetPhase TargetPhase;
            internal long ProgressVersion;
            internal DateTime LastProgressUtc;
            internal string ProgressStage;
            // The completion task is intentionally created at reservation time.  A
            // recovery state must never become externally visible while its task
            // identity is still being constructed.
            internal Task ReservationTask;
            internal Task WorkerTask;
            internal bool AutoRelease;
            internal bool Terminal;
            internal readonly object Gate = new object();
        }

        /// <summary>
        /// Two-phase recovery registration.  Reserve() installs the incident/task
        /// identity before a worker is created; Bind() attaches the worker; the
        /// owner is released only after the caller has published a terminal state.
        /// </summary>
        internal sealed class RecoveryTaskLease : IDisposable
        {
            private RecoveryTaskRegistry _registry;
            private readonly long _id;
            private readonly Task _reservationTask;

            internal RecoveryTaskLease(
                RecoveryTaskRegistry registry,
                long id,
                string operation,
                long runEpoch,
                int[] channels,
                Task reservationTask,
                Guid incidentId = default,
                Guid runId = default,
                Guid ownerId = default,
                RecoveryOwnerKind ownerKind = RecoveryOwnerKind.None,
                RecoveryTargetPhase targetPhase = RecoveryTargetPhase.None)
            {
                _registry = registry;
                _id = id;
                Operation = operation ?? "Recovery";
                RunEpoch = runEpoch;
                Channels = channels ?? Array.Empty<int>();
                _reservationTask = reservationTask ?? throw new ArgumentNullException(nameof(reservationTask));
                IncidentId = incidentId;
                RunId = runId;
                OwnerId = ownerId;
                OwnerKind = ownerKind;
                TargetPhase = targetPhase;
            }

            internal long Id => _id;
            internal string Operation { get; }
            internal long RunEpoch { get; }
            internal IReadOnlyList<int> Channels { get; }
            internal Guid IncidentId { get; }
            internal Guid RunId { get; }
            internal Guid OwnerId { get; }
            internal RecoveryOwnerKind OwnerKind { get; }
            internal RecoveryTargetPhase TargetPhase { get; }
            internal Task ReservationTask => _reservationTask;

            internal bool IsActive => _registry?.IsLeaseActive(_id) == true;
            internal bool IsBound => _registry?.IsLeaseBound(_id) == true;

            internal bool TryBind(Task workerTask)
            {
                if (workerTask == null) return false;
                return _registry?.Bind(this, workerTask) == true;
            }

            internal void ReportProgress(string stage)
            {
                _registry?.ReportProgress(_id, stage);
            }

            /// <summary>
            /// The caller must invoke this only after the terminal channel state
            /// has been published.  It is deliberately not tied to worker task
            /// completion, because a late continuation must not remove owner
            /// evidence before the state machine leaves Recovering.
            /// </summary>
            internal bool CompleteAfterTerminal()
            {
                var registry = Interlocked.Exchange(ref _registry, null);
                return registry?.CompleteAfterTerminal(_id) == true;
            }

            public void Dispose() => CompleteAfterTerminal();
        }

        private readonly ConcurrentDictionary<long, Entry> _active = new();
        private readonly object _changeGate = new object();
        private TaskCompletionSource<bool> _changeSignal = CreateChangeSignal();
        private long _sequence;

        internal int ActiveCount => _active.Count;

        /// <summary>
        /// Returns an already registered, real worker for a recovery cohort.
        /// Legacy state publishers may only use this bridge when their caller
        /// is itself running inside an observed recovery task; they must never
        /// create a placeholder/monitor task merely to satisfy ownership.
        /// </summary>
        internal Task FindBoundWorkerForChannels(
            long runEpoch,
            IEnumerable<int> channels)
        {
            var expected = (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .ToArray();
            if (expected.Length == 0) return null;
            foreach (var entry in _active.Values
                         .Where(item => item != null &&
                                        item.RunEpoch == runEpoch &&
                                        !item.Terminal &&
                                        item.Channels != null &&
                                        expected.All(channel =>
                                            item.Channels.Contains(channel)) &&
                                        IsRealRecoveryWorkerOperation(item.Operation))
                         .OrderBy(item => item.Id))
            {
                lock (entry.Gate)
                {
                    if (!entry.Terminal && entry.WorkerTask != null)
                        return entry.WorkerTask;
                }
            }
            return null;
        }

        private static bool IsRealRecoveryWorkerOperation(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return false;
            // These are safety/observation children.  They may overlap a
            // recovering channel, but they are not the body that owns the
            // recovery incident and must never be used as its worker identity.
            return operation.IndexOf("LeaseAbort", StringComparison.OrdinalIgnoreCase) < 0 &&
                   operation.IndexOf("StatePublication", StringComparison.OrdinalIgnoreCase) < 0 &&
                   operation.IndexOf("PowerDisable", StringComparison.OrdinalIgnoreCase) < 0 &&
                   operation.IndexOf("HydraulicRelease", StringComparison.OrdinalIgnoreCase) < 0 &&
                   operation.IndexOf("Snapshot", StringComparison.OrdinalIgnoreCase) < 0 &&
                   operation.IndexOf("Telemetry", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>
        /// Captures all active leases in one registry-owned pass.  Callers
        /// must invoke this while holding the controller's recovery contract
        /// gate when correlating it with channel state/contracts.
        /// </summary>
        internal RecoveryTaskLeaseSnapshot[] CaptureSnapshot()
        {
            return _active.Values
                .Select(entry =>
                {
                    lock (entry.Gate)
                    {
                        return new RecoveryTaskLeaseSnapshot(
                            entry.Id,
                            entry.Operation,
                            entry.RunEpoch,
                            entry.Channels,
                            entry.WorkerTask != null,
                            entry.Terminal,
                            entry.IncidentId,
                            entry.RunId,
                            entry.OwnerId,
                            entry.OwnerKind,
                            entry.TargetPhase,
                            entry.ProgressVersion,
                            entry.LastProgressUtc,
                            entry.ProgressStage);
                    }
                })
                .OrderBy(snapshot => snapshot.Id)
                .ToArray();
        }

        internal void Track(
            Task task,
            string operation,
            long runEpoch,
            params int[] channels)
        {
            if (task == null || !IsRecoveryOperation(operation)) return;
            var lease = Reserve(operation, runEpoch, channels);
            if (!lease.TryBind(task))
            {
                lease.CompleteAfterTerminal();
                return;
            }
            _ = task.ContinueWith(
                completed => lease.CompleteAfterTerminal(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Reserve a recovery incident before creating its worker Task.  The
        /// unresolved reservation task is active coverage for the affected
        /// channels and keeps the incident visible until terminal cleanup.
        /// </summary>
        internal RecoveryTaskLease Reserve(
            string operation,
            long runEpoch,
            params int[] channels)
        {
            return ReserveCore(
                operation,
                runEpoch,
                channels,
                Guid.Empty,
                Guid.Empty,
                Guid.Empty,
                RecoveryOwnerKind.None,
                RecoveryTargetPhase.None);
        }

        internal RecoveryTaskLease Reserve(RecoveryContractSnapshot contract)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            return ReserveCore(
                contract.Operation,
                contract.RunEpoch,
                contract.Channels,
                contract.IncidentId,
                contract.RunId,
                contract.OwnerId,
                contract.OwnerKind,
                contract.TargetPhase);
        }

        private RecoveryTaskLease ReserveCore(
            string operation,
            long runEpoch,
            IEnumerable<int> channels,
            Guid incidentId,
            Guid runId,
            Guid ownerId,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase)
        {
            if (!IsRecoveryOperation(operation))
                throw new ArgumentException("恢复登记操作名必须属于恢复/自愈域。", nameof(operation));

            var normalizedChannels = (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var reservation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new Entry
            {
                Id = Interlocked.Increment(ref _sequence),
                Operation = operation,
                RunEpoch = runEpoch,
                Channels = normalizedChannels,
                ReservationTask = reservation.Task,
                AutoRelease = false,
                IncidentId = incidentId,
                RunId = runId,
                OwnerId = ownerId,
                OwnerKind = ownerKind,
                TargetPhase = targetPhase,
                ProgressVersion = 1,
                LastProgressUtc = DateTime.UtcNow,
                ProgressStage = "Reserved"
            };
            _active[entry.Id] = entry;
            SignalChanged();
            return new RecoveryTaskLease(
                this,
                entry.Id,
                entry.Operation,
                entry.RunEpoch,
                entry.Channels,
                entry.ReservationTask,
                entry.IncidentId,
                entry.RunId,
                entry.OwnerId,
                entry.OwnerKind,
                entry.TargetPhase);
        }

        private void ReportProgress(long id, string stage)
        {
            if (!_active.TryGetValue(id, out var entry)) return;
            lock (entry.Gate)
            {
                if (entry.Terminal) return;
                entry.ProgressVersion++;
                entry.LastProgressUtc = DateTime.UtcNow;
                entry.ProgressStage = string.IsNullOrWhiteSpace(stage)
                    ? "Progress"
                    : stage.Trim();
            }
            SignalChanged();
        }

        internal void ReportProgressForChannel(long runEpoch, int channel, string stage)
        {
            if (runEpoch <= 0 || channel < 1 || channel > 12) return;
            foreach (var entry in _active.Values.Where(candidate =>
                         candidate != null &&
                         candidate.RunEpoch == runEpoch &&
                         !candidate.Terminal &&
                         candidate.Channels != null &&
                         candidate.Channels.Contains(channel)).ToArray())
                ReportProgress(entry.Id, stage);
        }

        private bool Bind(RecoveryTaskLease lease, Task workerTask)
        {
            if (lease == null || workerTask == null ||
                !_active.TryGetValue(lease.Id, out var entry)) return false;
            lock (entry.Gate)
            {
                if (entry.Terminal || entry.WorkerTask != null) return false;
                entry.WorkerTask = workerTask;
            }
            SignalChanged();
            return true;
        }

        private bool IsLeaseActive(long id)
        {
            return _active.TryGetValue(id, out var entry) &&
                   !Volatile.Read(ref entry.Terminal);
        }

        private bool IsLeaseBound(long id)
        {
            return _active.TryGetValue(id, out var entry) && entry.WorkerTask != null;
        }

        private bool CompleteAfterTerminal(long id)
        {
            if (!_active.TryGetValue(id, out var entry)) return false;
            lock (entry.Gate)
            {
                if (entry.Terminal) return false;
                entry.Terminal = true;
            }
            if (!_active.TryRemove(id, out _)) return false;
            SignalChanged();
            return true;
        }

        internal bool HasActiveTaskForChannel(int channel, long runEpoch)
        {
            if (channel < 1 || channel > 12) return false;
            return _active.Values.Any(entry =>
                entry.RunEpoch == runEpoch &&
                !entry.Terminal &&
                entry.Channels != null &&
                entry.Channels.Contains(channel));
        }

        internal bool TryGetActiveIncidentCoverage(
            int channel,
            long runEpoch,
            Guid incidentId,
            out RecoveryTaskLeaseSnapshot snapshot)
        {
            snapshot = null;
            if (channel < 1 || channel > 12 || runEpoch <= 0 || incidentId == Guid.Empty)
                return false;
            foreach (var entry in _active.Values
                         .Where(candidate => candidate != null &&
                                             candidate.RunEpoch == runEpoch &&
                                             candidate.IncidentId == incidentId &&
                                             !candidate.Terminal &&
                                             candidate.Channels != null &&
                                             candidate.Channels.Contains(channel))
                         .OrderBy(candidate => candidate.Id))
            {
                lock (entry.Gate)
                {
                    if (entry.Terminal) continue;
                    snapshot = new RecoveryTaskLeaseSnapshot(
                        entry.Id,
                        entry.Operation,
                        entry.RunEpoch,
                        entry.Channels,
                        entry.WorkerTask != null,
                        entry.Terminal,
                        entry.IncidentId,
                        entry.RunId,
                        entry.OwnerId,
                        entry.OwnerKind,
                        entry.TargetPhase,
                        entry.ProgressVersion,
                        entry.LastProgressUtc,
                        entry.ProgressStage);
                    return true;
                }
            }
            return false;
        }

        internal int[] CaptureActiveChannels(long runEpoch)
        {
            return _active.Values
                .Where(entry => entry.RunEpoch == runEpoch && !entry.Terminal)
                .SelectMany(entry => entry.Channels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        internal async Task<string[]> DrainThroughEpochAsync(long revokedRunEpoch, int timeoutMs)
        {
            var result = await DrainThroughEpochAsync(
                    revokedRunEpoch,
                    timeoutMs,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return result.Operations;
        }

        /// <summary>
        /// Waits for recovery leases through the revoked run epoch without
        /// polling completed Task objects.  A worker that has completed but
        /// has not published its terminal state is returned immediately as a
        /// protocol residue: waiting for that same completed Task again would
        /// otherwise create an allocation-only hot loop.
        /// </summary>
        internal async Task<DrainResult> DrainThroughEpochAsync(
            long revokedRunEpoch,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var boundedTimeoutMs = Math.Max(1, Math.Min(60000, timeoutMs));
            using (var deadlineCancellation = new CancellationTokenSource())
            {
                var deadlineTask = Task.Delay(
                    boundedTimeoutMs,
                    deadlineCancellation.Token);
                CancellationTokenRegistration cancellationRegistration;
                var cancellationTask = CreateCancellationTask(
                    cancellationToken,
                    out cancellationRegistration);
                try
                {
                    while (true)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return new DrainResult(false, true, CaptureDrainResidues(revokedRunEpoch));

                        // Capture the signal before the state snapshot.  A mutation
                        // between these two reads completes the captured signal, so a
                        // terminal publication cannot be missed.
                        var changed = CaptureChangeSignal();
                        var residues = CaptureDrainResidues(revokedRunEpoch);
                        if (residues.Length == 0)
                            return new DrainResult(true, false, residues);
                        if (residues.Any(item =>
                                item.Kind == DrainResidueKind.WorkerCompletedWithoutTerminal))
                            return new DrainResult(false, false, residues);

                        var runningWorkers = CaptureRunningWorkers(revokedRunEpoch);
                        var waiters = new List<Task>(runningWorkers.Length + 3)
                        {
                            changed,
                            deadlineTask
                        };
                        if (cancellationTask != null) waiters.Add(cancellationTask);
                        waiters.AddRange(runningWorkers);
                        var completed = await Task.WhenAny(waiters).ConfigureAwait(false);
                        if (ReferenceEquals(completed, deadlineTask))
                            return new DrainResult(false, false, CaptureDrainResidues(revokedRunEpoch));
                        if (cancellationTask != null && ReferenceEquals(completed, cancellationTask))
                            return new DrainResult(false, true, CaptureDrainResidues(revokedRunEpoch));
                    }
                }
                finally
                {
                    cancellationRegistration.Dispose();
                    deadlineCancellation.Cancel();
                }
            }
        }

        private DrainResidue[] CaptureDrainResidues(long revokedRunEpoch)
        {
            return _active.Values
                .Where(entry => entry != null && entry.RunEpoch <= revokedRunEpoch)
                .Select(entry =>
                {
                    lock (entry.Gate)
                    {
                        if (entry.Terminal) return null;
                        var kind = entry.WorkerTask == null
                            ? DrainResidueKind.ReservationOnly
                            : entry.WorkerTask.IsCompleted
                                ? DrainResidueKind.WorkerCompletedWithoutTerminal
                                : DrainResidueKind.WorkerRunning;
                        return new DrainResidue(
                            entry.Id,
                            entry.Operation,
                            entry.RunEpoch,
                            entry.Channels,
                            kind);
                    }
                })
                .Where(item => item != null)
                .OrderBy(item => item.Id)
                .ToArray();
        }

        private Task[] CaptureRunningWorkers(long revokedRunEpoch)
        {
            return _active.Values
                .Where(entry => entry != null && entry.RunEpoch <= revokedRunEpoch)
                .Select(entry =>
                {
                    lock (entry.Gate)
                    {
                        return entry.Terminal || entry.WorkerTask == null || entry.WorkerTask.IsCompleted
                            ? null
                            : entry.WorkerTask;
                    }
                })
                .Where(task => task != null)
                .Distinct()
                .ToArray();
        }

        private Task CaptureChangeSignal()
        {
            lock (_changeGate) return _changeSignal.Task;
        }

        private void SignalChanged()
        {
            TaskCompletionSource<bool> completed;
            lock (_changeGate)
            {
                completed = _changeSignal;
                _changeSignal = CreateChangeSignal();
            }
            completed.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> CreateChangeSignal() =>
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        private static Task CreateCancellationTask(
            CancellationToken cancellationToken,
            out CancellationTokenRegistration registration)
        {
            registration = default(CancellationTokenRegistration);
            if (!cancellationToken.CanBeCanceled) return null;
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            registration = cancellationToken.Register(
                state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                completion);
            return completion.Task;
        }

        internal static bool IsRecoveryOperation(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return false;
            return operation.IndexOf("Recovery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("SelfHealing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("Finalize", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("Restart", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("FaultHandling", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
