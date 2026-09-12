using System;
using System.Collections.Generic;
using System.Linq;

namespace MTTFTest.RecoveryControl
{
    // This assembly has framework dependencies only. Guard ships its own copy;
    // no type in this contract may reference Controller, Protocol or a driver.
    public enum RecoveryDesiredState { Run, Paused, Stopped, Completed }
    public enum RecoveryGuardMode { ObserveOnly, RecoverExited, RecoverStalled }
    public enum RecoveryStage { Claim, SafeStop, Retire, Launch, Verify, Complete, Cooldown, Blocked, Cancelled }
    public enum ProcessObservation { Unknown, ExactAlive, Exited }

    public sealed class RecoveryProcessIdentity
    {
        public int ProcessId { get; set; }
        public long StartUtcTicks { get; set; }
        public string ExecutablePath { get; set; }
        public string BootId { get; set; }

        public bool IsValid() => ProcessId > 0 && StartUtcTicks > 0 &&
            !string.IsNullOrWhiteSpace(ExecutablePath) && Guid.TryParse(BootId, out _);

        public bool Matches(RecoveryProcessIdentity other) => other != null &&
            ProcessId == other.ProcessId && StartUtcTicks == other.StartUtcTicks &&
            string.Equals(BootId, other.BootId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class RecoveryAuthorizationToken
    {
        public string InstallationId { get; set; }
        public string AuthorizationId { get; set; }
        public long IntentVersion { get; set; }
        public long TakeoverEpoch { get; set; }
    }

    public sealed class RecoveryRunIntent
    {
        public string AuthorizationId { get; set; }
        public string RootRunId { get; set; }
        public string RunId { get; set; }
        public string ConfigurationIdentity { get; set; }
        public long IntentVersion { get; set; }
        public RecoveryDesiredState DesiredState { get; set; }
        // The paused main still owns the notification handles from its Run
        // version. Preserve that identity until an explicit manual continuation.
        public long PausedSourceIntentVersion { get; set; }
        public long IssuedUtcTicks { get; set; }
        public string Reason { get; set; }
        public RecoveryProcessIdentity MainProcess { get; set; }
        public string WatchdogSessionId { get; set; }
        // Issued only while the exact original process is alive. This supports
        // the existing Watchdog's first-learning restart, not Guard takeover.
        public string LegacyStartupAdmissionSessionId { get; set; }
        public string LegacyStartupAdmissionBootId { get; set; }
    }

    public sealed class RecoveryChannelProgress
    {
        public int Channel { get; set; }
        public bool Eligible { get; set; }
        public bool Completed { get; set; }
        public bool PermanentlyIsolated { get; set; }
        public bool ManuallyExcluded { get; set; }
        public long SampleSequence { get; set; }
        public long SampleGeneration { get; set; }
        public long ControlSequence { get; set; }
        public long PersistedSequence { get; set; }
        public string Stage { get; set; }
        public long StageStartedUtcTicks { get; set; }
        public long StageDeadlineUtcTicks { get; set; }
    }

    public sealed class RecoveryObservationSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public RecoveryAuthorizationToken Authorization { get; set; }
        public string RunId { get; set; }
        public string ConfigurationIdentity { get; set; }
        public RecoveryProcessIdentity MainProcess { get; set; }
        public long Sequence { get; set; }
        public long PublishedUtcTicks { get; set; }
        public long SourceUtcTicks { get; set; }
        public long SourceVersion { get; set; }
        public bool SourceAvailable { get; set; }
        public string Stage { get; set; }
        public RecoveryChannelProgress[] Channels { get; set; } = Array.Empty<RecoveryChannelProgress>();
    }

    public sealed class RecoveryGuardObservation
    {
        public string AuthorizationId { get; set; }
        public string BootId { get; set; }
        public bool Established { get; set; }
        public bool Expired { get; set; }
        public bool CrossBootAdmission { get; set; }
        public long CrossBootAdmissionAnchorUtcTicks { get; set; }
        public long LastGuardUtcTicks { get; set; }
        public long LastTrustedRunUtcTicks { get; set; }
        public long LastVerifiedBusinessCommitUtcTicks { get; set; }
        public long LastSnapshotSequence { get; set; }
        public long SuspectSinceUtcTicks { get; set; }
        public int SuspectCount { get; set; }
        public RecoveryObservationSnapshot LastTrustedSnapshot { get; set; }
        public RecoveryObservationSnapshot LastSnapshot { get; set; }
        public List<RecoveryObservedChannel> ChannelClocks { get; set; } = new List<RecoveryObservedChannel>();
    }

