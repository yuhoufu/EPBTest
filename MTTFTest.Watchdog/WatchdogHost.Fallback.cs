using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed partial class WatchdogHost
    {
        private readonly object _fallbackGate = new object();
        private FallbackLedgerStore _fallbackStore;
        private long _fallbackSnapshotTimestamp;
        private long _fallbackSnapshotSequence;
        private Task _fallbackPipeTask;
        private Task _fallbackObservationTask;
        private readonly FallbackProgressVerifier _fallbackVerifier = new FallbackProgressVerifier();
        private bool _fallbackVerified;
        private string FallbackInstance => _sidecarProcessId + ":" + _sidecarProcessStartUtcTicks;

        private bool FallbackOriginalBusy =>
            Volatile.Read(ref _takeoverStarted) != 0 ||
            Volatile.Read(ref _relaunchAfterExitStarted) != 0 ||
            Volatile.Read(ref _relaunchStarted) != 0 ||
            Volatile.Read(ref _safetyHandoffStarted) != 0 ||
            Volatile.Read(ref _safetyPrerequisiteRetryStarted) != 0 ||
            Volatile.Read(ref _manualStopTakeoverStarted) != 0 ||
            Volatile.Read(ref _manualPauseSafetyTakeoverStarted) != 0 ||
            Volatile.Read(ref _circuitHalfOpenStarted) != 0 ||
            Volatile.Read(ref _operatorTransitionStopStarted) != 0 ||
            Volatile.Read(ref _applicationExitDeadlineStarted) != 0;

        private bool FallbackMayBeginRecovery()
        {
            if (_fallbackStore == null) return true;
            try
            {
                var value = _fallbackStore.Read();
                if (!string.IsNullOrEmpty(value.IndependentRecoveryRequestId)) return false;
                var scope = FallbackExecutionScope.Current.Value;
                if (value.ManualStopped) return false;
                if (scope == null) return value.Owner == "Original" && value.OwnerInstanceId == FallbackInstance && value.Phase != "Yielded";
                FallbackLedgerStore.RequireCurrent(value, scope.FenceGeneration, scope.OwnerInstanceId);
                return true;
            }
            catch { return false; }
        }

        private FallbackCommandResult ExecuteFallbackCommand(FallbackCommand command)
        {
            lock (_fallbackGate)
            {
                var value = _fallbackStore.Read();
                if (!string.IsNullOrEmpty(value.IndependentRecoveryRequestId))
                    throw new InvalidOperationException("IndependentFallbackOwnsReplacement");
                if (command == null || command.SchemaVersion != 1 || command.SessionId != _args.SessionId ||
                    command.RunId != value.RunId || command.RunEpoch != value.RunEpoch ||
                    !MatchesFallbackRun(value))
                    throw new InvalidDataException("FallbackCommandIdentityMismatch");
                if (command.Action == "Query")
                    return new FallbackCommandResult { Status = "Completed", Ledger = value };
                if (command.Action != "Return" && command.Action != "Cancel" &&
                    (_journal.ManualStopRequested || IsSessionRevoked() || IsRecoveryBlocked()))
                    throw new InvalidOperationException("FallbackOriginalAuthorityRejected");
                if (command.Action == "Request")
                {
                    if (value.CommandId != command.CommandId || value.Requester != command.Requester)
                        value = _fallbackStore.Request(command.Revision, command.CommandId, command.Requester);
                    // Database evidence can request the existing safe takeover
                    // while the main process is alive but logically quiescent.
                    // Ownership remains Original until the safety/exit boundary;
                    // no external Process.Start or second launch path is added.
                    if (!FallbackOriginalBusy)
                        BeginTakeover("DatabaseProgressStalled:" + command.CommandId);
                }
                else if (command.Action == "Cancel")
                    value = _fallbackStore.CancelRequest(command.Revision, command.Requester);
                else if (command.Action == "Acquire")
                {
                    if (FallbackOriginalBusy || IsCurrentProcessAlive() ||
                        HasActiveSafetyTransaction(_args.JournalDirectory, _args.SessionId))
                        throw new InvalidOperationException("FallbackYieldNoLongerQuiescent");
                    if (!(value.Owner == "Fallback" && value.Requester == command.Requester && value.CommandId == command.CommandId))
                        value = _fallbackStore.Acquire(command.Revision, command.Requester, command.CommandId);
                }
                else if (command.Action == "Return")
                {
                    if (value.Owner != "Fallback" || value.OwnerInstanceId != command.Requester ||
                        value.FenceGeneration != command.FenceGeneration)
                        throw new InvalidOperationException("FallbackReturnIdentityMismatch");
                    if (FallbackOriginalBusy || HasActiveSafetyTransaction(_args.JournalDirectory, _args.SessionId))
                        throw new InvalidOperationException("FallbackReturnOutstandingWork");
                    value = _fallbackStore.Return(command.Revision, command.Requester, FallbackInstance, _fallbackVerified);
                    _fallbackVerified = false;
                }
                else throw new InvalidDataException("FallbackUnknownCommand");
                return new FallbackCommandResult { Status = value.Phase == "Requested" ? "InProgress" : "Completed", Ledger = value };
            }
        }

        private bool MatchesFallbackRun(FallbackLedger ledger)
        {
            var heartbeat = _journal.LastHeartbeat;
            if (heartbeat?.RunId == ledger.RunId && heartbeat.RunEpoch == ledger.RunEpoch) return true;
            var verified = _journal.LastVerifiedActiveRun;
            return string.IsNullOrEmpty(heartbeat?.RunId) && verified != null &&
                verified.RunId == ledger.RunId && verified.RunEpoch == ledger.RunEpoch &&
                verified.ProcessId == _journal.CurrentPid &&
                verified.ProcessStartUtcTicks == _journal.CurrentProcessStartUtcTicks;
        }

        private void ObserveFallbackCoordination()
        {
            var now = Stopwatch.GetTimestamp();
            if (now - _fallbackSnapshotTimestamp < Stopwatch.Frequency) return;
            _fallbackSnapshotTimestamp = now;
            var heartbeat = _journal.LastHeartbeat;
            if (heartbeat == null) return;
            // Keep the observation/control endpoint alive after StopAll clears
            // RunId. The durable ledger still identifies the unfinished trial.
            if (_fallbackStore == null &&
                (!Guid.TryParse(heartbeat.RunId, out _) || heartbeat.RunEpoch <= 0)) return;
            var status = "OriginalPriority";
            try
            {
                lock (_fallbackGate)
                {
                    if (_fallbackStore == null)
                    {
                        var store = new FallbackLedgerStore(_args.JournalDirectory, _args.SessionId);
                        store.Initialize(heartbeat.RunId, heartbeat.RunEpoch, FallbackInstance);
                        _fallbackStore = store;
                        _fallbackPipeTask = Task.Run(() => FallbackControlPipe.ServeAsync(
                            _args.SessionId, ExecuteFallbackCommand, _stop.Token));
                    }
                    var ledger = _fallbackStore.Read();
                    if (ledger.Owner == "Original" && ledger.OwnerInstanceId == FallbackInstance &&
                        (ledger.Phase == "Idle" || ledger.Phase == "Requested" || ledger.Phase == "Yielded") &&
                        string.IsNullOrEmpty(ledger.OutstandingLaunch) &&
                        heartbeat.RunActive && heartbeat.Phase == "Formal" && !heartbeat.ManualStopRequested &&
                        !_journal.ManualStopRequested && !IsSessionRevoked() &&
                        heartbeat.ProcessId == _journal.CurrentPid && heartbeat.ProcessStartUtcTicks == _journal.CurrentProcessStartUtcTicks &&
                        (ledger.RunId != heartbeat.RunId || heartbeat.RunEpoch > ledger.RunEpoch))
                    {
                        if (ledger.Phase != "Idle") ledger = _fallbackStore.CancelRequest(ledger.Revision, ledger.Requester);
                        ledger = _fallbackStore.BindActiveRun(ledger.Revision, FallbackInstance, heartbeat.RunId, heartbeat.RunEpoch);
                    }
                    if (ledger.Owner == "Original" && ledger.OwnerInstanceId != FallbackInstance && ledger.Phase == "Idle")
                    {
                        var previous = (ledger.OwnerInstanceId ?? "").Split(':');
                        if (previous.Length == 2 && int.TryParse(previous[0], out var pid) && long.TryParse(previous[1], out var start) &&
                            WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(ProbeProcessIdentity(pid, start)))
                            ledger = _fallbackStore.RebindOriginal(ledger.Revision, ledger.OwnerInstanceId, FallbackInstance);
                    }
                    _fallbackVerified = ledger.Phase == "Verifying" &&
                        _fallbackVerifier.Observe(heartbeat, ledger, now) && IsCurrentProcessAlive() &&
                        !_journal.ManualStopRequested && !IsRecoveryBlocked();
                    if ((_journal.ManualStopRequested || heartbeat.ManualStopRequested || IsSessionRevoked()) &&
                        !ledger.ManualStopped) ledger = _fallbackStore.Stop();
                    var requesterParts = (ledger.Requester ?? string.Empty).Split(':');
                    var requesterExited = requesterParts.Length == 2 && int.TryParse(requesterParts[0], out var requesterPid) &&
                        long.TryParse(requesterParts[1], out var requesterStart) &&
                        WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(ProbeProcessIdentity(requesterPid, requesterStart));
                    if (requesterExited && ledger.Owner == "Original" &&
                        (ledger.Phase == "Requested" || ledger.Phase == "Yielded"))
                        ledger = _fallbackStore.CancelRequest(ledger.Revision, ledger.Requester);
                    if (requesterExited && ledger.Owner == "Fallback" && !FallbackOriginalBusy &&
                        !HasActiveSafetyTransaction(_args.JournalDirectory, _args.SessionId) &&
                        (ledger.Phase == "Acquired" || (ledger.Phase == "Verifying" && _fallbackVerified)))
                        ledger = _fallbackStore.Return(ledger.Revision, ledger.OwnerInstanceId, FallbackInstance, _fallbackVerified);
                    var safeBoundary = !FallbackOriginalBusy && !IsCurrentProcessAlive() &&
                        !HasActiveSafetyTransaction(_args.JournalDirectory, _args.SessionId) &&
                        !IsRecoveryBlocked() && !ledger.ManualStopped;
                    if (ledger.Phase == "Requested")
                    {
                        if (safeBoundary) ledger = _fallbackStore.Yield(ledger.Revision, FallbackInstance);
                        else status = "TakeoverRequested; original execution or safety ownership unresolved";
                    }
                    if (ledger.Owner == "Fallback" && ledger.Phase == "Acquired" && safeBoundary)
                    {
                        var permit = _relaunchCoordinator.Snapshot;
                        if (permit?.State == DurableRelaunchPermitState.Approved)
                        {
                            // All existing safety, exit proof, budget and formal
                            // Supervisor/SessionAgent launch checks still execute.
                            using (new FallbackExecutionScope(ledger))
                                BeginRelaunchAfterExit(permit.Generation);
                            status = "FallbackExecutingOriginalEntry";
                        }
                        else status = "FallbackAwaitingOriginalPermit; no new authorization";
                    }
                    if (ledger.Phase == "OriginalVerifying" &&
                        heartbeat.ProcessId == ledger.LaunchProcessId &&
                        heartbeat.ProcessStartUtcTicks == ledger.LaunchProcessStartUtcTicks &&
                        heartbeat.RecoveryBatchCommitGeneration > 0)
                        _fallbackStore.CompleteOriginalLaunch(ledger.Revision,
                            heartbeat.ProcessId, heartbeat.ProcessStartUtcTicks);
                }
            }
            catch (Exception error) { status = "Unknown:" + error.GetBaseException().Message; }
            try
            {
                BoundedJson.Write(Path.Combine(_args.JournalDirectory, "fallback-observation-" + _args.SessionId + ".json"),
                    new FallbackObservation
                    {
                        InstallationId = SupervisorProtocol.ComputeTextSha256(Path.GetFullPath(_args.ExecutablePath).ToUpperInvariant()),
                        SessionId = _args.SessionId, SourceInstanceId = FallbackInstance,
                        ProcessId = _sidecarProcessId, ProcessStartUtcTicks = _sidecarProcessStartUtcTicks,
                        Sequence = ++_fallbackSnapshotSequence, CapturedUtc = DateTime.UtcNow.ToString("O"),
                        OriginalBusy = FallbackOriginalBusy, RecoveryBlocked = IsRecoveryBlocked(),
                        ManualStopped = _journal.ManualStopRequested, CoordinationStatus = status, Heartbeat = heartbeat
                    });
            }
            catch (Exception error) { Record("FallbackObservationUnavailable", error.GetBaseException().Message); }
        }

        private void PrepareFallbackLaunch(DurableLaunchIntentCapability capability)
        {
            lock (_fallbackGate)
            {
                var store = _fallbackStore ?? new FallbackLedgerStore(_args.JournalDirectory, _args.SessionId);
                if (!store.Exists) return;
                if (_journal.ManualStopRequested || IsSessionRevoked() || IsRecoveryBlocked())
                    throw new InvalidOperationException("FallbackLaunchOriginalAuthorityRejected");
                var scope = FallbackExecutionScope.Current.Value;
                if (scope == null)
                {
                    var previous = store.Read();
                    if (previous.Owner == "Original" && previous.Phase == "OriginalVerifying" &&
                        WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(
                            ProbeProcessIdentity(previous.LaunchProcessId, previous.LaunchProcessStartUtcTicks)))
                        store.CompleteOriginalLaunch(previous.Revision, previous.LaunchProcessId, previous.LaunchProcessStartUtcTicks);
                    store.PrepareOriginalLaunch(FallbackInstance, capability.IntentId);
                }
                else store.PrepareLaunch(scope.FenceGeneration, scope.OwnerInstanceId, scope.CommandId);
            }
        }

        private void BindFallbackLaunch(DurableLaunchIntentCapability capability, Process process)
        {
            lock (_fallbackGate)
            {
                var store = _fallbackStore ?? new FallbackLedgerStore(_args.JournalDirectory, _args.SessionId);
                if (!store.Exists) return;
                var scope = FallbackExecutionScope.Current.Value;
                if (scope == null) store.BindOriginalLaunch(FallbackInstance, capability.IntentId,
                    process.Id, process.StartTime.ToUniversalTime().Ticks);
                else store.BindLaunch(scope.FenceGeneration, scope.OwnerInstanceId, scope.CommandId,
                    process.Id, process.StartTime.ToUniversalTime().Ticks);
            }
        }
    }
}
