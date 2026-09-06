using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// Owns the admission/ownership transaction for one recovery incident.
    ///
    /// The coordinator gate protects only the incident index and its small
    /// state machine. Reserve/Register/factory/scheduler/Bind/Publish and all
    /// safety callbacks are deliberately executed after leaving the gate.
    /// </summary>
    internal sealed class RecoveryIncidentCoordinator
    {
        internal enum BeginResult
        {
            Rejected = 0,
            ExistingSameScope = 1,
            Created = 2
        }

        /// <summary>
        /// Scope identity intentionally excludes source correlation. A burst
        /// of different callbacks for the same run/resource therefore shares
        /// one owner and cannot create competing workers.
        /// </summary>
        internal readonly struct ScopeKey : IEquatable<ScopeKey>
        {
            internal ScopeKey(
                Guid runId,
                long runEpoch,
                RecoveryOwnerKind ownerKind,
                RecoveryTargetPhase targetPhase,
                string resourceScope)
            {
                RunId = runId;
                RunEpoch = runEpoch;
                OwnerKind = ownerKind;
                TargetPhase = targetPhase;
                ResourceScope = resourceScope ?? string.Empty;
            }

            internal Guid RunId { get; }
            internal long RunEpoch { get; }
            internal RecoveryOwnerKind OwnerKind { get; }
            internal RecoveryTargetPhase TargetPhase { get; }
            internal string ResourceScope { get; }

            public bool Equals(ScopeKey other) =>
                RunId == other.RunId &&
                RunEpoch == other.RunEpoch &&
                OwnerKind == other.OwnerKind &&
                TargetPhase == other.TargetPhase &&
                string.Equals(ResourceScope, other.ResourceScope, StringComparison.Ordinal);

            public override bool Equals(object obj) =>
                obj is ScopeKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = RunId.GetHashCode();
                    hash = (hash * 397) ^ RunEpoch.GetHashCode();
                    hash = (hash * 397) ^ (int)OwnerKind;
                    hash = (hash * 397) ^ (int)TargetPhase;
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ResourceScope);
                    return hash;
                }
            }

            public override string ToString() =>
                $"{RunId:N}/{RunEpoch}/{OwnerKind}/{TargetPhase}/{ResourceScope}";
        }

        /// <summary>
        /// All side effects required by a production manager or deterministic
        /// test seam. Every callback is invoked outside the coordinator gate.
        /// IsRegistered lets production prove that a partially-written
        /// Register has really disappeared before releasing the lease.
        /// </summary>
        internal sealed class Port
        {
            internal Func<RecoveryContractSnapshot, RecoveryTaskRegistry.RecoveryTaskLease> Reserve;
            internal Func<Func<Task>, Task> Schedule;
            internal Func<RecoveryTaskRegistry.RecoveryTaskLease, Task, bool> Bind;
            internal Action<Incident, Task> Observe;
            internal Action<RecoveryContractSnapshot> PublishRecovering;
            internal Action<RecoveryContractSnapshot, string> CommandOff;
            internal Action<RecoveryContractSnapshot, string, string> PublishSafeTerminal;
            internal Func<RecoveryContractSnapshot, bool> IsRecoveringPublished;
            internal Func<RecoveryContractSnapshot, bool> IsTerminalCommitted;
            internal Func<RecoveryContractSnapshot, bool> IsRegistered;
            internal Func<Incident, bool> Start;
            internal Action<Incident> Register;
            internal Action<Incident> Unregister;
            internal Action<string, Exception> Fault;
        }

        /// <summary>
        /// Handle returned only after the real suspended worker has been
        /// created, bound and Recovering has been published. Start is explicit
        /// so a body cannot run during the admission window.
        /// </summary>
        internal sealed class Incident
        {
            private readonly RecoveryIncidentCoordinator _owner;
            private readonly TaskCompletionSource<bool> _startSignal =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _terminalSignal =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private int _startRequested;
            private int _cancelledBeforeStart;
            private int _aborting;
            private int _terminalPublished;
            private int _terminalPublishing;
            private int _terminalStateCommitted;

            private Incident(
                RecoveryIncidentCoordinator owner,
                ScopeKey scope,
                RecoveryContractSnapshot contract,
                RecoveryTaskRegistry.RecoveryTaskLease lease)
            {
                _owner = owner;
                Scope = scope;
                Contract = contract;
                TaskLease = lease;
            }

            internal ScopeKey Scope { get; }
            internal RecoveryContractSnapshot Contract { get; }
            internal RecoveryTaskRegistry.RecoveryTaskLease TaskLease { get; }
            internal Task WorkerTask { get; private set; }
            internal Task StartSignal => _startSignal.Task;
            internal Task TerminalSignal => _terminalSignal.Task;
            internal int TerminalPublished => Volatile.Read(ref _terminalPublished);
            internal int TerminalPublishing => Volatile.Read(ref _terminalPublishing);
            internal bool TerminalStateCommitted => Volatile.Read(ref _terminalStateCommitted) != 0;
            internal bool IsAborting => Volatile.Read(ref _aborting) != 0;

            internal static Incident Create(
                RecoveryIncidentCoordinator owner,
                ScopeKey scope,
                RecoveryContractSnapshot contract,
                RecoveryTaskRegistry.RecoveryTaskLease lease)
            {
                return new Incident(owner, scope, contract, lease);
            }

            internal void SetWorkerTask(Task workerTask)
            {
                if (workerTask == null)
                    throw new ArgumentNullException(nameof(workerTask));
                WorkerTask = workerTask;
            }

            internal bool Start() => _owner.Start(this);

            internal bool CompleteAfterTerminal(
                Action<RecoveryContractSnapshot> publishTerminal)
            {
                return _owner.Complete(this, publishTerminal);
            }

            internal bool TryMarkAborting()
            {
                return Interlocked.CompareExchange(ref _aborting, 1, 0) == 0;
            }

            internal bool TryMarkTerminalPublishing()
            {
                return Interlocked.CompareExchange(ref _terminalPublishing, 1, 0) == 0;
            }

            internal void ClearTerminalPublishing() =>
                Volatile.Write(ref _terminalPublishing, 0);

            internal void MarkTerminalStateCommitted()
            {
                Interlocked.Exchange(ref _terminalStateCommitted, 1);
            }

            internal void MarkTerminalPublished()
            {
                Interlocked.Exchange(ref _terminalPublished, 1);
                _terminalSignal.TrySetResult(true);
            }

            internal bool CancelBeforeStart()
            {
                if (Volatile.Read(ref _startRequested) != 0)
                    return false;
                if (Interlocked.Exchange(ref _cancelledBeforeStart, 1) != 0)
                    return false;
                _terminalSignal.TrySetCanceled();
                return _startSignal.TrySetCanceled();
            }

            internal bool ReleaseStartSignal()
            {
                if (Volatile.Read(ref _terminalPublished) != 0 ||
                    Volatile.Read(ref _terminalStateCommitted) != 0 ||
                    Volatile.Read(ref _cancelledBeforeStart) != 0)
                    return false;
                if (Interlocked.Exchange(ref _startRequested, 1) != 0)
                    return false;
                return _startSignal.TrySetResult(true);
            }
        }

        private sealed class BeginEntry
        {
            internal readonly ScopeKey Scope;
            internal readonly RecoveryContractSnapshot Contract;
            internal readonly TaskCompletionSource<BeginOutcome> Completion =
                new TaskCompletionSource<BeginOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly int BeginThreadId;
            internal Incident Incident;
            internal RecoveryTaskRegistry.RecoveryTaskLease Lease;
            internal bool RegisterAttempted;
            internal int AbortStarted;

            internal BeginEntry(
                ScopeKey scope,
                RecoveryContractSnapshot contract)
            {
                Scope = scope;
                Contract = contract;
                BeginThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            internal bool TryMarkAbortStarted()
            {
                return Interlocked.CompareExchange(ref AbortStarted, 1, 0) == 0;
            }
        }

        private sealed class BeginOutcome
        {
            internal BeginResult Result;
            internal Incident Incident;
        }

        private readonly object _gate;
        private readonly Port _port;
        // Scope entries include a not-yet-registered reservation placeholder.
        private readonly Dictionary<ScopeKey, BeginEntry> _activeByScope =
            new Dictionary<ScopeKey, BeginEntry>();
        private readonly Dictionary<Guid, BeginEntry> _activeByIncident =
            new Dictionary<Guid, BeginEntry>();

        internal RecoveryIncidentCoordinator(object gate, Port port)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _port = port ?? throw new ArgumentNullException(nameof(port));
            ValidatePort(_port);
        }

        internal int ActiveCount
        {
            get
            {
                lock (_gate)
                    return _activeByScope.Count;
            }
        }

        internal Incident[] CaptureActive()
        {
            lock (_gate)
            {
                return _activeByScope.Values
                    .Where(entry => entry.Incident != null &&
                                    entry.Incident.TerminalPublished == 0)
                    .Select(entry => entry.Incident)
                    .ToArray();
            }
        }

        internal bool HasActive(Guid runId, long runEpoch, IEnumerable<int> channels)
        {
            var resourceScope = BuildResourceScope(channels);
            lock (_gate)
            {
                return _activeByScope.Values.Any(entry =>
                    entry.Scope.RunId == runId &&
                    entry.Scope.RunEpoch == runEpoch &&
                    string.Equals(
                        entry.Scope.ResourceScope,
                        resourceScope,
                        StringComparison.Ordinal));
            }
        }

        internal BeginResult TryBegin(
            string operation,
            Guid runId,
            long runEpoch,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            Guid ownerId,
            IEnumerable<int> channels,
            Func<RecoveryContractSnapshot, Func<Task>> workerFactory,
            Action<RecoveryContractSnapshot> publishRecovering,
            out Incident incident,
            Guid incidentId = default)
        {
            return TryBegin(
                operation,
                runId,
                runEpoch,
                ownerKind,
                targetPhase,
                ownerId,
                channels,
                channels,
                workerFactory,
                publishRecovering,
                out incident,
                incidentId);
        }

        internal BeginResult TryBegin(
            string operation,
            Guid runId,
            long runEpoch,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            Guid ownerId,
            IEnumerable<int> ownedChannels,
            IEnumerable<int> safetyAffectedChannels,
            Func<RecoveryContractSnapshot, Func<Task>> workerFactory,
            Action<RecoveryContractSnapshot> publishRecovering,
            out Incident incident,
            Guid incidentId = default)
        {
            incident = null;
            var owned = NormalizeChannels(ownedChannels);
            var safetyAffected = NormalizeChannels(safetyAffectedChannels)
                .Concat(owned)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (string.IsNullOrWhiteSpace(operation) ||
                !RecoveryTaskRegistry.IsRecoveryOperation(operation) ||
                runId == Guid.Empty || runEpoch <= 0 || ownerId == Guid.Empty ||
                ownerKind == RecoveryOwnerKind.None || ownerKind == RecoveryOwnerKind.Unknown ||
                targetPhase == RecoveryTargetPhase.None || owned.Length == 0 ||
                safetyAffected.Length == 0 ||
                workerFactory == null || publishRecovering == null)
                return BeginResult.Rejected;

            var scope = new ScopeKey(
                runId,
                runEpoch,
                ownerKind,
                targetPhase,
                BuildResourceScope(safetyAffected));
            var startedUtc = DateTime.UtcNow;
            var contract = new RecoveryContractSnapshot(
                incidentId == Guid.Empty ? Guid.NewGuid() : incidentId,
                runId,
                runEpoch,
                ownerId,
                ownerKind,
                targetPhase,
                operation,
                startedUtc,
                startedUtc.AddMilliseconds(Math.Max(1, RecoveryGroupHardDeadlineMs)),
                owned,
                safetyAffected);

            BeginEntry entry = null;
            BeginEntry existing = null;
            lock (_gate)
            {
                if (_activeByScope.TryGetValue(scope, out existing))
                {
                    // Same scope is a single begin transaction. The caller
                    // waits outside the gate for its result below.
                }
                else if (_activeByScope.Values.Any(candidate =>
                             HasChannelOverlap(candidate.Scope, scope)))
                {
                    // A different cohort may not steal even one channel from
                    // the first incident. This returns before Reserve, OFF or
                    // any state publication.
                    return BeginResult.Rejected;
                }
                else
                {
                    entry = new BeginEntry(scope, contract.Clone());
                    _activeByScope.Add(scope, entry);
                }
            }

            if (existing != null)
            {
                // A callback that re-enters the same coordinator on the same
                // thread cannot wait for its own begin transaction. It gets
                // the existing handle if already available.
                if (existing.BeginThreadId == Thread.CurrentThread.ManagedThreadId)
                {
                    incident = existing.Incident;
                    return BeginResult.ExistingSameScope;
                }

                try
                {
                    var outcome = existing.Completion.Task
                        .GetAwaiter()
                        .GetResult();
                    incident = outcome?.Incident;
                    return outcome != null && outcome.Result == BeginResult.Created
                        ? BeginResult.ExistingSameScope
                        : BeginResult.Rejected;
                }
                catch (Exception waitError)
                {
                    ReportFaultOutsideGate("begin-wait", waitError);
                    return BeginResult.Rejected;
                }
            }

            return ExecuteBegin(
                entry,
                operation,
                owned,
                workerFactory,
                publishRecovering,
                out incident);
        }

        private BeginResult ExecuteBegin(
            BeginEntry entry,
            string operation,
            int[] owned,
            Func<RecoveryContractSnapshot, Func<Task>> workerFactory,
            Action<RecoveryContractSnapshot> publishRecovering,
            out Incident incident)
        {
            incident = null;
            try
            {
                // Reserve is intentionally outside _gate. The reservation
                // placeholder already blocks overlapping callers.
                var lease = _port.Reserve(entry.Contract.Clone());
                if (lease == null)
                    throw new InvalidOperationException("Recovery lease reserve returned null.");

                var created = Incident.Create(
                    this,
                    entry.Scope,
                    entry.Contract.Clone(),
                    lease);
                lock (_gate)
                {
                    if (!_activeByScope.TryGetValue(entry.Scope, out var current) ||
                        !ReferenceEquals(current, entry))
                        throw new InvalidOperationException("Recovery reservation disappeared before registration.");
                    entry.Lease = lease;
                    entry.Incident = created;
                    _activeByIncident[entry.Contract.IncidentId] = entry;
                }

                // Register may write the manager dictionary and then throw;
                // RegisterAttempted is frozen before the call so cleanup always
                // compensates with an idempotent Unregister.
                entry.RegisterAttempted = true;
                _port.Register?.Invoke(created);

                var body = workerFactory(entry.Contract.Clone());
                if (body == null)
                    throw new InvalidOperationException("Recovery worker factory returned null body.");

                var scheduled = _port.Schedule(() => RunWorkerAsync(created, body));
                if (scheduled == null)
                    throw new InvalidOperationException("Recovery worker scheduler returned null Task.");
                created.SetWorkerTask(scheduled);

                if (!_port.Bind(created.TaskLease, scheduled))
                    throw new InvalidOperationException("Recovery worker binding failed.");

                publishRecovering(entry.Contract.Clone());
                if (_port.IsRecoveringPublished != null &&
                    !_port.IsRecoveringPublished(entry.Contract.Clone()))
                    throw new InvalidOperationException(
                        "Recovering publication did not commit the complete owner contract.");

                _port.Observe?.Invoke(created, scheduled);
                incident = created;
                CompleteBegin(entry, BeginResult.Created, created);
                return BeginResult.Created;
            }
            catch (Exception registrationError)
            {
                ReportFaultOutsideGate("registration", registrationError);
                AbortEntryOutsideGate(
                    entry,
                    "RecoveryContractRegistrationFailed",
                    registrationError);
                CompleteBegin(entry, BeginResult.Rejected, null);
                incident = null;
                return BeginResult.Rejected;
            }
        }

        private void CompleteBegin(
            BeginEntry entry,
            BeginResult result,
            Incident incident)
        {
            entry.Completion.TrySetResult(new BeginOutcome
            {
                Result = result,
                Incident = incident
            });
        }

        private async Task RunWorkerAsync(Incident incident, Func<Task> body)
        {
            try
            {
                await incident.StartSignal.ConfigureAwait(false);
                if (incident.IsAborting ||
                    incident.TerminalPublished != 0 ||
                    incident.TerminalStateCommitted)
                    return;
                incident.TaskLease.ReportProgress("WorkerStarted");
                var bodyTask = body();
                if (bodyTask == null)
                    throw new InvalidOperationException(
                        $"Recovery worker body returned null Task. Incident={incident.Contract.IncidentId:N}");
                await bodyTask.ConfigureAwait(false);
                incident.TaskLease.ReportProgress("WorkerCompleted");
                if (incident.TerminalPublished == 0 &&
                    !incident.TerminalStateCommitted &&
                    !incident.IsAborting)
                {
                    // A normally-returning worker is not itself a terminal
                    // fact.  Fail closed and release the registry owner only
                    // after the authoritative state store confirms a safe
                    // terminal.  This removes WorkerCompletedWithoutTerminal
                    // as a permanent StopAll residue.
                    incident.CompleteAfterTerminal(contract =>
                    {
                        const string reason = "RecoveryWorkerCompletedWithoutTerminal";
                        _port.CommandOff?.Invoke(contract, reason);
                        _port.PublishSafeTerminal?.Invoke(
                            contract,
                            reason,
                            $"恢复 worker 已返回但未发布终态；已自动保持断电并收口。" +
                            $" Incident={contract.IncidentId:N}");
                    });
                }
            }
            catch (TaskCanceledException) when (incident.IsAborting)
            {
                // Admission failed before Start; physical safety cleanup has
                // already happened outside the coordinator gate.
            }
            catch (Exception workerError)
            {
                ReportFaultOutsideGate("body", workerError);
                var baseError = workerError.GetBaseException() ?? workerError;
                var reason = "RecoveryWorkerFailed:" + baseError.GetType().Name;
                var detail =
                    $"{baseError.Message};Incident={incident.Contract.IncidentId:N};" +
                    $"Scope={incident.Scope}";
                incident.CompleteAfterTerminal(contract =>
                {
                    _port.CommandOff?.Invoke(contract, reason);
                    _port.PublishSafeTerminal?.Invoke(contract, reason, detail);
                });
                throw;
            }
        }

        private bool Start(Incident incident)
        {
            if (incident == null) return false;
            BeginEntry entry;
            lock (_gate)
            {
                if (!_activeByIncident.TryGetValue(
                        incident.Contract.IncidentId,
                        out entry) ||
                    entry.Incident == null ||
                    !ReferenceEquals(entry.Incident, incident) ||
                    incident.IsAborting ||
                    incident.TerminalPublished != 0 ||
                    incident.TerminalStateCommitted)
                    return false;
            }

            try
            {
                var started = _port.Start == null
                    ? incident.ReleaseStartSignal()
                    : _port.Start(incident);
                if (started) return true;
                throw new InvalidOperationException("Recovery worker start was rejected.");
            }
            catch (Exception startError)
            {
                ReportFaultOutsideGate("start", startError);
                AbortEntryOutsideGate(
                    entry,
                    "RecoveryWorkerStartFailed",
                    startError);
                return false;
            }
        }

        private bool Complete(
            Incident incident,
            Action<RecoveryContractSnapshot> publishTerminal)
        {
            if (incident == null || incident.Contract == null)
                return false;

            BeginEntry entry;
            lock (_gate)
            {
                if (!_activeByIncident.TryGetValue(
                        incident.Contract.IncidentId,
                        out entry) ||
                    entry.Incident == null ||
                    !ReferenceEquals(entry.Incident, incident) ||
                    incident.TerminalPublished != 0 ||
                    !incident.TryMarkTerminalPublishing())
                    return false;
            }

            var verified = false;
            var published = false;
            try
            {
                if (publishTerminal == null)
                {
                    _port.PublishSafeTerminal?.Invoke(
                        incident.Contract.Clone(),
                        "RecoveryTerminalPublisherMissing",
                        "恢复终态发布回调缺失，已保持安全终态。");
                    published = true;
                }
                else
                {
                    publishTerminal(incident.Contract.Clone());
                    published = true;
                }
                verified = IsTerminalCommitted(incident.Contract);
                if (!verified)
                    verified = ForceSafeTerminalOutsideGate(
                        incident.Contract,
                        "RecoveryTerminalStateNotObserved",
                        "恢复终态未被权威状态存储完整观察，已先断电并保持安全终态。");
            }
            catch (Exception terminalError)
            {
                ReportFaultOutsideGate("terminal", terminalError);
                verified = ForceSafeTerminalOutsideGate(
                    incident.Contract,
                    "RecoveryTerminalPublisherFailed",
                    "恢复终态发布异常，已先断电并保持安全终态。");
                published = verified;
            }

            if (!verified)
            {
                incident.ClearTerminalPublishing();
                return false;
            }

            incident.MarkTerminalStateCommitted();
            incident.CancelBeforeStart();
            var unregistered = EnsureUnregisteredOutsideGate(entry);
            var cleaned = unregistered && RemoveEntryAfterCleanup(entry);
            if (!cleaned)
                incident.ClearTerminalPublishing();
            return published && verified && cleaned;
        }

        private bool IsTerminalCommitted(RecoveryContractSnapshot contract)
        {
            try
            {
                return _port.IsTerminalCommitted == null ||
                       _port.IsTerminalCommitted(contract.Clone());
            }
            catch (Exception error)
            {
                ReportFaultOutsideGate("terminal-recheck", error);
                return false;
            }
        }

        private bool ForceSafeTerminalOutsideGate(
            RecoveryContractSnapshot contract,
            string reasonCode,
            string reasonText)
        {
            try
            {
                _port.CommandOff?.Invoke(contract.Clone(), reasonCode);
            }
            catch (Exception offError)
            {
                ReportFaultOutsideGate("off", offError);
            }
            try
            {
                _port.PublishSafeTerminal?.Invoke(
                    contract.Clone(),
                    reasonCode,
                    reasonText);
            }
            catch (Exception terminalError)
            {
                ReportFaultOutsideGate("safe-terminal", terminalError);
            }
            return IsTerminalCommitted(contract);
        }

        private void AbortEntryOutsideGate(
            BeginEntry entry,
            string reasonCode,
            Exception error)
        {
            if (entry == null || !entry.TryMarkAbortStarted())
                return;

            var incident = entry.Incident;
            incident?.TryMarkAborting();
            incident?.CancelBeforeStart();

            // Physical OFF and safe terminal publication are strictly outside
            // the gate. No caller waits for the suspended wrapper here.
            var verified = ForceSafeTerminalOutsideGate(
                entry.Contract,
                reasonCode,
                $"恢复事务异常收口，已先断电并保持安全终态。 Error={error?.Message ?? "Unknown"}");
            var unregistered = EnsureUnregisteredOutsideGate(entry);
            if (verified && unregistered)
                RemoveEntryAfterCleanup(entry);
        }

        private bool EnsureUnregisteredOutsideGate(BeginEntry entry)
        {
            if (entry == null || !entry.RegisterAttempted || entry.Incident == null)
                return true;

            // Register may write its dictionary entry before throwing.
            // Compensate idempotently and release no lease until absence is
            // confirmed by the manager.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    _port.Unregister?.Invoke(entry.Incident);
                }
                catch (Exception unregisterError)
                {
                    ReportFaultOutsideGate("unregister", unregisterError);
                }

                if (!IsRegisteredOutsideGate(entry.Contract))
                    return true;
                Thread.Yield();
            }
            return false;
        }

        private bool IsRegisteredOutsideGate(RecoveryContractSnapshot contract)
        {
            if (_port.IsRegistered == null)
                return true;
            try
            {
                return _port.IsRegistered(contract.Clone());
            }
            catch (Exception registrationCheckError)
            {
                ReportFaultOutsideGate("registration-recheck", registrationCheckError);
                // A failed absence check is fail-closed.
                return true;
            }
        }

        private bool RemoveEntryAfterCleanup(BeginEntry entry)
        {
            if (entry == null) return false;
            RecoveryTaskRegistry.RecoveryTaskLease lease = null;
            var removed = false;
            lock (_gate)
            {
                if (_activeByScope.TryGetValue(entry.Scope, out var current) &&
                    ReferenceEquals(current, entry))
                {
                    _activeByScope.Remove(entry.Scope);
                    _activeByIncident.Remove(entry.Contract.IncidentId);
                    lease = entry.Lease;
                    removed = true;
                    entry.Incident?.MarkTerminalPublished();
                }
            }

            // Lease completion is intentionally after the dictionary CAS and
            // outside the gate; it can synchronously notify registry readers.
            if (removed)
                lease?.CompleteAfterTerminal();
            return removed;
        }

        private void ReportFaultOutsideGate(string stage, Exception error)
        {
            try { _port.Fault?.Invoke(stage, error); }
            catch { }
        }

        private static bool HasChannelOverlap(ScopeKey left, ScopeKey right)
        {
            // A physical channel cannot have two recovery cohorts. The exact
            // same scope is handled as a deduplicated begin above; every
            // different scope is rejected while either cohort still owns the
            // physical channel, including a stale run/epoch.
            var leftChannels = ParseResourceScope(left.ResourceScope);
            var rightChannels = ParseResourceScope(right.ResourceScope);
            return leftChannels.Any(channel => rightChannels.Contains(channel));
        }

        private static int[] ParseResourceScope(string resourceScope)
        {
            if (string.IsNullOrWhiteSpace(resourceScope))
                return Array.Empty<int>();
            var text = resourceScope.StartsWith("EPB:", StringComparison.OrdinalIgnoreCase)
                ? resourceScope.Substring(4)
                : resourceScope;
            return text
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var channel) ? channel : 0)
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .ToArray();
        }

        private static void ValidatePort(Port port)
        {
            if (port.Reserve == null || port.Schedule == null || port.Bind == null ||
                port.PublishRecovering == null || port.Register == null ||
                port.Unregister == null)
                throw new ArgumentException("RecoveryIncidentCoordinator Port is incomplete.", nameof(port));
        }

        internal static int[] NormalizeChannels(IEnumerable<int> channels) =>
            (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();

        internal static string BuildResourceScope(IEnumerable<int> channels)
        {
            var normalized = NormalizeChannels(channels);
            return normalized.Length == 0
                ? string.Empty
                : "EPB:" + string.Join(",", normalized);
        }

        private const int RecoveryGroupHardDeadlineMs = 60000;
    }
}
