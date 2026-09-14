using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.FallbackGuard
{
    public sealed class IndependentVerificationBaseline
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public long Generation { get; set; }
        public RecoveryDatabaseSnapshot Database { get; set; }

        public void ValidateAgainst(string path, long creation, string requestId, long generation, int[] targets)
        {
            if (SchemaVersion != 1 || !Guid.TryParseExact(RequestId, "N", out _) || RequestId != requestId ||
                Generation <= 0 || Generation != generation || Database == null || targets == null)
                throw new InvalidDataException("IndependentVerificationBaselineIdentityInvalid");
            Database.Validate();
            if (!string.Equals(Database.DatabasePath, path, StringComparison.OrdinalIgnoreCase) ||
                Database.CreationUtcTicks != creation || !Database.Channels.Select(c => c.Channel).OrderBy(c => c)
                    .SequenceEqual(targets.OrderBy(c => c)))
                throw new InvalidDataException("IndependentVerificationBaselineTargetsInvalid");
        }
    }

    // One registered project per service process. Native safety and launch
    // calls stay in bounded children. The safety runner owns the exclusive
    // executor lease for this runtime's entire lifetime.
    internal sealed class IndependentExecutorRuntime : IIndependentProjectRuntimeOperations, IDisposable
    {
        private readonly string _registrationPath, _executor;
        private readonly IndependentExecutorRegistration _registration;
        private readonly IndependentProjectStateStore _store;
        private readonly IndependentProjectSafetyRunner _safety;
        private readonly IndependentProjectRecoveryPump _pump;
        private readonly DatabaseStallMonitor _monitor = new DatabaseStallMonitor();
        private IndependentLaunchWorkerOperation _launch;
        private string _launchRequest;
        private string _activeTransactionRequest;
        private long _lastDatabaseRead;
        private long _lastVerificationRead;
        private bool _disposed;
        public string Detail { get; private set; } = "Starting";

        public IndependentExecutorRuntime(string registrationPath)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) throw new UnauthorizedAccessException("IndependentExecutorRequiresSystem");
            _registrationPath = Path.GetFullPath(registrationPath);
            _registration = IndependentExecutorRegistration.LoadTrusted(_registrationPath);
            _executor = "IndependentExecutor:" + _registration.InstallationId;
            _store = new IndependentProjectStateStore(_registration.StateDirectory);
            _safety = new IndependentProjectSafetyRunner(_registrationPath, _executor);
            _pump = new IndependentProjectRecoveryPump(_store, this, _executor, 30000, 30000);
        }

        public void Tick()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IndependentExecutorRuntime));
            var state = _store.Read();
            if (state?.Intent == null) { Detail = "AwaitingDurableRunIntent"; return; }
            _registration.RequireBoundIntent(state.Intent);
            if (_activeTransactionRequest != state.Transaction?.RequestId)
            {
                // An operator stop can replace a launch transaction between
                // ticks. Retire its bounded workers immediately, not after the
                // new cooperative window expires.
                CancelStageWorkers();
                _activeTransactionRequest = state.Transaction?.RequestId;
            }
            var now = DateTime.UtcNow.Ticks;
            if (state.Transaction != null && (!state.Transaction.IsTerminal || state.SafetyCleanupPending))
            {
                _pump.Tick(now);
                Detail = _store.Read().Transaction.Phase + ":" + Detail;
                if (Detail.Length > 512) Detail = Detail.Substring(0, 512);
                return;
            }
            var targets = state.Intent.RecoveryChannels();
            if (state.Maintenance || targets.Length == 0)
            { _monitor.Observe(null, null, 0); Detail = "StoppedPausedCompletedOrMaintenance"; return; }
            if (state.Controller == null) { Detail = "ControllerIdentityMissing"; return; }
            var previous = state.Transaction;
            if (previous != null && previous.Phase == IndependentRecoveryPhase.NeedsAttention &&
                previous.RunId == state.Intent.RunId && previous.AttemptsUtcTicks.Length >= 3)
            { Detail = "RetryBudgetExhausted;OperatorActionRequired"; return; }
            if (previous != null && now - previous.LastAttemptUtcTicks < TimeSpan.FromSeconds(60).Ticks) return;
            if (now < _lastDatabaseRead) throw new InvalidOperationException("IndependentExecutorClockRegressed");
            if (now - _lastDatabaseRead < TimeSpan.FromSeconds(5).Ticks) return;
            _lastDatabaseRead = now;
            var alive = ExactProcessAlive(state.Controller);
            try
            {
                DatabaseProgressSnapshot snapshot;
                if (state.Intent.MechanicalTargets.Length > 0)
                {
                    var proof = DatabaseProgressReader.ReadRecoveryIsolated(_registration.DatabasePath, targets);
                    var completed = RecoveryDatabaseEvidence.CompletedTargets(proof, state.Intent);
                    if (completed.Length > 0)
                    {
                        _store.RecordDatabaseCompletions(state.Revision, proof.DatabasePath, proof.CreationUtcTicks,
                            proof.Channels.ToDictionary(lane => lane.Channel, lane => lane.MechanicalCompletedCount), now);
                        _monitor.Observe(null, null, 0);
                        Detail = "MechanicalTargetCompleted:" + string.Join(",", completed) + ";NotRecoveryVerification";
                        return;
                    }
                    snapshot = new DatabaseProgressSnapshot
                    {
                        DatabasePath = proof.DatabasePath, CreationUtcTicks = proof.CreationUtcTicks,
                        Channels = proof.Channels.Select(lane => new DatabaseLaneProgress
                        {
                            Channel = lane.Channel, Cycle = lane.FormalRecords.Select(row => row.Cycle).DefaultIfEmpty(0).Max(),
                            RecentCompletedCycles = lane.FormalRecords.Select(row => row.Cycle).ToArray()
                        }).ToArray()
                    };
                }
                else snapshot = DatabaseProgressReader.ReadIsolated(_registration.DatabasePath, targets);
                if (alive == false)
                {
                    const string reason = "ExactControllerExited;IndependentTakeoverRequested";
                    _store.BeginRecovery(state.Revision, _executor, now, reason);
                    Detail = reason;
                    return;
                }
                var intent = new DatabaseWatchIntent
                {
                    SessionId = state.Controller.SessionToken, RunId = state.Intent.RunId,
                    RunEpoch = state.Intent.RunEpoch, ProcessId = state.Controller.Pid,
                    ProcessStartUtcTicks = state.Controller.StartUtcTicks,
                    DatabasePath = _registration.DatabasePath,
                    DatabaseCreationUtcTicks = _registration.DatabaseCreationUtcTicks,
                    Channels = targets, PeriodMs = state.Intent.PeriodMs
                };
                var stalled = _monitor.Observe(intent, snapshot, now / TimeSpan.TicksPerMillisecond);
                if (now < state.RunStartedUtcTicks) throw new InvalidDataException("IndependentRunClockRegressed");
                if (now < state.StartupDeadlineUtcTicks)
                { Detail = "ObservingStartupWithinDurableBudget"; return; }
                if (stalled)
                {
                    var reason = "DatabaseStalled:" + string.Join(",", _monitor.StalledChannels);
                    _store.BeginRecovery(state.Revision, _executor, now, reason);
                    Detail = reason;
                }
                else Detail = "ObservingFormalDatabaseProgress";
            }
            catch (Exception error)
            {
                // Includes the externally enforced reader timeout. Failed reads
                // must break consecutive stall confirmations, never count as one.
                _monitor.Unreadable();
                if (alive == false)
                {
                    const string reason = "ExactControllerExited;DatabaseUnreadable;IndependentTakeoverRequested";
                    _store.BeginRecovery(state.Revision, _executor, now, reason);
                    Detail = reason;
                    return;
                }
                Detail = "DatabaseObservationOrAdmissionFailed:" + error.GetType().Name + ":" + Clip(error.Message);
            }
        }

        public IndependentOperationResult CooperativeStop(IndependentRecoveryTransaction tx)
        {
            var state = _store.Read();
            var receipt = state?.CooperativeStopReceipt;
            if (receipt != null && state.Transaction?.RequestId == tx.RequestId &&
                state.Transaction.Phase == IndependentRecoveryPhase.CooperativeStop &&
                receipt.RequestId == tx.RequestId && receipt.Generation == tx.Generation &&
                state.Controller?.Matches(receipt.Controller) == true &&
                receipt.CompletedUtcTicks >= tx.LastAttemptUtcTicks &&
                receipt.CompletedUtcTicks < tx.PhaseDeadlineUtcTicks)
            {
                Detail = "CooperativeStopAcknowledged;IndependentSafetyStillRequired";
                return IndependentOperationResult.Completed;
            }
            // A missing reply cannot extend the independent 25 s deadline.
            Detail = "CooperativeStopRequested;WaitingWithinDurableDeadline";
            return IndependentOperationResult.Pending;
        }

        public IndependentOperationResult ConfirmPowerOff(IndependentRecoveryTransaction tx) => Safety(tx);
        public IndependentOperationResult RetireExactControls(IndependentRecoveryTransaction tx) => Safety(tx);
        public IndependentOperationResult ConfirmOutputsAndPressure(IndependentRecoveryTransaction tx) => Safety(tx);
        private IndependentOperationResult Safety(IndependentRecoveryTransaction tx)
        {
            var result = _safety.Poll(tx, DateTime.UtcNow.Ticks);
            Detail = _safety.Detail;
            return result;
        }

        private string BaselinePath(IndependentRecoveryTransaction tx) =>
            Path.Combine(_registration.StateDirectory, "verification-" + tx.RequestId + ".json");

        private IndependentVerificationBaseline ReadBaseline(IndependentProjectState state)
        {
            var path = BaselinePath(state.Transaction);
            IndependentProtectedFiles.RequireTrustedFile(path);
            var baseline = BoundedJson.Read<IndependentVerificationBaseline>(path);
            if (baseline == null)
                throw new InvalidDataException("IndependentVerificationBaselineIdentityInvalid");
            baseline.ValidateAgainst(_registration.DatabasePath, _registration.DatabaseCreationUtcTicks,
                state.Transaction.RequestId, state.Transaction.Generation, state.Transaction.Channels);
            return baseline;
        }

        public IndependentOperationResult LaunchOnce(IndependentRecoveryTransaction tx)
        {
            var state = _store.Read();
            if (state.Transaction.RequestId != tx.RequestId || state.Transaction.Generation != tx.Generation)
                throw new InvalidOperationException("IndependentRuntimeTransactionChanged");
            if (!File.Exists(BaselinePath(tx)))
            {
                if (state.Ticket != null) throw new InvalidDataException("DispatchedRecoveryBaselineMissing");
                var snapshot = DatabaseProgressReader.ReadRecoveryIsolated(_registration.DatabasePath, tx.Channels);
                BoundedJson.Write(BaselinePath(tx), new IndependentVerificationBaseline
                { RequestId = tx.RequestId, Generation = tx.Generation, Database = snapshot });
            }
            ReadBaseline(state);
            if (state.Ticket == null)
            {
                _store.IssueLaunchTicket(state.Revision, _executor, tx.Generation, _registration.ExecutableSha256,
                    state.Controller.WindowsSessionId, DateTime.UtcNow.Ticks);
                // Ticket issuance changed the state revision; let the next pump
                // tick take a fresh snapshot before dispatch/phase advancement.
                return IndependentOperationResult.Pending;
            }
            if (_launch == null || _launchRequest != tx.RequestId)
            {
                _launch?.Dispose();
                _launch = new IndependentLaunchWorkerOperation(_registrationPath, state.Ticket.Nonce);
                _launchRequest = tx.RequestId;
            }
            var result = _launch.Poll(DateTime.UtcNow.Ticks);
            Detail = _launch.Detail;
            return result;
        }

        public IndependentOperationResult VerifyActionsAndDatabase(IndependentRecoveryTransaction tx)
        {
            var state = _store.Read();
            if (state.Transaction.RequestId != tx.RequestId || state.Transaction.Generation != tx.Generation ||
                state.Ticket?.Consumer == null || !state.Ticket.Consumer.Matches(state.Controller))
                return IndependentOperationResult.Failed;
            var alive = ExactProcessAlive(state.Controller);
            if (alive == null) { Detail = "ReplacementIdentityUnreadable"; return IndependentOperationResult.Pending; }
            var now = DateTime.UtcNow.Ticks;
            if (now < _lastVerificationRead) throw new InvalidOperationException("IndependentVerificationClockRegressed");
            if (now - _lastVerificationRead < TimeSpan.FromSeconds(5).Ticks) return IndependentOperationResult.Pending;
            _lastVerificationRead = now;
            var baseline = ReadBaseline(state);
            var snapshot = DatabaseProgressReader.ReadRecoveryIsolated(_registration.DatabasePath, tx.Channels);
            var pending = RecoveryDatabaseEvidence.UnverifiedChannels(baseline.Database, snapshot);
            if (state.Intent.MechanicalTargets.Length > 0)
            {
                var completed = RecoveryDatabaseEvidence.CompletedTargets(snapshot, state.Intent);
                if (state.Intent.CompletedChannels.Intersect(tx.Channels).Except(completed).Any())
                    throw new InvalidDataException("IndependentCompletedTargetRegressed");
                if (completed.Except(state.Intent.CompletedChannels).Any())
                {
                    _store.RecordVerificationCompletions(state.Revision, snapshot.DatabasePath, snapshot.CreationUtcTicks,
                        snapshot.Channels.ToDictionary(lane => lane.Channel, lane => lane.MechanicalCompletedCount), now);
                    Detail = "TargetCompletionRecorded;AwaitingRemainingVerificationOrSafetyCleanup";
                    return IndependentOperationResult.Pending;
                }
                pending = pending.Except(completed).ToArray();
            }
            if (alive == false) return IndependentOperationResult.Failed;
            Detail = pending.Length == 0 ? "MechanicalAndThreeFormalRecordsAdvanced" :
                "AwaitingRecoveredChannels:" + string.Join(",", pending);
            return pending.Length == 0 ? IndependentOperationResult.Completed : IndependentOperationResult.Pending;
        }

        internal static bool? ExactProcessAlive(IndependentProcessIdentity expected)
        {
            expected.Validate();
            try
            {
                using (var process = Process.GetProcessById(expected.Pid))
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == expected.StartUtcTicks &&
                        process.SessionId == expected.WindowsSessionId &&
                        string.Equals(process.MainModule.FileName, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        public void CancelPendingLaunch(IndependentRecoveryTransaction tx)
        { _launch?.Dispose(); _launch = null; _launchRequest = null; }
        public void CancelStageWorkers() { try { CancelPendingLaunch(null); } finally { _safety.Cancel(); } }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { CancelStageWorkers(); } finally { _safety.Dispose(); }
        }
        private static string Clip(string text) => text == null || text.Length <= 400 ? text : text.Substring(0, 400);
    }
}
