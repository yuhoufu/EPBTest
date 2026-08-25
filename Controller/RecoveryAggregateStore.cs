using System;
using System.Collections.Generic;
using System.Linq;

namespace Controller
{
    /// <summary>
    /// Controller-owned commit point for watchdog recovery evidence.
    ///
    /// Producers replace one source at a time under a short store lock.  The
    /// source values are copied before the lock is released and a complete
    /// aggregate is built from those committed copies.  Readers never inspect
    /// a live controller dictionary and never receive a source object owned by
    /// a caller.  This is intentionally an in-memory store for this component;
    /// durable incident/journal storage remains a separate concern.
    /// </summary>
    internal sealed class RecoveryAggregateStore
    {
        internal sealed class DaqCheckpointReceipt
        {
            internal DaqCheckpointReceipt(long version, bool stable, string detail)
            {
                Version = version;
                Stable = stable;
                Detail = detail ?? string.Empty;
            }

            internal long Version { get; }
            internal bool Stable { get; }
            internal string Detail { get; }
        }

        private readonly object _gate = new object();
        private List<ChannelRuntimeStateChangedEvent> _channels =
            new List<ChannelRuntimeStateChangedEvent>();
        private List<RecoveryContractSnapshot> _contracts =
            new List<RecoveryContractSnapshot>();
        private List<WatchdogRecoveryLeaseSnapshot> _leases =
            new List<WatchdogRecoveryLeaseSnapshot>();
        private int _daqRecoveryCount;
        private Guid _daqRunId;
        private long _daqRunEpoch;
        private string _daqStage = string.Empty;
        private long _daqProgressVersion;
        private DateTime _daqStageStartedUtc;
        private DateTime _daqHardDeadlineUtc;
        private string _retainedCommittedStage = string.Empty;
        private long _retainedCommittedProgressVersion;
        private DateTime _retainedCommittedStageStartedUtc;
        private DateTime _retainedCommittedHardDeadlineUtc;
        private string _retainedTerminalStage = string.Empty;
        private long _retainedTerminalProgressVersion;
        private DateTime _retainedTerminalStageStartedUtc;
        private DateTime _retainedTerminalHardDeadlineUtc;
        private long _retainedCommittedAggregateVersion;
        private long _retainedTerminalAggregateVersion;
        private LogicalQuiescenceSnapshot _logical = new LogicalQuiescenceSnapshot();
        private StopSafetyProgressSnapshot _stop = new StopSafetyProgressSnapshot();
        private InfrastructureRecoverySource _infrastructure =
            new InfrastructureRecoverySource();
        private PowerRecoverySource _power = new PowerRecoverySource();
        private TimerRecoverySource _timer = new TimerRecoverySource();
        // A timeout/takeover is an incident-level fact.  It must remain in
        // every retained aggregate until a fresh validated process/run clears
        // it explicitly; a late inactive/diagnostic publish cannot erase the
        // evidence or make the sidecar believe the old process is healthy.
        private StopSafetyProgressSnapshot _stickyStopTerminal;
        private long _version;
        private WatchdogRecoveryAggregateSnapshot _stable;

        internal RecoveryAggregateStore()
        {
            lock (_gate)
                _stable = BuildStableSnapshotLocked();
        }

        /// <summary>
        /// Replace the complete ownership source.  The caller normally holds
        /// EpbManager's recovery contract gate while collecting these three
        /// lists, so the state/contract/lease relation is one source revision.
        /// </summary>
        internal WatchdogRecoveryAggregateSnapshot PublishOwnershipSource(
            IEnumerable<ChannelRuntimeStateChangedEvent> channels,
            IEnumerable<RecoveryContractSnapshot> contracts,
            IEnumerable<WatchdogRecoveryLeaseSnapshot> leases)
        {
            lock (_gate)
            {
                _channels = CloneChannels(channels);
                var recoveringChannels = new HashSet<int>(
                    _channels.Where(item => item.State == ChannelRuntimeState.Recovering)
                        .Select(item => item.Channel));
                // A contract/lease is active ownership evidence only while at
                // least one of its channels is still Recovering.  This keeps a
                // terminal aggregate from retaining an owner merely because
                // cleanup of the underlying registry is a later callback; a
                // partial terminal still retains the incident for the channels
                // that remain Recovering.
                _contracts = CloneContracts(contracts)
                    .Where(item => item.Channels.Any(recoveringChannels.Contains))
                    .ToList();
                _leases = CloneLeases(leases)
                    .Where(item => item.Channels.Any(recoveringChannels.Contains))
                    .ToList();
                return CommitLocked();
            }
        }