    public sealed class RecoveryObservedChannel
    {
        public int Channel { get; set; }
        public long SampleProgressUtcTicks { get; set; }
        public long ControlProgressUtcTicks { get; set; }
        public long PersistedProgressUtcTicks { get; set; }
    }

    public sealed class RecoveryTakeoverTransaction
    {
        public string TransactionId { get; set; }
        public string AuthorizationId { get; set; }
        public long IntentVersion { get; set; }
        public long Epoch { get; set; }
        public RecoveryProcessIdentity Owner { get; set; }
        public long LeaseUntilUtcTicks { get; set; }
        public long WorkerRetirementRequestedUtcTicks { get; set; }
        public RecoveryStage Stage { get; set; }
        public long StageStartedUtcTicks { get; set; }
        public long StageDeadlineUtcTicks { get; set; }
        public long LastProgressUtcTicks { get; set; }
        public int CircuitCooldownSeconds { get; set; }
        public long NextAttemptUtcTicks { get; set; }
        public string Reason { get; set; }
        public bool Active => Stage != RecoveryStage.Complete && Stage != RecoveryStage.Cancelled;
        public bool OwnershipReleased { get; set; }
        public long VerificationStartedUtcTicks { get; set; }
        public int VerificationEvidenceMaxAgeSeconds { get; set; }
        public long OrphanVerificationUntilUtcTicks { get; set; }
        public int VerifiedBusinessCommitCount { get; set; }
        public long ActionEpoch { get; set; }
        public string SafetyAuthorityId { get; set; }
        public string LaunchOperationId { get; set; }
        public RecoveryStage? RetryActionStage { get; set; }
        public long RetryActionEpoch { get; set; }
    }

    public sealed class RecoveryLaunchFence
    {
        public int SchemaVersion { get; set; } = 1;
        public bool IsRecovery { get; set; }
        public RecoveryAuthorizationToken Authorization { get; set; }

        public string Serialize() => new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(this);

        public static RecoveryLaunchFence Parse(string json, bool expectedRecovery)
        {
            if (string.IsNullOrEmpty(json)) return null;
            if (json.Length > 4096) throw new ArgumentException("RecoveryLaunchFenceOversized");
            var fence = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<RecoveryLaunchFence>(json);
            if (fence?.SchemaVersion != 1 || fence.IsRecovery != expectedRecovery || fence.Authorization == null ||
                !Guid.TryParseExact(fence.Authorization.InstallationId, "N", out var installation) || installation == Guid.Empty ||
                fence.Authorization.TakeoverEpoch < 0 ||
                (expectedRecovery && (fence.Authorization.IntentVersion < 1 ||
                 !Guid.TryParseExact(fence.Authorization.AuthorizationId, "N", out var authorization) || authorization == Guid.Empty)))
                throw new ArgumentException("RecoveryLaunchFenceInvalid");
            return fence;
        }
    }

    public sealed class RecoveryLaunchReservation
    {
        public string FailureEvidence { get; set; }
        public string OperationId { get; set; }
        public RecoveryAuthorizationToken Authorization { get; set; }
        public long ExpiresUtcTicks { get; set; }
        public string State { get; set; }
        public RecoveryProcessIdentity Process { get; set; }
    }

    public sealed class RecoverySessionRollover
    {
        public string OperationId { get; set; }
        public string AuthorizationId { get; set; }
        public long IntentVersion { get; set; }
        public string TransactionId { get; set; }
        public long Epoch { get; set; }
        public string PreviousSessionId { get; set; }
        public string NextSessionId { get; set; }
        public string SourceAuthoritySha256 { get; set; }
        public string PreviousSafetyAuthorityId { get; set; }
        public string PreviousLaunchOperationId { get; set; }
        public string PreviousRunId { get; set; }
        public string RootRunId { get; set; }
        public string ConfigurationIdentity { get; set; }
        public RecoveryProcessIdentity PreviousMain { get; set; }
        public RecoveryProcessIdentity SessionHost { get; set; }
        public string State { get; set; }
    }

    public sealed class RecoveryExecutionWorker
    {
        public RecoveryProcessIdentity Owner { get; set; }
        public long LastPulseUtcTicks { get; set; }
        public long LeaseUntilUtcTicks { get; set; }
        public long RetirementRequestedUtcTicks { get; set; }
    }

