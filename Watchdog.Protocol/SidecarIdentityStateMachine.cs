using System;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Deterministic state machine for the Sidecar identity lifecycle.
    ///
    /// Pending means a helper has been reserved/launched but has not yet
    /// completed an Attached handshake.  Authority is created only by a
    /// matching, validated Attached identity.  The runtime supplies process
    /// liveness checks; this class owns the identity and generation invariants
    /// so they can also be exercised without starting a real process.
    /// </summary>
    public sealed class SidecarIdentityStateMachine
    {
        public sealed class PendingSnapshot
        {
            public int ProcessId { get; }
            public long ProcessStartUtcTicks { get; }
            public string SessionId { get; }
            public long SessionGeneration { get; }
            public string InstanceNonce { get; }

            public PendingSnapshot(
                int processId,
                long processStartUtcTicks,
                string sessionId,
                long sessionGeneration,
                string instanceNonce)
            {
                ProcessId = processId;
                ProcessStartUtcTicks = processStartUtcTicks;
                SessionId = sessionId ?? string.Empty;
                SessionGeneration = sessionGeneration;
                InstanceNonce = instanceNonce ?? string.Empty;
            }
        }

        public sealed class AuthoritySnapshot
        {
            public int ProcessId { get; }
            public long ProcessStartUtcTicks { get; }
            public string SessionId { get; }
            public string InstanceNonce { get; }

            public AuthoritySnapshot(
                int processId,
                long processStartUtcTicks,
                string sessionId,
                string instanceNonce)
            {
                ProcessId = processId;
                ProcessStartUtcTicks = processStartUtcTicks;
                SessionId = sessionId ?? string.Empty;
                InstanceNonce = instanceNonce ?? string.Empty;
            }
        }

        private readonly object _gate = new object();
        private PendingSnapshot _pending;
        private AuthoritySnapshot _authority;

        public bool HasPending
        {
            get { lock (_gate) return _pending != null; }
        }

        public bool HasAuthority
        {
            get { lock (_gate) return _authority != null; }
        }

        public PendingSnapshot Pending
        {
            get { lock (_gate) return _pending; }
        }

        public AuthoritySnapshot Authority
        {
            get { lock (_gate) return _authority; }
        }

        /// <summary>
        /// Reserves exactly one pending helper. Repeating the same reservation
        /// is idempotent; a different helper is rejected while one is alive
        /// or awaiting handshake.
        /// </summary>
        public bool TryReservePending(
            int processId,
            long processStartUtcTicks,
            string sessionId,
            long sessionGeneration,
            string instanceNonce,
            out PendingSnapshot reservation,
            out string rejection)
        {
            reservation = null;
            rejection = string.Empty;
            if (!IsValidPending(
                    processId,
                    processStartUtcTicks,
                    sessionId,
                    sessionGeneration,
                    instanceNonce))
            {
                rejection = "pending helper identity incomplete";
                return false;
            }

            lock (_gate)
            {
                if (_authority != null)
                {
                    rejection = "validated authority already exists";
                    return false;
                }
                if (_pending != null)
                {
                    if (PendingMatches(
                            _pending,
                            processId,
                            processStartUtcTicks,
                            sessionId,
                            sessionGeneration,
                            instanceNonce))
                    {
                        reservation = _pending;
                        return true;
                    }
                    rejection = "another pending helper is already reserved";
                    return false;
                }

                _pending = new PendingSnapshot(
                    processId,
                    processStartUtcTicks,
                    sessionId,
                    sessionGeneration,
                    instanceNonce);
                reservation = _pending;
                return true;
            }
        }

        public bool CanAcceptAttached(
            AuthoritySnapshot candidate,
            string expectedSessionId,
            long sessionGeneration,
            out string rejection)
        {
            rejection = string.Empty;
            if (!IsValidAuthority(candidate) ||
                string.IsNullOrWhiteSpace(expectedSessionId) ||
                sessionGeneration <= 0 ||
                !string.Equals(candidate.SessionId, expectedSessionId, StringComparison.Ordinal))
            {
                rejection = "Attached authority identity is incomplete or session-mismatched";
                return false;
            }

            lock (_gate)
            {
                if (_pending != null)
                {
                    if (!PendingMatches(
                            _pending,
                            candidate.ProcessId,
                            candidate.ProcessStartUtcTicks,
                            expectedSessionId,
                            sessionGeneration,
                            candidate.InstanceNonce))
                    {
                        rejection = "Attached identity does not match pending helper";
                        return false;
                    }
                    return true;
                }
                if (_authority != null)
                {
                    if (!AuthorityMatches(_authority, candidate))
                    {
                        rejection = "Attached identity does not match validated authority";
                        return false;
                    }
                    return true;
                }
                rejection = "Attached has no pending helper or validated authority";
                return false;
            }
        }

        /// <summary>
        /// Seeds an authority that was already validated by the recovery
        /// command-line handoff. This is not a handshake shortcut: the
        /// caller must have checked the live PID/start identity first, and a
        /// subsequent Attached still has to match this exact snapshot.
        /// </summary>
        public bool TrySeedValidatedAuthority(
            AuthoritySnapshot candidate,
            string expectedSessionId,
            out string rejection)
        {
            rejection = string.Empty;
            if (!IsValidAuthority(candidate) ||
                string.IsNullOrWhiteSpace(expectedSessionId) ||
                !string.Equals(candidate.SessionId, expectedSessionId, StringComparison.Ordinal))
            {
                rejection = "validated authority seed is incomplete or session-mismatched";
                return false;
            }
            lock (_gate)
            {
                if (_pending != null)
                {
                    rejection = "cannot seed authority while a pending helper exists";
                    return false;
                }
                if (_authority != null && !AuthorityMatches(_authority, candidate))
                {
                    rejection = "a different validated authority already exists";
                    return false;
                }
                _authority = candidate;
                return true;
            }
        }

        /// <summary>
        /// Promotes a previously accepted Attached identity. The pending
        /// reservation is cleared in the same state transition.
        /// </summary>
        public bool TryPromoteAttached(
            AuthoritySnapshot candidate,
            string expectedSessionId,
            long sessionGeneration,
            out string rejection)
        {
            if (!CanAcceptAttached(candidate, expectedSessionId, sessionGeneration, out rejection))
                return false;
            lock (_gate)
            {
                // Re-check under the state-machine lock because another
                // caller may have reset or promoted between validation and
                // promotion.
                if (_pending != null)
                {
                    if (!PendingMatches(
                            _pending,
                            candidate.ProcessId,
                            candidate.ProcessStartUtcTicks,
                            expectedSessionId,
                            sessionGeneration,
                            candidate.InstanceNonce))
                    {
                        rejection = "pending helper changed before Attached promotion";
                        return false;
                    }
                    _authority = candidate;
                    _pending = null;
                    return true;
                }
                if (_authority != null && AuthorityMatches(_authority, candidate))
                    return true;
                rejection = "validated authority changed before Attached promotion";
                return false;
            }
        }

        public bool TryClearAuthority(AuthoritySnapshot expected)
        {
            if (!IsValidAuthority(expected)) return false;
            lock (_gate)
            {
                if (_authority == null || !AuthorityMatches(_authority, expected))
                    return false;
                _authority = null;
                return true;
            }
        }

        public bool TryClearPending(PendingSnapshot expected)
        {
            if (expected == null) return false;
            lock (_gate)
            {
                if (_pending == null ||
                    !PendingMatches(
                        _pending,
                        expected.ProcessId,
                        expected.ProcessStartUtcTicks,
                        expected.SessionId,
                        expected.SessionGeneration,
                        expected.InstanceNonce))
                    return false;
                _pending = null;
                return true;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _pending = null;
                _authority = null;
            }
        }

        private static bool IsValidPending(
            int processId,
            long processStartUtcTicks,
            string sessionId,
            long sessionGeneration,
            string instanceNonce)
        {
            return processId > 0 &&
                   processStartUtcTicks > 0 &&
                   !string.IsNullOrWhiteSpace(sessionId) &&
                   sessionGeneration > 0 &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(instanceNonce);
        }

        private static bool IsValidAuthority(AuthoritySnapshot identity)
        {
            return identity != null &&
                   identity.ProcessId > 0 &&
                   identity.ProcessStartUtcTicks > 0 &&
                   !string.IsNullOrWhiteSpace(identity.SessionId) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(identity.InstanceNonce);
        }

        private static bool PendingMatches(
            PendingSnapshot pending,
            int processId,
            long processStartUtcTicks,
            string sessionId,
            long sessionGeneration,
            string instanceNonce)
        {
            return pending != null &&
                   pending.ProcessId == processId &&
                   pending.ProcessStartUtcTicks == processStartUtcTicks &&
                   pending.SessionGeneration == sessionGeneration &&
                   string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal) &&
                   string.Equals(
                       pending.InstanceNonce,
                       instanceNonce,
                       StringComparison.Ordinal);
        }

        private static bool AuthorityMatches(
            AuthoritySnapshot left,
            AuthoritySnapshot right)
        {
            return left != null && right != null &&
                   left.ProcessId == right.ProcessId &&
                   left.ProcessStartUtcTicks == right.ProcessStartUtcTicks &&
                   string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
                   string.Equals(left.InstanceNonce, right.InstanceNonce, StringComparison.Ordinal);
        }
    }
}