        /// <summary>
        /// Publish the Controller-owned DAQ progress/checkpoint source.  The
        /// returned version is the aggregate version, not a UI-generated
        /// signature.  The aggregate is stable only after this replacement.
        /// </summary>
        internal DaqCheckpointReceipt PublishDaqCheckpoint(
            int daqRecoveryCount,
            string daqStage,
            long daqProgressVersion,
            DateTime daqStageStartedUtc,
            DateTime daqHardDeadlineUtc,
            string retainedCommittedStage = null,
            long retainedCommittedProgressVersion = 0,
            DateTime retainedCommittedStageStartedUtc = default(DateTime),
            DateTime retainedCommittedHardDeadlineUtc = default(DateTime),
            string detail = null,
            Guid runId = default(Guid),
            long runEpoch = 0)
        {
            lock (_gate)
            {
                if (runId != Guid.Empty &&
                    (_daqRunId != runId || _daqRunEpoch != runEpoch))
                {
                    _daqRunId = runId;
                    _daqRunEpoch = runEpoch;
                    _retainedCommittedStage = string.Empty;
                    _retainedCommittedProgressVersion = 0;
                    _retainedCommittedStageStartedUtc = default(DateTime);
                    _retainedCommittedHardDeadlineUtc = default(DateTime);
                    _retainedTerminalStage = string.Empty;
                    _retainedTerminalProgressVersion = 0;
                    _retainedTerminalStageStartedUtc = default(DateTime);
                    _retainedTerminalHardDeadlineUtc = default(DateTime);
                    _retainedCommittedAggregateVersion = 0;
                    _retainedTerminalAggregateVersion = 0;
                    _daqProgressVersion = 0;
                }
                _daqRecoveryCount = Math.Max(0, daqRecoveryCount);
                _daqStage = daqStage ?? string.Empty;
                _daqProgressVersion = Math.Max(0, daqProgressVersion);
                _daqStageStartedUtc = daqStageStartedUtc;
                _daqHardDeadlineUtc = daqHardDeadlineUtc;
                if (retainedCommittedStage != null)
                {
                    _retainedCommittedStage = retainedCommittedStage;
                    _retainedCommittedProgressVersion =
                        Math.Max(_retainedCommittedProgressVersion, retainedCommittedProgressVersion);
                    _retainedCommittedStageStartedUtc = retainedCommittedStageStartedUtc;
                    _retainedCommittedHardDeadlineUtc = retainedCommittedHardDeadlineUtc;
                }
                var snapshot = CommitLocked(retainedCommittedStage != null, false);
                return new DaqCheckpointReceipt(snapshot.Version, snapshot.IsStable,
                    detail ?? "RecoveryAggregateDaqCheckpoint");
            }
        }

        /// <summary>
        /// Retain the terminal evidence as a later aggregate commit.  A
        /// terminal snapshot is never allowed to reuse the Committed version.
        /// </summary>
        internal DaqCheckpointReceipt PublishDaqTerminal(
            string terminalStage,
            long terminalProgressVersion,
            DateTime terminalStageStartedUtc,
            DateTime terminalHardDeadlineUtc,
            string detail = null,
            Guid runId = default(Guid),
            long runEpoch = 0)
        {
            lock (_gate)
            {
                if (runId != Guid.Empty &&
                    (_daqRunId != runId || _daqRunEpoch != runEpoch))
                {
                    _daqRunId = runId;
                    _daqRunEpoch = runEpoch;
                    _retainedCommittedStage = string.Empty;
                    _retainedCommittedProgressVersion = 0;
                    _retainedCommittedStageStartedUtc = default(DateTime);
                    _retainedCommittedHardDeadlineUtc = default(DateTime);
                    _retainedCommittedAggregateVersion = 0;
                    _retainedTerminalAggregateVersion = 0;
                }
                _retainedTerminalStage = terminalStage ?? string.Empty;
                _retainedTerminalProgressVersion = Math.Max(
                    _retainedTerminalProgressVersion,
                    terminalProgressVersion);
                _retainedTerminalStageStartedUtc = terminalStageStartedUtc;
                _retainedTerminalHardDeadlineUtc = terminalHardDeadlineUtc;
                _daqStage = _retainedTerminalStage;
                _daqProgressVersion = Math.Max(
                    _daqProgressVersion,
                    terminalProgressVersion);
                _daqStageStartedUtc = terminalStageStartedUtc;
                _daqHardDeadlineUtc = terminalHardDeadlineUtc;
                var snapshot = CommitLocked(false, true);
                return new DaqCheckpointReceipt(snapshot.Version, snapshot.IsStable,
                    detail ?? "RecoveryAggregateDaqTerminal");
            }
        }

