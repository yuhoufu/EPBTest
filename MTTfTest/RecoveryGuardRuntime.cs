using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using MTTFTest.RecoveryControl;

namespace MTEmbTest
{
    internal static class RecoveryGuardRuntime
    {
        private sealed class RunLease
        {
            internal RecoveryAuthorizationToken Token;
            internal RecoveryProcessIdentity Process;
            internal RecoveryRevocationSignal Signal;
            internal string RunId;
            internal string RootRunId;
            internal string ConfigurationIdentity;
            internal int StopQueued;
            internal int PauseQueued;
            internal int Revoked;
        }

        private sealed class PendingAdmission
        {
            internal string RunId;
            internal int Revoked;
        }

        private sealed class TerminalRequest
        {
            internal RunLease Lease;
            internal RecoveryDesiredState State;
            internal string Reason;
        }

        private static readonly RecoveryControlStore Store = new RecoveryControlStore();
        private static readonly ConcurrentQueue<TerminalRequest> Terminals = new ConcurrentQueue<TerminalRequest>();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static RunLease _lease;
        private static PendingAdmission _admission;
        private static RecoveryObservationSnapshot _snapshot;
        private static Thread _worker;
        private static long _lastOffered;
        private static long _sequence;
        private static long _lastErrorUtcTicks;
        private static Func<string, string, Task> _externalStop;
        private static string _lastObservedStop;
        private static Action<string, string> _externalFence;
        private static string _lastObservedFence;

        internal static void Attach(EpbManager manager, GlobalConfig config)
        {
            if (manager == null || !Store.IsRegisteredOrPending) return;
            manager.ExternalRunAdmissionAsync = (request, token) => AdmitAsync(request, config, token);
            manager.ExternalManualPauseRequested = RequestManualPause;
            _externalFence = (runId, reason) =>
            {
                if (manager.WatchdogRunId.ToString("N") == runId && !manager.RequiresProcessRestart)
                    manager.RevokeExecutionForExternalRecovery(reason);
            };
            _externalStop = (runId, reason) => manager.WatchdogRunId.ToString("N") != runId
                ? Task.CompletedTask : manager.StopAllAsync(new StopContext
            {
                Source = StopSource.ManualUi,
                Reason = reason,
                Initiator = "RecoveryGuardOperatorStop",
                RunId = runId,
                RequestedUtc = DateTime.UtcNow
            }, CancellationToken.None);
            if (Volatile.Read(ref _worker) == null)
            {
                var thread = new Thread(PublishLoop) { IsBackground = true, Name = "MTTFTest.RecoveryGuardPublisher" };
                if (Interlocked.CompareExchange(ref _worker, thread, null) == null) thread.Start();
            }
        }

