using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Controller
{
    /// <summary>
    /// A small, controller-owned transaction for the safety part of a DAQ
    /// recovery.  The existing DAQ pipeline still owns the long running
    /// rebuild/validation work; this type owns the facts which are allowed to
    /// move the recovery state machine across the electrical safety boundary.
    /// In particular, admission and physical completion are deliberately
    /// separate facts.
    /// </summary>
    internal sealed class DaqRecoveryTransaction
    {
        internal enum Outcome
        {
            Active = 0,
            SafeIdle = 1,
            Committed = 2,
            Terminal = 3
        }

        /// <summary>
        /// Immutable receipt for one channel's high-priority OFF command.
        /// Accepted means only that the command entered the bounded DO worker;
        /// PhysicalCompletion is set only by the completion evidence callback.
        /// </summary>
        internal sealed class OffReceipt
        {
            internal OffReceipt(
                int channel,
                Guid commandId,
                bool accepted,
                bool physicalCompletion,
                string failure)
            {
                Channel = channel;
                CommandId = commandId;
                Accepted = accepted;
                PhysicalCompletion = physicalCompletion;
                Failure = failure ?? string.Empty;
            }

            internal int Channel { get; }
            internal Guid CommandId { get; }
            internal bool Accepted { get; }
            internal bool PhysicalCompletion { get; }
            internal string Failure { get; }

            internal OffReceipt WithPhysicalCompletion(bool completed)
            {
                return new OffReceipt(
                    Channel,
                    CommandId,
                    Accepted,
                    completed,
                    Failure);
            }
        }

        /// <summary>
        /// Receipt for the stable checkpoint which separates Committed from
        /// Terminal.  The current production implementation uses an injected
        /// in-memory publisher; the aggregate store can replace that publisher
        /// without changing the transaction contract.
        /// </summary>
        internal sealed class CheckpointReceipt
        {
            internal CheckpointReceipt(long version, bool stable, string detail = null)
            {
                Version = version;
                Stable = stable;
                Detail = detail ?? string.Empty;
            }

            internal long Version { get; }
            internal bool Stable { get; }
            internal string Detail { get; }
        }

        /// <summary>
        /// Hardware/state-store seam.  Production supplies real DO and power
        /// calls; tests inject only the boundary operations and observe the
        /// transaction itself.
        /// </summary>
        internal sealed class Port
        {
            internal Func<int, OffReceipt> TrySubmitOff { get; set; }
            internal Action<DaqRecoveryPhase> PublishPhase { get; set; }
            internal Action<IReadOnlyList<int>> SubmitSafetyOff { get; set; }
            internal Action<IReadOnlyList<int>> DisablePower { get; set; }
            internal Func<IReadOnlyList<int>, IReadOnlyCollection<int>> PublishTerminal { get; set; }
            internal Func<long, CheckpointReceipt> PublishCheckpoint { get; set; }
            internal Func<long, CheckpointReceipt> PublishTerminalCheckpoint { get; set; }
        }

        /// <summary>
        /// Bind the Controller-owned terminal callbacks after a production
        /// context has been created.  The transaction still owns the
        /// monotonic terminal protocol; EpbManager supplies the authoritative
        /// channel-state publication and phase gate used by the live context.
        /// This is intentionally an internal production seam, not a test copy
        /// of the terminal algorithm.
        /// </summary>
        internal void BindControllerTerminalCallbacks(
            Action<DaqRecoveryPhase> publishPhase,
            Func<IReadOnlyList<int>, IReadOnlyCollection<int>> publishTerminal)
        {
            lock (_gate)
            {
                if (publishPhase != null)
                    _port.PublishPhase = publishPhase;
                if (publishTerminal != null)
                    _port.PublishTerminal = publishTerminal;
            }
        }

        private sealed class MutableReceipt
        {
            internal MutableReceipt(OffReceipt receipt)
            {
                Channel = receipt.Channel;
                CommandId = receipt.CommandId;
                Accepted = receipt.Accepted;
                PhysicalCompletion = receipt.PhysicalCompletion;
                Failure = receipt.Failure;
            }

            internal int Channel;
            internal Guid CommandId;
            internal bool Accepted;
            internal bool PhysicalCompletion;
            internal string Failure;
        }

        private readonly object _gate = new object();
        private readonly int[] _channels;
        private readonly Dictionary<int, MutableReceipt> _offReceipts;
        private readonly HashSet<string> _earlyPhysicalCompletions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _terminalChannels = new HashSet<int>();
        private readonly Port _port;
        private DaqRecoveryPhase _phase = DaqRecoveryPhase.StaleDetected;
        private Outcome _outcome = Outcome.Active;
        private long _progressVersion;
        private long _committedVersion;
        private long _terminalVersion;
        private long _stableCheckpointVersion;
        // Transaction progress and aggregate-store revisions are different
        // clocks.  ProgressVersion/CommittedVersion/TerminalVersion are
        // monotonic phase evidence owned by this transaction; the two
        // aggregate versions below are receipts returned by the store and
        // must never be used to advance the phase clock.
        private long _committedAggregateVersion;
        private long _terminalAggregateVersion;
        private int _terminalPhasePublished;
        private int _terminalAttemptInProgress;
        private int _submissionInProgress;
        private int _safeIdleDecided;
        private int _offSafetyActionStarted;
        private int _powerSafetyActionStarted;
        private string _safeIdleReason;

        internal DaqRecoveryTransaction(
            IEnumerable<int> affectedChannels,
            Port port,
            Guid identity = default(Guid))
        {
            _channels = (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (_channels.Length == 0)
                throw new ArgumentException("DAQ恢复必须至少包含一个受影响通道。", nameof(affectedChannels));
            _port = port ?? throw new ArgumentNullException(nameof(port));
            if (_port.TrySubmitOff == null)
                throw new ArgumentException("DAQ恢复事务缺少OFF准入端口。", nameof(port));
            _offReceipts = new Dictionary<int, MutableReceipt>(_channels.Length);
            // The transaction identity is frozen at construction.  Terminal
            // publication must use this identity rather than a mutable/global
            // run epoch: CancelAll may advance the epoch while this same
            // incident still owns its safety closure.
            Identity = identity == Guid.Empty ? Guid.NewGuid() : identity;
        }

        internal IReadOnlyList<int> Channels => _channels;

        internal Guid Identity { get; }

        internal DaqRecoveryPhase Phase
        {
            get { lock (_gate) return _phase; }
        }

        internal Outcome CurrentOutcome
        {
            get { lock (_gate) return _outcome; }
        }

        internal long ProgressVersion
        {
            get { lock (_gate) return _progressVersion; }
        }

        internal long CommittedVersion
        {
            get { lock (_gate) return _committedVersion; }
        }

        internal long TerminalVersion
        {
            get { lock (_gate) return _terminalVersion; }
        }

        internal long StableCheckpointVersion
        {
            get { lock (_gate) return _stableCheckpointVersion; }
        }

        internal long CommittedAggregateVersion
        {
            get { lock (_gate) return _committedAggregateVersion; }
        }

        internal long TerminalAggregateVersion
        {
            get { lock (_gate) return _terminalAggregateVersion; }
        }

        internal int SafeIdleDecisionCount => Volatile.Read(ref _safeIdleDecided);

        internal string SafeIdleReason
        {
            get { lock (_gate) return _safeIdleReason ?? string.Empty; }
        }

        // An infrastructure outage is recoverable only at the incident level.
        // It must never manufacture a SystemFault, batch recycle, or process
        // restart request as a side effect of this transaction.
        internal bool SystemFault => false;
        internal bool BatchRestart => false;
        internal bool ProcessRestart => false;

        internal IReadOnlyList<OffReceipt> OffReceipts
        {
            get
            {
                lock (_gate)
                {
                    return _channels
                        .Where(channel => _offReceipts.ContainsKey(channel))
                        .Select(channel => ToReceipt(_offReceipts[channel]))
                        .ToArray();
                }
            }
        }

        internal IReadOnlyList<int> MissingAdmissionChannels
        {
            get
            {
                lock (_gate)
                    return _channels
                        .Where(channel => !_offReceipts.TryGetValue(channel, out var receipt) ||
                                          !receipt.Accepted)
                        .ToArray();
            }
        }

        internal IReadOnlyList<int> MissingPhysicalChannels
        {
            get
            {
                lock (_gate)
                    return _channels
                        .Where(channel => !_offReceipts.TryGetValue(channel, out var receipt) ||
                                          !receipt.Accepted ||
                                          !receipt.PhysicalCompletion)
                        .ToArray();
            }
        }

        internal IReadOnlyList<int> MissingTerminalChannels
        {
            get
            {
                lock (_gate)
                    return _channels.Where(channel => !_terminalChannels.Contains(channel)).ToArray();
            }
        }

        /// <summary>
        /// Submit each route exactly once for the current incident.  The phase
        /// remains StaleDetected until every route has returned accepted.  A
        /// blocked last route therefore cannot be mistaken for DoOffSubmitted.
        /// </summary>
        internal bool SubmitOffBatch()
        {
            lock (_gate)
            {
                if (_outcome != Outcome.Active)
                    return false;
                if (_phase != DaqRecoveryPhase.StaleDetected)
                    return _phase >= DaqRecoveryPhase.DoOffSubmitted;
            }

            // A second caller must observe the same in-flight incident rather
            // than issuing a duplicate high-priority command batch.
            if (Interlocked.CompareExchange(ref _submissionInProgress, 1, 0) != 0)
                return false;

            try
            {
                foreach (var channel in _channels)
                {
                    lock (_gate)
                    {
                        if (_outcome != Outcome.Active) return false;
                        if (_offReceipts.TryGetValue(channel, out var existing) && existing.Accepted)
                            continue;
                    }

                    OffReceipt receipt;
                    try
                    {
                        receipt = _port.TrySubmitOff(channel);
                    }
                    catch (Exception ex)
                    {
                        receipt = new OffReceipt(
                            channel,
                            Guid.Empty,
                            false,
                            false,
                            ex.GetType().Name + ":" + ex.Message);
                    }
                    if (receipt == null)
                        receipt = new OffReceipt(channel, Guid.Empty, false, false, "NullAdmissionReceipt");
                    if (receipt.Channel != channel)
                        receipt = new OffReceipt(
                            channel,
                            receipt.CommandId,
                            receipt.Accepted,
                            receipt.PhysicalCompletion,
                            "ChannelReceiptMismatch");
                    if (receipt.Accepted && receipt.CommandId == Guid.Empty)
                        receipt = new OffReceipt(
                            channel,
                            Guid.NewGuid(),
                            true,
                            receipt.PhysicalCompletion,
                            receipt.Failure);

                    var failed = false;
                    lock (_gate)
                    {
                        _offReceipts[channel] = new MutableReceipt(receipt);
                        if (receipt.Accepted &&
                            _earlyPhysicalCompletions.Remove(CompletionKey(channel, receipt.CommandId)))
                            _offReceipts[channel].PhysicalCompletion = true;
                        failed = !receipt.Accepted;
                    }
                    if (failed)
                    {
                        RequestSafeIdle("DaqOffAdmissionRejected:" + receipt.Failure);
                        return false;
                    }
                }

                lock (_gate)
                {
                    if (_outcome != Outcome.Active ||
                        _channels.Any(channel => !_offReceipts.TryGetValue(channel, out var receipt) ||
                                                 !receipt.Accepted))
                        return false;
                    if (_phase == DaqRecoveryPhase.StaleDetected)
                        _phase = DaqRecoveryPhase.DoOffSubmitted;
                    else if (_phase != DaqRecoveryPhase.DoOffSubmitted)
                        return false;
                    _progressVersion++;
                }
                if (!PublishPhase(DaqRecoveryPhase.DoOffSubmitted)) return false;
                TryPublishPhysicalConfirmedIfReady();
                return true;
            }
            finally
            {
                Volatile.Write(ref _submissionInProgress, 0);
            }
        }

        /// <summary>
        /// Record one real DO completion callback.  A completion for an old
        /// command or a different incident is ignored; it cannot advance the
        /// current incident.
        /// </summary>
        internal bool ReportPhysicalCompletion(int channel, Guid commandId, bool completed)
        {
            if (channel < 1 || commandId == Guid.Empty) return false;
            lock (_gate)
            {
                // A completion arriving after SafeIdle/Terminal can only be
                // late evidence for the old command; it must not mutate the
                // incident's receipt set or grow an unbounded late-callback
                // cache.
                if (_outcome == Outcome.SafeIdle || _outcome == Outcome.Terminal)
                    return false;
            }
            if (!completed)
            {
                RequestSafeIdle($"DaqOffPhysicalFailure:EPB{channel}");
                return false;
            }
            lock (_gate)
            {
                if (!_offReceipts.TryGetValue(channel, out var receipt) ||
                    receipt.CommandId != commandId || !receipt.Accepted)
                {
                    _earlyPhysicalCompletions.Add(CompletionKey(channel, commandId));
                    return false;
                }
                receipt.PhysicalCompletion = true;
            }
            // The caller needs to distinguish “this command was accepted as
            // the current receipt” from “the whole group is now confirmed”.
            // The latter is exposed by Phase; returning false for an otherwise
            // valid partial completion made a normal first completion look
            // like a rejected callback to production observers.
            TryPublishPhysicalConfirmedIfReady();
            return true;
        }

        /// <summary>
        /// Production reconciliation path.  It is used when the global DO
        /// completion event was already consumed before the transaction was
        /// attached, and only accepts a positive physical state predicate.
        /// </summary>
        internal bool ConfirmPhysicalFromController(Func<int, bool> physicallyOff)
        {
            if (physicallyOff == null) return false;
            int[] candidates;
            lock (_gate)
            {
                candidates = _offReceipts.Values
                    .Where(receipt => receipt.Accepted && !receipt.PhysicalCompletion)
                    .Select(receipt => receipt.Channel)
                    .ToArray();
            }
            foreach (var channel in candidates)
            {
                bool complete;
                try { complete = physicallyOff(channel); }
                catch { complete = false; }
                if (!complete) continue;
                Guid commandId;
                lock (_gate)
                {
                    if (!_offReceipts.TryGetValue(channel, out var receipt)) continue;
                    commandId = receipt.CommandId;
                }
                ReportPhysicalCompletion(channel, commandId, true);
            }
            return Phase >= DaqRecoveryPhase.DoOffConfirmed;
        }

        /// <summary>
        /// Retry only channels without an accepted or physical receipt.  A
        /// successful retry may close the same incident; it never creates a
        /// second OFF decision.
        /// </summary>
        internal bool RetryMissingOff()
        {
            int[] missing;
            lock (_gate)
            {
                // SafeIdle and Terminal are incident outcomes, not temporary
                // admission states.  Once either has been published, a late
                // maintenance tick must not manufacture another DO receipt
                // or re-energize the same incident's safety path.
                if (_outcome == Outcome.SafeIdle ||
                    _outcome == Outcome.Terminal ||
                    _phase == DaqRecoveryPhase.SafeIdle ||
                    _phase == DaqRecoveryPhase.Terminal)
                    return false;
                missing = _channels
                    .Where(channel => !_offReceipts.TryGetValue(channel, out var receipt) ||
                                      !receipt.Accepted ||
                                      !receipt.PhysicalCompletion)
                    .ToArray();
            }
            if (missing.Length == 0) return true;

            foreach (var channel in missing)
            {
                OffReceipt receipt;
                try { receipt = _port.TrySubmitOff(channel); }
                catch (Exception ex)
                {
                    receipt = new OffReceipt(channel, Guid.Empty, false, false, ex.Message);
                }
                if (receipt == null || !receipt.Accepted)
                {
                    RequestSafeIdle("DaqOffRetryRejected");
                    return false;
                }
                if (receipt.Channel != channel)
                    receipt = new OffReceipt(channel, receipt.CommandId, false, false, "ChannelReceiptMismatch");
                if (receipt.CommandId == Guid.Empty)
                    receipt = new OffReceipt(channel, Guid.NewGuid(), true, receipt.PhysicalCompletion, receipt.Failure);
                lock (_gate) _offReceipts[channel] = new MutableReceipt(receipt);
            }
            lock (_gate)
            {
                if (_outcome == Outcome.Active &&
                    _phase == DaqRecoveryPhase.StaleDetected)
                {
                    _phase = DaqRecoveryPhase.DoOffSubmitted;
                    _progressVersion++;
                }
            }
            PublishPhase(DaqRecoveryPhase.DoOffSubmitted);
            TryPublishPhysicalConfirmedIfReady();
            return true;
        }

        /// <summary>
        /// Advance the non-electrical stages with the same strict ordering as
        /// DaqRecoveryPhaseGate.  The transaction does not perform hardware
        /// work for these stages; it only prevents a caller from publishing a
        /// stage before the preceding evidence exists.
        /// </summary>
        internal bool TryAdvance(DaqRecoveryPhase next)
        {
            lock (_gate)
            {
                if (_outcome == Outcome.Terminal || _outcome == Outcome.SafeIdle) return false;
                if ((int)next != (int)_phase + 1) return false;
                if (next == DaqRecoveryPhase.DoOffSubmitted ||
                    next == DaqRecoveryPhase.DoOffConfirmed)
                    return false;
                _phase = next;
                _progressVersion++;
            }
            return PublishPhase(next);
        }

        /// <summary>
        /// Synchronize stages that are already committed by the controller's
        /// existing DAQ gate.  No callback is emitted and the version remains
        /// monotonic; this is the narrow compatibility seam while the rest of
        /// the long-running pipeline migrates to TryAdvance.
        /// </summary>
        internal bool SynchronizeControllerPhase(DaqRecoveryPhase phase)
        {
            lock (_gate)
            {
                if (_outcome == Outcome.Terminal) return false;
                if ((int)phase < (int)_phase) return false;
                if (phase == _phase) return true;
                _phase = phase;
                _progressVersion++;
                if (phase == DaqRecoveryPhase.Committed)
                {
                    _outcome = Outcome.Committed;
                    _committedVersion = _progressVersion;
                }
                return true;
            }
        }

        /// <summary>
        /// Enter the incident-level SafeIdle branch exactly once.  The safety
        /// OFF and power-disable actions are each single-shot; a hundred
        /// concurrent failure reports cannot multiply either action.
        /// </summary>
        internal bool RequestSafeIdle(string reason)
        {
            var first = false;
            lock (_gate)
            {
                if (_outcome == Outcome.Terminal)
                    return false;
                if (Interlocked.CompareExchange(ref _safeIdleDecided, 1, 0) == 0)
                {
                    first = true;
                    _safeIdleReason = reason ?? "DaqRecoverySafeIdle";
                    _outcome = Outcome.SafeIdle;
                    _phase = DaqRecoveryPhase.SafeIdle;
                    _progressVersion++;
                }
            }
            if (!first) return false;

            var missing = MissingPhysicalChannels;
            if (Interlocked.CompareExchange(ref _offSafetyActionStarted, 1, 0) == 0)
            {
                try { _port.SubmitSafetyOff?.Invoke(missing); }
                catch { }
            }
            if (Interlocked.CompareExchange(ref _powerSafetyActionStarted, 1, 0) == 0)
            {
                try { _port.DisablePower?.Invoke(_channels); }
                catch { }
            }
            PublishPhase(DaqRecoveryPhase.SafeIdle);
            return true;
        }

        /// <summary>
        /// Publish a stable checkpoint for Committed.  A failed publisher
        /// leaves the incident in Committed and intentionally prevents
        /// Terminal; callers can retry this method without recreating the
        /// recovery owner.
        /// </summary>
        internal bool PublishCommittedCheckpoint()
        {
            long version;
            lock (_gate)
            {
                if (_outcome == Outcome.SafeIdle || _outcome == Outcome.Terminal) return false;
                // Terminal phase publication may have succeeded while the
                // later terminal aggregate receipt was temporarily
                // unavailable.  Retrying the Committed receipt must never
                // move the transaction phase backwards from Terminal.
                if (_phase == DaqRecoveryPhase.Terminal &&
                    Volatile.Read(ref _terminalPhasePublished) != 0)
                {
                    if (_committedAggregateVersion > 0 &&
                        _stableCheckpointVersion >= _committedAggregateVersion)
                        return true;
                    version = _committedVersion;
                }
                else
                {
                if (_phase != DaqRecoveryPhase.Committed &&
                    (int)_phase < (int)DaqRecoveryPhase.Rejoining)
                    return false;
                if (_phase != DaqRecoveryPhase.Committed)
                {
                    _phase = DaqRecoveryPhase.Committed;
                    _outcome = Outcome.Committed;
                    _progressVersion++;
                    _committedVersion = _progressVersion;
                }
                else if (_committedVersion == 0)
                {
                    _outcome = Outcome.Committed;
                    _committedVersion = _progressVersion;
                }
                version = _committedVersion;
                }
            }

            CheckpointReceipt receipt;
            try
            {
                receipt = _port.PublishCheckpoint == null
                    ? new CheckpointReceipt(version, true, "InMemoryProductionCheckpoint")
                    : _port.PublishCheckpoint(version);
            }
            catch
            {
                return false;
            }
            if (receipt == null || !receipt.Stable || receipt.Version < version)
                return false;
            lock (_gate)
            {
                _stableCheckpointVersion = Math.Max(_stableCheckpointVersion, receipt.Version);
                _committedAggregateVersion = Math.Max(
                    _committedAggregateVersion,
                    receipt.Version);
            }
            return true;
        }

        /// <summary>
        /// Terminal is allowed only after all affected channels have an
        /// authoritative terminal receipt and, on the normal path, a stable
        /// checkpoint.  Partial publication retains all transaction state so
        /// RetryTerminal only asks for the missing routes.
        /// </summary>
        internal bool TryTerminal()
        {
            // A failed terminal callback must be retryable by the same
            // incident.  Serialize the callback sequence so concurrent
            // maintenance ticks cannot issue duplicate terminal or aggregate
            // publications.
            if (Interlocked.CompareExchange(ref _terminalAttemptInProgress, 1, 0) != 0)
                return false;

            try
            {
                int[] missing;
                bool checkpointRequired;
                bool terminalCheckpointRequired;
                bool terminalPhaseAlreadyPublished;
                long committedAggregateVersion;
                lock (_gate)
                {
                    if (_outcome == Outcome.Terminal) return true;
                    checkpointRequired = _outcome == Outcome.Committed;
                    if (!checkpointRequired && _outcome != Outcome.SafeIdle)
                        return false;
                    // SafeIdle/cancelled incidents have no Committed
                    // checkpoint, but they still need an independent stable
                    // terminal aggregate receipt before the incident can be
                    // considered closed.  A zero baseline is valid for this
                    // branch; the receipt itself must still be strictly
                    // positive and stable.
                    terminalCheckpointRequired = checkpointRequired ||
                                                  _outcome == Outcome.SafeIdle;
                    if (checkpointRequired &&
                        (_committedAggregateVersion <= 0 ||
                         _stableCheckpointVersion < _committedAggregateVersion))
                        return false;
                    missing = _channels
                        .Where(channel => !_terminalChannels.Contains(channel))
                        .ToArray();
                    terminalPhaseAlreadyPublished =
                        Volatile.Read(ref _terminalPhasePublished) != 0;
                    committedAggregateVersion = _committedAggregateVersion;
                }

                // First establish the authoritative state for every missing
                // route.  The production callback verifies the state-store
                // identity (or publishes the safe terminal state); a caller
                // cannot claim Terminal merely because a task returned.
                if (missing.Length > 0)
                {
                    IReadOnlyCollection<int> published;
                    try
                    {
                        published = _port.PublishTerminal == null
                            ? missing
                            : (_port.PublishTerminal(missing) ?? Array.Empty<int>());
                    }
                    catch
                    {
                        return false;
                    }

                    lock (_gate)
                    {
                        foreach (var channel in published ?? Array.Empty<int>())
                            if (_channels.Contains(channel) && missing.Contains(channel))
                                _terminalChannels.Add(channel);
                        if (_terminalChannels.Count != _channels.Length)
                            return false;
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        if (_terminalChannels.Count != _channels.Length)
                            return false;
                    }
                }

                // The Controller phase/context publication is deliberately
                // before the terminal aggregate receipt.  The store callback
                // therefore observes Stage=Terminal; a receipt from a
                // pre-terminal snapshot is not accepted as terminal evidence.
                if (checkpointRequired && !terminalPhaseAlreadyPublished)
                {
                    if (!PublishPhase(DaqRecoveryPhase.Terminal))
                        return false;
                    lock (_gate)
                    {
                        _phase = DaqRecoveryPhase.Terminal;
                        Volatile.Write(ref _terminalPhasePublished, 1);
                    }
                }
                else if (!checkpointRequired && !terminalPhaseAlreadyPublished)
                {
                    // SafeIdle has no normal Committed checkpoint barrier,
                    // but it still gets a real Terminal phase publication.
                    if (!PublishPhase(DaqRecoveryPhase.Terminal))
                        return false;
                    lock (_gate)
                    {
                        _phase = DaqRecoveryPhase.Terminal;
                        Volatile.Write(ref _terminalPhasePublished, 1);
                    }
                }

                CheckpointReceipt terminalCheckpoint = null;
                if (terminalCheckpointRequired)
                {
                    try
                    {
                        terminalCheckpoint = _port.PublishTerminalCheckpoint == null
                            ? null
                            : _port.PublishTerminalCheckpoint(committedAggregateVersion);
                    }
                    catch
                    {
                        return false;
                    }
                    if (terminalCheckpoint == null ||
                        !terminalCheckpoint.Stable ||
                        terminalCheckpoint.Version <= committedAggregateVersion)
                        return false;
                }

                lock (_gate)
                {
                    if (_terminalChannels.Count != _channels.Length ||
                        (terminalCheckpointRequired &&
                         Volatile.Read(ref _terminalPhasePublished) == 0))
                        return false;
                    if (terminalCheckpointRequired)
                    {
                        _terminalAggregateVersion = Math.Max(
                            _terminalAggregateVersion,
                            terminalCheckpoint.Version);
                    }
                    _outcome = Outcome.Terminal;
                    _phase = DaqRecoveryPhase.Terminal;
                    // Keep the progress clock in its own unit.  In
                    // particular, a large aggregate-store revision must not
                    // make a phase progress token jump or appear to regress.
                    _progressVersion = Math.Max(
                        _progressVersion + 1,
                        _committedVersion + 1);
                    _terminalVersion = _progressVersion;
                }
                return true;
            }
            finally
            {
                Volatile.Write(ref _terminalAttemptInProgress, 0);
            }
        }

        private bool TryPublishPhysicalConfirmedIfReady()
        {
            lock (_gate)
            {
                if (_outcome != Outcome.Active || _phase != DaqRecoveryPhase.DoOffSubmitted)
                    return _phase >= DaqRecoveryPhase.DoOffConfirmed;
                if (_channels.Any(channel => !_offReceipts.TryGetValue(channel, out var receipt) ||
                                             !receipt.Accepted || !receipt.PhysicalCompletion))
                    return false;
                _phase = DaqRecoveryPhase.DoOffConfirmed;
                _progressVersion++;
            }
            return PublishPhase(DaqRecoveryPhase.DoOffConfirmed);
        }

        private bool PublishPhase(DaqRecoveryPhase phase)
        {
            if (_port.PublishPhase == null) return true;
            try
            {
                _port.PublishPhase(phase);
                return true;
            }
            catch
            {
                if (phase != DaqRecoveryPhase.SafeIdle && phase != DaqRecoveryPhase.Terminal)
                    RequestSafeIdle("DaqRecoveryPhasePublicationFailed:" + phase);
                return false;
            }
        }

        private static MutableReceipt CloneReceipt(OffReceipt receipt)
        {
            return new MutableReceipt(receipt);
        }

        private static OffReceipt ToReceipt(MutableReceipt receipt)
        {
            return new OffReceipt(
                receipt.Channel,
                receipt.CommandId,
                receipt.Accepted,
                receipt.PhysicalCompletion,
                receipt.Failure);
        }

        private static string CompletionKey(int channel, Guid commandId)
            => channel.ToString() + ":" + commandId.ToString("N");
    }
}