        /// <summary>
        /// Replace the Stop source with a deep copy and atomically publish a
        /// complete aggregate.
        /// </summary>
        internal WatchdogRecoveryAggregateSnapshot PublishStopSource(
            StopSafetyProgressSnapshot stop)
        {
            lock (_gate)
            {
                var candidate = stop?.Clone() ?? new StopSafetyProgressSnapshot();
                if (_stickyStopTerminal != null)
                {
                    // A stop timeout/takeover is an incident-level terminal
                    // tuple.  Do not merge fields from a late/new candidate:
                    // that could replace the original transaction identity or
                    // clear Active while the Host is still deciding takeover.
                    // Only an explicitly validated fresh process may clear it
                    // through ClearStopSafetyStickyTerminal.
                    _stop = _stickyStopTerminal.Clone();
                    return CommitLocked();
                }
                if (candidate.TakeoverRequired || candidate.TimedOut)
                {
                    if (_stickyStopTerminal == null)
                        _stickyStopTerminal = candidate.Clone();
                    else
                    {
                        _stickyStopTerminal.TakeoverRequired = true;
                        _stickyStopTerminal.TimedOut = _stickyStopTerminal.TimedOut ||
                                                       candidate.TimedOut;
                        if (string.IsNullOrWhiteSpace(_stickyStopTerminal.TerminalReason))
                            _stickyStopTerminal.TerminalReason = candidate.TerminalReason ?? string.Empty;
                    }
                }
                _stop = candidate;
                return CommitLocked();
            }
        }

        /// <summary>
        /// Clear the retained stop incident only after a newer validated
        /// process/run has completed its safety preflight.  Generation is
        /// intentionally required so a stale callback cannot clear a timeout.
        /// </summary>
        internal bool ClearStopSafetyStickyTerminal(long validatedGeneration)
        {
            lock (_gate)
            {
                if (_stickyStopTerminal == null ||
                    validatedGeneration <= _stickyStopTerminal.Generation)
                    return false;
                _stickyStopTerminal = null;
                _stop = new StopSafetyProgressSnapshot
                {
                    Generation = validatedGeneration,
                    Active = false,
                    Detail = "Validated process attached after stop-safety incident"
                };
                CommitLocked();
                return true;
            }
        }

        /// <summary>
        /// Replace the logical source with a deep copy and atomically publish
        /// a complete aggregate.
        /// </summary>
        internal WatchdogRecoveryAggregateSnapshot PublishLogicalSource(
            LogicalQuiescenceSnapshot logical)
        {
            lock (_gate)
            {
                _logical = logical?.Clone() ?? new LogicalQuiescenceSnapshot();
                return CommitLocked();
            }
        }

        /// <summary>
        /// Replace the Controller's operational recovery sources in one store
        /// commit.  The caller may sample live dictionaries while holding the
        /// Controller recovery gate, but readers only ever see the copied
        /// source graph retained by this store.
        /// </summary>
        internal WatchdogRecoveryAggregateSnapshot PublishOperationalSources(
            InfrastructureRecoverySource infrastructure,
            PowerRecoverySource power,
            TimerRecoverySource timer)
        {
            lock (_gate)
            {
                _infrastructure = (infrastructure ?? new InfrastructureRecoverySource()).Clone();
                _power = (power ?? new PowerRecoverySource()).Clone();
                _timer = (timer ?? new TimerRecoverySource()).Clone();
                return CommitLocked();
            }
        }

        /// <summary>
        /// Return only the last committed aggregate.  The returned graph is a
        /// fresh deep copy, so mutating it cannot affect the retained snapshot
        /// or a later reader.
        /// </summary>
        internal WatchdogRecoveryAggregateSnapshot Capture()
        {
            lock (_gate)
                return CloneAggregate(_stable);
        }

        internal long CommittedVersion
        {
            get { lock (_gate) return _stable?.Version ?? 0; }
        }

        private WatchdogRecoveryAggregateSnapshot CommitLocked(
            bool retainedCommitted = false,
            bool retainedTerminal = false)
        {
            unchecked { _version++; }
            if (retainedCommitted)
                _retainedCommittedAggregateVersion = _version;
            if (retainedTerminal)
                _retainedTerminalAggregateVersion = _version;
            _stable = BuildStableSnapshotLocked();
            return CloneAggregate(_stable);
        }