    public sealed class RecoveryControlState
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public string InstallationId { get; set; }
        public string BenchId { get; set; }
        public string MainExecutablePath { get; set; }
        public long LastTakeoverEpoch { get; set; }
        public RecoveryRunIntent Intent { get; set; }
        public RecoveryGuardObservation Observation { get; set; }
        public RecoveryTakeoverTransaction Transaction { get; set; }
        public RecoverySessionRollover SessionRollover { get; set; }
        public RecoveryExecutionWorker ExecutionWorker { get; set; }
        public List<RecoveryLaunchReservation> Launches { get; set; } = new List<RecoveryLaunchReservation>();

        public RecoveryAuthorizationToken Token() => Intent == null ? null : new RecoveryAuthorizationToken
        {
            InstallationId = InstallationId,
            AuthorizationId = Intent.AuthorizationId,
            IntentVersion = Intent.IntentVersion,
            TakeoverEpoch = LastTakeoverEpoch
        };

        public bool Matches(RecoveryAuthorizationToken token) => token != null && Intent != null &&
            InstallationId == token.InstallationId && Intent.AuthorizationId == token.AuthorizationId &&
            Intent.IntentVersion == token.IntentVersion && LastTakeoverEpoch == token.TakeoverEpoch;

        // Reconciliation only: this must never authorize output or a new launch.
        public bool MatchesBoundTerminalLaunch(RecoveryLaunchReservation launch) =>
            Intent?.DesiredState == RecoveryDesiredState.Run && Transaction != null &&
            !Transaction.OwnershipReleased && Transaction.Epoch == LastTakeoverEpoch &&
            Transaction.AuthorizationId == Intent.AuthorizationId && Transaction.IntentVersion == Intent.IntentVersion &&
            Transaction.ActionEpoch > 0 && Transaction.ActionEpoch <= Transaction.Epoch &&
            launch?.Authorization != null && !string.IsNullOrEmpty(launch.OperationId) &&
            Transaction.LaunchOperationId == launch.OperationId &&
            InstallationId == launch.Authorization.InstallationId &&
            Intent.AuthorizationId == launch.Authorization.AuthorizationId && Intent.IntentVersion == launch.Authorization.IntentVersion &&
            Transaction.ActionEpoch == launch.Authorization.TakeoverEpoch &&
            ((launch.State == "Exited" && launch.Process?.IsValid() == true) ||
             (launch.State == "StartFailed" && launch.Process == null && launch.FailureEvidence == "ReservationExpiredBeforeConsumption"));
    }

    public sealed class RecoveryGuardSettings
    {
        // Experimental commissioning settings, not hardware acceptance limits.
        public int SchemaVersion { get; set; } = 1;
        public RecoveryGuardMode Mode { get; set; } = RecoveryGuardMode.ObserveOnly;
        public int ScanSeconds { get; set; } = 60;
        public int SnapshotMaxAgeSeconds { get; set; } = 120;
        public int BusinessStallSeconds { get; set; } = 600;
        public int FirstAdmissionSeconds { get; set; } = 900;
        public int SupervisionExpirySeconds { get; set; } = 3600;
        public int ConfirmationCount { get; set; } = 2;
        public int StageTimeoutSeconds { get; set; } = 120;
        public int VerificationTimeoutSeconds { get; set; } = 300;
        public int OwnerLeaseSeconds { get; set; } = 180;
        public int CooldownSeconds { get; set; } = 600;

        public void Validate()
        {
            if (SchemaVersion != 1 || !Enum.IsDefined(typeof(RecoveryGuardMode), Mode) ||
                ScanSeconds < 1 || ScanSeconds > 300 || SnapshotMaxAgeSeconds < ScanSeconds ||
                ConfirmationCount < 2 || ConfirmationCount > 10 || BusinessStallSeconds < SnapshotMaxAgeSeconds ||
                FirstAdmissionSeconds <= BusinessStallSeconds + ScanSeconds * ConfirmationCount ||
                SupervisionExpirySeconds != 3600 || FirstAdmissionSeconds > SupervisionExpirySeconds ||
                StageTimeoutSeconds < 10 || StageTimeoutSeconds > 1800 ||
                VerificationTimeoutSeconds <= ScanSeconds * 3L || VerificationTimeoutSeconds > 1800 ||
                OwnerLeaseSeconds < ScanSeconds * 2 || OwnerLeaseSeconds > 1800 ||
                CooldownSeconds < 1 || CooldownSeconds > 86400)
                throw new ArgumentException("RecoveryGuardSettingsInvalid");
        }
    }

    public sealed class RecoveryDecision
    {
        public string Code { get; set; }
        public bool CanClaim { get; set; }
        public bool CanContinue { get; set; }
        public string Detail { get; set; }
    }
}
