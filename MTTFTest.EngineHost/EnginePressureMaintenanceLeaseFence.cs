using System;
using System.Diagnostics;
using System.IO;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    // Memory-only executor fence. Receiving a lease NEVER authorizes hardware:
    // a matching Prepare command after independent safety is still required.
    internal sealed class EnginePressureMaintenanceLeaseFence
    {
        private readonly object _gate = new object();
        private readonly string _sessionId, _runId, _engineId;
        private readonly long _epoch;
        private readonly Func<long> _utcNow, _monotonicTicks;
        private PressureMaintenanceLease _lease;
        private long _monotonicDeadline, _lastUtc;
        private bool _closed, _bound;

        internal EnginePressureMaintenanceLeaseFence(string sessionId, string runId, long epoch, string engineId,
            Func<long> utcNow = null, Func<long> monotonicTicks = null)
        {
            _sessionId = sessionId; _runId = runId; _epoch = epoch; _engineId = engineId;
            _utcNow = utcNow ?? (() => DateTime.UtcNow.Ticks);
            _monotonicTicks = monotonicTicks ?? (() => (long)(Stopwatch.GetTimestamp() * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        }

        internal PressureMaintenanceLease Receive(PressureMaintenanceLease lease)
        {
            if (lease?.IsStructurallyValid() != true || lease.SessionId != _sessionId || lease.RunId != _runId ||
                lease.RunEpoch != _epoch || lease.EngineInstanceId != _engineId) throw new InvalidDataException("MaintenanceLeaseEngineIdentityMismatch");
            lock (_gate)
            {
                var now = _utcNow(); var monotonic = _monotonicTicks();
                CheckLifetime(now, monotonic);
                if (!lease.Revoked && (!lease.IsLive(now) || lease.LastHeartbeatUtcTicks > now))
                    throw new InvalidDataException("MaintenanceLeaseNotLive");
                if (_lease != null && _lease.SameSession(lease))
                {
                    if (lease.Revision < _lease.Revision || lease.Revision == _lease.Revision && lease.ComputeSha256() != _lease.ComputeSha256())
                        throw new InvalidDataException("MaintenanceLeaseRevisionConflict");
                    if (_closed && !lease.Revoked) throw new InvalidDataException("MaintenanceLeaseLocallyRetired");
                    if (lease.Revision == _lease.Revision) return _lease.Clone(); // No duplicate deadline extension.
                    if (!lease.Revoked && (lease.LastHeartbeatSequence <= _lease.LastHeartbeatSequence ||
                        lease.LastHeartbeatUtcTicks < _lease.LastHeartbeatUtcTicks || lease.ExpiresUtcTicks < _lease.ExpiresUtcTicks))
                        throw new InvalidDataException("MaintenanceLeaseRenewalNotMonotonic");
                }
                else if (_lease != null)
                {
                    // Retained tombstone rejects delayed owners without an unbounded cache.
                    // A different owner cannot displace an executor not yet safely retired.
                    if (_bound || !_closed || lease.CreatedUtcTicks <= _lease.CreatedUtcTicks)
                        throw new InvalidDataException("MaintenanceLeaseOwnerConflict");
                }
                _lease = lease.Clone(); _closed = lease.Revoked; _lastUtc = now;
                _monotonicDeadline = checked(monotonic + Math.Max(0, lease.ExpiresUtcTicks - now));
                return _lease.Clone();
            }
        }

        internal PressureMaintenanceLease RequireForCommand(RecoveryCommand command)
        {
            lock (_gate)
            {
                CheckLifetime(_utcNow(), _monotonicTicks());
                if (command?.IsStructurallyValid() != true || !PressureMaintenanceProtocol.IsExecution(command.Kind) ||
                    command.PressureMaintenance == null || _lease == null || _closed ||
                    !_lease.SameSession(command.PressureMaintenance) || !_lease.Binds(command.Identity, command.OwnerId) ||
                    _lease.Revision < command.PressureMaintenance.Revision || command.DeadlineUtcTicks <= _utcNow())
                    throw new InvalidOperationException("MaintenanceCommandLeaseNotAuthorized");
                if (command.Kind != RecoveryCommandKind.PreparePressureMaintenance && !_bound)
                    throw new InvalidOperationException("MaintenanceExecutorNotPrepared");
                if (command.Kind == RecoveryCommandKind.PreparePressureMaintenance) _bound = true;
                return _lease.Clone();
            }
        }

        // Hardware execution must check this before/after every energizing call.
        // It does no hardware I/O while holding the renewal lock.
        internal bool IsAuthorized(string incidentId, string ownerId)
        {
            lock (_gate)
            {
                CheckLifetime(_utcNow(), _monotonicTicks());
                return _bound && !_closed && _lease?.IncidentId == incidentId && _lease.OwnerId == ownerId;
            }
        }

        internal void RetireAfterSafetyHandoff()
        {
            lock (_gate) { _closed = true; _bound = false; }
        }

        internal PressureMaintenanceLease CaptureAuthorized(string incidentId, string ownerId)
        {
            lock (_gate)
            {
                CheckLifetime(_utcNow(), _monotonicTicks());
                return _bound && !_closed && _lease?.IncidentId == incidentId && _lease.OwnerId == ownerId ? _lease.Clone() : null;
            }
        }

        internal void Invalidate()
        {
            lock (_gate) _closed = true; // Keep bound until actual hardware handoff completes.
        }

        internal void CompleteSafetyHandoff(RecoveryCommand command)
        {
            lock (_gate)
            {
                // The first OFF of a new maintenance intent precedes preparation.
                // It must retire a previous owner, but not its own new live lease.
                if (command.PressureMaintenance != null && !command.PressureMaintenance.Revoked &&
                    _lease?.SameSession(command.PressureMaintenance) == true && !_bound) return;
                _closed = true; _bound = false;
            }
        }

        private void CheckLifetime(long now, long monotonic)
        {
            if (_lease != null && (now < _lastUtc || !_lease.IsLive(now) || monotonic >= _monotonicDeadline)) _closed = true;
            _lastUtc = Math.Max(_lastUtc, now);
        }
    }
}