        private static Task AdmitAsync(RunAdmissionRequest request, GlobalConfig config, CancellationToken token)
        {
            var pending = new PendingAdmission { RunId = request.Identity.RunId.ToString("N") };
            Volatile.Write(ref _admission, pending);
            return Task.Run(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var process = RecoveryProcessProbe.Current();
                    var previous = Volatile.Read(ref _lease);
                    if (request.Origin == RunAdmissionOrigin.ManualContinue && previous != null &&
                        Volatile.Read(ref previous.PauseQueued) != 0)
                        CommitTerminal(new TerminalRequest
                        {
                            Lease = previous, State = RecoveryDesiredState.Paused, Reason = "ManualGracefulPause"
                        });
                    var state = Store.Read();
                    var configIdentity = UnattendedRunCheckpointStore.ComputeConfigurationHash(config);
                    var rootRun = request.Identity.EffectiveRootRunId.ToString("N");
                    var runId = request.Identity.RunId.ToString("N");
                    RecoveryAuthorizationToken authorization;
                    string recoveredOperation = null;
                    if (request.Origin == RunAdmissionOrigin.ManualStart)
                    {
                        // Controller has already established a cold safe baseline.
                        // Only this explicit manual origin may supersede a run.
                        if (state.Transaction != null && !state.Transaction.OwnershipReleased)
                            throw new InvalidOperationException("RecoveryTakeoverMustBeClosedBeforeManualStart");
                        if (state.Intent?.DesiredState == RecoveryDesiredState.Run)
                            Store.SetOperatorIntent(state.Intent.AuthorizationId, state.Intent.IntentVersion,
                                RecoveryDesiredState.Stopped, "SupersededByExplicitManualStart");
                        authorization = Store.BeginManualRun(rootRun, runId, configIdentity, process, DateTime.UtcNow);
                    }
                    else if (request.Origin == RunAdmissionOrigin.ManualContinue)
                    {
                        if (state.Intent == null || state.Intent.RootRunId != rootRun || state.Intent.ConfigurationIdentity != configIdentity)
                            throw new InvalidOperationException("RecoveryManualContinueIdentityMismatch");
                        authorization = Store.ContinueManually(state.Intent.AuthorizationId,
                            state.Intent.IntentVersion, runId, process, DateTime.UtcNow);
                    }
                    else
                    {
                        if (state.Intent == null || state.Intent.RootRunId != rootRun || state.Intent.ConfigurationIdentity != configIdentity)
                            throw new InvalidOperationException("RecoveryAutomaticRunIdentityMismatch");
                        authorization = state.Token();
                        if (state.Intent.MainProcess?.Matches(process) == true)
                            Store.ContinueInProcess(authorization, rootRun, runId, configIdentity, process, DateTime.UtcNow);
                        else
                        {
                            var launch = state.Launches.LastOrDefault(l => l.State == "Started" && l.Process?.Matches(process) == true);
                            if (launch == null) throw new InvalidOperationException("RecoveryAutomaticLaunchReservationMissing");
                            authorization = launch.Authorization;
                            Store.BindRecoveredRun(authorization, launch.OperationId, runId, process, DateTime.UtcNow);
                            recoveredOperation = launch.OperationId;
                        }
                    }
                    var lease = new RunLease
                    {
                        Token = authorization, Process = process, RunId = runId, RootRunId = rootRun,
                        ConfigurationIdentity = configIdentity, Signal = new RecoveryRevocationSignal(authorization)
                    };
                    Interlocked.Exchange(ref _snapshot, null);
                    var previousLease = Interlocked.Exchange(ref _lease, lease);
                    try { previousLease?.Signal.Dispose(); } catch { }
                    if (token.IsCancellationRequested || Volatile.Read(ref pending.Revoked) != 0)
                    {
                        QueueTerminal(lease, RecoveryDesiredState.Stopped, "ManualStopDuringAdmission");
                        throw new OperationCanceledException("RecoveryRunAdmissionCancelled");
                    }
                    if (recoveredOperation != null)
                        await WaitForRecoveredRunAdmissionAsync(authorization, recoveredOperation, process, token).ConfigureAwait(false);
                    Store.AssertRunAllowed(authorization, process, DateTime.UtcNow);
                    var watchdogSession = WatchdogRuntime.SessionId;
                    if (!string.IsNullOrWhiteSpace(watchdogSession))
                        Store.BindWatchdogSession(authorization, runId, process, watchdogSession, DateTime.UtcNow);
                }
                finally { Interlocked.CompareExchange(ref _admission, null, pending); }
            });
        }

        internal static async Task WaitForRecoveredRunAdmissionAsync(RecoveryAuthorizationToken authorization,
            string operationId, RecoveryProcessIdentity process, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Store.IsRecoveredRunReadyForAdmission(authorization, operationId, process, DateTime.UtcNow)) return;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        // Called from the existing no-file-I/O revocation barrier. An event Set
        // is bounded; durable writes and the independent receiver acknowledgment
        // are performed outside the control and acquisition threads.
        internal static void RevokeInMemory(StopContext context)
        {
            if (context == null || (context.Source != StopSource.ManualUi &&
                context.Source != StopSource.ApplicationClosing && context.Source != StopSource.ProgramExit)) return;
            if (context.FaultScope == FaultScope.Channel) return;
            var runId = Normalize(context.RunId);
            var pending = Volatile.Read(ref _admission);
            if (pending != null && (runId.Length == 0 || pending.RunId == runId))
                Interlocked.Exchange(ref pending.Revoked, 1);
            var lease = Volatile.Read(ref _lease);
            if (context.Source == StopSource.ApplicationClosing && lease != null &&
                Volatile.Read(ref lease.PauseQueued) != 0 && Volatile.Read(ref lease.StopQueued) == 0) return;
            if (lease != null && (runId.Length == 0 || lease.RunId == runId))
                QueueTerminal(lease, RecoveryDesiredState.Stopped, context.Reason ?? "ManualStop");
        }

        internal static void CompleteRecoveredRunBeforeAdmission(UnattendedRunCheckpoint checkpoint, GlobalConfig config)
        {
            if (!Store.IsRegisteredOrPending) return;
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            var process = RecoveryProcessProbe.Current();
            var state = Store.Read();
            var launch = state.Launches.LastOrDefault(l => l.State == "Started" && l.Process?.Matches(process) == true);
            if (launch == null) throw new InvalidOperationException("RecoveryTerminalLaunchReservationMissing");
            Store.CompleteRecoveredRunBeforeAdmission(launch.Authorization, launch.OperationId, process,
                Normalize(checkpoint.RootRunId), Normalize(checkpoint.RunId),
                UnattendedRunCheckpointStore.ComputeConfigurationHash(config), DateTime.UtcNow);
        }

        internal static void CheckpointTerminal(UnattendedRunCheckpoint checkpoint)
        {
            var lease = Volatile.Read(ref _lease);
            if (lease == null || checkpoint == null || lease.RunId != Normalize(checkpoint.RunId)) return;
            if (checkpoint.Armed && !checkpoint.GracefulPaused) return;
            var desired = checkpoint.GracefulPaused ? RecoveryDesiredState.Paused :
                    checkpoint.LastReason == "FormalRunCompleted" ||
                    checkpoint.LastReason == "FormalRunAlreadyCompleted" ||
                    checkpoint.LastReason == "FormalRunAlreadyCompletedAtRecoveryStartup"
                        ? RecoveryDesiredState.Completed : RecoveryDesiredState.Stopped;
            var reason = checkpoint.GracefulPaused ? "ManualGracefulPause" : checkpoint.LastReason ?? "AuthorizationRevoked";
            QueueTerminal(lease, desired, reason);
            // SaveUnsafe is already a disk-persistence path, not the synchronous
            // controller barrier. Complete external revocation here as well so
            // normal shutdown does not rely solely on a background queue. A
            // failed external write must not prevent the local stop tombstone.
            try { CommitTerminal(new TerminalRequest { Lease = lease, State = desired, Reason = reason }); }
            catch (Exception ex) { Report(ex); }
        }

        private static void RequestManualPause(Guid runId)
        {
            var lease = Volatile.Read(ref _lease);
            if (lease != null && lease.RunId == runId.ToString("N"))
                QueueTerminal(lease, RecoveryDesiredState.Paused, "ManualPauseRequested");
        }

        internal static string RootForCurrentRun(string runId)
        {
            var lease = Volatile.Read(ref _lease);
            return lease != null && lease.RunId == Normalize(runId) ? lease.RootRunId : null;
        }

        private static void QueueTerminal(RunLease lease, RecoveryDesiredState state, string reason)
        {
            if (state == RecoveryDesiredState.Stopped || state == RecoveryDesiredState.Completed)
            {
                Interlocked.Exchange(ref lease.Revoked, 1);
                try { lease.Signal.Revoke(); } catch { /* local revoke remains in force */ }
                if (Interlocked.Exchange(ref lease.StopQueued, 1) != 0) return;
            }
            else
            {
                try { lease.Signal.Pause(); } catch (Exception ex) { Report(ex); }
                if (Interlocked.Exchange(ref lease.PauseQueued, 1) != 0) return;
            }
            Terminals.Enqueue(new TerminalRequest { Lease = lease, State = state, Reason = reason });
            Wake.Set();
        }

        internal static bool ShouldCaptureSnapshot()
        {
            if (Volatile.Read(ref _lease) == null) return false;
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastOffered);
            return now - previous >= Stopwatch.Frequency * 10 &&
                Interlocked.CompareExchange(ref _lastOffered, now, previous) == previous;
        }

        internal static void Offer(RecoveryObservationSnapshot snapshot)
        {
            var lease = Volatile.Read(ref _lease);
            if (lease == null || snapshot == null || snapshot.RunId != lease.RunId) return;
            snapshot.Authorization = lease.Token;
            snapshot.MainProcess = lease.Process;
            snapshot.ConfigurationIdentity = lease.ConfigurationIdentity;
            snapshot.Sequence = Interlocked.Increment(ref _sequence);
            snapshot.PublishedUtcTicks = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _snapshot, snapshot);
            Wake.Set();
        }

        private static void PublishLoop()
        {
            TerminalRequest pending = null;
            long publishedSequence = 0;
            while (true)
            {
                try
                {
                    if (pending == null) Terminals.TryDequeue(out pending);
                    while (pending != null)
                    {
                        CommitTerminal(pending);
                        pending = null;
                        Terminals.TryDequeue(out pending);
                    }
                    var authority = Store.Read();
                    ApplyTakeoverFence(authority);
                    var lease = Volatile.Read(ref _lease);
                    if (lease != null)
                    {
                        var current = authority.Intent;
                        if (current?.AuthorizationId == lease.Token.AuthorizationId &&
                            current.DesiredState == RecoveryDesiredState.Stopped &&
                            _lastObservedStop != current.AuthorizationId + ":" + current.IntentVersion)
                        {
                            _lastObservedStop = current.AuthorizationId + ":" + current.IntentVersion;
                            Interlocked.Exchange(ref lease.Revoked, 1);
                            var stop = _externalStop;
                            if (stop != null)
                                _ = Task.Run(() => stop(lease.RunId, "外部恢复授权已人工停止")).ContinueWith(t => Report(t.Exception),
                                    TaskContinuationOptions.OnlyOnFaulted);
                        }
                    }
                    var snapshot = Volatile.Read(ref _snapshot);
                    if (snapshot != null && snapshot.Sequence != publishedSequence)
                    {
                        if (authority.Matches(snapshot.Authorization) && authority.Intent.DesiredState == RecoveryDesiredState.Run)
                            Store.PublishSnapshot(snapshot);
                        publishedSequence = snapshot.Sequence;
                    }
                }
                catch (Exception ex) { Report(ex); }
                Wake.WaitOne(1000);
            }
        }

        internal static void ApplyTakeoverFence(RecoveryControlState authority)
        {
            var lease = Volatile.Read(ref _lease);
            var takeover = authority?.Transaction;
            if (lease == null || takeover == null ||
                takeover.AuthorizationId != lease.Token.AuthorizationId ||
                takeover.IntentVersion != lease.Token.IntentVersion || takeover.Epoch <= lease.Token.TakeoverEpoch)
                return;
            var identity = takeover.TransactionId + ":" + takeover.Epoch;
            if (_lastObservedFence == identity) return;
            Interlocked.Exchange(ref lease.Revoked, 1);
            // Revoke execution and enqueue OFF using the controller's existing
            // external-recovery boundary. This is not a confirmed safe-stop
            // receipt, and it must not turn automatic takeover into user Stop.
            _externalFence?.Invoke(lease.RunId, "RecoveryGuardTakeover:" + identity);
            _lastObservedFence = identity;
        }

        private static void CommitTerminal(TerminalRequest request)
        {
            var intent = Store.Read().Intent;
            if (intent?.AuthorizationId == request.Lease.Token.AuthorizationId &&
                (intent.IntentVersion == request.Lease.Token.IntentVersion ||
                 (request.State == RecoveryDesiredState.Stopped && intent.DesiredState == RecoveryDesiredState.Paused &&
                  intent.IntentVersion == request.Lease.Token.IntentVersion + 1 && intent.RunId == request.Lease.RunId)))
            {
                if (intent.DesiredState == RecoveryDesiredState.Run ||
                    (intent.DesiredState == RecoveryDesiredState.Paused && request.State == RecoveryDesiredState.Stopped))
                    Store.SetOperatorIntent(intent.AuthorizationId, intent.IntentVersion, request.State, request.Reason);
            }
        }

        private static string Normalize(string value) => Guid.TryParse(value, out var id) ? id.ToString("N") : string.Empty;
        private static void Report(Exception ex)
        {
            var now = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref _lastErrorUtcTicks);
            if (now - previous < TimeSpan.FromMinutes(1).Ticks ||
                Interlocked.CompareExchange(ref _lastErrorUtcTicks, now, previous) != previous) return;
            try { ProjectLogHub.Write(ProjectLogLevel.Error, "RecoveryGuard 状态发布/撤权未确认：" + ex?.GetBaseException().Message, "RecoveryGuard"); }
            catch { }
        }
    }
}
