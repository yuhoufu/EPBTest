using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
                bool terminal)
            {
                Id = id;
                Operation = operation ?? string.Empty;
                RunEpoch = runEpoch;
                Channels = (channels ?? Array.Empty<int>()).ToArray();
                IsBound = bound;
                IsTerminal = terminal;
            }

            internal long Id { get; }
            internal string Operation { get; }
            internal long RunEpoch { get; }
            internal IReadOnlyList<int> Channels { get; }
            internal bool IsBound { get; }
            internal bool IsTerminal { get; }
        }

        private sealed class Entry
        {
            internal long Id;
            internal string Operation;
            internal long RunEpoch;
            internal int[] Channels;
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
                Task reservationTask)
            {
                _registry = registry;
                _id = id;
                Operation = operation ?? "Recovery";
                RunEpoch = runEpoch;
                Channels = channels ?? Array.Empty<int>();
                _reservationTask = reservationTask ?? throw new ArgumentNullException(nameof(reservationTask));
            }

            internal long Id => _id;
            internal string Operation { get; }
            internal long RunEpoch { get; }
            internal IReadOnlyList<int> Channels { get; }
            internal Task ReservationTask => _reservationTask;

            internal bool IsActive => _registry?.IsLeaseActive(_id) == true;
            internal bool IsBound => _registry?.IsLeaseBound(_id) == true;

            internal bool TryBind(Task workerTask)
            {
                if (workerTask == null) return false;
                return _registry?.Bind(this, workerTask) == true;
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
                            entry.Terminal);
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
                AutoRelease = false
            };
            _active[entry.Id] = entry;
            return new RecoveryTaskLease(
                this,
                entry.Id,
                entry.Operation,
                entry.RunEpoch,
                entry.Channels,
                entry.ReservationTask);
        }

        private bool Bind(RecoveryTaskLease lease, Task workerTask)
        {
            if (lease == null || workerTask == null ||
                !_active.TryGetValue(lease.Id, out var entry)) return false;
            lock (entry.Gate)
            {
                if (entry.Terminal || entry.WorkerTask != null) return false;
                entry.WorkerTask = workerTask;
                return true;
            }
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
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000d * Stopwatch.Frequency);
            while (true)
            {
                var pending = _active.Values
                    .Where(entry => entry.RunEpoch <= revokedRunEpoch && !entry.Terminal)
                    .ToArray();
                if (pending.Length == 0) return Array.Empty<string>();
                var remaining = (int)Math.Ceiling(
                    (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
                if (remaining <= 0)
                    return pending.Select(entry => entry.Operation).Distinct().OrderBy(x => x).ToArray();
                // A reservation with no worker is intentionally not considered
                // drained; this forces the owner to publish a terminal state and
                // close the lease explicitly rather than silently disappearing.
                var all = Task.WhenAll(pending.Select(entry =>
                    entry.WorkerTask ?? entry.ReservationTask));
                if (await Task.WhenAny(all, Task.Delay(remaining)).ConfigureAwait(false) != all)
                    return pending.Where(entry => !entry.Terminal)
                        .Select(entry => entry.Operation).Distinct().OrderBy(x => x).ToArray();
                try { await all.ConfigureAwait(false); } catch { }
            }
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
