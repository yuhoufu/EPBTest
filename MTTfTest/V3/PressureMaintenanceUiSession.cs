using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    // One explicit page operation, never a recovery owner. UI responsiveness is
    // observed on the WinForms thread; a background loop cannot renew a frozen UI.
    internal sealed class PressureMaintenanceUiSession : IDisposable
    {
        private readonly V3MonitorSession _session;
        private readonly object _gate = new object();
        private CancellationTokenSource _renewStop;
        private PressureMaintenanceLease _lease;
        private string _sessionId, _runId, _engineId;
        private long _epoch, _requestedUtc, _uiPulse, _sequence;
        private int _hydraulicId;
        private Task<bool> _endTask;
        private Task _endSubmission;
        private EngineStateSnapshot _requestedEngine;
        private bool _requested, _disposed, _ending;
        private string _failure = string.Empty;
        internal bool Requested { get { lock (_gate) return _requested; } }
        internal bool Ending { get { lock (_gate) return _ending; } }
        internal string Failure { get { lock (_gate) return _failure; } }
        internal PressureMaintenanceLease Lease { get { lock (_gate) return _lease?.Clone(); } }
        internal Task RenewalTask { get; private set; } = Task.CompletedTask;

        internal PressureMaintenanceUiSession(V3MonitorSession session) { _session = session ?? throw new ArgumentNullException(nameof(session)); }
        internal async Task BeginAsync(int hydraulicId)
        {
            if (!_session.CanBeginMaintenance || hydraulicId < 1 || hydraulicId > 2) return;
            lock (_gate)
            {
                if (_disposed || _requested) return;
                var engine = _session.Latest.Engine;
                _requestedEngine = engine;
                _sessionId = engine.SessionId; _runId = engine.RunId; _epoch = engine.RunEpoch; _engineId = engine.EngineInstanceId;
                _hydraulicId = hydraulicId; _requestedUtc = DateTime.UtcNow.Ticks; _uiPulse = Stopwatch.GetTimestamp();
                _requested = true; _ending = false; _failure = string.Empty; _endTask = null; _endSubmission = null; _lease = null; _sequence = 0;
                _renewStop = new CancellationTokenSource();
                var renewalSource = _renewStop;
                RenewalTask = Task.Run(async () =>
                { try { await RenewalLoopAsync(renewalSource.Token).ConfigureAwait(false); } finally { renewalSource.Dispose(); } });
            }
            var response = await _session.SubmitMaintenanceAsync(OperatorCommandKind.BeginPressureMaintenance, Payload(null, 0));
            if (response != null && !response.Accepted)
            {
                lock (_gate) { _failure = response.FailureCode; _requested = false; }
                StopRenewal();
            }
            ObserveUi();
        }

        internal void ObserveUi()
        {
            var latest = _session.Latest;
            if (!_session.IsConnected || latest?.Kernel.IsFresh(DateTime.UtcNow.Ticks) != true) return;
            lock (_gate)
            {
                if (_disposed || !_requested || _ending || !SameRun(latest.Engine) || latest.Engine.EngineInstanceId != _engineId) return;
                var lease = latest.Kernel.PressureMaintenance; var client = _session.MaintenanceClient;
                if (lease?.IsStructurallyValid() != true || lease.CreatedUtcTicks < _requestedUtc || lease.SessionId != _sessionId ||
                    lease.RunId != _runId || lease.RunEpoch != _epoch || lease.EngineInstanceId != _engineId || lease.HydraulicId != _hydraulicId ||
                    lease.UiProcessId != client.UiProcessId || lease.UiProcessStartUtcTicks != client.UiProcessStartUtcTicks ||
                    (_lease != null && !_lease.SameSession(lease))) return;
                if (_lease == null || lease.Revision >= _lease.Revision) _lease = lease.Clone();
                _uiPulse = Stopwatch.GetTimestamp();
            }
        }

        internal bool Ready
        {
            get
            {
                var lease = Lease; var latest = _session.Latest;
                return Requested && !Ending && Failure.Length == 0 && _session.IsConnected && lease?.IsLive(DateTime.UtcNow.Ticks) == true &&
                    latest.Kernel.IsFresh(DateTime.UtcNow.Ticks) && latest.Kernel.PressureMaintenance?.SameSession(lease) == true &&
                    latest.Kernel.PressureMaintenanceStage == RecoveryStage.PressureMaintenanceReady && !latest.Kernel.CommandPending &&
                    !_session.CommandPending && !_session.HasUnresolvedCommands && latest.PressureMaintenance?.Binds(latest.Kernel.PressureMaintenance) == true &&
                    latest.PressureMaintenance.AuthorityValid && latest.PressureMaintenance.Prepared && DisplayFresh(latest.PressureMaintenance);
            }
        }
        internal EngineUiPressureMaintenance Display => Ready ? _session.Latest.PressureMaintenance : null;
        internal bool MayBeEnergized => Requested && (_session.Latest?.Engine.OutputsEnergized == true ||
            _session.Latest?.PressureMaintenance?.MayBeEnergized == true || !Ready);
        internal Task<SupervisorOperatorCommandResponse> OutputAsync(double pressure) => Ready
            ? _session.SubmitMaintenanceAsync(OperatorCommandKind.SetMaintenancePressure, Payload(Lease, pressure)) : Task.FromResult<SupervisorOperatorCommandResponse>(null);
        internal Task<SupervisorOperatorCommandResponse> StopOutputAsync() => Lease == null
            ? Task.FromResult<SupervisorOperatorCommandResponse>(null) : _session.SubmitMaintenanceAsync(OperatorCommandKind.StopMaintenanceOutput, Payload(Lease, 0));
        internal Task<bool> EndAsync()
        {
            lock (_gate)
            {
                if (!_requested) return Task.FromResult(_session.CanClose);
                if (_endTask != null)
                {
                    if (_session.CanClose && SameRun(_session.Latest?.Engine))
                    {
                        _requested = false; _ending = false;
                        return Task.FromResult(true);
                    }
                    return _endTask;
                }
                _ending = true; StopRenewal();
                return _endTask = EndCoreAsync();
            }
        }
        private async Task<bool> EndCoreAsync()
        {
            var lease = Lease;
            if (SameRun(_session.Latest?.Engine))
            {
                if (lease != null) await RequestKnownLeaseEndAsync();
                else await _session.SubmitAsync(OperatorCommandKind.Stop); // The original Begin may still be awaiting its receipt.
            }
            var started = Stopwatch.GetTimestamp();
            while (!_disposed && (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency < 45)
            {
                await _session.RefreshAsync();
                if (_session.CanClose && SameRun(_session.Latest.Engine))
                {
                    lock (_gate) { _requested = false; _ending = false; }
                    return true;
                }
                if (!SameRun(_session.Latest?.Engine)) break;
                await Task.Delay(100);
            }
            lock (_gate) _failure = "维护安全收尾未确认；保持停止请求，请查看后台报警。";
            return false;
        }

        private async Task RenewalLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    PressureMaintenanceLease lease; long sequence;
                    lock (_gate)
                    {
                        if (_ending || _disposed || !_requested) return;
                        if ((Stopwatch.GetTimestamp() - _uiPulse) / (double)Stopwatch.Frequency > 3)
                            throw new InvalidOperationException("标定界面或状态刷新失联，已停止续租。");
                        lease = _lease?.Clone(); if (lease == null) continue;
                        sequence = _sequence = Math.Max(_sequence, lease.LastHeartbeatSequence) + 1;
                    }
                    if (!lease.IsLive(DateTime.UtcNow.Ticks)) throw new InvalidOperationException("维护权限已失效，不能续租复活");
                    var response = await _session.MaintenanceClient.RenewMaintenanceAsync(new PressureMaintenanceHeartbeat
                    {
                        SessionId = lease.SessionId, RunId = lease.RunId, RunEpoch = lease.RunEpoch, IncidentId = lease.IncidentId,
                        OwnerId = lease.OwnerId, EngineInstanceId = lease.EngineInstanceId, Generation = lease.Generation,
                        UiProcessId = lease.UiProcessId, UiProcessStartUtcTicks = lease.UiProcessStartUtcTicks,
                        Sequence = sequence, IssuedUtcTicks = DateTime.UtcNow.Ticks
                    }, token).ConfigureAwait(false);
                    if (!response.Accepted || response.Lease?.SameSession(lease) != true) throw new InvalidOperationException("维护续租被拒绝：" + response.Detail);
                    lock (_gate) if (!_ending && _lease?.SameSession(response.Lease) == true && response.Lease.Revision > _lease.Revision) _lease = response.Lease.Clone();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_gate) _failure = "维护续租中断：" + ex.GetBaseException().Message;
                // No WinForms callback is needed to request OFF. This is only an
                // End bound to the existing lease; it cannot create output authority.
                await RequestKnownLeaseEndAsync().ConfigureAwait(false);
            }
        }
        private Task RequestKnownLeaseEndAsync()
        {
            lock (_gate)
            {
                if (!_requested || _lease == null) return Task.CompletedTask;
                return _endSubmission ?? (_endSubmission = SubmitKnownLeaseEndAsync(_requestedEngine, _lease.Clone()));
            }
        }
        private async Task SubmitKnownLeaseEndAsync(EngineStateSnapshot engine, PressureMaintenanceLease lease)
        {
            try
            {
                // Submit exactly once, retain the client's original command ID on
                // timeout, and let normal refresh query its durable final result.
                var response = await _session.MaintenanceClient.SubmitMaintenanceAsync(engine,
                    OperatorCommandKind.EndPressureMaintenance, Payload(lease, 0), CancellationToken.None).ConfigureAwait(false);
                if (response?.Accepted != true) throw new InvalidOperationException(response?.FailureCode ?? "维护结束回执缺失");
            }
            catch (Exception ex)
            {
                lock (_gate) if (_lease?.SameSession(lease) == true)
                    _failure = "维护结束尚未确认；原命令继续查询，租约不再续期：" + ex.GetBaseException().Message;
            }
        }
        private PressureMaintenanceCommand Payload(PressureMaintenanceLease lease, double pressure) => new PressureMaintenanceCommand
        { EngineInstanceId = _engineId, UiProcessId = _session.MaintenanceClient.UiProcessId,
            UiProcessStartUtcTicks = _session.MaintenanceClient.UiProcessStartUtcTicks, HydraulicId = _hydraulicId,
            IncidentId = lease?.IncidentId ?? string.Empty, OwnerId = lease?.OwnerId ?? string.Empty, PressureBar = pressure };
        private bool SameRun(EngineStateSnapshot engine) => engine != null && engine.SessionId == _sessionId && engine.RunId == _runId && engine.RunEpoch == _epoch;
        private static bool DisplayFresh(EngineUiPressureMaintenance display) => display.CapturedUtcTicks <= DateTime.UtcNow.Ticks &&
            DateTime.UtcNow.Ticks - display.CapturedUtcTicks < TimeSpan.FromSeconds(3).Ticks;
        private void StopRenewal() { lock (_gate) { try { _renewStop?.Cancel(); } catch (ObjectDisposedException) { } } }
        public void Dispose()
        {
            lock (_gate) { _disposed = true; StopRenewal(); }
            _ = RequestKnownLeaseEndAsync();
        }
    }
}
