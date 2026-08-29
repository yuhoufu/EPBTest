using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Rehydrates the immutable reporter identity for an exact raw failure
    /// request replay.  A named-pipe reconnect necessarily has a new
    /// connection generation (and the Host journal may already have moved
    /// to a later recovery attempt), but those transport changes must not
    /// turn the same correlation/payload into a second durable operation.
    /// The authority still compares the complete canonical tuple; this
    /// helper only reuses the tuple that was durably registered for the
    /// exact raw request identity.
    /// </summary>
    internal sealed class StrictRecoveryFailureReplayIdentity
    {
        internal string OperationId { get; private set; }
        internal string SessionNonce { get; private set; }
        internal int ReporterProcessId { get; private set; }
        internal long ReporterProcessStartUtcTicks { get; private set; }
        internal long ConnectionGeneration { get; private set; }
        internal long RecoveryAttemptGeneration { get; private set; }

        internal static bool TryResolve(
            DurableRelaunchPermitRecord record,
            string requestCorrelationId,
            string requestPayloadSha256,
            out StrictRecoveryFailureReplayIdentity identity)
        {
            identity = null;
            Guid correlation;
            Guid operation = Guid.Empty;
            Guid recordedCorrelation = Guid.Empty;
            if (!Guid.TryParseExact(requestCorrelationId ?? string.Empty, "N", out correlation) ||
                record == null ||
                !Guid.TryParseExact(record.LastFailureCorrelationId ?? string.Empty, "N", out recordedCorrelation) ||
                correlation != recordedCorrelation ||
                !IsSha256(requestPayloadSha256) ||
                !string.Equals(record.LastFailurePayloadSha256, requestPayloadSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(record.LastFailureOperationId ?? string.Empty, "N", out operation) ||
                string.IsNullOrWhiteSpace(record.LastFailureSessionNonce) ||
                record.LastFailureProcessId <= 0 ||
                record.LastFailureProcessStartUtcTicks <= 0 ||
                record.LastFailureConnectionGeneration <= 0 ||
                record.LastFailureAttemptGeneration <= 0)
                return false;

            identity = new StrictRecoveryFailureReplayIdentity
            {
                OperationId = operation.ToString("N"),
                SessionNonce = record.LastFailureSessionNonce,
                ReporterProcessId = record.LastFailureProcessId,
                ReporterProcessStartUtcTicks = record.LastFailureProcessStartUtcTicks,
                ConnectionGeneration = record.LastFailureConnectionGeneration,
                RecoveryAttemptGeneration = record.LastFailureAttemptGeneration
            };
            return true;
        }

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
                   value.All(c => (c >= '0' && c <= '9') ||
                                  (c >= 'A' && c <= 'F') ||
                                  (c >= 'a' && c <= 'f'));
        }
    }

    /// <summary>
    /// Host-facing adapter for the strict V4 authority.  It deliberately
    /// owns the only production approval/lifecycle path.  The few
    /// compatibility-shaped result methods below are projections for
    /// unchanged host control flow; all durable writes go through V4.
    /// </summary>
    internal sealed class StrictHostV4AuthorityAdapter
    {
        private readonly DurableRelaunchAuthorityV4 _authority;
        private readonly string _sessionId;
        private readonly string _sessionNonce;
        private readonly int _sidecarPid;
        private readonly long _sidecarStartTicks;
        private readonly string _executablePath;
        private readonly string _workingDirectory;
        private readonly ConcurrentDictionary<long, DurableLaunchIntentCapability> _capabilities =
            new ConcurrentDictionary<long, DurableLaunchIntentCapability>();
        private readonly ConcurrentDictionary<long, string> _arguments =
            new ConcurrentDictionary<long, string>();

        internal StrictHostV4AuthorityAdapter(
            WatchdogArguments args, int sidecarPid, long sidecarStartTicks)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            _sessionId = args.SessionId;
            _sessionNonce = args.SidecarInstanceNonce;
            _sidecarPid = sidecarPid;
            _sidecarStartTicks = sidecarStartTicks;
            _executablePath = Path.GetFullPath(args.ExecutablePath);
            _workingDirectory = Path.GetDirectoryName(_executablePath) ?? Environment.CurrentDirectory;

            // The controller/runtime owns the one-time bootstrap.  A Host
            // process is only a reader/reconciler: it must never create a
            // primary snapshot, proof, or relaunch authority as a fallback.
            // Missing/corrupt/unproven bootstrap therefore fails closed.
            DurableAuthorityOpenResult open =
                DurableRelaunchAuthorityV4Factory.TryOpenExisting(
                    args.JournalDirectory, args.SessionId);
            if (open == null || !open.Succeeded || open.Authority == null)
                throw new InvalidOperationException("StrictAuthorityUnavailable:" + (open?.Reason ?? "Unknown"));
            _authority = open.Authority;

            // An Intent/Started/Attached record is resumed by read-back only;
            // no process is started from this constructor.
            var resumed = _authority.ResumeLaunchIntent();
            if (resumed != null && resumed.Succeeded && resumed.Capability != null)
                _capabilities[resumed.Record.Generation] = resumed.Capability;
        }

        internal DurableRelaunchPermitRecord Snapshot => Project(_authority.Snapshot);

        internal DurableRelaunchResult ApproveOrGetExisting(DurableRelaunchRequest request)
        {
            var record = _authority.Snapshot;
            var budget = record?.MaximumProcessRelaunches ?? RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit;
            var fingerprint = string.IsNullOrWhiteSpace(request?.Fingerprint) ? "HostRecovery" : request.Fingerprint;
            var operation = MakeOperation(
                "HostApproval:" + fingerprint,
                "HostRecoveryFailure",
                fingerprint,
                request?.ProgressToken,
                request?.ProcessSource,
                request?.RunId,
                request?.RunEpoch ?? 0,
                request?.RecoveryStage,
                budget);
            var decision = _authority.RegisterFailureAndDecide(operation);
            return ProjectDecision(decision);
        }

        internal DurableAuthorityDecisionResult RegisterFailure(RecoveryFailureOperation operation)
        {
            // Failure registration is one strict V4 authority transaction.
            // In particular, do not close a permit and then register a second
            // operation: that split would create a window in which a crash can
            // lose the failure or consume a new launch generation.
            return _authority.RegisterFailureAndDecide(operation);
        }

        internal DurableRelaunchResult BeginLaunch(DurableRelaunchPermitIdentity identity) =>
            BeginLaunch(identity, string.Empty);

        internal DurableRelaunchResult BeginLaunch(DurableRelaunchPermitIdentity identity, string arguments)
        {
            var record = _authority.Snapshot;
            if (!Matches(record, identity)) return Result(false, false, false, "PermitIdentityMismatch", record);
            var executableSha = Sha256File(_executablePath);
            var intent = new DurableLaunchIntent
            {
                SessionId = _sessionId, SessionNonce = _sessionNonce,
                Generation = record.Generation, PermitId = record.PermitId,
                PermitNonce = record.PermitNonce, IntentId = Guid.NewGuid().ToString("N"),
                ExecutablePath = _executablePath, ExecutableSha256 = executableSha,
                Arguments = arguments ?? string.Empty, WorkingDirectory = _workingDirectory,
                LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical,
            };
            intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
            var transition = _authority.PrepareLaunchIntent(intent);
            if (transition == null || !transition.Succeeded || transition.Capability == null)
                return Result(false, false, false, transition?.Reason ?? "LaunchIntentCommitFailed", transition?.Record ?? record,
                    transition?.Status ?? DurableAuthorityTransitionStatus.Unproven);
            _capabilities[record.Generation] = transition.Capability;
            _arguments[record.Generation] = arguments ?? string.Empty;
            return Result(true, false, true, transition.Reason, transition.Record,
                transition.Status);
        }

        internal DurableLaunchIntentCapability GetLaunchCapability(long generation)
        {
            DurableLaunchIntentCapability capability;
            return _capabilities.TryGetValue(generation, out capability) ? capability : null;
        }

        internal bool IsCapabilityCurrent(DurableLaunchIntentCapability capability)
        {
            var record = _authority.Snapshot;
            return capability != null && record != null &&
                   record.State == DurableRelaunchPermitState.LaunchIntent &&
                   record.Generation == capability.Generation &&
                   string.Equals(record.SessionId, capability.SessionId, StringComparison.Ordinal) &&
                   string.Equals(record.PermitId, capability.PermitId, StringComparison.Ordinal) &&
                   string.Equals(record.PermitNonce, capability.PermitNonce, StringComparison.Ordinal) &&
                   string.Equals(record.LaunchIntentId, capability.IntentId, StringComparison.Ordinal) &&
                   !record.LaunchConsumed &&
                   record.LaunchAuthorityRevision == capability.AuthorityRevision &&
                   string.Equals(record.LaunchAuthoritySha256, capability.AuthoritySha256, StringComparison.Ordinal) &&
                   string.Equals(record.LaunchSpecSha256, capability.LaunchSpecSha256, StringComparison.Ordinal);
        }

        internal bool ConsumeLaunchIntent(DurableLaunchIntentCapability capability)
        {
            var transition = _authority.ConsumeLaunchIntent(capability);
            return transition != null && transition.Succeeded;
        }

        internal DurableRelaunchResult CommitStarted(DurableRelaunchPermitIdentity identity, int processId, long processStartUtcTicks)
        {
            DurableLaunchIntentCapability capability;
            if (!_capabilities.TryGetValue(identity?.Generation ?? 0, out capability))
            {
                var resumed = _authority.ResumeLaunchIntent();
                capability = resumed?.Capability;
                if (capability != null) _capabilities[identity.Generation] = capability;
            }
            var transition = _authority.CommitStarted(capability, processId, processStartUtcTicks);
            return Result(transition?.Succeeded == true, false, transition?.Succeeded == true,
                transition?.Reason ?? "StartedCommitFailed", transition?.Record ?? _authority.Snapshot,
                transition?.Status ?? DurableAuthorityTransitionStatus.Unproven);
        }

        internal DurableRelaunchResult CommitAttached(DurableRelaunchPermitIdentity identity, int processId, long processStartUtcTicks)
        {
            DurableLaunchIntentCapability capability;
            _capabilities.TryGetValue(identity?.Generation ?? 0, out capability);
            var record = _authority.Snapshot;
            if (record != null && record.State == DurableRelaunchPermitState.Started &&
                (record.ProcessId != processId || record.ProcessStartUtcTicks != processStartUtcTicks))
                return Result(false, false, false, "ProcessIdentityMismatch", record);
            var transition = _authority.CommitAttached(capability);
            return Result(transition?.Succeeded == true, false, transition?.Succeeded == true,
                transition?.Reason ?? "AttachedCommitFailed", transition?.Record ?? _authority.Snapshot,
                transition?.Status ?? DurableAuthorityTransitionStatus.Unproven);
        }

        internal DurableRelaunchResult CommitRecoveryBatch(
            DurableRelaunchPermitIdentity identity,
            string runId,
            long runEpoch,
            string recoveryStage,
            string progressToken,
            long recoveryCommitGeneration)
        {
            DurableLaunchIntentCapability capability;
            _capabilities.TryGetValue(identity?.Generation ?? 0, out capability);
            var record = _authority.Snapshot;
            var transition = _authority.CommitCommitted(
                capability,
                string.IsNullOrWhiteSpace(runId) ? record?.RunId : runId,
                runEpoch > 0 ? runEpoch : record?.RunEpoch ?? 0,
                string.IsNullOrWhiteSpace(recoveryStage) ? record?.RecoveryStage : recoveryStage,
                string.IsNullOrWhiteSpace(progressToken) ? record?.RecoveryProgressToken : progressToken,
                recoveryCommitGeneration);
            if (transition?.Succeeded == true) _capabilities.TryRemove(identity.Generation, out _);
            return Result(transition?.Succeeded == true, false, transition?.Succeeded == true,
                transition?.Reason ?? "RecoveryCommitFailed", transition?.Record ?? _authority.Snapshot,
                transition?.Status ?? DurableAuthorityTransitionStatus.Unproven);
        }

        internal DurableRelaunchResult RecoverAfterRestart(Func<int, long, DurableRelaunchProcessObservation> processProbe)
        {
            var reconciliation = _authority.ReconcileAfterRestart(processProbe);
            var decision = reconciliation?.Decision;
            return Result(reconciliation?.Decision?.Durable == true, false,
                reconciliation?.ActionAllowed == true,
                reconciliation?.Reason ?? decision?.Reason ?? "ReconcileCompleted",
                decision?.Record ?? _authority.Snapshot,
                MapStatus(decision?.CommitStatus));
        }

        internal DurableRelaunchResult Block(string reason)
        {
            var transition = _authority.BlockCurrent(reason);
            return Result(transition?.Succeeded == true, false, false,
                transition?.Reason ?? "AuthorityBlockFailed", transition?.Record ?? _authority.Snapshot,
                transition?.Status ?? DurableAuthorityTransitionStatus.Unproven);
        }

        internal DurableRelaunchResult Revoke(string reason)
        {
            var transition = _authority.RevokeCurrent(reason);
            if (transition?.Succeeded == true)
            {
                _capabilities.Clear();
                _arguments.Clear();
            }
            return Result(
                transition?.Succeeded == true,
                false,
                false,
                transition?.Reason ?? "AuthorityRevokeFailed",
                transition?.Record ?? _authority.Snapshot,
                transition?.Status ?? DurableAuthorityTransitionStatus.Unproven);
        }

        internal DurableRelaunchResult RejectNoWork(
            DurableRelaunchPermitIdentity identity,
            string reason)
        {
            var transition = _authority.RejectCurrentNoWork(identity, reason);
            if (transition?.Succeeded == true)
            {
                _capabilities.TryRemove(identity?.Generation ?? 0, out _);
                _arguments.TryRemove(identity?.Generation ?? 0, out _);
            }
            return Result(
                transition?.Succeeded == true,
                false,
                false,
                transition?.Reason ?? "RejectedNoWorkCommitFailed",
                transition?.Record ?? _authority.Snapshot,
                transition?.Status ?? DurableAuthorityTransitionStatus.Unproven);
        }

        private RecoveryFailureOperation MakeOperation(string operationSeed, string failureCode, string fingerprint,
            string progress, string source, string runId, long runEpoch, string stage, int budget)
        {
            var seed = Sha256Text(_sessionId + "|" + operationSeed + "|" + (_authority.Snapshot?.ConsecutiveFailures ?? 0).ToString(CultureInfo.InvariantCulture));
            return new RecoveryFailureOperation
            {
                OperationId = seed.Substring(0, 32), SessionId = _sessionId, SessionNonce = _sessionNonce,
                SidecarProcessId = _sidecarPid, SidecarProcessStartUtcTicks = _sidecarStartTicks,
                ConnectionGeneration = 1, RecoveryAttemptGeneration = Math.Max(1, _authority.Snapshot?.ConsecutiveFailures ?? 1),
                RequestCorrelationId = seed.Substring(0, 32), RequestPayloadSha256 = Sha256Text(operationSeed),
                FailureCode = failureCode, FailureFingerprint = fingerprint ?? "HostRecovery",
                Permanent = false, DetailCode = "HostRecoveryFailure",
                RunId = string.IsNullOrWhiteSpace(runId) ? "host-run" : runId,
                RunEpoch = runEpoch > 0 ? runEpoch : 1,
                RecoveryStage = string.IsNullOrWhiteSpace(stage) ? "Recovery" : stage,
                RecoveryProgressToken = string.IsNullOrWhiteSpace(progress) ? "host-progress" : progress,
                RecoveryProcessSource = string.IsNullOrWhiteSpace(source) ? "WatchdogHost" : source,
                DeviceOrChannelGroup = "WatchdogHost", MaximumProcessRelaunches = Math.Max(1, budget)
            };
        }

        private static DurableRelaunchResult ProjectDecision(DurableAuthorityDecisionResult decision)
        {
            return Result(decision?.Durable == true, decision?.Receipt != null &&
                string.Equals(decision.Receipt.Disposition, RecoveryFailureDispositions.RelaunchAlreadyPending, StringComparison.Ordinal),
                decision?.ActionAllowed == true, decision?.Reason ?? "AuthorityDecision", decision?.Record,
                MapStatus(decision?.CommitStatus));
        }

        private static DurableAuthorityTransitionStatus MapStatus(
            DurableAuthorityCommitStatus? status)
        {
            switch (status)
            {
                case DurableAuthorityCommitStatus.Committed:
                case DurableAuthorityCommitStatus.CandidateApplied:
                case DurableAuthorityCommitStatus.ExistingBlocked:
                case DurableAuthorityCommitStatus.DurableBlocked:
                    return DurableAuthorityTransitionStatus.Committed;
                case DurableAuthorityCommitStatus.Busy:
                    return DurableAuthorityTransitionStatus.Busy;
                case DurableAuthorityCommitStatus.Conflict:
                    return DurableAuthorityTransitionStatus.Conflict;
                case DurableAuthorityCommitStatus.Invalid:
                    return DurableAuthorityTransitionStatus.Invalid;
                case DurableAuthorityCommitStatus.ReadFailed:
                    return DurableAuthorityTransitionStatus.Unproven;
                default:
                    return DurableAuthorityTransitionStatus.WriteFailed;
            }
        }

        private static DurableRelaunchResult Result(
            bool succeeded,
            bool existing,
            bool actionAllowed,
            string reason,
            DurableRelaunchAuthorityRecord strict,
            DurableAuthorityTransitionStatus transitionStatus =
                DurableAuthorityTransitionStatus.Committed)
        {
            var blocked = strict != null && (strict.State == DurableRelaunchPermitState.Blocked || strict.State == DurableRelaunchPermitState.Revoked || strict.CircuitOpen);
            return new DurableRelaunchResult
            {
                Succeeded = succeeded, Existing = existing, ActionAllowed = actionAllowed,
                Blocked = blocked, Reason = reason, Record = Project(strict)
                ,TransitionStatus = transitionStatus
            };
        }

        private static DurableRelaunchPermitRecord Project(DurableRelaunchAuthorityRecord value)
        {
            if (value == null) return null;
            return new DurableRelaunchPermitRecord
            {
                SchemaVersion = value.SchemaVersion, SessionId = value.SessionId,
                Generation = value.Generation, PermitId = value.PermitId, State = value.State,
                Fingerprint = value.LastFailureFingerprint, ProgressToken = value.RecoveryProgressToken,
                ProcessSource = value.RecoveryProcessSource, RunId = value.RunId,
                RunEpoch = value.RunEpoch,
                RecoveryStage = value.RecoveryStage, PermitNonce = value.PermitNonce,
                ConsecutiveFailures = value.ConsecutiveFailures, MaximumProcessRelaunches = value.MaximumProcessRelaunches,
                ProcessId = value.ProcessId, ProcessStartUtcTicks = value.ProcessStartUtcTicks,
                RecoveryCommitGeneration = value.RecoveryCommitGeneration,
                LastFailureCode = value.LastFailureCode, LastFailureReason = value.DetailCode,
                LastFailureOperationId = value.LastFailureOperationId, LastFailureCorrelationId = value.LastFailureCorrelationId,
                LastFailurePayloadSha256 = value.LastFailurePayloadSha256, LastFailureFingerprint = value.LastFailureFingerprint,
                LastFailureSessionNonce = value.LastFailureSessionNonce, LastFailureProcessId = value.LastFailureProcessId,
                LastFailureProcessStartUtcTicks = value.LastFailureProcessStartUtcTicks,
                LastFailureConnectionGeneration = value.LastFailureConnectionGeneration,
                LastFailureAttemptGeneration = value.LastFailureAttemptGeneration,
                LastFailureDisposition = value.LastFailureDisposition,
                LastFailureDecisionSequence = value.LastFailureDecisionSequence,
                LastFailureDecisionUtcTicks = value.LastFailureDecisionUtcTicks,
                LastFailurePermanent = value.LastFailurePermanent,
                LastTransitionUtcTicks = value.LastTransitionUtcTicks,
                CircuitOpen = value.CircuitOpen
            };
        }

        private static bool Matches(DurableRelaunchPermitRecord record, DurableRelaunchPermitIdentity identity) =>
            record != null && identity != null && record.Generation == identity.Generation &&
            string.Equals(record.SessionId, identity.SessionId, StringComparison.Ordinal) &&
            string.Equals(record.PermitId, identity.PermitId, StringComparison.Ordinal);

        private static bool Matches(DurableRelaunchAuthorityRecord record, DurableRelaunchPermitIdentity identity) =>
            record != null && identity != null && record.Generation == identity.Generation &&
            string.Equals(record.SessionId, identity.SessionId, StringComparison.Ordinal) &&
            string.Equals(record.PermitId, identity.PermitId, StringComparison.Ordinal);

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToUpperInvariant();
        }

        private static string Sha256Text(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty).ToUpperInvariant();
        }
    }
}