        private WatchdogRecoveryAggregateSnapshot BuildStableSnapshotLocked()
        {
            return new WatchdogRecoveryAggregateSnapshot(
                _version,
                DateTime.UtcNow,
                _channels,
                _contracts,
                _leases,
                _daqRecoveryCount,
                _daqStage,
                _daqProgressVersion,
                _daqStageStartedUtc,
                _daqHardDeadlineUtc,
                _logical,
                _stop,
                _retainedCommittedStage,
                _retainedCommittedProgressVersion,
                _retainedCommittedStageStartedUtc,
                _retainedCommittedHardDeadlineUtc,
                _retainedTerminalStage,
                _retainedTerminalProgressVersion,
                isStable: ValidateStableSourcesLocked(),
                retainedCommittedAggregateVersion: _retainedCommittedAggregateVersion,
                retainedTerminalAggregateVersion: _retainedTerminalAggregateVersion,
                infrastructure: _infrastructure,
                power: _power,
                timer: _timer);
        }

        private bool ValidateStableSourcesLocked()
        {
            // An ownership source is structurally stable only when every live
            // Recovering channel has the same run/epoch/owner contract and a
            // bound lease covering that channel.  Empty startup state is valid.
            foreach (var state in _channels.Where(item =>
                         item != null && item.State == ChannelRuntimeState.Recovering))
            {
                var contract = _contracts.FirstOrDefault(item =>
                    item != null &&
                    item.RunId == state.RunId &&
                    item.RunEpoch == state.RunEpoch &&
                    item.OwnerId == state.RecoveryOwnerId &&
                    item.OwnerKind == state.RecoveryOwnerKind &&
                    item.TargetPhase == state.RecoveryTargetPhase &&
                    item.Channels.Contains(state.Channel));
                if (contract == null) return false;
                var lease = _leases.FirstOrDefault(item =>
                    item != null && item.RunEpoch == state.RunEpoch &&
                    item.IsBound && !item.IsTerminal &&
                    item.Channels.Contains(state.Channel));
                if (lease == null) return false;
            }
            return true;
        }

        private static WatchdogRecoveryAggregateSnapshot CloneAggregate(
            WatchdogRecoveryAggregateSnapshot source)
        {
            if (source == null)
                return new WatchdogRecoveryAggregateSnapshot(
                    0,
                    DateTime.UtcNow,
                    Array.Empty<ChannelRuntimeStateChangedEvent>(),
                    Array.Empty<RecoveryContractSnapshot>(),
                    Array.Empty<WatchdogRecoveryLeaseSnapshot>(),
                    0,
                    string.Empty,
                    0,
                    default(DateTime),
                    default(DateTime),
                    new LogicalQuiescenceSnapshot(),
                    new StopSafetyProgressSnapshot(),
                    isStable: true);

            return new WatchdogRecoveryAggregateSnapshot(
                source.Version,
                source.CapturedUtc,
                source.ChannelStates,
                source.Contracts,
                source.RegistryLeases,
                source.DaqRecoveryCount,
                source.DaqStage,
                source.DaqProgressVersion,
                source.DaqStageStartedUtc,
                source.DaqHardDeadlineUtc,
                source.Logical,
                source.StopProgress,
                source.RetainedCommittedStage,
                source.RetainedCommittedProgressVersion,
                source.RetainedCommittedStageStartedUtc,
                source.RetainedCommittedHardDeadlineUtc,
                source.RetainedTerminalStage,
                source.RetainedTerminalProgressVersion,
                source.IsStable,
                source.RetainedCommittedAggregateVersion,
                source.RetainedTerminalAggregateVersion,
                source.Infrastructure,
                source.Power,
                source.Timer);
        }

        private static List<ChannelRuntimeStateChangedEvent> CloneChannels(
            IEnumerable<ChannelRuntimeStateChangedEvent> source)
        {
            return (source ?? Array.Empty<ChannelRuntimeStateChangedEvent>())
                .Where(item => item != null)
                .Select(item => item.Clone())
                .OrderBy(item => item.Channel)
                .ToList();
        }

        private static List<RecoveryContractSnapshot> CloneContracts(
            IEnumerable<RecoveryContractSnapshot> source)
        {
            return (source ?? Array.Empty<RecoveryContractSnapshot>())
                .Where(item => item != null)
                .Select(item => item.Clone())
                .OrderBy(item => item.IncidentId)
                .ToList();
        }

        private static List<WatchdogRecoveryLeaseSnapshot> CloneLeases(
            IEnumerable<WatchdogRecoveryLeaseSnapshot> source)
        {
            return (source ?? Array.Empty<WatchdogRecoveryLeaseSnapshot>())
                .Where(item => item != null)
                .Select(item => item.Clone())
                .OrderBy(item => item.Id)
                .ToList();
        }
    }
}
