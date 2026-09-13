using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.FallbackGuard
{
    // The watchdog pipe and its relaunch permit are deliberately not used here.
    // A fresh response from the main safety bridge is required before termination.
    internal sealed class IndependentRecovery : IDisposable
    {
        private readonly Func<string, IndependentFallbackRequest, IndependentFallbackReceipt, IndependentFallbackStatus, Tuple<int, long>> _restart;
        private readonly Func<string, int[], DatabaseProgressSnapshot> _readDatabase;
        private readonly Func<long> _timestamp;
        private readonly Func<long> _utcTicks;
        internal IndependentRecovery(
            Func<string, IndependentFallbackRequest, IndependentFallbackReceipt, IndependentFallbackStatus, Tuple<int, long>> restart = null,
            Func<string, int[], DatabaseProgressSnapshot> readDatabase = null,
            Func<long> timestamp = null, Func<long> utcTicks = null)
        {
            _restart = restart ?? Restart;
            _readDatabase = readDatabase ?? DatabaseProgressReader.ReadIsolated;
            _timestamp = timestamp ?? Stopwatch.GetTimestamp;
            _utcTicks = utcTicks ?? (() => DateTime.UtcNow.Ticks);
        }
        private IndependentFallbackRequest _request;
        private EventWaitHandle _revoked, _attached;
        private long _nextAttempt;
        private int _attempts;
        private bool _launched;
        private string _lastDetail;
        private int _launchedPid;
        private long _launchedStart, _launchedAt, _lastRead;
        private readonly DatabaseStallMonitor _databaseVerifier = new DatabaseStallMonitor();
        private readonly FallbackProgressVerifier _actionVerifier = new FallbackProgressVerifier();
        private DatabaseWatchIntent _verificationIntent;
        private bool _verified, _verificationEnded;
        private Mutex _lease;
        private bool _ownsLease;
        public bool IsPending => _request != null && !_verificationEnded;

        public void Observe(string directory, bool active, bool stalled)
        {
            try
            {
                if (_launched) { Verify(directory); return; }
                var now = _utcTicks();
                if (!active)
                {
                    if (_request != null) Report(directory, "DisabledBeforeLaunch;NotRecovered");
                    _request = null;
                    ReleaseLease();
                    return;
                }
                if (now < _nextAttempt) return;
                if (_request != null && now - _request.RequestedUtcTicks > TimeSpan.FromSeconds(90).Ticks)
                {
                    Report(directory, "RequestExpired;NotRecovered");
                    _request = null;
                    ReleaseLease();
                    _nextAttempt = now + TimeSpan.FromMinutes(1).Ticks;
                    return;
                }
                var statusPath = IndependentFallbackProtocol.StatusPath(directory);
                if (!File.Exists(statusPath)) return;
                var status = BoundedJson.Read<IndependentFallbackStatus>(statusPath);
                if (!status.Armed)
                {
                    if (_request != null) Report(directory, "AuthorizationRevoked;NotRecovered");
                    _request = null;
                    ReleaseLease();
                    return;
                }
                if (now < status.UpdatedUtcTicks ||
                    now - status.UpdatedUtcTicks > TimeSpan.FromSeconds(5).Ticks) return;
                if (_request == null)
                {
                    if (!stalled || _attempts >= 3) return;
                    if (status.Heartbeat?.Phase != "Formal" &&
                        now - status.ProcessStartUtcTicks < TimeSpan.FromMinutes(5).Ticks) return;
                    _lease ??= new Mutex(false, "Global\\MTTF-IndependentRecovery-" +
                        SupervisorProtocol.ComputeTextSha256(Path.GetFullPath(directory).ToUpperInvariant()));
                    if (!_ownsLease)
                    {
                        try { _ownsLease = _lease.WaitOne(0); }
                        catch (AbandonedMutexException) { _ownsLease = true; }
                        if (!_ownsLease) return;
                    }
                    using (var self = Process.GetCurrentProcess())
                        _request = new IndependentFallbackRequest { Id = Guid.NewGuid().ToString("N"),
                            RunId = status.RunId, RunEpoch = status.RunEpoch,
                            ProcessId = status.ProcessId, ProcessStartUtcTicks = status.ProcessStartUtcTicks,
                            GuardProcessId = self.Id, GuardStartUtcTicks = self.StartTime.ToUniversalTime().Ticks,
                            RequestedUtcTicks = now };
                    var original = new FallbackLedgerStore(directory, status.SessionId);
                    if (original.Exists)
                        original.RetireForIndependentRecovery(_request.Id, status.ProcessId, status.ProcessStartUtcTicks,
                            original.Read().IndependentRecoveryRequestId);
                    BoundedJson.Write(IndependentFallbackProtocol.RequestPath(directory), _request);
                    _attempts++;
                    Report(directory, "FreshSafetyRequested");
                    return;
                }
                if (!IndependentFallbackProtocol.Matches(_request, status, now))
                { Report(directory, "RequestExpiredOrRunChanged"); _request = null; ReleaseLease(); _nextAttempt = now + TimeSpan.FromMinutes(1).Ticks; return; }
                var receiptPath = IndependentFallbackProtocol.ReceiptPath(directory);
                if (!File.Exists(receiptPath)) return;
                var receipt = BoundedJson.Read<IndependentFallbackReceipt>(receiptPath);
                if (receipt.RequestId != _request.Id) return;
                if (!receipt.Ready)
                { Report(directory, "SafetyPreparationFailed:" + receipt.Detail); return; }
                if (!IndependentFallbackProtocol.CanTerminate(_request, receipt, status, now)) return;
                var launched = _restart(directory, _request, receipt, status);
                _launched = true;
                _launchedPid = launched.Item1;
                _launchedStart = launched.Item2;
                _launchedAt = _utcTicks();
                Report(directory, "LaunchedAwaitingDatabaseVerification:PID=" + _launchedPid);
            }
            catch (Exception ex) { Report(directory, "IndependentRecoveryFailed:" + ex.GetBaseException().Message); }
        }

        private Tuple<int, long> Restart(string directory, IndependentFallbackRequest request,
            IndependentFallbackReceipt receipt, IndependentFallbackStatus status)
        {
                ValidateCheckpoint(receipt);
                using (var main = Process.GetProcessById(request.ProcessId))
                {
                    if (main.StartTime.ToUniversalTime().Ticks != request.ProcessStartUtcTicks ||
                        !string.Equals(main.MainModule.FileName, status.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("IndependentMainIdentityChanged");
                    // Fence the OLD watchdog before terminating its main. The new
                    // recovery enters through the nonce/checkpoint path and creates
                    // a new watchdog session. Old permits cannot launch in parallel.
                    var oldLedger = new FallbackLedgerStore(directory, status.SessionId);
                    ValidateCheckpoint(receipt);
                    main.Kill();
                    if (!main.WaitForExit(5000)) throw new IOException("IndependentMainExitUnproven");
                    if (oldLedger.Exists)
                    {
                        var retired = oldLedger.Read();
                        if (retired.Phase == "OriginalVerifying" && retired.LaunchProcessId == request.ProcessId &&
                            retired.LaunchProcessStartUtcTicks == request.ProcessStartUtcTicks)
                            oldLedger.CompleteOriginalLaunch(retired.Revision, retired.LaunchProcessId,
                                retired.LaunchProcessStartUtcTicks);
                        else if (retired.Phase == "Requested" || retired.Phase == "Yielded")
                            oldLedger.CancelRequest(retired.Revision, retired.Requester);
                    }
                }
                ValidateCheckpoint(receipt);
                _revoked = new EventWaitHandle(false, EventResetMode.ManualReset,
                    "Local\\MTTFTest_RecoveryRevoked_" + receipt.Nonce);
                _attached = new EventWaitHandle(false, EventResetMode.ManualReset,
                    "Local\\MTTFTest_RecoveryAttached_" + receipt.Nonce);
                var arguments = "--epb-recover " + receipt.Nonce + " --wait-parent " + request.ProcessId +
                    " --parent-start-ticks " + request.ProcessStartUtcTicks;
                using (var launched = Process.Start(new ProcessStartInfo(status.ExecutablePath, arguments)
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(status.ExecutablePath) }))
                {
                    return Tuple.Create(launched.Id, launched.StartTime.ToUniversalTime().Ticks);
                }
        }

        private void Verify(string directory)
        {
            if (_verificationEnded) return;
            if (_utcTicks() - _launchedAt > TimeSpan.FromMinutes(10).Ticks)
            { _verificationEnded = true; Report(directory, "VerificationTimedOut;NotRecovered"); return; }
            var now = _timestamp();
            if (now - _lastRead < Stopwatch.Frequency * 5) return;
            _lastRead = now;
            var status = BoundedJson.Read<IndependentFallbackStatus>(IndependentFallbackProtocol.StatusPath(directory));
            if (status.ProcessId != _launchedPid || status.ProcessStartUtcTicks != _launchedStart ||
                _utcTicks() - status.UpdatedUtcTicks > TimeSpan.FromSeconds(5).Ticks) return;
            if (!status.Armed) { _verificationEnded = true; Report(directory, "ReplacementStoppedOrRejected;NotRecovered"); return; }
            var database = Path.Combine(Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)), "index.db");
            var sample = _readDatabase(database, status.Channels);
            if (_verificationIntent == null)
                _verificationIntent = new DatabaseWatchIntent { SessionId = status.SessionId, RunId = status.RunId,
                    RunEpoch = status.RunEpoch, Channels = status.Channels, DatabasePath = sample.DatabasePath,
                    DatabaseCreationUtcTicks = sample.CreationUtcTicks,
                    PeriodMs = Math.Max(1, status.Heartbeat?.ExpectedCyclePeriodMs ?? 15000) };
            _databaseVerifier.BeginVerification(_launchedPid, _launchedStart);
            _databaseVerifier.Observe(_verificationIntent, sample, (long)(now * 1000d / Stopwatch.Frequency));
            var actions = _actionVerifier.Observe(status.Heartbeat, new FallbackLedger { RunId = status.RunId,
                RunEpoch = status.RunEpoch, LaunchProcessId = _launchedPid, LaunchProcessStartUtcTicks = _launchedStart }, now);
            if (_databaseVerifier.RecoveryVerified && actions)
            { _verified = true; _verificationEnded = true; Report(directory, "DatabaseAndActionRecoveryVerified"); }
        }

        private static void ValidateCheckpoint(IndependentFallbackReceipt receipt)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MTTFTest", "unattended-run-checkpoint.json");
            var value = BoundedJson.Read<Dictionary<string, object>>(path);
            if (!value.TryGetValue("Armed", out var armed) || !(armed is bool a) || !a ||
                !value.TryGetValue("RestartPending", out var pending) || !(pending is bool p) || !p ||
                !value.TryGetValue("RunId", out var run) || !Equals(run, receipt.RunId) ||
                !value.TryGetValue("RecoveryNonce", out var nonce) || !Equals(nonce, receipt.Nonce))
                throw new InvalidOperationException("IndependentCheckpointRevokedOrChanged");
        }

        private void Report(string directory, string detail)
        {
            if (detail == _lastDetail) return;
            _lastDetail = detail;
            BoundedJson.Write(Path.Combine(directory, "fallback-independent-status.json"),
                new { UpdatedUtc = DateTime.UtcNow.ToString("O"), RequestId = _request?.Id,
                    Detail = detail, Attempts = _attempts, RecoveryVerified = _verified });
            Console.WriteLine("IndependentFallback " + detail);
        }
        private void ReleaseLease()
        {
            if (_ownsLease) { _lease.ReleaseMutex(); _ownsLease = false; }
        }
        public void Dispose() { ReleaseLease(); _lease?.Dispose(); _revoked?.Dispose(); _attached?.Dispose(); }
    }
}
