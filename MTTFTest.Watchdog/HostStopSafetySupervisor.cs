using System;
using System.Collections.Generic;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Sidecar-side stop safety decision gate.  Heartbeats only update the
    /// immutable projection; the monitor is the sole caller that can dispatch
    /// a takeover.  A transaction is dispatched once per sidecar session and
    /// is not retried merely because the Controller stopped sending updates.
    /// A new validated Attach is the only reset boundary.
    /// </summary>
    internal sealed class HostStopSafetySupervisor
    {
        private readonly object _gate = new object();
        private readonly string _sessionId;
        private readonly HashSet<string> _dispatched =
            new HashSet<string>(StringComparer.Ordinal);
        private StopSafetyHeartbeatProjection _projection =
            new StopSafetyHeartbeatProjection();
        private string _lastObservedKey = string.Empty;
        private long _validatedAttachGeneration;
        private string _validatedAttachTransactionId = string.Empty;
        private string _lastValidatedAttachKey = string.Empty;
        private int _validatedProcessId;
        private long _validatedProcessStartUtcTicks;
        private long _validatedAttachEpoch;

        internal HostStopSafetySupervisor(string sessionId)
        {
            _sessionId = sessionId ?? string.Empty;
        }

        internal StopSafetyHeartbeatProjection Snapshot
        {
            get
            {
                lock (_gate) return _projection.Clone();
            }
        }

        internal int DispatchCount
        {
            get { lock (_gate) return _dispatched.Count; }
        }

        internal double NoProgressSeconds(DateTime utcNow)
        {
            lock (_gate)
            {
                if (!_projection.Active && !_projection.TakeoverRequired &&
                    !_projection.TimedOut)
                    return 0;
                var ticks = _projection.LastMaterialProgressUtc > 0
                    ? _projection.LastMaterialProgressUtc
                    : _projection.StageStartedUtc;
                if (ticks <= 0) return 0;
                return Math.Max(0, (utcNow.Ticks - ticks) /
                    (double)TimeSpan.TicksPerSecond);
            }
        }

        internal void Observe(StopSafetyHeartbeatProjection projection)
        {
            if (projection == null) return;
            lock (_gate)
            {
                // Generation/version ordering is scoped to the validated
                // process attachment.  A late heartbeat from an older
                // process is rejected before it can reset a newer stop
                // decision, even when its generation is numerically higher.
                if (_validatedProcessId > 0 &&
                    (projection.ProcessId != _validatedProcessId ||
                     projection.ProcessStartUtcTicks != _validatedProcessStartUtcTicks))
                    return;
                if (_validatedAttachEpoch > 0 && projection.AttachEpoch > 0 &&
                    projection.AttachEpoch != _validatedAttachEpoch)
                    return;
                if (_validatedAttachGeneration > 0)
                {
                    if (projection.Generation < _validatedAttachGeneration)
                        return;
                    if (projection.Generation == _validatedAttachGeneration &&
                        !string.IsNullOrWhiteSpace(_validatedAttachTransactionId) &&
                        !string.IsNullOrWhiteSpace(projection.TransactionId) &&
                        !string.Equals(
                            projection.TransactionId,
                            _validatedAttachTransactionId,
                            StringComparison.Ordinal))
                        return;
                }
                var key = BuildKey(projection);
                if (!string.Equals(_lastObservedKey, key, StringComparison.Ordinal) ||
                    IsNewer(projection, _projection))
                {
                    _projection = projection.Clone();
                    _lastObservedKey = key;
                }
            }
        }

        /// <summary>
        /// Called only after the host has validated the Attach identity.  A
        /// stale/invalid Attached message must never invoke this method.
        /// </summary>
        internal void NotifyValidatedAttached(
            string sessionId,
            string transactionId,
            long generation,
            string attachmentIdentity = null,
            int processId = 0,
            long processStartUtcTicks = 0,
            long attachEpoch = 0)
        {
            if (!string.Equals(_sessionId, sessionId ?? string.Empty,
                               StringComparison.Ordinal))
                return;
            lock (_gate)
            {
                // The host's validated identity is monotonic.  A late
                // response from an older recovery process cannot reset a
                // newer session's once-only gate.
                if (attachEpoch > 0 &&
                    _validatedAttachEpoch > 0 &&
                    attachEpoch < _validatedAttachEpoch)
                    return;
                var attachKey = (transactionId ?? string.Empty) + "|" +
                                generation.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture) + "|" +
                                (attachmentIdentity ?? string.Empty) + "|" +
                                processId.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture) + "|" +
                                processStartUtcTicks.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture) + "|" +
                                attachEpoch.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture);
                if (string.Equals(_lastValidatedAttachKey, attachKey,
                                  StringComparison.Ordinal))
                    return;
                _lastValidatedAttachKey = attachKey;
                // A new validated PID/start or attachment epoch is a fresh
                // ordering domain.  It may legitimately restart at a lower
                // generation; only late observations from the old domain are
                // rejected by the identity/epoch checks above.
                var newAttachment =
                    (attachEpoch > 0 && attachEpoch != _validatedAttachEpoch) ||
                    (processId > 0 && processId != _validatedProcessId) ||
                    (processStartUtcTicks > 0 &&
                     processStartUtcTicks != _validatedProcessStartUtcTicks);
                if (newAttachment || _validatedAttachEpoch == 0)
                {
                    _validatedAttachGeneration = Math.Max(0, generation);
                    _validatedAttachTransactionId = transactionId ?? string.Empty;
                    _validatedProcessId = processId;
                    _validatedProcessStartUtcTicks = processStartUtcTicks;
                    _validatedAttachEpoch = attachEpoch > 0
                        ? attachEpoch
                        : _validatedAttachEpoch + 1;
                }
                else if (generation >= _validatedAttachGeneration)
                {
                    _validatedAttachGeneration = generation;
                    _validatedAttachTransactionId = transactionId ?? string.Empty;
                }
                _dispatched.Clear();
                _lastObservedKey = string.Empty;
                _projection = new StopSafetyHeartbeatProjection
                {
                    TransactionId = transactionId ?? string.Empty,
                    Generation = generation,
                    ProcessId = processId,
                    ProcessStartUtcTicks = processStartUtcTicks,
                    AttachEpoch = _validatedAttachEpoch
                };
            }
        }

        /// <summary>
        /// Returns true only for a newly observed transaction requiring an
        /// immediate safety takeover.  Active=false does not suppress a
        /// latched takeover/timeout decision.
        /// </summary>
        internal bool EvaluateAndDispatch(Action<string> dispatch)
        {
            if (dispatch == null) return false;
            string reason;
            lock (_gate)
            {
                if (!_projection.TakeoverRequired && !_projection.TimedOut)
                    return false;
                var key = BuildKey(_projection);
                if (!_dispatched.Add(key)) return false;
                reason = string.IsNullOrWhiteSpace(_projection.TerminalReason)
                    ? (_projection.TimedOut
                        ? "StopSafetyTimedOut"
                        : "StopSafetyTakeoverRequired")
                    : _projection.TerminalReason;
            }
            try
            {
                dispatch(reason);
            }
            catch
            {
                // The decision remains consumed.  The host's global takeover
                // gate and durable journal are the retry/terminal authority;
                // invoking an unbounded second stop request is unsafe.
            }
            return true;
        }

        /// <summary>
        /// Uses the same once-per-transaction gate for a deadline decision
        /// calculated by the host monitor.  This prevents the monitor's
        /// sticky terminal projection and the broader watchdog policy from
        /// dispatching two StopAll requests for one transaction.
        /// </summary>
        internal bool TryDispatchReason(string reason, Action<string> dispatch)
        {
            if (dispatch == null) return false;
            lock (_gate)
            {
                var key = BuildKey(_projection);
                if (!_dispatched.Add(key)) return false;
            }
            try
            {
                dispatch(string.IsNullOrWhiteSpace(reason)
                    ? "StopSafetyPolicyTakeover"
                    : reason);
            }
            catch
            {
                // The once-only decision is intentionally consumed.  The
                // durable host/global takeover gate owns any later retry.
            }
            return true;
        }

        private static string BuildKey(StopSafetyHeartbeatProjection projection)
        {
            var transaction = string.IsNullOrWhiteSpace(projection.TransactionId)
                ? "legacy"
                : projection.TransactionId;
            return transaction + "|" + projection.Generation.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsNewer(
            StopSafetyHeartbeatProjection candidate,
            StopSafetyHeartbeatProjection current)
        {
            if (candidate == null) return false;
            if (current == null) return true;
            if (candidate.Generation != current.Generation)
                return candidate.Generation > current.Generation;
            if (!string.Equals(candidate.TransactionId, current.TransactionId,
                               StringComparison.Ordinal))
                return !string.IsNullOrWhiteSpace(candidate.TransactionId);
            if (candidate.ProgressVersion > current.ProgressVersion)
                return true;
            if (candidate.ProgressVersion < current.ProgressVersion)
                return false;
            // Terminal safety facts are sticky at an equal version: a late
            // normal/active projection must not clear an already observed
            // takeover/timeout, while a same-version positive latch remains
            // admissible for peers that publish the flags after the stage.
            return (candidate.TakeoverRequired && !current.TakeoverRequired) ||
                   (candidate.TimedOut && !current.TimedOut);
        }
    }
}
