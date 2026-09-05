using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed class RecoveryLaunchRejectedNoWorkException : InvalidOperationException
    {
        internal RecoveryLaunchRejectedNoWorkException(string message) : base(message) { }
    }

    internal sealed class WatchdogVerifiedActiveRun
    {
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public int[] RecoveryEligibleChannels { get; set; } = Array.Empty<int>();
        public int[] CompletedChannels { get; set; } = Array.Empty<int>();
        public int[] ManuallyDisabledChannels { get; set; } = Array.Empty<int>();
        public int[] PermanentAlarmedChannels { get; set; } = Array.Empty<int>();
        public long HeartbeatSequence { get; set; }
        public string CheckpointRunId { get; set; }
        public long CheckpointRunEpoch { get; set; }
        public long CheckpointRevision { get; set; }
        public string CheckpointSha256 { get; set; }
        public string CapturedUtc { get; set; }
    }

    /// <summary>
    /// Minimal, forward-compatible view of the main application's durable
    /// unattended checkpoint.  JavaScriptSerializer ignores fields owned by
    /// newer checkpoint schemas, so the sidecar can make the final launch
    /// authorization decision without taking a binary dependency on the UI.
    /// </summary>
    internal sealed class DurableCheckpointLaunchObservation
    {
        public long Revision { get; set; }
        public bool Armed { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string WatchdogSessionId { get; set; }
    }

    internal enum RecoveryBootstrapObservation
    {
        None = 0,
        RejectedNoWork = 1,
        StartupFailed = 2
    }

    internal enum RecoveryFailureStageKind
    {
        SafetyPrerequisite = 0,
        MainLaunch = 1,
        MainAttach = 2,
        CheckpointResume = 3,
        FirstCycleCommit = 4
    }

    internal enum WatchdogCloseFenceAction
    {
        None = 0,
        SuppressRelaunch = 1,
        TerminateSession = 2
    }

    internal sealed class WatchdogJournal
    {
        public int SchemaVersion { get; set; } = WatchdogJournalPolicy.CurrentSchemaVersion;
        public string SessionId { get; set; }
        public string ExecutablePath { get; set; }
        public string PipeName { get; set; }
        public int CurrentPid { get; set; }
        public long CurrentProcessStartUtcTicks { get; set; }
        public int RecoveryAttempt { get; set; }
        public int ConsecutiveStartupFailures { get; set; }
        public int SafetyPrerequisiteFailureCount { get; set; }
        public int MainLaunchFailureCount { get; set; }
        public int MainAttachFailureCount { get; set; }
        public int CheckpointResumeFailureCount { get; set; }
        public int FirstCycleCommitFailureCount { get; set; }
        public string LastSafetyPrerequisiteFailureKey { get; set; }
        public string LastMainLaunchFailureKey { get; set; }
        public string LastMainAttachFailureKey { get; set; }
        public string LastCheckpointResumeFailureKey { get; set; }
        public string LastFirstCycleCommitFailureKey { get; set; }
        public int SafetyPrerequisiteRetryAttempt { get; set; }
        public long SafetyPrerequisiteFirstFailureUtcTicks { get; set; }
        public long SafetyPrerequisiteNextRetryUtcTicks { get; set; }
        public long RelaunchGeneration { get; set; }
        public int CircuitProbeAttempt { get; set; }
        public bool RecoveryBlocked { get; set; }
        public string RecoveryFailureCode { get; set; }
        public bool RecoveryFailurePermanent { get; set; }
        public string RecoveryFailureFingerprint { get; set; }
        public string RootCode { get; set; }
        public string DeviceOrChannelGroup { get; set; }
        public string RunId { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryProgressToken { get; set; }
        public string RecoveryProcessSource { get; set; }
        public int RecoveryFailureMaxProcessRelaunches { get; set; }
        // Schema4 durable relaunch permit projection.  The in-memory legacy
        // counters below remain only as compatibility/audit mirrors; the
        // coordinator is the launch authority.
        public string RelaunchState { get; set; }
        public long RelaunchPermitGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
        public string RelaunchPermitNonce { get; set; }
        public string RelaunchFingerprint { get; set; }
        public string RelaunchProgressToken { get; set; }
        public string RelaunchProcessSource { get; set; }
        public string RelaunchRunId { get; set; }
        public string RelaunchRecoveryStage { get; set; }
        public int RelaunchProcessId { get; set; }
        public long RelaunchProcessStartUtcTicks { get; set; }
        public int RelaunchConsecutiveFailures { get; set; }
        public int RelaunchMaximumProcessRelaunches { get; set; }
        public long RelaunchRecoveryCommitGeneration { get; set; }
        public string RelaunchFailureCode { get; set; }
        public string RelaunchFailureReason { get; set; }
        public long RecoveryFirstFailureUtcTicks { get; set; }
        public long RecoveryLastFailureUtcTicks { get; set; }
        public long RecoveryBlockedUtcTicks { get; set; }
        public long LastRecoveryBatchCommitGeneration { get; set; }
        public string LastRecoveryCommitRunId { get; set; }
        public bool ManualStopRequested { get; set; }
        public string State { get; set; }
        public string LastReason { get; set; }
        public long LastHeartbeatSequence { get; set; }
        public long LastHeartbeatAckSequence { get; set; }
        public long LastHeartbeatUtcTicks { get; set; }
        public bool OrphanPauseTriggered { get; set; }
        public bool PowerDisableTriggered { get; set; }
        public bool RecoveryChannelIntentFrozen { get; set; }
        public int[] FrozenExcludedChannels { get; set; } = Array.Empty<int>();
        public int FrozenExcludedSourceProcessId { get; set; }
        public long FrozenExcludedSourceProcessStartUtcTicks { get; set; }
        public string FrozenExcludedSourceRunId { get; set; }
        public long FrozenExcludedSourceRunEpoch { get; set; }
        public string UpdatedUtc { get; set; }
        public string StartedUtc { get; set; }
        public long EventSequence { get; set; }
        public long DroppedEventCount { get; set; }
        public WatchdogHeartbeat LastHeartbeat { get; set; }
        public WatchdogVerifiedActiveRun LastVerifiedActiveRun { get; set; }
        public WatchdogCheckpointMirror LastCheckpointMirror { get; set; }
    }

    internal sealed class FrozenChannelIntentResolution
    {
        internal bool Succeeded { get; set; }
        internal bool NewlyFrozen { get; set; }
        internal int[] ExcludedChannels { get; set; } = Array.Empty<int>();
        internal string FailureReason { get; set; }
    }

    /// <summary>
    /// Freezes the recovery channel intent from the last verified initial
    /// process heartbeat. Recovery children can consume this evidence but can
    /// never redefine it, including by reporting every channel disabled.
    /// </summary>
    internal static class WatchdogRecoveryChannelIntentPolicy
    {
        internal static bool TryCaptureLastVerifiedActiveRun(
            WatchdogJournal journal,
            WatchdogHeartbeat heartbeat,
            bool identityValidated)
        {
            if (journal == null || heartbeat == null || !identityValidated ||
                !heartbeat.RunActive || string.IsNullOrWhiteSpace(heartbeat.RunId) ||
                heartbeat.RunEpoch <= 0 ||
                !string.Equals(
                    heartbeat.RecoveryProcessSource,
                    RecoveryFailurePolicy.InitialProcessSource,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var checkpoint = journal.LastCheckpointMirror;
            var checkpointMatches = checkpoint != null && checkpoint.Armed &&
                                    string.Equals(
                                        checkpoint.RunId,
                                        heartbeat.RunId,
                                        StringComparison.OrdinalIgnoreCase) &&
                                    checkpoint.RunEpoch == heartbeat.RunEpoch &&
                                    (string.IsNullOrWhiteSpace(checkpoint.SessionId) ||
                                     string.Equals(
                                         checkpoint.SessionId,
                                         journal.SessionId,
                                         StringComparison.Ordinal));
            var selected = checkpointMatches
                ? NormalizeChannels(checkpoint.SelectedChannels)
                : NormalizeChannels(
                    (heartbeat.EnabledChannels ?? Array.Empty<int>())
                    .Concat(heartbeat.RecoveryEligibleChannels ?? Array.Empty<int>())
                    .Concat(heartbeat.CompletedChannels ?? Array.Empty<int>())
                    .Concat(heartbeat.ManuallyDisabledChannels ?? Array.Empty<int>())
                    .Concat(heartbeat.PermanentAlarmedChannels ?? Array.Empty<int>()));
            var previous = journal.LastVerifiedActiveRun;
            var changed = previous == null ||
                          !string.Equals(previous.RunId, heartbeat.RunId, StringComparison.OrdinalIgnoreCase) ||
                          previous.RunEpoch != heartbeat.RunEpoch ||
                          previous.ProcessId != heartbeat.ProcessId ||
                          previous.ProcessStartUtcTicks != heartbeat.ProcessStartUtcTicks ||
                          !NormalizeChannels(previous.SelectedChannels).SequenceEqual(selected) ||
                          !NormalizeChannels(previous.RecoveryEligibleChannels).SequenceEqual(
                              NormalizeChannels(heartbeat.RecoveryEligibleChannels)) ||
                          !NormalizeChannels(previous.CompletedChannels).SequenceEqual(
                              NormalizeChannels(heartbeat.CompletedChannels)) ||
                          !NormalizeChannels(previous.ManuallyDisabledChannels).SequenceEqual(
                              NormalizeChannels(heartbeat.ManuallyDisabledChannels)) ||
                          !NormalizeChannels(previous.PermanentAlarmedChannels).SequenceEqual(
                              NormalizeChannels(heartbeat.PermanentAlarmedChannels)) ||
                          !string.Equals(
                              previous.CheckpointSha256,
                              checkpointMatches ? checkpoint.Sha256 : string.Empty,
                              StringComparison.OrdinalIgnoreCase);
            journal.LastVerifiedActiveRun = new WatchdogVerifiedActiveRun
            {
                RunId = heartbeat.RunId,
                RunEpoch = heartbeat.RunEpoch,
                ProcessId = heartbeat.ProcessId,
                ProcessStartUtcTicks = heartbeat.ProcessStartUtcTicks,
                SelectedChannels = selected,
                RecoveryEligibleChannels = NormalizeChannels(heartbeat.RecoveryEligibleChannels),
                CompletedChannels = NormalizeChannels(heartbeat.CompletedChannels),
                ManuallyDisabledChannels = NormalizeChannels(heartbeat.ManuallyDisabledChannels),
                PermanentAlarmedChannels = NormalizeChannels(heartbeat.PermanentAlarmedChannels),
                HeartbeatSequence = heartbeat.Sequence,
                CheckpointRunId = checkpointMatches ? checkpoint.RunId : string.Empty,
                CheckpointRunEpoch = checkpointMatches ? checkpoint.RunEpoch : 0,
                CheckpointRevision = checkpointMatches ? checkpoint.Revision : 0,
                CheckpointSha256 = checkpointMatches ? checkpoint.Sha256 : string.Empty,
                CapturedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            return changed;
        }

        internal static FrozenChannelIntentResolution ResolveAndPersist(
            WatchdogJournal journal,
            Func<bool> persistSynchronously)
        {
            var result = Resolve(journal);
            if (!result.Succeeded || !result.NewlyFrozen)
                return result;

            var persisted = false;
            try
            {
                persisted = persistSynchronously != null &&
                            persistSynchronously();
            }
            catch
            {
                persisted = false;
            }
            if (persisted) return result;
            return Failed("FrozenExcludedChannelsPersistenceFailed");
        }

        internal static FrozenChannelIntentResolution Resolve(WatchdogJournal journal)
        {
            if (journal == null)
                return Failed("FrozenExcludedChannelsJournalMissing");

            if (journal.RecoveryChannelIntentFrozen)
            {
                var frozen = journal.FrozenExcludedChannels;
                if (frozen == null ||
                    frozen.Any(channel => channel < 1 || channel > 12) ||
                    !frozen.SequenceEqual(frozen.Distinct().OrderBy(channel => channel)) ||
                    journal.FrozenExcludedSourceProcessId <= 0 ||
                    journal.FrozenExcludedSourceProcessStartUtcTicks <= 0 ||
                    string.IsNullOrWhiteSpace(journal.FrozenExcludedSourceRunId) ||
                    journal.FrozenExcludedSourceRunEpoch <= 0)
                    return Failed("FrozenExcludedChannelsEvidenceInvalid");

                return Succeeded(frozen, newlyFrozen: false);
            }

            WatchdogHeartbeat heartbeat;
            if (journal.LastVerifiedActiveRun != null)
            {
                heartbeat = BuildVerifiedHeartbeat(journal.LastVerifiedActiveRun);
                if (heartbeat == null)
                    return Failed("LastVerifiedActiveRunIdentityInvalid");
            }
            else
            {
                heartbeat = GetValidInitialHeartbeat(journal.LastHeartbeat) ??
                            BuildCheckpointFallback(journal);
            }
            if (heartbeat == null)
                return Failed("FrozenExcludedChannelsInitialHeartbeatMissing");
            if (journal.LastVerifiedActiveRun == null &&
                (heartbeat.ProcessId != journal.CurrentPid ||
                 heartbeat.ProcessStartUtcTicks != journal.CurrentProcessStartUtcTicks))
                return Failed("FrozenExcludedChannelsInitialIdentityMismatch");
            if (!string.Equals(
                    heartbeat.RecoveryProcessSource,
                    RecoveryFailurePolicy.InitialProcessSource,
                    StringComparison.OrdinalIgnoreCase))
                return Failed("FrozenExcludedChannelsInitialSourceInvalid");
            if (string.IsNullOrWhiteSpace(heartbeat.RunId) || heartbeat.RunEpoch <= 0)
                return Failed("FrozenExcludedChannelsRunIdentityMissing");
            if (string.Equals(
                    journal.RelaunchState,
                    DurableRelaunchPermitState.LaunchIntent.ToString(),
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    journal.RelaunchState,
                    DurableRelaunchPermitState.Started.ToString(),
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    journal.RelaunchState,
                    DurableRelaunchPermitState.Attached.ToString(),
                    StringComparison.OrdinalIgnoreCase))
                return Failed("FrozenExcludedChannelsMissingAfterRecoveryStarted");

            var excluded = (heartbeat.ManuallyDisabledChannels ?? Array.Empty<int>())
                .Concat(heartbeat.CompletedChannels ?? Array.Empty<int>())
                .Concat(heartbeat.PermanentAlarmedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            journal.RecoveryChannelIntentFrozen = true;
            journal.FrozenExcludedChannels = excluded;
            journal.FrozenExcludedSourceProcessId = heartbeat.ProcessId;
            journal.FrozenExcludedSourceProcessStartUtcTicks =
                heartbeat.ProcessStartUtcTicks;
            journal.FrozenExcludedSourceRunId = heartbeat.RunId;
            journal.FrozenExcludedSourceRunEpoch = heartbeat.RunEpoch;
            return Succeeded(excluded, newlyFrozen: true);
        }

        private static WatchdogHeartbeat BuildVerifiedHeartbeat(
            WatchdogVerifiedActiveRun activeRun)
        {
            if (activeRun == null || string.IsNullOrWhiteSpace(activeRun.RunId) ||
                activeRun.RunEpoch <= 0 || activeRun.ProcessId <= 0 ||
                activeRun.ProcessStartUtcTicks <= 0)
                return null;
            return new WatchdogHeartbeat
            {
                ProcessId = activeRun.ProcessId,
                ProcessStartUtcTicks = activeRun.ProcessStartUtcTicks,
                RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                RunId = activeRun.RunId,
                RunEpoch = activeRun.RunEpoch,
                RunActive = true,
                CompletedChannels = NormalizeChannels(activeRun.CompletedChannels),
                ManuallyDisabledChannels = NormalizeChannels(activeRun.ManuallyDisabledChannels),
                PermanentAlarmedChannels = NormalizeChannels(activeRun.PermanentAlarmedChannels)
            };
        }

        private static WatchdogHeartbeat GetValidInitialHeartbeat(
            WatchdogHeartbeat heartbeat)
        {
            return heartbeat != null && heartbeat.RunActive &&
                   !string.IsNullOrWhiteSpace(heartbeat.RunId) &&
                   heartbeat.RunEpoch > 0
                ? heartbeat
                : null;
        }

        private static WatchdogHeartbeat BuildCheckpointFallback(WatchdogJournal journal)
        {
            var checkpoint = journal?.LastCheckpointMirror;
            if (checkpoint == null || !checkpoint.Armed ||
                string.IsNullOrWhiteSpace(checkpoint.RunId) || checkpoint.RunEpoch <= 0 ||
                journal.CurrentPid <= 0 || journal.CurrentProcessStartUtcTicks <= 0 ||
                (!string.IsNullOrWhiteSpace(checkpoint.SessionId) &&
                 !string.Equals(checkpoint.SessionId, journal.SessionId, StringComparison.Ordinal)))
                return null;
            return new WatchdogHeartbeat
            {
                ProcessId = journal.CurrentPid,
                ProcessStartUtcTicks = journal.CurrentProcessStartUtcTicks,
                RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                RunId = checkpoint.RunId,
                RunEpoch = checkpoint.RunEpoch,
                RunActive = true
            };
        }

        private static int[] NormalizeChannels(IEnumerable<int> channels)
        {
            return (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        private static FrozenChannelIntentResolution Succeeded(
            int[] excludedChannels,
            bool newlyFrozen)
        {
            return new FrozenChannelIntentResolution
            {
                Succeeded = true,
                NewlyFrozen = newlyFrozen,
                ExcludedChannels = (excludedChannels ?? Array.Empty<int>()).ToArray()
            };
        }

        private static FrozenChannelIntentResolution Failed(string reason)
        {
            return new FrozenChannelIntentResolution
            {
                Succeeded = false,
                FailureReason = reason
            };
        }
    }

    internal sealed class WatchdogArguments
    {
        public int ParentPid;
        public long ParentStartTicks;
        public string SessionId;
        public string PipeName;
        public string ExecutablePath;
        public string JournalDirectory;
        public string SidecarInstanceNonce;
        public WatchdogJournalPolicy JournalPolicy;

        public static WatchdogArguments Parse(string[] args)
        {
            string Read(string name)
            {
                for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return string.Empty;
            }
            int.TryParse(Read("--parent-pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid);
            long.TryParse(Read("--parent-start-ticks"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks);
            var result = new WatchdogArguments
            {
                ParentPid = pid,
                ParentStartTicks = ticks,
                SessionId = Read("--session"),
                PipeName = Read("--pipe"),
                ExecutablePath = Read("--executable"),
                JournalDirectory = Read("--journal-directory"),
                SidecarInstanceNonce = Read("--sidecar-instance-nonce"),
                JournalPolicy = new WatchdogJournalPolicy
                {
                    RetentionDays = ReadInt(Read("--journal-retention-days"), WatchdogJournalPolicy.DefaultRetentionDays),
                    RetainSessionCount = ReadInt(Read("--journal-retain-sessions"), WatchdogJournalPolicy.DefaultRetainSessionCount),
                    MaxTotalBytes = ReadLong(Read("--journal-max-total-bytes"), WatchdogJournalPolicy.DefaultMaxTotalBytes),
                    MaxSessionBytes = ReadLong(Read("--journal-max-session-bytes"), WatchdogJournalPolicy.DefaultMaxSessionBytes),
                    HeartbeatCheckpointSeconds = ReadInt(Read("--journal-heartbeat-checkpoint-seconds"), WatchdogJournalPolicy.DefaultHeartbeatCheckpointSeconds),
                    EmergencySpoolMaxBytes = ReadLong(Read("--journal-emergency-spool-max-bytes"), WatchdogJournalPolicy.DefaultEmergencySpoolMaxBytes)
                }
            };
            if (pid <= 0 || ticks <= 0 || string.IsNullOrWhiteSpace(result.SessionId) ||
                string.IsNullOrWhiteSpace(result.PipeName) || string.IsNullOrWhiteSpace(result.ExecutablePath) ||
                string.IsNullOrWhiteSpace(result.JournalDirectory) ||
                !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                    result.SidecarInstanceNonce))
                throw new ArgumentException("Watchdog startup arguments are incomplete.");
            result.ExecutablePath = Path.GetFullPath(result.ExecutablePath);
            result.JournalDirectory = WatchdogJournalPaths.ValidateProjectDirectory(result.JournalDirectory);
            result.JournalPolicy.Normalize();
            return result;
        }

        internal static string ReadRaw(string[] args, string name)
        {
            for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return string.Empty;
        }

        private static int ReadInt(string value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed : fallback;

        private static long ReadLong(string value, long fallback) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed : fallback;
    }

    internal sealed class AutomaticTakeoverPipelineResult
    {
        internal bool Succeeded { get; set; }
        internal long PermitGeneration { get; set; }
        internal TakeoverTransactionStage Stage { get; set; }
        internal string Failure { get; set; }
    }

    /// <summary>
    /// The production automatic-takeover stage driver.  Hardware/process
    /// actions are injected, but ordering and coordinator transitions are not;
    /// tests therefore exercise the same Dump→Permit→Terminate→Relaunch path
    /// used by WatchdogHost.TakeoverAsync.
    /// </summary>
    internal static class AutomaticTakeoverStageExecutor
    {
        internal static async Task<AutomaticTakeoverPipelineResult> ExecuteAsync(
            TakeoverTransactionCoordinator coordinator,
            TakeoverTransactionLease lease,
            Func<bool> isAuthorized,
            Func<Task> captureDump,
            Func<long> approvePermit,
            Func<bool> terminateOldProcess,
            Func<long, Task> relaunch,
            Action<TakeoverTransactionStage, TakeoverTransactionStage, string> rejected)
        {
            if (coordinator == null || lease == null || isAuthorized == null ||
                captureDump == null || approvePermit == null || relaunch == null)
                throw new ArgumentNullException("AutomaticTakeoverPipelinePort");

            AutomaticTakeoverPipelineResult Fail(
                TakeoverTransactionStage requested,
                string failure)
            {
                rejected?.Invoke(lease.Stage, requested, failure);
                return new AutomaticTakeoverPipelineResult
                {
                    Succeeded = false,
                    Stage = lease.Stage,
                    Failure = failure ?? string.Empty
                };
            }

            if (!isAuthorized())
                return Fail(TakeoverTransactionStage.DumpCapture, "AuthorityRevokedBeforeDump");
            if (!coordinator.TryAdvance(lease, TakeoverTransactionStage.DumpCapture))
                return Fail(TakeoverTransactionStage.DumpCapture, "StageRejected");
            await captureDump().ConfigureAwait(false);

            if (!isAuthorized())
                return Fail(TakeoverTransactionStage.RelaunchPermit, "AuthorityRevokedAfterDump");
            if (!coordinator.TryExecute(
                    lease,
                    TakeoverTransactionStage.RelaunchPermit,
                    approvePermit,
                    out var permitGeneration) ||
                permitGeneration <= 0)
                return Fail(TakeoverTransactionStage.RelaunchPermit, "PermitNotConsumable");

            if (!isAuthorized())
                return Fail(TakeoverTransactionStage.ProcessTermination, "AuthorityRevokedBeforeTermination");
            if (terminateOldProcess != null &&
                (!coordinator.TryExecute(
                     lease,
                     TakeoverTransactionStage.ProcessTermination,
                     terminateOldProcess,
                     out var terminated) ||
                 !terminated))
                return Fail(TakeoverTransactionStage.ProcessTermination, "TerminationRejected");

            if (!isAuthorized())
                return Fail(TakeoverTransactionStage.Relaunching, "AuthorityRevokedBeforeRelaunch");
            if (!coordinator.TryAdvance(lease, TakeoverTransactionStage.Relaunching))
                return Fail(TakeoverTransactionStage.Relaunching, "StageRejected");
            await relaunch(permitGeneration).ConfigureAwait(false);
            return new AutomaticTakeoverPipelineResult
            {
                Succeeded = true,
                PermitGeneration = permitGeneration,
                Stage = lease.Stage,
                Failure = string.Empty
            };
        }
    }

    internal enum WatchdogAttachPurpose
    {
        InitialAuthorityAttach = 0,
        NewRecoveryAuthority = 1,
        SameAuthorityReconnect = 2
    }

    internal sealed class WatchdogHost : IDisposable
    {
        internal static bool RequiresSupervisorSafetyAuthority(
            string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) return false;
            string directory;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            }
            catch
            {
                return false;
            }
            return !string.IsNullOrWhiteSpace(directory) &&
                   File.Exists(Path.Combine(
                       directory,
                       WatchdogRuntimeConfigPaths.FormalModeMarkerName));
        }

        internal static bool ExecutableSha256Matches(
            string executablePath,
            string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !File.Exists(executablePath))
                return false;
            var actual = SupervisorProtocol.ComputeSha256(executablePath);
            return Sha256Equals(actual, expectedSha256);
        }

        private static bool Sha256Equals(string actual, string expected)
        {
            if ((actual?.Length ?? 0) != 64 || (expected?.Length ?? 0) != 64)
                return false;
            for (var index = 0; index < 64; index++)
            {
                if (!Uri.IsHexDigit(actual[index]) ||
                    !Uri.IsHexDigit(expected[index]))
                    return false;
            }
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private readonly WatchdogArguments _args;
        private readonly object _gate = new object();
        private readonly object _processLaunchGate = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _journalGate = new object();
        private readonly WatchdogChannelProgressTracker _channelProgressTracker =
            new WatchdogChannelProgressTracker();
        private readonly TakeoverTransactionCoordinator _automaticTakeover =
            new TakeoverTransactionCoordinator();
        private readonly WatchdogJournalStore _journalStore;
        // Strict V4 is the sole relaunch authority for production Host.
        // Legacy adapters remain in the project only for isolated migration
        // tests and are never constructed here.
        private readonly StrictHostV4AuthorityAdapter _relaunchCoordinator;
        private readonly GuardedProcessLauncher _processLauncher;
        private readonly WatchdogHostStopMonitor _stopSafetyMonitor;
        private readonly RecoveryTransitionWindow _transitionWindow;
        // These values are frozen for the lifetime of this helper.  A duplicate
        // helper which exits because the session mutex is owned elsewhere must
        // never publish a replacement identity.
        private readonly int _sidecarProcessId;
        private readonly long _sidecarProcessStartUtcTicks;
        private readonly string _sidecarInstanceNonce;
        private const int SendWriteTimeoutMs = WatchdogTransportPolicy.SendWriteTimeoutMs;
        private const string OperatorTransitionStopMarkerReason =
            "OperatorCanceledAutomaticRecoveryFromTransitionWindow";
        private WatchdogHostSendQueueOwner _sendOwner;
        private readonly IUnattendedAlarmSink _unattendedAlarmSink;
        private readonly RepeatedEventSummarizer _repeatedEventSummarizer =
            new RepeatedEventSummarizer(30L * Stopwatch.Frequency);
        private readonly WatchdogApplicationLivenessSupervisor _applicationLiveness =
            new WatchdogApplicationLivenessSupervisor(new WindowsWatchdogUiProbe());
        private WatchdogJournal _journal;
        private long _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        private long _lastProgressTimestamp = Stopwatch.GetTimestamp();
        private long _lastProgressVersion;
        private long _lastCompletedCycleCount = -1;
        private long _lastFormalProgressTimestamp = Stopwatch.GetTimestamp();
        private long _lastHeartbeatSequence;
        private long _lastHeartbeatAckSequence;
        private int _permitStalledLogged;
        private long _expectedExitObserverEpoch;
        private long _eventSequence;
        private long _lastHeartbeatCheckpointTimestamp;
        private long _pendingCommitGenerationAwaitingRunIdentity;
        private long _sameAuthorityReconnectAwaitingHeartbeatGeneration;
        private readonly ResponsiveControlRepairSupervisor _responsiveControlRepair =
            new ResponsiveControlRepairSupervisor();
        private string _lastSuppressedControlRepairStopTransaction = string.Empty;
        private int _diagnosticSinkStallLogged;
        private readonly object _recoveryCommitRetryGate = new object();
        private readonly Dictionary<string, RecoveryCommitRetryState> _recoveryCommitRetries =
            new Dictionary<string, RecoveryCommitRetryState>(StringComparer.Ordinal);
        private int _takeoverStarted;
        private int _relaunchAfterExitStarted;
        private int _relaunchStarted;
        private int _safetyPrerequisiteRetryStarted;
        private int _recoveryBlockedStopRequested;
        private int _heartbeatSuspectLogged;
        private int _firstLivenessEvidenceCaptured;
        private int _unstructuredRecoveryLogged;
        private int _terminalPublished;
        private int _circuitHalfOpenStarted;
        private long _nextCircuitHalfOpenTimestamp;
        private long _manualStopIntentTimestamp;
        private long _manualPauseStartedTimestamp;
        private long _manualPauseProgressTimestamp = Stopwatch.GetTimestamp();
        private readonly object _manualPauseProgressGate = new object();
        private string _manualPauseProgressSignature = string.Empty;
        private int _manualStopEmergencyResent;
        private int _manualStopTakeoverStarted;
        private int _manualPauseSafetyTakeoverStarted;
        private int _safetyHandoffStarted;
        private readonly object _safetyAuthorityTokenGate = new object();
        private SupervisorSafetyAuthorityToken _safetyAuthorityToken;
        private int _applicationExitDeadlineStarted;
        private int _physicalStopConfirmed;
        private int _transitionActive;
        private int _operatorTransitionStopStarted;
        private long _observedClosingFenceVersion;
        private string _activeTakeoverCorrelationId = string.Empty;
        private readonly TaskCompletionSource<bool> _operatorStopAcknowledged =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _attached;
        // Monotonic ordering domain for validated main-process attachments.
        // Heartbeat generation/progress is compared only inside this domain.
        private long _validatedAttachEpoch;
        // Attach handshake state is per pipe connection, while process
        // authority is session-scoped.  A reconnect from the same exact
        // PID/start identity may attach on a new pipe without resetting the
        // stop monitor; a duplicate Attach on the same pipe is rejected.
        private long _connectionGeneration;
        private long _attachedConnectionGeneration;

        private sealed class RecoveryCommitRetryState
        {
            internal int DeferredCount;
            internal DateTime LastLoggedUtc;
        }

        private sealed class OldProcessIdentitySnapshot
        {
            internal int ProcessId;
            internal long ProcessStartUtcTicks;
            internal long ExitHardDeadlineUtcTicks;
            internal string Source = string.Empty;

            internal bool IsValid => ProcessId > 0 && ProcessStartUtcTicks > 0;
        }

        private sealed class SafetyHandoffWaitResult
        {
            internal WatchdogSafetyHandoffWaitOutcome Outcome;
            internal long ReadyTimestamp;
            internal string Detail = string.Empty;

            internal bool AllowsRelaunch =>
                Outcome == WatchdogSafetyHandoffWaitOutcome.NotRequired ||
                Outcome == WatchdogSafetyHandoffWaitOutcome.Completed;
        }

        private WatchdogHost(WatchdogArguments args)
        {
            _args = args;
            _unattendedAlarmSink = UnattendedAlarmSink.Create(
                Path.GetDirectoryName(args.ExecutablePath) ?? Environment.CurrentDirectory,
                args.JournalDirectory);
            _processLauncher = new GuardedProcessLauncher(
                capability => _relaunchCoordinator != null &&
                              _relaunchCoordinator.IsCapabilityCurrent(capability),
                capability => _relaunchCoordinator != null &&
                              _relaunchCoordinator.ConsumeLaunchIntent(capability));
            _stopSafetyMonitor = new WatchdogHostStopMonitor(args.SessionId);
            using (var process = Process.GetCurrentProcess())
            {
                _sidecarProcessId = process.Id;
                _sidecarProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            // The controller owns the launch challenge.  The sidecar must
            // echo that exact value; generating a second nonce here would let
            // a stale/duplicate helper establish a different identity.
            _sidecarInstanceNonce = args.SidecarInstanceNonce;
            using (var process = Process.GetCurrentProcess())
                _journalStore = new WatchdogJournalStore(
                    args.JournalDirectory,
                    args.SessionId,
                    "sidecar",
                    args.JournalPolicy,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks);
            _transitionWindow = new RecoveryTransitionWindow(
                OnTransitionPromptDismissRequested,
                OnTransitionOperatorStopRequested,
                OnTransitionMuteP0BuzzerRequested);
            var startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var previous = TryLoadPreviousJournal(args);
            _journal = new WatchdogJournal
            {
                SessionId = args.SessionId,
                ExecutablePath = args.ExecutablePath,
                PipeName = args.PipeName,
                CurrentPid = args.ParentPid,
                CurrentProcessStartUtcTicks = args.ParentStartTicks,
                RecoveryAttempt = previous?.RecoveryAttempt ?? 0,
                ConsecutiveStartupFailures = previous?.ConsecutiveStartupFailures ?? 0,
                SafetyPrerequisiteFailureCount =
                    previous?.SafetyPrerequisiteFailureCount ?? 0,
                MainLaunchFailureCount = previous?.MainLaunchFailureCount ?? 0,
                MainAttachFailureCount = previous?.MainAttachFailureCount ?? 0,
                CheckpointResumeFailureCount =
                    previous?.CheckpointResumeFailureCount ?? 0,
                FirstCycleCommitFailureCount =
                    previous?.FirstCycleCommitFailureCount ?? 0,
                LastSafetyPrerequisiteFailureKey =
                    previous?.LastSafetyPrerequisiteFailureKey,
                LastMainLaunchFailureKey = previous?.LastMainLaunchFailureKey,
                LastMainAttachFailureKey = previous?.LastMainAttachFailureKey,
                LastCheckpointResumeFailureKey =
                    previous?.LastCheckpointResumeFailureKey,
                LastFirstCycleCommitFailureKey =
                    previous?.LastFirstCycleCommitFailureKey,
                SafetyPrerequisiteRetryAttempt =
                    previous?.SafetyPrerequisiteRetryAttempt ?? 0,
                SafetyPrerequisiteFirstFailureUtcTicks =
                    previous?.SafetyPrerequisiteFirstFailureUtcTicks ?? 0,
                SafetyPrerequisiteNextRetryUtcTicks =
                    previous?.SafetyPrerequisiteNextRetryUtcTicks ?? 0,
                RelaunchGeneration = previous?.RelaunchGeneration ?? 0,
                RecoveryBlocked = previous?.RecoveryBlocked == true,
                RecoveryFailureCode = previous?.RecoveryFailureCode,
                RecoveryFailurePermanent = previous?.RecoveryFailurePermanent == true,
                RecoveryFailureFingerprint = previous?.RecoveryFailureFingerprint,
                RootCode = previous?.RootCode,
                DeviceOrChannelGroup = previous?.DeviceOrChannelGroup,
                RunId = previous?.RunId,
                RecoveryStage = previous?.RecoveryStage,
                RecoveryProgressToken = previous?.RecoveryProgressToken,
                RecoveryProcessSource = previous?.RecoveryProcessSource,
                RecoveryChannelIntentFrozen =
                    previous?.RecoveryChannelIntentFrozen == true,
                FrozenExcludedChannels =
                    previous?.FrozenExcludedChannels?.ToArray() ?? Array.Empty<int>(),
                FrozenExcludedSourceProcessId =
                    previous?.FrozenExcludedSourceProcessId ?? 0,
                FrozenExcludedSourceProcessStartUtcTicks =
                    previous?.FrozenExcludedSourceProcessStartUtcTicks ?? 0,
                FrozenExcludedSourceRunId =
                    previous?.FrozenExcludedSourceRunId,
                FrozenExcludedSourceRunEpoch =
                    previous?.FrozenExcludedSourceRunEpoch ?? 0,
                LastVerifiedActiveRun = previous?.LastVerifiedActiveRun,
                LastCheckpointMirror = previous?.LastCheckpointMirror,
                RecoveryFailureMaxProcessRelaunches =
                    previous?.RecoveryFailureMaxProcessRelaunches ?? 0,
                RecoveryFirstFailureUtcTicks = previous?.RecoveryFirstFailureUtcTicks ?? 0,
                RecoveryLastFailureUtcTicks = previous?.RecoveryLastFailureUtcTicks ?? 0,
                RecoveryBlockedUtcTicks = previous?.RecoveryBlockedUtcTicks ?? 0,
                LastRecoveryCommitRunId = previous?.LastRecoveryCommitRunId,
                LastReason = previous?.LastReason,
                ManualStopRequested = previous?.ManualStopRequested == true,
                State = previous?.RecoveryBlocked == true
                    ? "SafeIdleRecoveryBlocked"
                    : "Starting",
                UpdatedUtc = startedUtc,
                StartedUtc = startedUtc
            };
            _relaunchCoordinator = new StrictHostV4AuthorityAdapter(
                args, _sidecarProcessId, _sidecarProcessStartUtcTicks);
            var restartReconciliation = _relaunchCoordinator.RecoverAfterRestart(
                ProbeProcessIdentity);
            lock (_journalGate)
                ApplyDurablePermitLocked(
                    restartReconciliation.Record ?? _relaunchCoordinator.Snapshot);
            SaveJournal();
            if (_journal.RecoveryBlocked)
            {
                ScheduleNextCircuitHalfOpen();
                RecordEvent(
                    "RecoveryBlockedRestored",
                    $"Code={_journal.RecoveryFailureCode};" +
                    $"Fingerprint={_journal.RecoveryFailureFingerprint};" +
                    $"Count={_journal.ConsecutiveStartupFailures}");
                ShowRecoveryBlockedTransition(_journal.LastReason);
            }
            else
            {
                RecordEvent("Starting", "SidecarStarted");
            }
        }

        private static WatchdogJournal TryLoadPreviousJournal(WatchdogArguments args)
        {
            try
            {
                var path = Path.Combine(
                    args.JournalDirectory,
                    "session-" + WatchdogJournalPaths.SafeName(args.SessionId) + ".json");
                if (!File.Exists(path)) return null;
                string content;
                using (var stream = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                    content = reader.ReadToEnd();
                var previous = Json.Deserialize<WatchdogJournal>(content);
                if (previous == null ||
                    !string.Equals(previous.SessionId, args.SessionId, StringComparison.OrdinalIgnoreCase) ||
                    (previous.SchemaVersion != 2 && previous.SchemaVersion != 3 &&
                     previous.SchemaVersion != 4 &&
                     previous.SchemaVersion != WatchdogJournalPolicy.CurrentSchemaVersion))
                    return null;
                // V2 did not carry the structured report fields, but its
                // durable RecoveryBlocked bit and legacy code/fingerprint are
                // authoritative and must survive the additive migration.
                if (previous.SchemaVersion >= 2 &&
                    previous.SchemaVersion < WatchdogJournalPolicy.CurrentSchemaVersion)
                    previous.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
                return previous;
            }
            catch
            {
                return null;
            }
        }

        public static int Run(string[] rawArgs)
        {
            var args = WatchdogArguments.Parse(rawArgs);
            using (var singleton = new Mutex(false, "Local\\MTTFTest.Watchdog." + args.SessionId))
            {
                bool owns;
                try { owns = singleton.WaitOne(0, false); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) return 0;
                try
                {
                    using (var host = new WatchdogHost(args))
                    {
                        foreach (var cleanup in RetireStaleSidecars(args))
                            host.RecordEvent(
                                cleanup.Succeeded
                                    ? "StaleSidecarRetired"
                                    : "StaleSidecarCleanupSkipped",
                                cleanup.Detail);
                        return host.RunAsync().GetAwaiter().GetResult();
                    }
                }
                finally { try { singleton.ReleaseMutex(); } catch { } }
            }
        }

        private sealed class StaleSidecarCleanupResult
        {
            internal bool Succeeded;
            internal string Detail;
        }

        private sealed class StaleSidecarCandidate
        {
            internal int ProcessId;
            internal long ProcessStartUtcTicks;
            internal int ParentProcessId;
            internal long ParentProcessStartUtcTicks;
            internal string SessionId;
            internal string JournalDirectory;
            internal string MainExecutablePath;
        }

        private static IEnumerable<StaleSidecarCleanupResult> RetireStaleSidecars(
            WatchdogArguments current)
        {
            var results = new List<StaleSidecarCleanupResult>();
            var candidates = DiscoverStaleSidecars(current).ToArray();
            long currentStartUtcTicks;
            using (var currentProcess = Process.GetCurrentProcess())
                currentStartUtcTicks = currentProcess.StartTime
                    .ToUniversalTime().Ticks;
            foreach (var candidate in candidates)
            {
                var reason = "SupersededByNewSession:" + current.SessionId;
                var precheckParent = ObserveProcessIdentity(
                    candidate.ParentProcessId,
                    candidate.ParentProcessStartUtcTicks);
                var precheckSafetyActive = HasActiveSafetyTransaction(
                    candidate.JournalDirectory,
                    candidate.SessionId);
                if (!StaleSidecarCleanupPolicy.CanRetire(
                        SameProductScope(
                            current.ExecutablePath,
                            candidate.MainExecutablePath),
                        SameJournalScope(
                            current.JournalDirectory,
                            candidate.JournalDirectory),
                        candidate.ProcessStartUtcTicks < currentStartUtcTicks,
                        candidateIdentityExact: true,
                        precheckParent,
                        precheckSafetyActive))
                {
                    results.Add(new StaleSidecarCleanupResult
                    {
                        Detail = $"PID={candidate.ProcessId};Session={candidate.SessionId};" +
                                 $"Parent={precheckParent};" +
                                 $"SafetyTransactionActive={precheckSafetyActive};" +
                                 "ScopeOrSafetyPrecheckRejected=true"
                    });
                    continue;
                }
                try
                {
                    // 先持久撤销旧会话的重拉权威，再允许终止旧Sidecar。
                    WatchdogControlMarker.WriteLocal(candidate.SessionId, reason);
                    try
                    {
                        WatchdogControlMarker.WriteProject(
                            candidate.JournalDirectory,
                            candidate.SessionId,
                            reason);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    results.Add(new StaleSidecarCleanupResult
                    {
                        Detail = $"PID={candidate.ProcessId};Session={candidate.SessionId};" +
                                 "RevocationWriteFailed=" +
                                 ex.GetBaseException().Message
                    });
                    continue;
                }

                Process process = null;
                try
                {
                    process = Process.GetProcessById(candidate.ProcessId);
                    var exact = !process.HasExited &&
                        process.StartTime.ToUniversalTime().Ticks ==
                        candidate.ProcessStartUtcTicks;
                    var parentObservation = ObserveProcessIdentity(
                        candidate.ParentProcessId,
                        candidate.ParentProcessStartUtcTicks);
                    var safetyTransactionActive = HasActiveSafetyTransaction(
                        candidate.JournalDirectory,
                        candidate.SessionId);
                    if (!StaleSidecarCleanupPolicy.CanRetire(
                            SameProductScope(
                                current.ExecutablePath,
                                candidate.MainExecutablePath),
                            SameJournalScope(
                                current.JournalDirectory,
                                candidate.JournalDirectory),
                            candidate.ProcessStartUtcTicks <
                                currentStartUtcTicks,
                            exact,
                            parentObservation,
                            safetyTransactionActive))
                    {
                        results.Add(new StaleSidecarCleanupResult
                        {
                            Detail = $"PID={candidate.ProcessId};Session={candidate.SessionId};" +
                                     $"Parent={parentObservation};IdentityExact={exact};" +
                                     $"SafetyTransactionActive={safetyTransactionActive}"
                        });
                        continue;
                    }

                    if (!process.WaitForExit(3000))
                    {
                        // Process对象持有原进程句柄；在kill前再次核对启动时间，
                        // 避免PID复用误伤无关进程。
                        if (process.HasExited ||
                            process.StartTime.ToUniversalTime().Ticks !=
                            candidate.ProcessStartUtcTicks)
                            continue;
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                    results.Add(new StaleSidecarCleanupResult
                    {
                        Succeeded = process.HasExited,
                        Detail = $"PID={candidate.ProcessId};Session={candidate.SessionId};" +
                                 $"Parent={parentObservation};Revoked=true;" +
                                 $"Exited={process.HasExited}"
                    });
                }
                catch (ArgumentException)
                {
                    results.Add(new StaleSidecarCleanupResult
                    {
                        Succeeded = true,
                        Detail = $"PID={candidate.ProcessId};Session={candidate.SessionId};" +
                                 "Revoked=true;ExitedBeforeOpen=true"
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new StaleSidecarCleanupResult
                    {
                        Detail = $"PID={candidate.ProcessId};Session={candidate.SessionId};" +
                                 "CleanupFailed=" + ex.GetBaseException().Message
                    });
                }
                finally
                {
                    try { process?.Dispose(); } catch { }
                }
            }
            return results;
        }

        private static IEnumerable<StaleSidecarCandidate> DiscoverStaleSidecars(
            WatchdogArguments current)
        {
            var candidates = new List<StaleSidecarCandidate>();
            try
            {
                long currentStartUtcTicks;
                using (var currentProcess = Process.GetCurrentProcess())
                    currentStartUtcTicks = currentProcess.StartTime
                        .ToUniversalTime().Ticks;
                using (var searcher = new ManagementObjectSearcher(
                           "SELECT ProcessId,CommandLine FROM Win32_Process " +
                           "WHERE Name='MTTFTest.Watchdog.exe'"))
                using (var processes = searcher.Get())
                {
                    foreach (ManagementObject item in processes)
                    {
                        using (item)
                        {
                            var processId = Convert.ToInt32(
                                item["ProcessId"],
                                CultureInfo.InvariantCulture);
                            if (processId <= 0 ||
                                processId == Process.GetCurrentProcess().Id)
                                continue;
                            var commandLine = item["CommandLine"] as string;
                            var sessionId = ReadCommandLineArgument(
                                commandLine,
                                "--session");
                            if (!Guid.TryParseExact(
                                    sessionId ?? string.Empty,
                                    "N",
                                    out _) ||
                                string.Equals(
                                    sessionId,
                                    current.SessionId,
                                    StringComparison.Ordinal))
                                continue;
                            if (!int.TryParse(
                                    ReadCommandLineArgument(commandLine, "--parent-pid"),
                                    NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out var parentPid) || parentPid <= 0 ||
                                !long.TryParse(
                                    ReadCommandLineArgument(
                                        commandLine,
                                        "--parent-start-ticks"),
                                    NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out var parentStart) || parentStart <= 0)
                                continue;
                            var journal = ReadCommandLineArgument(
                                commandLine,
                                "--journal-directory");
                            var executable = ReadCommandLineArgument(
                                commandLine,
                                "--executable");
                            if (string.IsNullOrWhiteSpace(journal) ||
                                string.IsNullOrWhiteSpace(executable))
                                continue;
                            journal = WatchdogJournalPaths.ValidateProjectDirectory(journal);
                            executable = Path.GetFullPath(executable);
                            using (var process = Process.GetProcessById(processId))
                            {
                                if (process.HasExited) continue;
                                var candidateStartUtcTicks = process.StartTime
                                    .ToUniversalTime().Ticks;
                                if (!StaleSidecarCleanupPolicy.CanRetire(
                                        SameProductScope(
                                            current.ExecutablePath,
                                            executable),
                                        SameJournalScope(
                                            current.JournalDirectory,
                                            journal),
                                        candidateStartUtcTicks <
                                            currentStartUtcTicks,
                                        candidateIdentityExact: true,
                                        ObserveProcessIdentity(
                                            parentPid,
                                            parentStart),
                                        HasActiveSafetyTransaction(
                                            journal,
                                            sessionId)))
                                    continue;
                                candidates.Add(new StaleSidecarCandidate
                                {
                                    ProcessId = processId,
                                    ProcessStartUtcTicks = candidateStartUtcTicks,
                                    ParentProcessId = parentPid,
                                    ParentProcessStartUtcTicks = parentStart,
                                    SessionId = sessionId,
                                    JournalDirectory = journal,
                                    MainExecutablePath = executable
                                });
                            }
                        }
                    }
                }
            }
            catch
            {
                // WMI不可用时不影响当前会话启动；不做任何猜测性进程操作。
            }
            return candidates;
        }

        private static string ReadCommandLineArgument(
            string commandLine,
            string name)
        {
            if (string.IsNullOrWhiteSpace(commandLine) ||
                string.IsNullOrWhiteSpace(name))
                return string.Empty;
            var match = Regex.Match(
                commandLine,
                "(?:^|\\s)" + Regex.Escape(name) +
                "\\s+(?:\"(?<quoted>[^\"]*)\"|(?<plain>\\S+))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return !match.Success
                ? string.Empty
                : match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["plain"].Value;
        }

        private static DurableRelaunchProcessObservation ObserveProcessIdentity(
            int processId,
            long processStartUtcTicks)
        {
            if (processId <= 0 || processStartUtcTicks <= 0)
                return DurableRelaunchProcessObservation.Unknown;
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                        return DurableRelaunchProcessObservation.Dead;
                    return process.StartTime.ToUniversalTime().Ticks ==
                           processStartUtcTicks
                        ? DurableRelaunchProcessObservation.Alive
                        : DurableRelaunchProcessObservation.IdentityMismatch;
                }
            }
            catch (ArgumentException)
            {
                return DurableRelaunchProcessObservation.Dead;
            }
            catch
            {
                return DurableRelaunchProcessObservation.Unknown;
            }
        }

        private static bool SameProductScope(string current, string candidate)
        {
            try
            {
                if (!File.Exists(current) || !File.Exists(candidate)) return false;
                var left = FileVersionInfo.GetVersionInfo(current);
                var right = FileVersionInfo.GetVersionInfo(candidate);
                var leftOriginal = string.IsNullOrWhiteSpace(left.OriginalFilename)
                    ? Path.GetFileName(current)
                    : left.OriginalFilename;
                var rightOriginal = string.IsNullOrWhiteSpace(right.OriginalFilename)
                    ? Path.GetFileName(candidate)
                    : right.OriginalFilename;
                return string.Equals(
                           leftOriginal,
                           rightOriginal,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           left.ProductName ?? string.Empty,
                           right.ProductName ?? string.Empty,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           left.CompanyName ?? string.Empty,
                           right.CompanyName ?? string.Empty,
                           StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool SameJournalScope(string current, string candidate)
        {
            try
            {
                var left = Path.GetFullPath(
                    WatchdogJournalPaths.ValidateProjectDirectory(current))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var right = Path.GetFullPath(
                    WatchdogJournalPaths.ValidateProjectDirectory(candidate))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static bool HasActiveSafetyTransaction(
            string journalDirectory,
            string sessionId)
        {
            try
            {
                var localHandoff = WatchdogJournalPaths.LocalSafetyHandoffPath(sessionId);
                var projectHandoff = WatchdogJournalPaths.ProjectSafetyHandoffPath(
                    journalDirectory,
                    sessionId);
                WatchdogSafetyHandoffReceipt handoff;
                if (WatchdogSafetyHandoffReceiptStore.TryRead(
                        journalDirectory,
                        sessionId,
                        out handoff) && handoff != null)
                    return !handoff.IsTerminal;
                if (DurableEvidenceExists(localHandoff, projectHandoff))
                    return true;

                var localClosing = WatchdogJournalPaths.LocalClosingPath(sessionId);
                var projectClosing = WatchdogJournalPaths.ProjectClosingPath(
                    journalDirectory,
                    sessionId);
                WatchdogClosingTombstone closing;
                if (WatchdogClosingTombstoneStore.TryRead(
                        journalDirectory,
                        sessionId,
                        out closing) && closing != null)
                    return closing.State != WatchdogClosingTombstoneState.Terminal ||
                           !string.IsNullOrWhiteSpace(closing.SafetyHandoffId) &&
                           !closing.IsSafetyTerminal;
                return DurableEvidenceExists(localClosing, projectClosing);
            }
            catch
            {
                // 清退不是当前会话安全运行的前置条件。旧会话证据无法读取时保持不动。
                return true;
            }
        }

        private static bool DurableEvidenceExists(string localPath, string projectPath)
        {
            return File.Exists(localPath) || File.Exists(localPath + ".bak") ||
                   File.Exists(projectPath) || File.Exists(projectPath + ".bak");
        }

        private async Task<int> RunAsync()
        {
            // A closing marker cancels trial resume, not an already durable
            // independent shutdown. Resume that owner before retiring the host.
            while (!_stop.IsCancellationRequested && ObserveSafetyHandoffProgress())
                await Task.Delay(250, _stop.Token).ConfigureAwait(false);
            var startupFenceAction = CaptureCloseFenceAction(
                activeConnection: false,
                currentProcessAlive: IsCurrentProcessAlive(),
                out var closing,
                out var legacyRevoked);
            if (startupFenceAction != WatchdogCloseFenceAction.None)
            {
                _journal.ManualStopRequested = true;
                var revoked = _relaunchCoordinator.Revoke(
                    closing?.State == WatchdogClosingTombstoneState.Closing
                        ? "ClosingSessionObservedOnStartup"
                        : "TerminalSessionObservedOnStartup");
                if (revoked?.Record != null)
                    lock (_journalGate) ApplyDurablePermitLocked(revoked.Record);
                var startupEvent = closing?.State == WatchdogClosingTombstoneState.Closing
                    ? "ClosingSessionObservedOnStartup"
                    : "TerminalSessionObservedOnStartup";
                RecordEvent(
                    startupEvent,
                    closing == null
                        ? legacyRevoked
                            ? "LegacyRevocationMarker"
                            : "UnknownCloseFence"
                        : $"State={closing.State};Version={closing.StateVersion};" +
                          $"Generation={closing.SessionGeneration};Lease={closing.SessionLease}");
                _transitionWindow.Hide();
                PublishTerminal(
                    startupEvent,
                    closing?.TerminalReason ??
                    (legacyRevoked ? "SessionRevoked" : "ClosingOwnerUnavailable"));
                SaveJournal();
                return 0;
            }

            var monitor = MonitorAsync(_stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                               _args.PipeName, PipeDirection.InOut, 1,
                               PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                        await ServeConnectionAsync(pipe, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (IOException ex)
                {
                    Record("PipeDisconnected", ex.Message);
                    await Task.Delay(250).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var root = ex.GetBaseException();
                    if (root is ObjectDisposedException || root is InvalidOperationException)
                        RecordEvent("PipeDisconnected", root.Message);
                    else
                        Record("PipeLoopError", root.Message);
                    if (_stop.IsCancellationRequested) break;
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
            try { await monitor.ConfigureAwait(false); } catch { }
            return 0;
        }

        private async Task ServeConnectionAsync(Stream pipe, CancellationToken token)
        {
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
            {
                long connectionGeneration;
                WatchdogHostSendQueueOwner sendOwner;
                lock (_gate)
                {
                    connectionGeneration = ++_connectionGeneration;
                    sendOwner = new WatchdogHostSendQueueOwner(
                        pipe,
                        writer,
                        connectionGeneration);
                    _sendOwner = sendOwner;
                }
                sendOwner.WorkerTask = Task.Run(() => RunSendQueueAsync(sendOwner));
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (!WatchdogWireFrame.TryDecode(
                                line,
                                out line,
                                out var frameFailure))
                        {
                            Record(
                                "TransportInterruptedPartialFrame",
                                frameFailure ?? "FrameRejected");
                            break;
                        }
                        WatchdogMessage message;
                        string failurePayloadSha256 = null;
                        if (WatchdogProtocol.TryPeekWireMessageType(line, out var wireType) &&
                            string.Equals(wireType, WatchdogMessageType.RecoveryAttemptFailed,
                                StringComparison.Ordinal))
                        {
                            if (!WatchdogProtocol.TryParseRecoveryFailureRequestWire(
                                    line,
                                    _args.SessionId,
                                    out message,
                                    out failurePayloadSha256,
                                    out var requestFailure))
                            {
                                Record("RecoveryFailureRequestRejected",
                                    requestFailure ?? "InvalidExactV3Request");
                                continue;
                            }
                        }
                        else
                        {
                            try { message = WatchdogProtocol.Deserialize(line); }
                            catch (Exception ex) { Record("InvalidMessage", ex.Message); continue; }
                        }
                        if (message == null ||
                            !WatchdogProtocol.IsSupportedVersion(message.ProtocolVersion) ||
                            !string.Equals(message.SessionId, _args.SessionId, StringComparison.Ordinal))
                            continue;
                        await HandleMessageAsync(
                                message,
                                connectionGeneration,
                                failurePayloadSha256)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_sendOwner, sendOwner))
                            _sendOwner = null;
                    }
                    sendOwner.StopAccepting();
                    try { pipe.Dispose(); } catch { }
                    var worker = sendOwner.WorkerTask;
                    if (worker != null)
                    {
                        var joined = await Task.WhenAny(
                                worker,
                                Task.Delay(SendWriteTimeoutMs + 250))
                            .ConfigureAwait(false);
                        if (!ReferenceEquals(joined, worker))
                            RecordEvent(
                                "HostTransportWriterJoinTimeout",
                                "ConnectionGeneration=" + connectionGeneration);
                        else
                            try { await worker.ConfigureAwait(false); } catch { }
                    }
                    RecordEvent(
                        "HostTransportConnectionRetired",
                        "ConnectionGeneration=" + connectionGeneration);
                }
            }
        }

        private Task HandleMessageAsync(
            WatchdogMessage message,
            long connectionGeneration,
            string recoveryFailurePayloadSha256 = null)
        {
            switch (message.Type)
            {
                case WatchdogMessageType.Attach:
                    var attachFenceAction = CaptureCloseFenceAction(
                        activeConnection: false,
                        currentProcessAlive: IsCurrentProcessAlive(),
                        out var attachClosing,
                        out _);
                    if (attachFenceAction != WatchdogCloseFenceAction.None)
                    {
                        _journal.ManualStopRequested = true;
                        var attachReason = attachClosing?.State ==
                                           WatchdogClosingTombstoneState.Closing
                            ? "AttachClosingFence"
                            : "AttachRevocationMarker";
                        Record("SessionRevoked", attachReason);
                        PublishTerminal("SessionRevoked", attachReason);
                        _stop.Cancel();
                        break;
                    }
                    // Validate process/session identity before touching the
                    // durable current-authority fields or the attachment
                    // epoch.  A late pipe from an old process must be a
                    // no-op, not a journal overwrite followed by a rejected
                    // heartbeat.
                    if (!TryValidateAttachBeforeMutation(
                            message.Session,
                            connectionGeneration,
                            out var sameAuthorityReconnect,
                            out var attachValidationFailure))
                    {
                        RecordEvent(
                            "AttachRejected",
                            attachValidationFailure ?? "AttachIdentityInvalid");
                        break;
                    }
                    var attachPurpose = sameAuthorityReconnect
                        ? WatchdogAttachPurpose.SameAuthorityReconnect
                        : message.Session?.RecoveryProcess == true
                            ? WatchdogAttachPurpose.NewRecoveryAuthority
                            : WatchdogAttachPurpose.InitialAuthorityAttach;
                    string normalizedAttachExecutablePath = null;
                    if (!string.IsNullOrWhiteSpace(message.Session?.ExecutablePath))
                    {
                        try
                        {
                            normalizedAttachExecutablePath = Path.GetFullPath(
                                message.Session.ExecutablePath);
                        }
                        catch (Exception ex)
                        {
                            RecordEvent(
                                "AttachRejected",
                                "AttachExecutablePathInvalid:" + ex.Message);
                            break;
                        }
                    }
                    if (attachPurpose == WatchdogAttachPurpose.SameAuthorityReconnect &&
                        message.Session?.RecoveryProcess == true)
                    {
                        if (!TryValidateRecoveryReconnectPermit(
                                message.Session,
                                out var recoveryReconnectFailure))
                        {
                            // A rejected transport reconnect must not mutate or
                            // revoke the still-live process authority.  Only a
                            // brand-new recovery authority is allowed to consume
                            // or poison the one-shot relaunch permit.
                            RecordEvent(
                                "RecoveryReconnectRejected",
                                recoveryReconnectFailure ??
                                "RecoveryReconnectPermitIdentityInvalid");
                            break;
                        }
                    }
                    else if (attachPurpose == WatchdogAttachPurpose.NewRecoveryAuthority)
                    {
                        if (!TryCommitAttachedPermit(
                                message.Session,
                                out var recoveryAttachFailure))
                        {
                            RecordEvent(
                                "RecoveryAttachRejected",
                                recoveryAttachFailure ?? "RecoveryAttachPermitInvalid");
                            BlockLaunchOutcomeUnknown(
                                recoveryAttachFailure ?? "RecoveryAttachPermitInvalid");
                            Send(
                                WatchdogMessageType.RequestStopAll,
                                "DurableRecoveryBlocked",
                                Guid.NewGuid().ToString("N"));
                            break;
                        }
                    }
                    if (message.Session != null)
                    {
                        lock (_journalGate)
                        {
                            _journal.CurrentPid = message.Session.ProcessId;
                            _journal.CurrentProcessStartUtcTicks = message.Session.ProcessStartUtcTicks;
                            if (!string.IsNullOrWhiteSpace(normalizedAttachExecutablePath))
                                _journal.ExecutablePath = normalizedAttachExecutablePath;
                            _journal.RecoveryAttempt = Math.Max(_journal.RecoveryAttempt, message.Session.RecoveryAttempt);
                            _journal.RelaunchGeneration = Math.Max(
                                _journal.RelaunchGeneration,
                                message.Session.RelaunchGeneration);
                            ApplyDurablePermitLocked(_relaunchCoordinator.Snapshot);
                        }
                    }
                    _attached = true;
                    if (_journal.RecoveryBlocked)
                    {
                        RecordEvent(
                            "RecoveryProcessRejectedByDurableBlock",
                            $"Code={_journal.RecoveryFailureCode};" +
                            $"Fingerprint={_journal.RecoveryFailureFingerprint}");
                        ShowRecoveryBlockedTransition(_journal.LastReason);
                        if (Interlocked.CompareExchange(ref _recoveryBlockedStopRequested, 1, 0) == 0)
                            Send(
                                WatchdogMessageType.RequestStopAll,
                                "DurableRecoveryBlocked",
                                Guid.NewGuid().ToString("N"));
                        // Keep the exact current main-process transport
                        // attachable even after the relaunch authority is
                        // blocked.  The client may have lost the first
                        // durable failure receipt immediately after the Host
                        // committed it; it must reconnect and replay the same
                        // raw correlation to retrieve that receipt.  This
                        // does not authorize a recovery child: recovery
                        // process permit validation above still rejects a
                        // blocked/invalid launch identity.
                    }
                    lock (_journalGate)
                    {
                        _attachedConnectionGeneration = connectionGeneration;
                    }
                    // A validated attach proves that the exact process can
                    // still execute the client transport path.  Start a fresh
                    // transport grace window so a same-authority reconnect is
                    // not retired again before its first heartbeat reaches the
                    // single-writer queue.  This does not reset UI readiness or
                    // suppress any formal progress / StopAll safety deadline.
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                    if (attachPurpose != WatchdogAttachPurpose.SameAuthorityReconnect)
                    {
                        CancelAutomaticTakeover("ValidatedNewProcessAttached");
                        _takeoverStarted = 0;
                        _activeTakeoverCorrelationId = string.Empty;
                        _relaunchAfterExitStarted = 0;
                        _relaunchStarted = 0;
                        _lastProgressVersion = 0;
                        _channelProgressTracker.Reset();
                        Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _lastCompletedCycleCount, -1);
                        Interlocked.Exchange(ref _lastFormalProgressTimestamp, Stopwatch.GetTimestamp());
                        _journal.OrphanPauseTriggered = false;
                        _journal.PowerDisableTriggered = false;
                        var validatedAttachEpoch = Interlocked.Increment(ref _validatedAttachEpoch);
                        _applicationLiveness.ResetForNewProcess(
                            validatedAttachEpoch,
                            Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _expectedExitObserverEpoch, 0);
                        _stopSafetyMonitor.NotifyValidatedAttached(
                            _args.SessionId,
                            // Do not seed the new attachment with a heartbeat
                            // retained from the previous process.  The first
                            // fresh heartbeat establishes the new stop transaction
                            // and may legitimately use a lower generation; the
                            // supervisor's attach epoch prevents old traffic from
                            // reviving the retained decision.
                            string.Empty,
                            0,
                            message.Session == null
                                ? string.Empty
                                : message.Session.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" +
                                  message.Session.ProcessStartUtcTicks.ToString(
                                      CultureInfo.InvariantCulture),
                            message.Session?.ProcessId ?? 0,
                            message.Session?.ProcessStartUtcTicks ?? 0,
                            validatedAttachEpoch);
                        Record("Attached", message.Session?.RecoveryProcess == true
                            ? "RecoveryProcess"
                            : "MainProcess");
                    }
                    else
                    {
                        Interlocked.Exchange(
                            ref _sameAuthorityReconnectAwaitingHeartbeatGeneration,
                            connectionGeneration);
                        Record("AttachedReconnect",
                            "SameAuthority;ConnectionGeneration=" + connectionGeneration);
                    }
                    if (attachPurpose == WatchdogAttachPurpose.NewRecoveryAuthority)
                    {
                        Interlocked.Exchange(ref _transitionActive, 1);
                        _transitionWindow.BeginTransition();
                        _transitionWindow.Show(
                            "主程序已重新启动",
                            "正在连接恢复会话并加载安全检查点",
                            0,
                            _journal.RecoveryAttempt);
                    }
                    Send(new WatchdogMessage
                    {
                        Type = WatchdogMessageType.Attached,
                        SessionId = _args.SessionId,
                        Reason = "Attached",
                        CorrelationId = message.CorrelationId,
                        SidecarProcessId = _sidecarProcessId,
                        SidecarProcessStartUtcTicks = _sidecarProcessStartUtcTicks,
                        SidecarStartUtcTicks = _sidecarProcessStartUtcTicks,
                        StartUtcTicks = _sidecarProcessStartUtcTicks,
                        SidecarAuthoritySessionId = _args.SessionId,
                        SidecarSessionId = _args.SessionId,
                        SidecarInstanceNonce = _sidecarInstanceNonce,
                        InstanceNonce = _sidecarInstanceNonce,
                        RelaunchPermitGeneration = message.Session?.RelaunchGeneration ?? 0,
                        RelaunchPermitId = message.Session?.RelaunchPermitId,
                        RelaunchPermitNonce = message.Session?.RelaunchPermitNonce
                    });
                    break;
                case WatchdogMessageType.MainUiReady:
                    if (_journal.RecoveryBlocked)
                        RecordEvent("MainUiReadyRejectedByDurableBlock", message.Reason);
                    else
                        Record("MainUiReady", message.Reason ?? "MainWindowShown");
                    _applicationLiveness.ObserveMainUiReady(
                        Volatile.Read(ref _validatedAttachEpoch));
                    if (!_journal.RecoveryBlocked &&
                        Interlocked.CompareExchange(ref _transitionActive, 0, 0) != 0)
                    {
                        _transitionWindow.Hide();
                        Interlocked.Exchange(ref _transitionActive, 0);
                    }
                    break;
                case WatchdogMessageType.Heartbeat:
                    if (message.Heartbeat == null) break;
                    if (!IsValidatedHeartbeatIdentity(message.Heartbeat))
                    {
                        RecordEvent(
                            "HeartbeatRejectedOldProcess",
                            $"Pid={message.Heartbeat.ProcessId};" +
                            $"Start={message.Heartbeat.ProcessStartUtcTicks}");
                        break;
                    }
                    if (Interlocked.CompareExchange(
                            ref _sameAuthorityReconnectAwaitingHeartbeatGeneration,
                            0,
                            connectionGeneration) == connectionGeneration)
                    {
                        CancelAutomaticTakeover("SameAuthorityReconnectFreshHeartbeat");
                        _channelProgressTracker.Reset();
                        Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _lastFormalProgressTimestamp, Stopwatch.GetTimestamp());
                        _responsiveControlRepair.Reset();
                        RecordEvent(
                            "AttachedReconnectValidated",
                            "FreshHeartbeat;ConnectionGeneration=" + connectionGeneration);
                    }
                    _attached = true;
                    Interlocked.Exchange(ref _heartbeatSuspectLogged, 0);
                    var semanticAgeSeconds = SemanticSnapshotAgeSeconds(
                        message.Heartbeat,
                        DateTime.UtcNow);
                    if (semanticAgeSeconds < 2)
                        _applicationLiveness.ObserveHeartbeat();
                    message.Heartbeat.AttachEpoch = Volatile.Read(
                        ref _validatedAttachEpoch);
                    _journal.CurrentPid = message.Heartbeat.ProcessId;
                    _journal.CurrentProcessStartUtcTicks = message.Heartbeat.ProcessStartUtcTicks;
                    _journal.LastHeartbeat = message.Heartbeat;
                    var verifiedActiveRunChanged =
                        WatchdogRecoveryChannelIntentPolicy.TryCaptureLastVerifiedActiveRun(
                            _journal,
                            message.Heartbeat,
                            identityValidated: true);
                    var stopProjection = StopSafetyHeartbeatProjection.FromHeartbeat(
                        message.Heartbeat);
                    stopProjection.AttachEpoch = Volatile.Read(
                        ref _validatedAttachEpoch);
                    _stopSafetyMonitor.Observe(stopProjection);
                    if (string.IsNullOrWhiteSpace(_journal.RecoveryFailureFingerprint))
                    {
                        _journal.RunId = message.Heartbeat.RunId;
                        _journal.RecoveryStage = message.Heartbeat.RecoveryStage;
                        _journal.RecoveryProgressToken =
                            message.Heartbeat.RecoveryProgressVersion.ToString(CultureInfo.InvariantCulture);
                        _journal.RecoveryProcessSource = message.Heartbeat.RecoveryProcessSource;
                    }
                    if (message.Heartbeat.RecoveryBatchCommitGeneration >
                        _journal.LastRecoveryBatchCommitGeneration)
                    {
                        TryAcceptRecoveryBatchCommit(
                            message.Heartbeat.RecoveryBatchCommitGeneration,
                            "Heartbeat",
                            $"Generation={message.Heartbeat.RecoveryBatchCommitGeneration}",
                            message.Heartbeat);
                    }
                    _lastHeartbeatSequence = message.Heartbeat.Sequence;
                    _journal.LastHeartbeatSequence = message.Heartbeat.Sequence;
                    _journal.LastHeartbeatUtcTicks = DateTime.UtcNow.Ticks;
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                    if (message.Heartbeat.DiagnosticSinkStalled)
                    {
                        if (Interlocked.CompareExchange(ref _diagnosticSinkStallLogged, 1, 0) == 0)
                        {
                            RecordEvent(
                                "DiagnosticSinkStalled",
                                $"Accepted={message.Heartbeat.DiagnosticSinkAcceptedVersion};" +
                                $"Flushed={message.Heartbeat.DiagnosticSinkFlushedVersion};" +
                                $"Queue={message.Heartbeat.DiagnosticSinkQueueDepth};" +
                                $"Spool={message.Heartbeat.DiagnosticSinkEmergencySpool};" +
                                $"Failure={message.Heartbeat.DiagnosticSinkFailure}");
                        }
                    }
                    else if (Interlocked.Exchange(ref _diagnosticSinkStallLogged, 0) != 0)
                    {
                        RecordEvent(
                            "DiagnosticSinkRecovered",
                            $"Flushed={message.Heartbeat.DiagnosticSinkFlushedVersion}");
                    }
                    if (WatchdogTakeoverPolicy.IsManualPauseCommanded(
                            message.Heartbeat.ManualPauseActive,
                            message.Heartbeat.ManualPausePending))
                        Interlocked.CompareExchange(
                            ref _manualPauseStartedTimestamp,
                            Stopwatch.GetTimestamp(),
                            0);
                    else
                        Interlocked.Exchange(ref _manualPauseStartedTimestamp, 0);
                    TrackManualPauseProgress(message.Heartbeat);
                    if (!_journal.RecoveryBlocked &&
                        Interlocked.CompareExchange(ref _transitionActive, 0, 0) != 0 &&
                        RecoveryTransitionPolicy.ShouldHide(message.Heartbeat))
                    {
                        _transitionWindow.Hide();
                        Interlocked.Exchange(ref _transitionActive, 0);
                    }
                    if (message.Heartbeat.RecoveryProgressVersion != _lastProgressVersion)
                    {
                        _lastProgressVersion = message.Heartbeat.RecoveryProgressVersion;
                        Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
                    }
                    if (!string.Equals(message.Heartbeat.Phase, "Formal", StringComparison.OrdinalIgnoreCase) ||
                        message.Heartbeat.CompletedCycleCount !=
                        Interlocked.Read(ref _lastCompletedCycleCount))
                    {
                        Interlocked.Exchange(
                            ref _lastCompletedCycleCount,
                            message.Heartbeat.CompletedCycleCount);
                        Interlocked.Exchange(
                            ref _lastFormalProgressTimestamp,
                            Stopwatch.GetTimestamp());
                    }
                    _lastHeartbeatAckSequence = message.Heartbeat.Sequence;
                    _journal.LastHeartbeatAckSequence = _lastHeartbeatAckSequence;
                    if (verifiedActiveRunChanged)
                    {
                        lock (_journalGate)
                        {
                            if (!TryPersistJournalSnapshotLocked())
                                RecordEvent(
                                    "LastVerifiedActiveRunPersistenceFailed",
                                    $"RunId={message.Heartbeat.RunId};" +
                                    $"Epoch={message.Heartbeat.RunEpoch};" +
                                    $"Sequence={message.Heartbeat.Sequence}");
                        }
                    }
                    SaveJournal();
                    RecordHeartbeatCheckpoint();
                    Send(new WatchdogMessage
                    {
                        Type = WatchdogMessageType.HeartbeatAck,
                        SessionId = _args.SessionId,
                        AckSequence = message.Heartbeat.Sequence,
                        CorrelationId = message.CorrelationId
                    });
                    break;
                case WatchdogMessageType.Pong:
                    break;
                case WatchdogMessageType.ExternalRecoveryRequired:
                    BeginTakeover("ExternalRecoveryRequired:" + message.Reason);
                    break;
                case WatchdogMessageType.RecoveryCheckpointValidated:
                    if (message.CheckpointMirror != null)
                    {
                        _journal.LastCheckpointMirror = message.CheckpointMirror;
                        var lastVerified = _journal.LastVerifiedActiveRun;
                        if (lastVerified != null && message.CheckpointMirror.Armed &&
                            string.Equals(
                                lastVerified.RunId,
                                message.CheckpointMirror.RunId,
                                StringComparison.OrdinalIgnoreCase) &&
                            lastVerified.RunEpoch == message.CheckpointMirror.RunEpoch)
                        {
                            lastVerified.SelectedChannels =
                                (message.CheckpointMirror.SelectedChannels ?? Array.Empty<int>())
                                .Where(channel => channel >= 1 && channel <= 12)
                                .Distinct()
                                .OrderBy(channel => channel)
                                .ToArray();
                            lastVerified.CheckpointRunId = message.CheckpointMirror.RunId;
                            lastVerified.CheckpointRunEpoch = message.CheckpointMirror.RunEpoch;
                            lastVerified.CheckpointRevision = message.CheckpointMirror.Revision;
                            lastVerified.CheckpointSha256 = message.CheckpointMirror.Sha256;
                            lock (_journalGate) TryPersistJournalSnapshotLocked();
                        }
                    }
                    Record("RecoveryCheckpointValidated", message.Reason);
                    break;
                case WatchdogMessageType.SafetyPreflightPassed:
                    Record("SafetyPreflightPassed", message.Reason);
                    break;
                case WatchdogMessageType.RecoveryBatchCommitted:
                    var pipeCommitGeneration = message.RecoveryCommitGeneration > 0
                        ? message.RecoveryCommitGeneration
                        : Math.Max(1, _journal.LastRecoveryBatchCommitGeneration + 1);
                    var pipeCommitHeartbeat = message.Heartbeat ?? _journal.LastHeartbeat;
                    TryAcceptRecoveryBatchCommit(
                        pipeCommitGeneration,
                        "PipeMessage",
                        message.Reason,
                        pipeCommitHeartbeat,
                        message.RecoveryProgressToken);
                    break;
                case WatchdogMessageType.BatchStartFailed:
                    Record("BatchStartFailed", message.Reason);
                    if (BatchStartTakeoverPolicy.ShouldTakeover(message.BatchStartFailure))
                    {
                        BeginTakeover("BatchStartFailed:" + message.Reason);
                    }
                    else
                    {
                        CancelAutomaticTakeover(
                            "BatchStartFailedSafeIdle:" +
                            BatchStartTakeoverPolicy.DescribeRejection(message.BatchStartFailure));
                        Record(
                            "BatchStartFailedSafeIdle",
                            $"TakeoverRejected={BatchStartTakeoverPolicy.DescribeRejection(message.BatchStartFailure)};" +
                            $"Reason={message.Reason};主进程已完成启动回滚，Sidecar保持附着但禁止杀进程。");
                    }
                    break;
                case WatchdogMessageType.RecoveryAttemptFailed:
                    if (RecoveryFailurePolicy.IsSupersededByWatchdogTakeover(
                            Interlocked.CompareExchange(ref _takeoverStarted, 0, 0) != 0,
                            _activeTakeoverCorrelationId,
                            message.RecoveryFailureOwner,
                            message.RecoveryFailureCorrelationId,
                            message.RecoveryFailureCode,
                            message.Reason,
                            message.RecoveryFailureDetail))
                    {
                        Record(
                            "RecoverySupersededByTakeover",
                            $"CorrelationId={message.RecoveryFailureCorrelationId};" +
                            $"Code={message.RecoveryFailureCode};{message.Reason}");
                        _attached = false;
                        break;
                    }
                    var classification = RecoveryFailurePolicy.Classify(
                        message.RecoveryFailureCode,
                        message.RecoveryFailurePermanent,
                        message.Reason);
                    var failureReport = BuildFailureReport(message, classification);
                    Record(
                        "RecoveryAttemptFailed",
                        $"Code={classification.Code};Permanent={classification.Permanent};" +
                        $"RootCode={failureReport.RootCode};DeviceOrChannelGroup={failureReport.DeviceOrChannelGroup};" +
                        $"RunId={failureReport.RunId};Stage={failureReport.RecoveryStage};" +
                        $"ProgressToken={failureReport.RecoveryProgressToken};" +
                        $"ProcessSource={failureReport.RecoveryProcessSource};" +
                        $"ContextSha256={message.RecoveryFailureContextSha256};" +
                        (message.Reason ?? string.Empty));
                    _attached = false;
                    var failureDecision = RegisterRecoveryFailure(
                        failureReport,
                        classification,
                        message.CorrelationId,
                        recoveryFailurePayloadSha256,
                        message.CorrelationId,
                        connectionGeneration,
                        message.RecoveryFailureFingerprint);
                    if (failureDecision.DurableDecisionRetryPending)
                    {
                        Record(
                            "RecoveryFailureDecisionDeferred",
                            "StrictAuthorityBusy;CorrelationId=" + message.CorrelationId);
                        break;
                    }
                    if (classification.Permanent ||
                        failureDecision.SafeIdleRecoveryBlocked ||
                        (!failureDecision.ProcessRelaunchAllowed &&
                         !failureDecision.RelaunchPermitAlreadyPending))
                        EnterRelaunchCircuitOpen(
                            failureDecision.Fingerprint,
                            failureDecision.ConsecutiveCount,
                            message.RecoveryFailureDetail ?? message.Reason);
                    else if (failureDecision.RelaunchPermitGeneration > 0)
                        BeginRelaunchAfterExit(failureDecision.RelaunchPermitGeneration);
                    break;
                case WatchdogMessageType.StopCompleted:
                    Record("StopCompleted", message.StopSummary?.Detail ?? message.Reason);
                    if (_journal.ManualStopRequested)
                    {
                        if (Interlocked.CompareExchange(ref _operatorTransitionStopStarted, 0, 0) != 0)
                            _operatorStopAcknowledged.TrySetResult(true);
                        else
                        {
                            PublishTerminal("ManualStopCompleted", message.StopSummary?.Detail ?? message.Reason);
                            _stop.Cancel();
                        }
                    }
                    else if (Interlocked.CompareExchange(
                                 ref _manualPauseSafetyTakeoverStarted,
                                 0,
                                 0) != 0)
                    {
                        // The manual-pause safety owner must reopen only in
                        // idle mode.  Generic recovery relaunch would resume
                        // the test and violate the operator's pause command.
                    }
                    else
                    {
                        // The durable permit must exist before the main process is
                        // allowed to publish its typed takeover-exit receipt.  The
                        // client reads this exact authority tuple and refuses to
                        // close if approval has not reached disk yet.
                        var approvedGeneration = ApproveRelaunchPermit("StopCompleted");
                        if (approvedGeneration > 0)
                            BeginRelaunchAfterExit(approvedGeneration);
                        else
                            Record(
                                "TakeoverExitPermitApprovalFailed",
                                "StopCompleted;MainProcessMustRemainVisible");
                    }
                    break;
                case WatchdogMessageType.PhysicalStopConfirmed:
                    Interlocked.Exchange(ref _physicalStopConfirmed, 1);
                    Record("PhysicalStopConfirmed", message.Reason);
                    if (Interlocked.CompareExchange(ref _operatorTransitionStopStarted, 0, 0) != 0)
                        _operatorStopAcknowledged.TrySetResult(true);
                    break;
                case WatchdogMessageType.ManualStopIntent:
                case WatchdogMessageType.ManualStopRequested:
                    _journal.ManualStopRequested = true;
                    CancelAutomaticTakeover("ManualStopIntent");
                    Interlocked.CompareExchange(
                        ref _manualStopIntentTimestamp,
                        Stopwatch.GetTimestamp(),
                        0);
                    Record("ManualStopIntent", message.Reason);
                    break;
                case WatchdogMessageType.RunStopped:
                case WatchdogMessageType.RunCompleted:
                    _journal.ManualStopRequested = true;
                    CancelAutomaticTakeover(message.Type);
                    Record(message.Type, message.Reason);
                    if (Interlocked.CompareExchange(ref _operatorTransitionStopStarted, 0, 0) != 0)
                        _operatorStopAcknowledged.TrySetResult(true);
                    // RunStopped/RunCompleted only close the relaunch permit.
                    // Keep the exact pipe alive until StopCompleted proves the
                    // physical/data stop boundary, or an explicit application
                    // exit completes the connection lifecycle.
                    break;
                case WatchdogMessageType.ApplicationClosing:
                    _journal.ManualStopRequested = true;
                    CancelAutomaticTakeover("ApplicationClosing");
                    Record(message.Type, message.Reason);
                    PublishTerminal("ExpectedApplicationExit", message.Reason);
                    _stop.Cancel();
                    break;
                case WatchdogMessageType.ApplicationExitRequested:
                    Record(
                        "ApplicationExitRequested",
                        message.ApplicationExit?.ExitIntentId ?? message.Reason);
                    ObserveApplicationExitIntent();
                    break;
                case WatchdogMessageType.SafetyHandoffRequested:
                    Record("SafetyHandoffRequested", message.SafetyHandoff?.HandoffId ?? message.Reason);
                    WatchdogSafetyHandoffReceipt requested;
                    if (WatchdogSafetyHandoffReceiptStore.TryRead(
                            _args.JournalDirectory,
                            _args.SessionId,
                            out requested))
                        BeginSafetyHandoff(requested);
                    break;
                case WatchdogMessageType.ShutdownExpected:
                    _journal.ManualStopRequested = true;
                    CancelAutomaticTakeover("ShutdownExpected");
                    Record(message.Type, message.Reason);
                    PublishTerminal("ExpectedApplicationExit", message.Reason);
                    if (!ObserveApplicationExitIntent())
                        StartExpectedMainExitObserver(
                            message.Type,
                            message.Reason,
                            completeHostOnExit: true);
                    break;
                case WatchdogMessageType.WatchdogTakeoverExit:
                    // Watchdog接管已完成StopCompleted后，旧主进程只交回UI/传输所有权。
                    // 不得把它当成人工ShutdownExpected，否则会撤销仍需消费的恢复许可。
                    Record(message.Type, message.Reason);
                    StartExpectedMainExitObserver(
                        message.Type,
                        message.Reason,
                        completeHostOnExit: false);
                    break;
            }
            return Task.CompletedTask;
        }

        private void StartExpectedMainExitObserver(
            string messageType,
            string reason,
            bool completeHostOnExit)
        {
            int processId;
            long processStartUtcTicks;
            long attachEpoch;
            lock (_journalGate)
            {
                processId = _journal.CurrentPid;
                processStartUtcTicks = _journal.CurrentProcessStartUtcTicks;
                attachEpoch = Interlocked.Read(ref _validatedAttachEpoch);
            }
            if (processId <= 0 || processStartUtcTicks <= 0 || attachEpoch <= 0)
            {
                Record("MainProcessExitObserverRejected", "ExactIdentityMissing:" + messageType);
                return;
            }
            if (Interlocked.CompareExchange(
                    ref _expectedExitObserverEpoch,
                    attachEpoch,
                    0) != 0)
                return;
            _ = Task.Run(() => ObserveExpectedMainExitAsync(
                messageType,
                reason,
                completeHostOnExit,
                processId,
                processStartUtcTicks,
                attachEpoch));
        }

        private async Task ObserveExpectedMainExitAsync(
            string messageType,
            string reason,
            bool completeHostOnExit,
            int processId,
            long processStartUtcTicks,
            long attachEpoch)
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline &&
                       IsExactProcessAlive(processId, processStartUtcTicks))
                    await Task.Delay(100).ConfigureAwait(false);
                if (!IsExactProcessAlive(processId, processStartUtcTicks))
                {
                    Record(
                        "MainProcessExited",
                        $"{messageType}:{reason};PID={processId};AttachEpoch={attachEpoch}");
                    if (completeHostOnExit) _stop.Cancel();
                    return;
                }

                Record(
                    "MainProcessExitStalled",
                    $"PID={processId};Message={messageType};Reason={reason};AttachEpoch={attachEpoch}");
                _transitionWindow.Show(
                    "安全停止已完成，正在释放旧程序",
                    "主程序在退出回执后超过5秒仍未结束，正在执行有界回收。",
                    0,
                    0);
                try
                {
                    using (var process = Process.GetProcessById(processId))
                    {
                        if (!MatchesExactProcess(process, processStartUtcTicks))
                        {
                            Record("MainProcessExitStalledIdentityMismatch", processId.ToString());
                            return;
                        }
                        await CaptureMiniDumpBeforeTerminationAsync(
                                process,
                                "MainProcessExitStalled:" + messageType)
                            .ConfigureAwait(false);
                        process.Kill();
                        process.WaitForExit(5000);
                        Record("MainProcessExitStalledTerminated", $"PID={processId}");
                    }
                }
                catch (Exception ex)
                {
                    Record("MainProcessExitStalledTerminationFailed", ex.Message);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _expectedExitObserverEpoch, 0, attachEpoch);
                if (completeHostOnExit) _stop.Cancel();
            }
        }

        private static bool IsExactProcessAlive(int processId, long processStartUtcTicks)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return MatchesExactProcess(process, processStartUtcTicks) && !process.HasExited;
            }
            catch { return false; }
        }

        private static bool MatchesExactProcess(Process process, long processStartUtcTicks)
        {
            if (process == null || processStartUtcTicks <= 0) return false;
            try { return process.StartTime.ToUniversalTime().Ticks == processStartUtcTicks; }
            catch { return false; }
        }

        private bool IsValidatedHeartbeatIdentity(WatchdogHeartbeat heartbeat)
        {
            if (heartbeat == null || heartbeat.ProcessId <= 0 ||
                heartbeat.ProcessStartUtcTicks <= 0)
                return false;
            lock (_journalGate)
            {
                // Before the first Attach, the journal is seeded with the
                // expected parent identity.  After Attach it is replaced by
                // the validated process identity.  A late pipe from an old
                // process therefore cannot mutate the durable heartbeat or
                // stop ordering state.
                return _journal.CurrentPid == heartbeat.ProcessId &&
                       _journal.CurrentProcessStartUtcTicks ==
                       heartbeat.ProcessStartUtcTicks;
            }
        }

        internal static bool IsVerifiedHardwareSafeIdle(
            WatchdogHeartbeat heartbeat,
            int currentProcessId,
            long currentProcessStartUtcTicks,
            bool processAlive,
            double heartbeatAgeSeconds,
            bool sessionRevoked,
            bool manualStopRequested,
            bool alreadyTakingOver)
        {
            return heartbeat != null &&
                   processAlive &&
                   heartbeatAgeSeconds >= 0 &&
                   heartbeatAgeSeconds < 5 &&
                   !sessionRevoked &&
                   !manualStopRequested &&
                   !alreadyTakingOver &&
                   heartbeat.ProcessId == currentProcessId &&
                   heartbeat.ProcessStartUtcTicks == currentProcessStartUtcTicks &&
                   RecoveryFailurePolicy.IsRecoveryProcessSource(
                       heartbeat.RecoveryProcessSource) &&
                   heartbeat.HardwareUnavailable &&
                   string.Equals(
                       heartbeat.Phase,
                       "SafeIdleHardwareUnavailable",
                       StringComparison.Ordinal) &&
                   heartbeat.EnergizedChannelCount == 0 &&
                   !string.IsNullOrWhiteSpace(
                       heartbeat.HardwareFailureFingerprint) &&
                   heartbeat.HardwareProbeAttempt > 0 &&
                   heartbeat.HardwareNextProbeUtc > 0;
        }

        private bool TryValidateAttachBeforeMutation(
            WatchdogRunSession session,
            long connectionGeneration,
            out bool sameAuthorityReconnect,
            out string failure)
        {
            sameAuthorityReconnect = false;
            failure = string.Empty;
            if (session == null)
            {
                failure = "AttachSessionMissing";
                return false;
            }
            if (!string.Equals(
                    _args.SessionId,
                    session.SessionId,
                    StringComparison.Ordinal))
            {
                failure = "AttachSessionMismatch";
                return false;
            }
            if (session.ProcessId <= 0 || session.ProcessStartUtcTicks <= 0)
            {
                failure = "AttachProcessIdentityMissing";
                return false;
            }
            if (ProbeProcessIdentity(
                    session.ProcessId,
                    session.ProcessStartUtcTicks) !=
                DurableRelaunchProcessObservation.Alive)
            {
                failure = "AttachProcessNotAliveOrReused";
                return false;
            }

            int currentPid;
            long currentStartTicks;
            bool alreadyAttached;
            long attachedConnectionGeneration;
            lock (_journalGate)
            {
                currentPid = _journal.CurrentPid;
                currentStartTicks = _journal.CurrentProcessStartUtcTicks;
                alreadyAttached = _attached;
                attachedConnectionGeneration = _attachedConnectionGeneration;
            }

            var sameIdentity = currentPid == session.ProcessId &&
                               currentStartTicks == session.ProcessStartUtcTicks;
            if (alreadyAttached && sameIdentity &&
                attachedConnectionGeneration == connectionGeneration)
            {
                failure = "AttachDuplicateCurrentAuthority";
                return false;
            }

            if (alreadyAttached && sameIdentity &&
                attachedConnectionGeneration != connectionGeneration)
                sameAuthorityReconnect = true;

            // An existing live identity owns the session even when the pipe
            // flag has been cleared after a transport interruption.  Only a
            // dead/reused authority may be replaced by a validated recovery
            // process or a new main process.
            if (currentPid > 0 && currentStartTicks > 0 && !sameIdentity &&
                ProbeProcessIdentity(currentPid, currentStartTicks) ==
                    DurableRelaunchProcessObservation.Alive)
            {
                failure = "AttachCurrentAuthorityStillAlive";
                return false;
            }
            return true;
        }

        private async Task MonitorAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    if (ObserveSafetyHandoffProgress()) continue;
                    if (ShouldProbeCircuitHalfOpen(IsRecoveryBlocked(), _attached))
                    {
                        TryBeginAutomaticCircuitHalfOpen();
                        continue;
                    }
                    ObserveApplicationExitIntent();
                    if (ObserveDurableSafetyState()) continue;
                    if (!_attached) continue;
                    if (WatchdogRecoveryCommitMarker.TryRead(
                            _args.JournalDirectory,
                            _args.SessionId,
                            out WatchdogRecoveryCommitEvidence marker) &&
                        marker != null &&
                        (marker.Legacy ||
                         marker.RunEpoch > 0 &&
                         !string.IsNullOrWhiteSpace(marker.RunId) &&
                         !string.IsNullOrWhiteSpace(marker.Stage)) &&
                        marker.Generation > 0 &&
                        (marker.Legacy || marker.GeneratedUtcTicks > 0) &&
                        marker.Generation is var durableCommitGeneration &&
                        durableCommitGeneration > _journal.LastRecoveryBatchCommitGeneration)
                    {
                        var commitHeartbeat = _journal.LastHeartbeat;
                        // The marker proves only that the client durably reached
                        // the boundary.  Wait for a heartbeat carrying the same
                        // generation and full new-run context before mutating the
                        // strict authority.
                        if (commitHeartbeat != null &&
                            commitHeartbeat.RecoveryBatchCommitGeneration >= durableCommitGeneration &&
                            (marker.Legacy ||
                             string.Equals(marker.RunId, commitHeartbeat.RunId, StringComparison.OrdinalIgnoreCase) &&
                             marker.RunEpoch == commitHeartbeat.RunEpoch &&
                             string.Equals(marker.Stage, commitHeartbeat.RecoveryStage, StringComparison.Ordinal)))
                            TryAcceptRecoveryBatchCommit(
                                durableCommitGeneration,
                                "DurableMarker",
                                $"Generation={durableCommitGeneration}",
                                commitHeartbeat);
                    }
                    var closeFenceAction = CaptureCloseFenceAction(
                        activeConnection: _attached,
                        currentProcessAlive: IsCurrentProcessAlive(),
                        out var closingFence,
                        out var legacyRevoked);
                    if (closeFenceAction == WatchdogCloseFenceAction.SuppressRelaunch)
                    {
                        ObserveClosingFence(closingFence);
                        continue;
                    }
                    if (closeFenceAction == WatchdogCloseFenceAction.TerminateSession)
                    {
                        // 过渡窗人工停止会先写耐久撤权 marker，让主程序在管道失效时
                        // 也能主动全断能。此时 Sidecar 必须继续等待该停止收口，不能
                        // 被自己刚写的 marker 提前结束。
                        if (IsTransitionOperatorStopInProgress()) continue;
                        _journal.ManualStopRequested = true;
                        var closingOwnerExited = closingFence?.State ==
                                                 WatchdogClosingTombstoneState.Closing;
                        var eventType = closingOwnerExited
                            ? "SessionClosingOwnerExited"
                            : "SessionRevoked";
                        var detail = closingOwnerExited
                            ? $"StateVersion={closingFence.StateVersion};MainProcessAlive=False"
                            : legacyRevoked
                                ? "LegacyRevocationMarker"
                                : $"ClosingTombstoneState={closingFence?.State}";
                        Record(eventType, detail);
                        PublishTerminal(eventType, detail);
                        _stop.Cancel();
                        continue;
                    }
                    var permit = _relaunchCoordinator.Snapshot;
                    if (permit?.State == DurableRelaunchPermitState.Approved &&
                        _automaticTakeover.ActiveStage ==
                            TakeoverTransactionStage.RelaunchPermit &&
                        permit.LastTransitionUtcTicks > 0)
                    {
                        var permitAgeSeconds = Math.Max(
                            0,
                            (DateTime.UtcNow.Ticks - permit.LastTransitionUtcTicks) /
                            (double)TimeSpan.TicksPerSecond);
                        if (permitAgeSeconds >= 2 &&
                            Interlocked.CompareExchange(ref _permitStalledLogged, 1, 0) == 0)
                            Record(
                                "PermitStalled",
                                $"Generation={permit.Generation};AgeSeconds={permitAgeSeconds:F3};" +
                                $"Stage={_automaticTakeover.ActiveStage}");
                        if (permitAgeSeconds >= 5)
                        {
                            CancelAutomaticTakeover("PermitStalledHardDeadline");
                            continue;
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _permitStalledLogged, 0);
                    }
                    var heartbeatAge = ElapsedSeconds(Interlocked.Read(ref _lastHeartbeatTimestamp));
                    if (heartbeatAge >= 3 && heartbeatAge < 5)
                    {
                        if (Interlocked.CompareExchange(ref _heartbeatSuspectLogged, 1, 0) == 0)
                            RecordEvent("HeartbeatSuspect", $"HeartbeatAgeSeconds={heartbeatAge:F3}");
                        Send(WatchdogMessageType.Ping, "HeartbeatSuspect", null);
                    }
                    var heartbeat = _journal.LastHeartbeat;
                    var semanticSnapshotAge = SemanticSnapshotAgeSeconds(
                        heartbeat,
                        DateTime.UtcNow);
                    var typedClosingGrace = heartbeat != null &&
                        !string.IsNullOrWhiteSpace(heartbeat.TypedExitTransactionId) &&
                        semanticSnapshotAge < 30 &&
                        (string.Equals(heartbeat.UiLifecycle, "Stopping",
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(heartbeat.UiLifecycle, "Closed",
                             StringComparison.OrdinalIgnoreCase));
                    if (semanticSnapshotAge >= 1 && !typedClosingGrace &&
                        Interlocked.CompareExchange(
                            ref _firstLivenessEvidenceCaptured,
                            1,
                            0) == 0)
                    {
                        RecordEvent(
                            "SemanticSnapshotStalledFirstEvidence",
                            $"Pulse={heartbeat?.PulseSequence};" +
                            $"SnapshotRevision={heartbeat?.SnapshotRevision};" +
                            $"SnapshotAgeSeconds={semanticSnapshotAge:F3};" +
                            $"UiLifecycle={heartbeat?.UiLifecycle};" +
                            $"ControlProgress={heartbeat?.ControlProgressVersion}");
                        CaptureFirstLivenessEvidence(
                            currentProcessId: _journal.CurrentPid,
                            currentProcessStartUtcTicks:
                                _journal.CurrentProcessStartUtcTicks,
                            reason: "SemanticSnapshotStalled");
                    }
                    var effectiveHeartbeatAge = typedClosingGrace
                        ? heartbeatAge
                        : Math.Max(heartbeatAge, semanticSnapshotAge);
                    var eligibleChannels = GetRecoveryEligibleChannels(heartbeat);
                    var processAlive = IsCurrentProcessAlive();
                    int currentProcessId;
                    long currentProcessStartUtcTicks;
                    lock (_journalGate)
                    {
                        currentProcessId = _journal.CurrentPid;
                        currentProcessStartUtcTicks = _journal.CurrentProcessStartUtcTicks;
                    }
                    WatchdogHostSendQueueOwner activeSendOwner;
                    lock (_gate) activeSendOwner = _sendOwner;
                    var attachEpoch = Volatile.Read(ref _validatedAttachEpoch);
                    var liveness = _applicationLiveness.Evaluate(
                        effectiveHeartbeatAge,
                        processAlive,
                        currentProcessId,
                        currentProcessStartUtcTicks,
                        attachEpoch,
                        activeSendOwner?.ConnectionGeneration ?? 0,
                        Stopwatch.GetTimestamp(),
                        Stopwatch.Frequency);
                    if (liveness.RetireConnection && activeSendOwner != null)
                    {
                        RecordEvent(
                            "HeartbeatTransportDegraded",
                            $"HeartbeatAgeSeconds={heartbeatAge:F3};" +
                            $"Probe={liveness.ProbeStatus};Detail={liveness.Detail};" +
                            $"ConnectionGeneration={activeSendOwner.ConnectionGeneration}");
                        RetireConnection(activeSendOwner, "ResponsiveUiHeartbeatTransportDegraded");
                        _channelProgressTracker.Reset();
                        Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
                        Interlocked.Exchange(ref _lastFormalProgressTimestamp, Stopwatch.GetTimestamp());
                    }
                    else if (heartbeatAge >=
                             WatchdogApplicationLivenessSupervisor.HeartbeatTakeoverSeconds &&
                             liveness.ReportEvent &&
                             liveness.ProbeStatus != WatchdogUiProbeStatus.Throttled &&
                             liveness.ProbeStatus != WatchdogUiProbeStatus.NotRequired)
                    {
                        RecordEvent(
                            liveness.HeartbeatUnresponsiveConfirmed
                                ? "HeartbeatUnresponsiveConfirmed"
                                : "HeartbeatUiProbe",
                            $"HeartbeatAgeSeconds={heartbeatAge:F3};" +
                            $"Probe={liveness.ProbeStatus};Failures={liveness.ConsecutiveFailures};" +
                            $"Detail={liveness.Detail}");
                    }
                    var heartbeatAgeForTakeover = liveness.SuppressHeartbeatTakeover
                        ? 0
                        : effectiveHeartbeatAge;
                    var nowUtc = DateTime.UtcNow;
                    var manualPauseCommanded = heartbeat != null &&
                        WatchdogTakeoverPolicy.IsManualPauseCommanded(
                            heartbeat.ManualPauseActive,
                            heartbeat.ManualPausePending);
                    var manualPauseAgeSeconds = manualPauseCommanded &&
                                                Interlocked.Read(ref _manualPauseStartedTimestamp) > 0
                        ? ElapsedSeconds(Interlocked.Read(ref _manualPauseStartedTimestamp))
                        : 0;
                    var manualPauseNoProgressSeconds = manualPauseCommanded
                        ? ElapsedSeconds(Interlocked.Read(ref _manualPauseProgressTimestamp))
                        : 0;
                    var stageSinceUtc = heartbeat?.StopAllActive == true && heartbeat.StopStageStartedUtc > 0
                        ? heartbeat.StopStageStartedUtc
                        : heartbeat?.PowerOffUnconfirmed == true && heartbeat.PowerDisableSince > 0
                            ? heartbeat.PowerDisableSince
                            : heartbeat?.PauseSince ?? 0;
                    var stageAgeSeconds = stageSinceUtc > 0
                        ? Math.Max(0, (DateTime.UtcNow.Ticks - stageSinceUtc) / (double)TimeSpan.TicksPerSecond)
                        : ElapsedSeconds(Interlocked.Read(ref _lastProgressTimestamp));
                    var stopNoProgressSeconds = _stopSafetyMonitor
                        .NoProgressSeconds(DateTime.UtcNow);
                    if (_journal.ManualStopRequested)
                    {
                        var manualAge = ElapsedSeconds(Interlocked.Read(ref _manualStopIntentTimestamp));
                        if (manualAge >= 5 &&
                            Interlocked.CompareExchange(ref _manualStopEmergencyResent, 1, 0) == 0)
                        {
                            Record("ManualStopEmergencyResent", $"AgeSeconds={manualAge:F3}");
                            Send(WatchdogMessageType.RequestStopAll,
                                "ManualStopNoProgress5s", Guid.NewGuid().ToString("N"));
                        }
                    }
                    var logicalResidue = heartbeat != null && !manualPauseCommanded && !heartbeat.RunActive &&
                        (heartbeat.TimerCount > 0 || heartbeat.RunnerCount > 0 ||
                         heartbeat.EnergizedChannelCount > 0 ||
                         heartbeat.StopCtsCount > 0 || heartbeat.CyclePauseCtsCount > 0 ||
                         heartbeat.ActiveCycleCount > 0 ||
                         heartbeat.DaqRecoveryCount > 0 || heartbeat.SoftwareRecoveryCount > 0 ||
                         heartbeat.RecoveryOwnerCount > 0);
                    var inconsistentRecovery = heartbeat != null && !manualPauseCommanded && !heartbeat.RecoveryActive &&
                        (heartbeat.DaqRecoveryCount > 0 || heartbeat.SoftwareRecoveryCount > 0 ||
                         heartbeat.RecoveryOwnerCount > 0);
                    var unstructuredRecovery = heartbeat != null && !manualPauseCommanded &&
                        WatchdogRecoveryTelemetryPolicy.IsUnstructuredRecoveryClaim(heartbeat);
                    if (unstructuredRecovery)
                    {
                        if (Interlocked.CompareExchange(ref _unstructuredRecoveryLogged, 1, 0) == 0)
                            RecordEvent(
                                "RecoveryTelemetryInconsistent",
                                $"RecoveryActive=true without owner/incident/stage;" +
                                $"SoftwareRecoveryCount={heartbeat.SoftwareRecoveryCount};" +
                                $"ActiveCycleCount={heartbeat.ActiveCycleCount};global takeover suppressed");
                        Send(WatchdogMessageType.Ping, "RecoveryTelemetryRefreshRequested", null);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _unstructuredRecoveryLogged, 0);
                    }
                    var formalProgressStalled = heartbeat != null &&
                        heartbeat.RunActive &&
                        string.Equals(heartbeat.Phase, "Formal", StringComparison.OrdinalIgnoreCase) &&
                        (heartbeat.TimerCount > 0 || heartbeat.RunnerCount > 0 ||
                         heartbeat.EnergizedChannelCount > 0) &&
                        ElapsedSeconds(Interlocked.Read(ref _lastFormalProgressTimestamp)) >=
                        WatchdogTakeoverPolicy.SelectFormalProgressTimeoutSeconds(
                            heartbeat.ExpectedCyclePeriodMs);
                    var channelSupervision = EvaluateChannelSupervision(
                        heartbeat,
                        manualPauseCommanded,
                        eligibleChannels);
                    if (channelSupervision.RefreshRequested)
                    {
                        var logicalSourceRefresh =
                            channelSupervision.RefreshReason?.StartsWith(
                                "WatchdogLogicalSourceStale",
                                StringComparison.Ordinal) == true;
                        RecordEvent(
                            logicalSourceRefresh
                                ? "WatchdogLogicalSourceRefreshRequested"
                                : "ChannelRuntimeContractRefreshRequested",
                            channelSupervision.RefreshReason);
                        Send(
                            WatchdogMessageType.Ping,
                            (logicalSourceRefresh
                                ? "WatchdogLogicalSourceRefreshRequested:"
                                : "ChannelRuntimeContractRefreshRequested:") +
                            channelSupervision.RefreshReason,
                            null);
                    }
                    var channelSupervisionReason = channelSupervision.TakeoverReason;
                    var channelSupervisionFailed =
                        !string.IsNullOrWhiteSpace(channelSupervisionReason);
                    var applicationTakeoverConfirmed = !processAlive ||
                        liveness.HeartbeatUnresponsiveConfirmed;
                    var stopActive = heartbeat?.StopAllActive == true ||
                        heartbeat?.StopTakeoverRequired == true ||
                        heartbeat?.StopTimedOut == true;
                    var responsiveControlFault = ResponsiveControlRepairPolicy.ShouldRequest(
                        processAlive,
                        applicationTakeoverConfirmed,
                        stopActive,
                        logicalResidue,
                        inconsistentRecovery,
                        formalProgressStalled,
                        channelSupervisionFailed);
                    var repairReason = channelSupervisionFailed
                        ? channelSupervisionReason
                        : formalProgressStalled
                            ? "FormalProgressStalled"
                            : logicalResidue
                                ? "LogicalResidue"
                                : "InconsistentRecovery";
                    var responsiveRepair = _responsiveControlRepair.Evaluate(
                        responsiveControlFault,
                        stopActive,
                        Stopwatch.GetTimestamp(),
                        Stopwatch.Frequency);
                    if (responsiveRepair.ReportTakeoverConfirmation)
                    {
                        RecordEvent(
                            "ResponsiveControlRepairDeadlineExceeded",
                            $"Reason={repairReason};" +
                            $"ElapsedSeconds={responsiveRepair.ElapsedSeconds:F3};" +
                            "UiResponsive=true;StopTransactionObserved=false");
                    }
                    applicationTakeoverConfirmed |= responsiveRepair.TakeoverConfirmed;
                    if (stopActive &&
                        (logicalResidue || inconsistentRecovery ||
                         formalProgressStalled || channelSupervisionFailed) &&
                        !string.Equals(
                            _lastSuppressedControlRepairStopTransaction,
                            heartbeat?.StopTransactionId ?? string.Empty,
                            StringComparison.Ordinal))
                    {
                        _lastSuppressedControlRepairStopTransaction =
                            heartbeat?.StopTransactionId ?? string.Empty;
                        RecordEvent(
                            "ResponsiveControlRepairSuppressedDuringStop",
                            $"Transaction={heartbeat?.StopTransactionId};" +
                            $"Stage={heartbeat?.StopStage};LogicalResidue={logicalResidue};" +
                            $"InconsistentRecovery={inconsistentRecovery};" +
                            $"FormalProgressStalled={formalProgressStalled};" +
                            $"ChannelSupervisionFailed={channelSupervisionFailed}");
                    }
                    else if (!stopActive)
                        _lastSuppressedControlRepairStopTransaction = string.Empty;
                    if (responsiveRepair.RequestStopAll)
                    {
                        RecordEvent(
                            "ResponsiveApplicationControlRepairRequested",
                            repairReason);
                        Send(
                            WatchdogMessageType.RequestStopAll,
                            "ResponsiveApplicationControlRepair:" + repairReason,
                            Guid.NewGuid().ToString("N"));
                    }
                    var manualPauseDeadlineUtc = heartbeat?.ManualPauseHardDeadlineUtc ?? 0;
                    if (manualPauseCommanded && manualPauseDeadlineUtc <= 0)
                    {
                        var fallbackSeconds = ManualPauseSafetyPolicy.SelectHardDeadlineMilliseconds(
                            heartbeat?.ExpectedCyclePeriodMs ?? 1) / 1000.0;
                        manualPauseDeadlineUtc = DateTime.UtcNow
                            .AddSeconds(Math.Max(0, fallbackSeconds - manualPauseAgeSeconds)).Ticks;
                    }
                    var manualPauseEnergizedCount =
                        heartbeat?.ManualPauseEnergizedChannels?.Length > 0
                            ? heartbeat.ManualPauseEnergizedChannels.Length
                            : heartbeat?.EnergizedChannelCount ?? 0;
                    var manualPauseUnsafe = heartbeat != null && ManualPauseSafetyPolicy.ShouldTakeover(
                        heartbeat.ManualPausePending,
                        heartbeat.ManualPauseActive,
                        heartbeat.ManualPauseSafetyFault,
                        manualPauseEnergizedCount,
                        manualPauseDeadlineUtc,
                        nowUtc.Ticks,
                        manualPauseNoProgressSeconds);

                    // StopAll deadline/liveness decisions have one production
                    // owner.  The monitor evaluates the shared projection and
                    // the policy here; the supervisor supplies the once-only
                    // transaction gate.  The broad watchdog policy below is
                    // deliberately not given StopAll stage deadlines.
                    var sessionRevoked = IsSessionRevoked();
                    var alreadyTakingOver =
                        Interlocked.CompareExchange(ref _takeoverStarted, 0, 0) != 0;
                    var hardwareSafeIdle = IsVerifiedHardwareSafeIdle(
                        heartbeat,
                        _journal.CurrentPid,
                        _journal.CurrentProcessStartUtcTicks,
                        processAlive,
                        heartbeatAge,
                        sessionRevoked,
                        _journal.ManualStopRequested,
                        alreadyTakingOver);
                    _stopSafetyMonitor.EvaluateTick(
                        nowUtc,
                        processAlive,
                        heartbeatAgeForTakeover,
                        sessionRevoked,
                        _journal.ManualStopRequested,
                        alreadyTakingOver,
                        recoveryActive: false,
                        orphanPaused: false,
                        powerDisablePending: false,
                        hasRecoveryEligibleChannels: false,
                        manualPauseActive: manualPauseCommanded,
                        manualPauseUnsafe: manualPauseUnsafe,
                        hardwareSafeIdle: hardwareSafeIdle,
                        sink: DispatchStopSafetySupervisorTakeover);

                    var shouldTakeover = WatchdogTakeoverPolicy.ShouldTakeover(
                        IsSessionRevoked(),
                        _journal.ManualStopRequested,
                        Interlocked.CompareExchange(ref _takeoverStarted, 0, 0) != 0,
                        processAlive,
                        heartbeatAgeForTakeover,
                        WatchdogRecoveryTelemetryPolicy.ShouldTreatAsRecoveryActive(heartbeat),
                        heartbeat?.OrphanPaused == true,
                        heartbeat?.PowerOffUnconfirmed == true,
                        stageAgeSeconds,
                        eligibleChannels.Length > 0,
                        false,
                        logicalResidue && !stopActive && applicationTakeoverConfirmed,
                        inconsistentRecovery && !stopActive && applicationTakeoverConfirmed,
                        (formalProgressStalled || channelSupervisionFailed) && !stopActive &&
                        applicationTakeoverConfirmed,
                        manualPauseCommanded,
                        manualPauseUnsafe,
                        heartbeat?.RecoveryHardDeadlineUtc ?? 0,
                        nowUtc.Ticks,
                        ElapsedSeconds(Interlocked.Read(ref _lastProgressTimestamp)),
                        0,
                        0,
                        0,
                        0);
                    if (shouldTakeover)
                    {
                        var reason = responsiveRepair.TakeoverConfirmed
                            ? "ResponsiveControlRepairDeadlineExceeded:" + repairReason
                            : !processAlive || heartbeatAgeForTakeover >= 5
                            ? (heartbeatAgeForTakeover >= 5 ? "HeartbeatUnresponsive" : "ProcessExitedUnexpectedly")
                            : heartbeat?.PowerOffUnconfirmed == true && stageAgeSeconds >= 5
                                ? "PowerOffUnconfirmedTimeout"
                                : heartbeat?.OrphanPaused == true && stageAgeSeconds >= 5
                                    ? "OrphanPausedTimeout"
                                    : channelSupervisionFailed
                                        ? channelSupervisionReason
                                    : formalProgressStalled
                                        ? "FormalProgressStalledAggregateFallback"
                                    : "ExternalRecoveryStageStalled";
                        if (!processAlive)
                            Record("ProcessExitedUnexpectedly", reason);
                        if (manualPauseCommanded)
                            BeginManualPauseSafetyTakeover(
                                heartbeat?.ManualPauseSafetyFault == true
                                    ? "ManualPauseSafetyFault:" + heartbeat.ManualPauseSafetyFaultReason
                                    : "ManualPauseHardDeadlineExceeded");
                        else if (heartbeat?.StopAllActive == true ||
                                 heartbeat?.StopTakeoverRequired == true ||
                                 heartbeat?.StopTimedOut == true ||
                                 _journal.ManualStopRequested)
                            // StopAll/manual-stop is already evaluated by the
                            // single production EvaluateTick above.  Do not
                            // run a second policy or dispatch path here.
                            _ = reason;
                        else
                            BeginTakeover(reason);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Record("MonitorError", ex.Message); }
            }
        }

        private void BeginTakeover(string reason)
        {
            if (_journal.RecoveryBlocked || _journal.ManualStopRequested || IsSessionRevoked() ||
                Interlocked.CompareExchange(ref _takeoverStarted, 1, 0) != 0) return;
            var oldIdentity = FreezeCurrentProcessIdentity("AutomaticTakeoverBegin");
            if (!oldIdentity.IsValid)
            {
                Record("OldProcessExitUnproven", "AutomaticTakeoverIdentityMissing");
                BlockLaunchOutcomeUnknown("OldProcessExitUnproven:AutomaticTakeoverIdentityMissing");
                Interlocked.Exchange(ref _takeoverStarted, 0);
                return;
            }
            var correlationId = Guid.NewGuid().ToString("N");
            var authorityIdentity = string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}:{2}:{3}:{4}",
                _args.SessionId ?? string.Empty,
                _journal.CurrentPid,
                _journal.CurrentProcessStartUtcTicks,
                Volatile.Read(ref _validatedAttachEpoch),
                _journal.RunId ?? string.Empty);
            if (!_automaticTakeover.TryBegin(
                    correlationId,
                    authorityIdentity,
                    out var transaction))
            {
                Interlocked.Exchange(ref _takeoverStarted, 0);
                return;
            }
            Record(
                "TakeoverRequested",
                $"{reason};Generation={transaction.Generation};Authority={authorityIdentity}");
            _activeTakeoverCorrelationId = correlationId;
            Interlocked.Exchange(ref _transitionActive, 1);
            _transitionWindow.BeginTransition();
            _transitionWindow.Show(
                "检测到异常，正在安全接管",
                "正在请求原程序关闭全部输出。原因：" + DescribeRecoveryReason(reason),
                0,
                _journal.RecoveryAttempt + 1);
            Send(WatchdogMessageType.RequestStopAll, reason, correlationId);
            _ = Task.Run(() => TakeoverAsync(reason, transaction, oldIdentity));
        }

        private void CancelAutomaticTakeover(string reason)
        {
            if (!_automaticTakeover.TryCancel(reason, out var cancelled)) return;
            if (cancelled.CancelledFromStage >= TakeoverTransactionStage.RelaunchPermit &&
                cancelled.CancelledFromStage < TakeoverTransactionStage.ProcessTermination)
            {
                var revoked = _relaunchCoordinator.Revoke(
                    "AutomaticTakeoverSuperseded:" + (reason ?? "Unknown"));
                if (revoked?.Succeeded != true)
                {
                    Record(
                        "RelaunchPermitRevocationFailed",
                        $"Reason={reason};Generation={cancelled.Generation};" +
                        $"Status={revoked?.TransitionStatus};Detail={revoked?.Reason}");
                    BlockLaunchOutcomeUnknown("PermitRevocationUnproven:" + reason);
                }
                else
                {
                    lock (_journalGate)
                        ApplyDurablePermitLocked(revoked.Record);
                    Record(
                        "RelaunchPermitRevoked",
                        $"Reason={reason};Generation={revoked.Record?.Generation}");
                }
            }
            Record(
                "TakeoverCancelled",
                $"Reason={reason};Generation={cancelled.Generation};" +
                $"CorrelationId={cancelled.CorrelationId};" +
                $"Stage={cancelled.CancelledFromStage}");
            if (string.Equals(
                    _activeTakeoverCorrelationId,
                    cancelled.CorrelationId,
                    StringComparison.Ordinal))
                _activeTakeoverCorrelationId = string.Empty;
            Interlocked.Exchange(ref _takeoverStarted, 0);
            Interlocked.Exchange(ref _transitionActive, 0);
            _channelProgressTracker.Reset();
            _transitionWindow.Hide();
        }

        private void DispatchStopSafetySupervisorTakeover(string reason)
        {
            var detail = "StopSafetySupervisor:" + (reason ?? "Unknown");
            if (_journal.ManualStopRequested)
                BeginManualStopTakeover(detail);
            else
                BeginTakeover(detail);
        }

        private WatchdogChannelSupervisionEvaluation EvaluateChannelSupervision(
            WatchdogHeartbeat heartbeat,
            bool manualPauseCommanded,
            int[] eligibleChannels)
        {
            return _channelProgressTracker.EvaluateDetailed(
                heartbeat,
                eligibleChannels,
                manualPauseCommanded,
                Stopwatch.GetTimestamp(),
                Stopwatch.Frequency,
                DateTime.UtcNow.Ticks);
        }

        private void BeginManualStopTakeover(string reason)
        {
            if (!_journal.ManualStopRequested || IsSessionRevoked() ||
                Interlocked.CompareExchange(ref _manualStopTakeoverStarted, 1, 0) != 0)
                return;
            Record("ManualStopTakeoverRequested", reason);
            _ = Task.Run(() => ManualStopTakeoverAsync(reason));
        }

        private void BeginManualPauseSafetyTakeover(string reason)
        {
            if (IsSessionRevoked() ||
                Interlocked.CompareExchange(ref _takeoverStarted, 1, 0) != 0)
                return;
            Interlocked.Exchange(ref _manualPauseSafetyTakeoverStarted, 1);
            var correlationId = Guid.NewGuid().ToString("N");
            _activeTakeoverCorrelationId = correlationId;
            Interlocked.Exchange(ref _transitionActive, 1);
            _transitionWindow.BeginTransition();
            Record("ManualPauseSafetyTakeoverRequested", reason);
            _transitionWindow.Show(
                "人工暂停安全确认异常",
                "仅执行全断能并安全重开到空闲态，不会自动续跑。原因：" + DescribeRecoveryReason(reason),
                0,
                0);
            Send(WatchdogMessageType.RequestStopAll, "ManualPauseSafety:" + reason, correlationId);
            _ = Task.Run(() => ManualPauseSafetyTakeoverAsync(reason));
        }

        private async Task ManualPauseSafetyTakeoverAsync(string reason)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!IsTransitionOperatorStopInProgress() && !IsSessionRevoked() &&
                   DateTime.UtcNow < deadline && IsCurrentProcessAlive())
                await Task.Delay(250).ConfigureAwait(false);
            if (IsTransitionOperatorStopInProgress() ||
                DurableStopOrPauseCompletionSupersedesTakeover()) return;
            // An operator pause is never an automatic-resume authority.  Keep
            // requesting the main process' single StopAll owner and wait for a
            // durable Closing/handoff receipt; do not kill it or launch a UI.
            Send(WatchdogMessageType.RequestStopAll,
                "ManualPauseSafetyAwaitingDurableHandoff:" + reason,
                _activeTakeoverCorrelationId);
            Record("ManualPauseSafetyAwaitingDurableHandoff", reason);
        }

        private async Task ManualStopTakeoverAsync(string reason)
        {
            await Task.Yield();
            if (IsTransitionOperatorStopInProgress() ||
                DurableStopOrPauseCompletionSupersedesTakeover()) return;
            Send(WatchdogMessageType.RequestStopAll,
                "ManualStopSafetyAwaitingDurableHandoff:" + reason,
                Guid.NewGuid().ToString("N"));
            Record("ManualStopSafetyAwaitingDurableHandoff", reason);
        }

        private bool LaunchIdleRestart(string reason)
        {
            Process started;
            GuardedProcessOwnerReceipt launchOwner = null;
            var startedPid = 0;
            lock (_processLaunchGate)
            {
                if (RecoveryTransitionPolicy.MustSuppressAutomaticRestart(
                        IsTransitionOperatorStopInProgress(),
                        IsSessionRevoked()))
                {
                    Record("IdleRestartSuppressed", "OperatorTransitionStopOrSessionRevoked:" + reason);
                    return false;
                }
                var arguments = string.Format(
                    CultureInfo.InvariantCulture,
                    "--watchdog-idle-restart {0} --watchdog-pipe {1} --previous-pid {2} " +
                    "--sidecar-pid {3} --sidecar-start-ticks {4} --sidecar-instance-nonce {5}",
                    Quote(_args.SessionId),
                    Quote(_args.PipeName),
                    _journal.CurrentPid,
                    _sidecarProcessId,
                    _sidecarProcessStartUtcTicks,
                    Quote(_sidecarInstanceNonce));
                var approved = ApproveRelaunchPermit("IdleRestart:" + reason);
                if (approved <= 0) return false;
                DurableRelaunchPermitIdentity identity;
                int ignoredAttempt;
                if (!TryBeginLaunchIntent(approved, out identity, out ignoredAttempt, arguments)) return false;
                try
                {
                    launchOwner = TryStartCommittedLaunch(approved);
                }
                catch (RecoveryLaunchRejectedNoWorkException ex)
                {
                    Record("IdleRestartRejectedNoWork", ex.Message);
                    _transitionWindow.Hide();
                    return false;
                }
                catch (Exception ex)
                {
                    BlockLaunchOutcomeUnknown(
                        "IdleRestartLaunchOutcomeUnknown:" + ex.GetBaseException().Message);
                    return false;
                }
                started = launchOwner.Process;
                string startFailure;
                if (!TryCommitStarted(identity, launchOwner, out startFailure))
                {
                    launchOwner.KillExactAndDispose();
                    BlockLaunchOutcomeUnknown("IdleRestartStartedCommitFailed:" + startFailure);
                    return false;
                }
                startedPid = started.Id;
                _journal.CurrentPid = startedPid;
                _journal.CurrentProcessStartUtcTicks = started.StartTime.ToUniversalTime().Ticks;
            }
            Record("IdleProcessLaunched", $"PID={startedPid};Reason={reason};AutoResume=false");
            try { launchOwner?.Dispose(); } catch { }
            return true;
        }

        private async Task TakeoverAsync(
            string reason,
            TakeoverTransactionLease transaction,
            OldProcessIdentitySnapshot oldIdentity)
        {
            try
            {
                _transitionWindow.Show(
                    "正在确认设备安全状态",
                    "等待原程序完成全断能并退出。原因：" + DescribeRecoveryReason(reason),
                    0,
                    _journal.RecoveryAttempt + 1);
                var deadline = ResolveOldProcessExitDeadlineTimestamp(oldIdentity);
                while (!_journal.ManualStopRequested &&
                       !IsSessionRevoked() &&
                       Stopwatch.GetTimestamp() < deadline)
                {
                    var observation = ProbeProcessIdentity(
                        oldIdentity.ProcessId,
                        oldIdentity.ProcessStartUtcTicks);
                    if (observation != DurableRelaunchProcessObservation.Alive) break;
                    await Task.Delay(250, transaction.CancellationToken).ConfigureAwait(false);
                }
                if (_journal.ManualStopRequested ||
                    IsSessionRevoked() ||
                    !_automaticTakeover.IsAuthorized(transaction))
                    return;

                Process oldProcess = null;
                try
                {
                    var oldObservation = ProbeProcessIdentity(
                        oldIdentity.ProcessId,
                        oldIdentity.ProcessStartUtcTicks);
                    if (oldObservation == DurableRelaunchProcessObservation.Unknown)
                    {
                        Record("OldProcessExitUnproven", "AutomaticTakeoverProbeUnknown");
                        BlockLaunchOutcomeUnknown("OldProcessExitUnproven:AutomaticTakeoverProbeUnknown");
                        return;
                    }
                    if (oldObservation == DurableRelaunchProcessObservation.Alive)
                    {
                        oldProcess = Process.GetProcessById(oldIdentity.ProcessId);
                        if (!WatchdogProcessIdentityPolicy.CanKillOldProcess(
                                IsSessionRevoked(),
                                _journal.ManualStopRequested,
                                MatchesExactProcess(
                                    oldProcess,
                                    oldIdentity.ProcessStartUtcTicks)))
                            return;
                    }

                    var pipeline = await AutomaticTakeoverStageExecutor.ExecuteAsync(
                            _automaticTakeover,
                            transaction,
                            () => !_journal.ManualStopRequested &&
                                  !IsSessionRevoked() &&
                                  _automaticTakeover.IsAuthorized(transaction),
                            () => oldProcess == null
                                ? Task.CompletedTask
                                : CaptureMiniDumpBeforeTerminationAsync(
                                    oldProcess,
                                    "AutomaticTakeover:" + reason),
                            () => ApproveRelaunchPermit(reason),
                            oldProcess == null || oldProcess.HasExited
                                ? (Func<bool>)null
                                : () =>
                                {
                                    oldProcess.Kill();
                                    var exited = oldProcess.WaitForExit(
                                        WatchdogRecoveryReadinessPolicy
                                            .ForcedExitConfirmationSeconds * 1000);
                                    if (!exited) return false;
                                    if (oldProcess.HasExited)
                                        Record("OldProcessTerminated", reason);
                                    return oldProcess.HasExited;
                                },
                            permit =>
                            {
                                // AutomaticTakeover 与 ApplicationExit 只提交给同一个
                                // permit-scoped replacement coordinator。后续重复观察由
                                // _relaunchAfterExitStarted 幂等合并。
                                BeginRelaunchAfterExit(permit);
                                return Task.CompletedTask;
                            },
                            (current, requested, failure) => Record(
                                requested == TakeoverTransactionStage.RelaunchPermit &&
                                string.Equals(failure, "PermitNotConsumable", StringComparison.Ordinal)
                                    ? "TakeoverAbortedNoConsumablePermit"
                                    : "TakeoverStageRegression",
                                $"Current={current};Requested={requested};" +
                                $"Failure={failure};Reason={reason}"))
                        .ConfigureAwait(false);
                    if (!pipeline.Succeeded)
                    {
                        if (string.Equals(
                                pipeline.Failure,
                                "TerminationRejected",
                                StringComparison.Ordinal))
                        {
                            Record(
                                "OldProcessExitUnproven",
                                $"PID={oldIdentity.ProcessId};StartUtcTicks=" +
                                $"{oldIdentity.ProcessStartUtcTicks};WaitForExit=false");
                            BlockLaunchOutcomeUnknown(
                                "OldProcessExitUnproven:WaitForExitFalse");
                        }
                        return;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Record("OldProcessTerminationFailed", ex.Message);
                    Record(
                        "OldProcessExitUnproven",
                        $"PID={oldIdentity.ProcessId};StartUtcTicks=" +
                        $"{oldIdentity.ProcessStartUtcTicks};Error={ex.GetBaseException().Message}");
                    BlockLaunchOutcomeUnknown(
                        "OldProcessExitUnproven:TerminationProbeException");
                    return;
                }
                finally
                {
                    try { oldProcess?.Dispose(); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                // SafeIdle/manual-stop cancellation is an expected terminal
                // outcome for this generation, not a takeover failure.
            }
            catch (Exception ex)
            {
                Record("TakeoverTransactionFailed", ex.GetBaseException().Message);
            }
            finally
            {
                _automaticTakeover.Complete(transaction);
            }
        }

        private void BeginRelaunchAfterExit(long approvedPermitGeneration = 0)
        {
            if (IsRecoveryBlocked() || _journal.ManualStopRequested || IsSessionRevoked()) return;
            if (Interlocked.CompareExchange(
                    ref _relaunchAfterExitStarted,
                    1,
                    0) != 0)
                return;
            // Freeze the old process identity before the asynchronous observer
            // starts. Journal.CurrentPid is intentionally never consulted by
            // this transaction after a replacement launch can mutate it.
            var oldIdentity = FreezeOldProcessIdentityForPermit(
                approvedPermitGeneration,
                "RelaunchAfterExitBegin");
            if (!oldIdentity.IsValid)
            {
                Record("OldProcessExitUnproven", "RelaunchAfterExitIdentityMissing");
                BlockLaunchOutcomeUnknown("OldProcessExitUnproven:IdentityMissing");
                Interlocked.Exchange(ref _relaunchAfterExitStarted, 0);
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var permitGeneration = approvedPermitGeneration;
                    if (_journal.RecoveryAttempt > _journal.ConsecutiveStartupFailures)
                    {
                        // RegisterRecoveryFailure is the single strict-V4 failure
                        // transaction; it closes any active permit itself.
                        var decision = RegisterRecoveryFailure(
                            "RecoveryProcessExitedBeforeBatchCommit",
                            RecoveryFailurePolicy.Classify(
                                "RecoveryProcessExitedBeforeBatchCommit",
                                false,
                                "RecoveryProcessExitedBeforeBatchCommit"));
                        if (decision.SafeIdleRecoveryBlocked ||
                            (!decision.ProcessRelaunchAllowed &&
                             !decision.RelaunchPermitAlreadyPending))
                        {
                            EnterRelaunchCircuitOpen(
                                decision.Fingerprint,
                                decision.ConsecutiveCount,
                                "RecoveryProcessExitedBeforeBatchCommit");
                            return;
                        }
                        permitGeneration = decision.RelaunchPermitGeneration;
                    }
                    if (permitGeneration <= 0)
                        permitGeneration = ApproveRelaunchPermit("StopCompleted");
                    if (permitGeneration <= 0) return;
                    if (!IsConsumableRelaunchPermit(permitGeneration))
                    {
                        Record(
                            "RelaunchAfterExitAbortedNoConsumablePermit",
                            $"Generation={permitGeneration}");
                        return;
                    }
                    var approvedRecord = _relaunchCoordinator.Snapshot;
                    if (approvedRecord == null ||
                        approvedRecord.Generation != permitGeneration ||
                        !AdvanceReplacementState(
                            permitGeneration,
                            approvedRecord.PermitId,
                            RecoveryReplacementState.Approved,
                            "Approved recovery permit observed"))
                    {
                        BlockLaunchOutcomeUnknown(
                            "ReplacementTransactionPersistFailed:Approved");
                        return;
                    }
                    if (!await WaitForOldProcessExitProofAsync(
                            oldIdentity,
                            "StopCompleted")
                        .ConfigureAwait(false))
                        return;
                    if (!AugmentPostExitSafetyEvidence(
                            permitGeneration,
                            approvedRecord,
                            oldIdentity))
                    {
                        BlockLaunchOutcomeUnknown(
                            "PostExitSafetyEvidenceAugmentationFailed");
                        return;
                    }
                    if (!AdvanceReplacementState(
                            permitGeneration,
                            approvedRecord.PermitId,
                            RecoveryReplacementState.OldProcessExitProven,
                            "Exact old process exit proven"))
                    {
                        BlockLaunchOutcomeUnknown(
                            "ReplacementTransactionPersistFailed:OldProcessExitProven");
                        return;
                    }
                    if (_journal.ManualStopRequested || IsSessionRevoked()) return;

                    SafetyHandoffWaitResult crashPreparationFailure;
                    if (!EnsureCrashRecoverySafetyHandoffIfRequired(
                            permitGeneration,
                            oldIdentity,
                            out crashPreparationFailure))
                    {
                        BlockSafetyPrerequisite(
                            permitGeneration,
                            crashPreparationFailure);
                        return;
                    }

                    var safety = await AwaitSafetyHandoffBeforeRelaunchAsync(
                            permitGeneration,
                            TimeSpan.FromSeconds(
                                WatchdogRecoveryReadinessPolicy
                                    .SafetyHandoffDeadlineSeconds))
                        .ConfigureAwait(false);
                    if (!safety.AllowsRelaunch)
                    {
                        BlockSafetyPrerequisite(permitGeneration, safety);
                        return;
                    }
                    if (!AdvanceReplacementState(
                            permitGeneration,
                            approvedRecord.PermitId,
                            RecoveryReplacementState.SafetyAgentRunning,
                            "SafetyAgent stage evidence observed") ||
                        !AdvanceReplacementState(
                            permitGeneration,
                            approvedRecord.PermitId,
                            RecoveryReplacementState.SafetyCompleted,
                            "Physical safety handoff completed"))
                    {
                        BlockLaunchOutcomeUnknown(
                            "ReplacementTransactionPersistFailed:SafetyCompleted");
                        return;
                    }
                    Record(
                        "RecoveryReady",
                        $"PermitGeneration={permitGeneration};" +
                        $"SafetyOutcome={safety.Outcome};" +
                        $"ReadyTimestamp={safety.ReadyTimestamp};Detail={safety.Detail}");
                    await RelaunchLoopAsync(
                            "StopCompleted",
                            permitGeneration,
                            initialSafetyReplacement: true,
                            recoveryReadyTimestamp: safety.ReadyTimestamp)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Record("RelaunchAfterExitFailed", ex.GetBaseException().Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _relaunchAfterExitStarted, 0);
                }
            });
        }

        private OldProcessIdentitySnapshot FreezeCurrentProcessIdentity(string source)
        {
            lock (_journalGate)
            {
                return new OldProcessIdentitySnapshot
                {
                    ProcessId = _journal.CurrentPid,
                    ProcessStartUtcTicks = _journal.CurrentProcessStartUtcTicks,
                    ExitHardDeadlineUtcTicks = DateTime.UtcNow.AddSeconds(
                        WatchdogRecoveryReadinessPolicy
                            .OldProcessExitDeadlineSeconds).Ticks,
                    Source = source ?? "JournalSnapshot"
                };
            }
        }

        private OldProcessIdentitySnapshot FreezeOldProcessIdentityForPermit(
            long permitGeneration,
            string source)
        {
            WatchdogApplicationExitReceipt receipt;
            if (permitGeneration > 0 &&
                WatchdogApplicationExitReceiptStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out receipt) &&
                receipt?.PreservesApprovedPermit == true &&
                receipt.RelaunchPermitGeneration == permitGeneration &&
                WatchdogTakeoverPermitBindingPolicy.Matches(
                    _relaunchCoordinator.Snapshot,
                    _args.SessionId,
                    receipt.RelaunchPermitGeneration,
                    receipt.RelaunchPermitId,
                    receipt.RelaunchPermitNonceSha256))
            {
                return new OldProcessIdentitySnapshot
                {
                    ProcessId = receipt.MainProcessId,
                    ProcessStartUtcTicks = receipt.MainProcessStartUtcTicks,
                    ExitHardDeadlineUtcTicks = receipt.HardDeadlineUtcTicks,
                    Source = "ApplicationExitReceipt:" + (receipt.ExitIntentId ?? string.Empty)
                };
            }
            return FreezeCurrentProcessIdentity(source);
        }

        private async Task<bool> WaitForOldProcessExitProofAsync(
            OldProcessIdentitySnapshot oldIdentity,
            string reason)
        {
            if (oldIdentity?.IsValid != true)
            {
                Record("OldProcessExitUnproven", "FrozenIdentityMissing");
                BlockLaunchOutcomeUnknown("OldProcessExitUnproven:FrozenIdentityMissing");
                return false;
            }

            var deadline = ResolveOldProcessExitDeadlineTimestamp(oldIdentity);
            while (!_stop.IsCancellationRequested &&
                   !_journal.ManualStopRequested &&
                   !IsSessionRevoked())
            {
                var observation = ProbeProcessIdentity(
                    oldIdentity.ProcessId,
                    oldIdentity.ProcessStartUtcTicks);
                if (WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(observation))
                {
                    Record(
                        "OldProcessExitProven",
                        $"PID={oldIdentity.ProcessId};StartUtcTicks=" +
                        $"{oldIdentity.ProcessStartUtcTicks};Observation={observation};" +
                        $"Source={oldIdentity.Source}");
                    return true;
                }
                if (observation == DurableRelaunchProcessObservation.Unknown)
                {
                    Record(
                        "OldProcessExitUnproven",
                        $"PID={oldIdentity.ProcessId};StartUtcTicks=" +
                        $"{oldIdentity.ProcessStartUtcTicks};Observation=Unknown");
                    BlockLaunchOutcomeUnknown("OldProcessExitUnproven:ProbeUnknown");
                    return false;
                }
                if (Stopwatch.GetTimestamp() >= deadline) break;
                await Task.Delay(250).ConfigureAwait(false);
            }
            if (_stop.IsCancellationRequested || _journal.ManualStopRequested ||
                IsSessionRevoked())
                return false;

            try
            {
                using (var process = Process.GetProcessById(oldIdentity.ProcessId))
                {
                    if (!MatchesExactProcess(process, oldIdentity.ProcessStartUtcTicks))
                    {
                        Record(
                            "OldProcessExitProven",
                            $"PID={oldIdentity.ProcessId};Observation=IdentityMismatch");
                        return true;
                    }
                    await CaptureMiniDumpBeforeTerminationAsync(
                            process,
                            "OldProcessExitDeadline:" + reason)
                        .ConfigureAwait(false);
                    process.Kill();
                    var exited = process.WaitForExit(
                        WatchdogRecoveryReadinessPolicy
                            .ForcedExitConfirmationSeconds * 1000);
                    if (!exited)
                    {
                        Record(
                            "OldProcessExitUnproven",
                            $"PID={oldIdentity.ProcessId};WaitForExit=false");
                        BlockLaunchOutcomeUnknown(
                            "OldProcessExitUnproven:WaitForExitFalse");
                        return false;
                    }
                }
            }
            catch (ArgumentException)
            {
                // The exact PID disappeared between the probe and handle open.
            }
            catch (Exception ex)
            {
                Record(
                    "OldProcessExitUnproven",
                    $"PID={oldIdentity.ProcessId};ProbeOrTerminationError=" +
                    ex.GetBaseException().Message);
                BlockLaunchOutcomeUnknown(
                    "OldProcessExitUnproven:ProbeOrTerminationException");
                return false;
            }

            var finalObservation = ProbeProcessIdentity(
                oldIdentity.ProcessId,
                oldIdentity.ProcessStartUtcTicks);
            if (!WatchdogRecoveryReadinessPolicy.CanAuthorizeAfterExitObservation(
                    finalObservation,
                    waitForExitWasRequired: true,
                    waitForExitSucceeded: true))
            {
                Record(
                    "OldProcessExitUnproven",
                    $"PID={oldIdentity.ProcessId};StartUtcTicks=" +
                    $"{oldIdentity.ProcessStartUtcTicks};Observation={finalObservation}");
                BlockLaunchOutcomeUnknown(
                    "OldProcessExitUnproven:" + finalObservation);
                return false;
            }
            Record(
                "OldProcessExitProven",
                $"PID={oldIdentity.ProcessId};StartUtcTicks=" +
                $"{oldIdentity.ProcessStartUtcTicks};Observation={finalObservation};" +
                "ForcedTermination=true");
            return true;
        }

        private bool AugmentPostExitSafetyEvidence(
            long permitGeneration,
            DurableRelaunchPermitRecord authority,
            OldProcessIdentitySnapshot oldIdentity)
        {
            if (authority == null || oldIdentity?.IsValid != true ||
                authority.Generation != permitGeneration ||
                !WatchdogTakeoverPermitBindingPolicy.Matches(
                    _relaunchCoordinator.Snapshot,
                    _args.SessionId,
                    authority.Generation,
                    authority.PermitId,
                    WatchdogTakeoverPermitBindingPolicy.HashNonce(
                        authority.PermitNonce)))
                return false;

            var observation = ProbeProcessIdentity(
                oldIdentity.ProcessId,
                oldIdentity.ProcessStartUtcTicks);
            if (!WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(observation))
                return false;

            var observedUtcTicks = DateTime.UtcNow.Ticks;
            WatchdogSafetyHandoffReceipt handoff;
            if (WatchdogSafetyHandoffReceiptStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out handoff) && handoff != null)
            {
                if (handoff.State == WatchdogSafetyHandoffState.Failed ||
                    handoff.RelaunchDisposition !=
                        WatchdogRelaunchDisposition.PreserveApprovedPermit ||
                    handoff.RelaunchPermitGeneration != permitGeneration ||
                    !WatchdogTakeoverPermitBindingPolicy.Matches(
                        authority,
                        _args.SessionId,
                        handoff.RelaunchPermitGeneration,
                        handoff.RelaunchPermitId,
                        handoff.RelaunchPermitNonceSha256))
                    return false;

                if (handoff.SchemaVersion >= 5 && handoff.OldProcessExitProven)
                {
                    if (handoff.OldProcessId != oldIdentity.ProcessId ||
                        handoff.OldProcessStartUtcTicks !=
                            oldIdentity.ProcessStartUtcTicks ||
                        handoff.OldProcessObservation != observation)
                        return false;
                }
                else
                {
                    handoff.SchemaVersion = SupervisorProtocol.SchemaVersion;
                    handoff.CrashRecovery = true;
                    handoff.OldProcessExitProven = true;
                    handoff.OldProcessId = oldIdentity.ProcessId;
                    handoff.OldProcessStartUtcTicks =
                        oldIdentity.ProcessStartUtcTicks;
                    handoff.OldProcessObservation = observation;
                    handoff.OldProcessExitObservedUtcTicks = observedUtcTicks;
                    handoff.OldProcessExitEvidenceOwner =
                        WatchdogSafetyEvidenceOwner.SupervisorService;
                    handoff.OldProcessExitEvidenceSource =
                        oldIdentity.Source ?? "FrozenOldProcessIdentity";
                    handoff.LogicalQuiescent = true;
                    handoff.CallbacksIsolated = true;
                    handoff.HardwareResourcesReleased = true;
                    handoff.ExecutionAuthorizationRevoked = true;
                    handoff.DataAuditState = handoff.PersistenceDrained
                        ? WatchdogDataAuditState.Drained
                        : WatchdogDataAuditState.CrashRepairRequired;
                    handoff.Revision++;
                    handoff.Detail = string.IsNullOrWhiteSpace(handoff.Detail)
                        ? "Exact old process exit proven by supervisor"
                        : handoff.Detail +
                          ";Exact old process exit proven by supervisor";
                    WatchdogSafetyHandoffReceiptStore.WriteThrough(
                        _args.JournalDirectory,
                        handoff);
                }
            }

            WatchdogClosingTombstone closing;
            if (WatchdogClosingTombstoneStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out closing) && closing != null)
            {
                if (closing.RelaunchDisposition !=
                        WatchdogRelaunchDisposition.PreserveApprovedPermit ||
                    closing.RelaunchPermitGeneration != permitGeneration ||
                    !WatchdogTakeoverPermitBindingPolicy.Matches(
                        authority,
                        _args.SessionId,
                        closing.RelaunchPermitGeneration,
                        closing.RelaunchPermitId,
                        closing.RelaunchPermitNonceSha256))
                    return false;
                if (closing.OldProcessExitProven &&
                    (closing.OldProcessId != oldIdentity.ProcessId ||
                     closing.OldProcessStartUtcTicks !=
                         oldIdentity.ProcessStartUtcTicks ||
                     closing.OldProcessObservation != observation))
                    return false;
                if (!closing.OldProcessExitProven || closing.SchemaVersion < 5)
                {
                    closing.SchemaVersion = SupervisorProtocol.SchemaVersion;
                    closing.OldProcessExitProven = true;
                    closing.OldProcessId = oldIdentity.ProcessId;
                    closing.OldProcessStartUtcTicks =
                        oldIdentity.ProcessStartUtcTicks;
                    closing.OldProcessObservation = observation;
                    closing.OldProcessExitObservedUtcTicks = observedUtcTicks;
                    closing.OldProcessExitEvidenceOwner =
                        WatchdogSafetyEvidenceOwner.SupervisorService;
                    closing.OldProcessExitEvidenceSource =
                        oldIdentity.Source ?? "FrozenOldProcessIdentity";
                    closing.DataAuditState = closing.PersistenceDrained
                        ? WatchdogDataAuditState.Drained
                        : WatchdogDataAuditState.CrashRepairRequired;
                    closing.StateVersion++;
                    WatchdogClosingTombstoneStore.WriteThrough(
                        _args.JournalDirectory,
                        closing);
                }
            }

            Record(
                "PostExitSafetyEvidenceAugmented",
                $"PID={oldIdentity.ProcessId};StartUtcTicks=" +
                $"{oldIdentity.ProcessStartUtcTicks};Observation={observation};" +
                $"PermitGeneration={permitGeneration}");
            return true;
        }

        private static long ResolveOldProcessExitDeadlineTimestamp(
            OldProcessIdentitySnapshot oldIdentity)
        {
            var maximum = TimeSpan.FromSeconds(
                WatchdogRecoveryReadinessPolicy.OldProcessExitDeadlineSeconds);
            var remaining = oldIdentity?.ExitHardDeadlineUtcTicks > 0
                ? TimeSpan.FromTicks(Math.Max(
                    0,
                    oldIdentity.ExitHardDeadlineUtcTicks - DateTime.UtcNow.Ticks))
                : maximum;
            if (remaining > maximum) remaining = maximum;
            return Stopwatch.GetTimestamp() +
                   (long)Math.Ceiling(remaining.TotalSeconds * Stopwatch.Frequency);
        }

        private bool EnsureCrashRecoverySafetyHandoffIfRequired(
            long permitGeneration,
            OldProcessIdentitySnapshot oldIdentity,
            out SafetyHandoffWaitResult failure)
        {
            failure = null;
            try
            {
                var authority = _relaunchCoordinator.Snapshot;
                WatchdogClosingTombstone closing;
                var hasClosing = WatchdogClosingTombstoneStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out closing);
                if (hasClosing && closing != null)
                {
                    var exactClosing = closing.SchemaVersion >= 5 &&
                        closing.PreservesApprovedPermit &&
                        closing.RelaunchPermitGeneration == permitGeneration &&
                        WatchdogTakeoverPermitBindingPolicy.Matches(
                            authority,
                            _args.SessionId,
                            closing.RelaunchPermitGeneration,
                            closing.RelaunchPermitId,
                            closing.RelaunchPermitNonceSha256);
                    if (!exactClosing)
                    {
                        failure = new SafetyHandoffWaitResult
                        {
                            Outcome = WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                            Detail = "ExistingClosingTombstonePermitMismatch"
                        };
                        return false;
                    }
                    if (closing.State == WatchdogClosingTombstoneState.Terminal ||
                        !string.IsNullOrWhiteSpace(closing.SafetyHandoffId))
                        return true;
                }

                WatchdogCrashRecoverySeed seed;
                if (!WatchdogCrashRecoverySeedStore.TryRead(
                        _args.JournalDirectory,
                        _args.SessionId,
                        out seed))
                {
                    failure = new SafetyHandoffWaitResult
                    {
                        Outcome = WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                        Detail = "CrashRecoverySeedMissingOrCorrupt"
                    };
                    return false;
                }
                var oldObservation = ProbeProcessIdentity(
                    oldIdentity.ProcessId,
                    oldIdentity.ProcessStartUtcTicks);
                if (!WatchdogRecoveryReadinessPolicy.CanPrepareCrashSafetyHandoff(
                        seed,
                        _args.SessionId,
                        oldObservation,
                        authority) ||
                    authority.Generation != permitGeneration)
                {
                    failure = new SafetyHandoffWaitResult
                    {
                        Outcome = WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                        Detail = "CrashRecoverySeedOldProcessOrAuthorityMismatch"
                    };
                    return false;
                }

                var seedSnapshot = WatchdogSafetyConfigSnapshotStore.Validate(
                    _args.JournalDirectory,
                    seed.SeedId,
                    seed.ConfigSnapshotPath,
                    seed.ConfigSnapshotManifestPath,
                    seed.ConfigSnapshotManifestSha256);
                if (seedSnapshot?.Succeeded != true)
                    throw new InvalidDataException(
                        "CrashRecoverySeedSnapshotInvalid:" +
                        (seedSnapshot?.Error ?? "Unavailable"));
                if (!File.Exists(seed.MainExecutablePath) ||
                    !File.Exists(seed.SafetyAgentExecutablePath))
                    throw new FileNotFoundException(
                        "CrashRecoverySeedExecutableMissing");
                var mainSha = SupervisorProtocol.ComputeSha256(
                    seed.MainExecutablePath);
                var agentSha = SupervisorProtocol.ComputeSha256(
                    seed.SafetyAgentExecutablePath);
                if (!Sha256Equals(mainSha, seed.MainExecutableSha256) ||
                    !Sha256Equals(agentSha, seed.SafetyAgentExecutableSha256))
                    throw new InvalidDataException(
                        "CrashRecoverySeedExecutableHashChanged");

                WatchdogSafetyHandoffReceipt priorHandoff;
                if (WatchdogSafetyHandoffReceiptStore.TryRead(
                        _args.JournalDirectory,
                        _args.SessionId,
                        out priorHandoff) && priorHandoff != null)
                {
                    var exactPrior = priorHandoff.SessionGeneration ==
                                         seed.SessionGeneration &&
                                     priorHandoff.SessionLease == seed.SessionLease &&
                                     priorHandoff.RelaunchDisposition ==
                                         WatchdogRelaunchDisposition.PreserveApprovedPermit &&
                                     priorHandoff.RelaunchPermitGeneration ==
                                         permitGeneration &&
                                     WatchdogTakeoverPermitBindingPolicy.Matches(
                                         authority,
                                         _args.SessionId,
                                         priorHandoff.RelaunchPermitGeneration,
                                         priorHandoff.RelaunchPermitId,
                                         priorHandoff.RelaunchPermitNonceSha256);
                    if (!exactPrior)
                        throw new InvalidDataException(
                            "ExistingSafetyHandoffPermitMismatch");
                    if (closing == null ||
                        string.IsNullOrWhiteSpace(closing.SafetyHandoffId))
                    {
                        closing = CreateCrashRecoveryClosingTombstone(
                            seed,
                            authority,
                            priorHandoff.HandoffId,
                            priorHandoff.StopSafetyTransactionId,
                            closing,
                            oldIdentity);
                        WatchdogClosingTombstoneStore.WriteThrough(
                            _args.JournalDirectory,
                            closing);
                    }
                    if (!priorHandoff.IsTerminal)
                        BeginSafetyHandoff(priorHandoff);
                    return true;
                }

                var handoffId = Guid.NewGuid().ToString("N");
                var snapshot = WatchdogSafetyConfigSnapshotStore.Create(
                    _args.JournalDirectory,
                    handoffId,
                    seedSnapshot.ConfigDirectory,
                    string.Empty,
                    seed.BuildIdentity,
                    seedSnapshot.Runtime,
                    _args.SessionId,
                    seed.SessionGeneration,
                    seed.SessionLease,
                    authority.Generation,
                    authority.PermitId,
                    mainSha,
                    agentSha);
                if (snapshot?.Succeeded != true)
                    throw new InvalidDataException(
                        "CrashRecoverySafetySnapshotCreateFailed:" +
                        (snapshot?.Error ?? "Unavailable"));

                var stopSafetyTransactionId = closing != null &&
                                              !string.IsNullOrWhiteSpace(
                                                  closing.StopSafetyTransactionId)
                    ? closing.StopSafetyTransactionId
                    : Guid.NewGuid().ToString("N");
                var receipt = new WatchdogSafetyHandoffReceipt
                {
                    SchemaVersion = SupervisorProtocol.SchemaVersion,
                    SessionId = _args.SessionId,
                    SessionGeneration = seed.SessionGeneration,
                    SessionLease = seed.SessionLease,
                    HandoffId = handoffId,
                    Nonce = Guid.NewGuid().ToString("N"),
                    StopSafetyTransactionId = stopSafetyTransactionId,
                    RunId = _journal.RunId ?? string.Empty,
                    RunEpoch = _journal.LastHeartbeat?.RunEpoch ?? 0,
                    Revision = 1,
                    State = WatchdogSafetyHandoffState.Accepted,
                    Stage = WatchdogSafetyStage.None,
                    PersistenceDrained = false,
                    LogicalQuiescent = true,
                    HardwareResourcesReleased = true,
                    ExecutionAuthorizationRevoked = true,
                    CallbacksIsolated = true,
                    CrashRecovery = true,
                    OldProcessExitProven = true,
                    OldProcessId = oldIdentity.ProcessId,
                    OldProcessStartUtcTicks = oldIdentity.ProcessStartUtcTicks,
                    OldProcessObservation = oldObservation,
                    OldProcessExitObservedUtcTicks = DateTime.UtcNow.Ticks,
                    OldProcessExitEvidenceOwner =
                        WatchdogSafetyEvidenceOwner.SupervisorService,
                    OldProcessExitEvidenceSource =
                        oldIdentity.Source ?? "FrozenOldProcessIdentity",
                    DataAuditState = WatchdogDataAuditState.CrashRepairRequired,
                    SidecarProcessId = _sidecarProcessId,
                    SidecarProcessStartUtcTicks = _sidecarProcessStartUtcTicks,
                    ProjectDirectory = seed.ProjectDirectory,
                    MainExecutablePath = seed.MainExecutablePath,
                    MainExecutableSha256 = mainSha,
                    SafetyAgentExecutablePath = seed.SafetyAgentExecutablePath,
                    SafetyAgentExecutableSha256 = agentSha,
                    ConfigSnapshotPath = snapshot.ConfigDirectory,
                    ConfigSnapshotManifestPath = snapshot.ManifestPath,
                    ConfigSnapshotManifestSha256 = snapshot.ManifestSha256,
                    ConfigSnapshotSchemaVersion = 2,
                    RelaunchDisposition =
                        WatchdogRelaunchDisposition.PreserveApprovedPermit,
                    RelaunchPermitGeneration = authority.Generation,
                    RelaunchPermitId = authority.PermitId,
                    RelaunchPermitNonceSha256 =
                        WatchdogTakeoverPermitBindingPolicy.HashNonce(
                            authority.PermitNonce),
                    Detail = "MainProcessExitedWithoutClosingTombstone"
                };
                WatchdogSafetyHandoffReceiptStore.WriteThrough(
                    _args.JournalDirectory,
                    receipt);
                closing = CreateCrashRecoveryClosingTombstone(
                    seed,
                    authority,
                    handoffId,
                    stopSafetyTransactionId,
                    closing,
                    oldIdentity);
                WatchdogClosingTombstoneStore.WriteThrough(
                    _args.JournalDirectory,
                    closing);
                Record(
                    "CrashRecoverySafetyHandoffCreated",
                    $"HandoffId={handoffId};PermitGeneration={permitGeneration};" +
                    $"OldProcessObservation={oldObservation}");
                BeginSafetyHandoff(receipt);
                return true;
            }
            catch (Exception ex)
            {
                failure = new SafetyHandoffWaitResult
                {
                    Outcome = WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                    Detail = ex.GetBaseException().Message
                };
                return false;
            }
        }

        private WatchdogClosingTombstone CreateCrashRecoveryClosingTombstone(
            WatchdogCrashRecoverySeed seed,
            DurableRelaunchPermitRecord authority,
            string handoffId,
            string stopSafetyTransactionId,
            WatchdogClosingTombstone existing,
            OldProcessIdentitySnapshot oldIdentity)
        {
            var takeoverTransactionId = existing?.TakeoverTransactionId;
            Guid parsed;
            if (!Guid.TryParseExact(
                    takeoverTransactionId ?? string.Empty,
                    "N",
                    out parsed))
                takeoverTransactionId = Guid.TryParseExact(
                        _activeTakeoverCorrelationId ?? string.Empty,
                        "N",
                        out parsed)
                    ? _activeTakeoverCorrelationId
                    : Guid.NewGuid().ToString("N");
            return new WatchdogClosingTombstone
            {
                SchemaVersion = SupervisorProtocol.SchemaVersion,
                SessionId = _args.SessionId,
                SessionGeneration = seed.SessionGeneration,
                SessionLease = seed.SessionLease,
                CloseIntent = existing?.CloseIntent ?? "WatchdogCrashRecovery",
                TakeoverTransactionId = takeoverTransactionId,
                ExitDisposition = WatchdogExitDisposition.TakeoverReplacementExit,
                RelaunchDisposition =
                    WatchdogRelaunchDisposition.PreserveApprovedPermit,
                RelaunchPermitGeneration = authority.Generation,
                RelaunchPermitId = authority.PermitId,
                RelaunchPermitNonceSha256 =
                    WatchdogTakeoverPermitBindingPolicy.HashNonce(
                        authority.PermitNonce),
                StopSafetyTransactionId = stopSafetyTransactionId,
                StopRunId = existing?.StopRunId ?? (_journal.RunId ?? string.Empty),
                StopRunEpoch = existing?.StopRunEpoch ??
                               (_journal.LastHeartbeat?.RunEpoch ?? 0),
                StopSafetyBoundaryGeneration =
                    existing?.StopSafetyBoundaryGeneration ?? 0,
                StateVersion = (existing?.StateVersion ?? 0) + 1,
                State = existing?.State ?? WatchdogClosingTombstoneState.Closing,
                SafetyStage = existing?.SafetyStage ??
                              WatchdogClosingSafetyStage.ClosingIntent,
                ControllerStopStage = existing?.ControllerStopStage ?? 0,
                ControllerProgressVersion =
                    existing?.ControllerProgressVersion ?? 0,
                ControllerProgressDetail =
                    existing?.ControllerProgressDetail ?? string.Empty,
                FinalSafetyResultCommitted =
                    existing?.FinalSafetyResultCommitted ?? false,
                MotorsOff = existing?.MotorsOff ?? false,
                PowerOff = existing?.PowerOff ?? false,
                PressureSafe = existing?.PressureSafe ?? false,
                PersistenceDrained = existing?.PersistenceDrained ?? false,
                LogicalQuiescent = existing?.LogicalQuiescent ?? false,
                DataContinuityVerified =
                    existing?.DataContinuityVerified ?? false,
                OldProcessExitProven = true,
                OldProcessId = oldIdentity.ProcessId,
                OldProcessStartUtcTicks = oldIdentity.ProcessStartUtcTicks,
                OldProcessObservation = ProbeProcessIdentity(
                    oldIdentity.ProcessId,
                    oldIdentity.ProcessStartUtcTicks),
                OldProcessExitObservedUtcTicks = DateTime.UtcNow.Ticks,
                OldProcessExitEvidenceOwner =
                    WatchdogSafetyEvidenceOwner.SupervisorService,
                OldProcessExitEvidenceSource =
                    oldIdentity.Source ?? "FrozenOldProcessIdentity",
                DataAuditState = existing?.PersistenceDrained == true
                    ? WatchdogDataAuditState.Drained
                    : WatchdogDataAuditState.CrashRepairRequired,
                SafetyOwner = "IndependentSafetyAgentCrashRecovery",
                SafetyHandoffId = handoffId,
                TerminalReason = string.IsNullOrWhiteSpace(existing?.TerminalReason)
                    ? "MainProcessExitedWithoutClosingTombstone"
                    : existing.TerminalReason
            };
        }

        private async Task<SafetyHandoffWaitResult> AwaitSafetyHandoffBeforeRelaunchAsync(
            long permitGeneration,
            TimeSpan timeout)
        {
            var effective = timeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(
                    WatchdogRecoveryReadinessPolicy.SafetyHandoffDeadlineSeconds)
                : timeout;
            var deadline = Stopwatch.GetTimestamp() +
                           (long)Math.Ceiling(effective.TotalSeconds * Stopwatch.Frequency);
            var supervisorAuthorityRequired =
                RequiresSupervisorSafetyAuthority(_args.ExecutablePath);
            var lastAuthorityReadFailure = string.Empty;
            while (!_stop.IsCancellationRequested && Stopwatch.GetTimestamp() <= deadline)
            {
                WatchdogClosingTombstone closing;
                if (!WatchdogClosingTombstoneStore.TryRead(
                        _args.JournalDirectory,
                        _args.SessionId,
                        out closing) ||
                    closing == null || closing.SchemaVersion < 5 ||
                    !closing.PreservesApprovedPermit ||
                    closing.RelaunchPermitGeneration != permitGeneration ||
                    !WatchdogTakeoverPermitBindingPolicy.Matches(
                        _relaunchCoordinator.Snapshot,
                        _args.SessionId,
                        closing.RelaunchPermitGeneration,
                        closing.RelaunchPermitId,
                        closing.RelaunchPermitNonceSha256))
                {
                    return new SafetyHandoffWaitResult
                    {
                        Outcome = WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                        Detail = "ClosingTombstoneMissingCorruptOrPermitMismatch"
                    };
                }

                if (string.IsNullOrWhiteSpace(closing.SafetyHandoffId))
                {
                    WatchdogSafetyHandoffWaitOutcome stopOutcome;
                    if (WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                            closing,
                            null,
                            exactClosingPermitBinding: true,
                            exactReceiptIdentity: false,
                            configSnapshotValid: false,
                            timedOut: false,
                            out stopOutcome))
                    {
                        return new SafetyHandoffWaitResult
                        {
                            Outcome = stopOutcome,
                            ReadyTimestamp = stopOutcome ==
                                             WatchdogSafetyHandoffWaitOutcome.NotRequired
                                ? Stopwatch.GetTimestamp()
                                : 0,
                            Detail = stopOutcome ==
                                     WatchdogSafetyHandoffWaitOutcome.NotRequired
                                ? "ExactStopCompletedSafetyProof"
                                : "StopCompletedSafetyProofIncomplete"
                        };
                    }
                    await Task.Delay(250).ConfigureAwait(false);
                    continue;
                }

                WatchdogSafetyHandoffReceipt receipt;
                if (supervisorAuthorityRequired)
                {
                    var authorityToken = GetSafetyAuthorityToken(
                        closing.SafetyHandoffId,
                        closing.RelaunchPermitGeneration,
                        closing.RelaunchPermitId);
                    if (authorityToken == null)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        continue;
                    }
                    string authorityReadFailure;
                    if (!SupervisorSafetyAgentLaunchClient.TryReadAuthority(
                            authorityToken,
                            out receipt,
                            out authorityReadFailure))
                    {
                        if (!string.Equals(
                                lastAuthorityReadFailure,
                                authorityReadFailure,
                                StringComparison.Ordinal))
                        {
                            lastAuthorityReadFailure = authorityReadFailure ??
                                                       "SupervisorSafetyAuthorityReadFailed";
                            Record(
                                "SupervisorSafetyAuthorityReadFailed",
                                lastAuthorityReadFailure);
                        }
                        await Task.Delay(250).ConfigureAwait(false);
                        continue;
                    }
                }
                else if (!WatchdogSafetyHandoffReceiptStore.TryRead(
                             _args.JournalDirectory,
                             _args.SessionId,
                             out receipt))
                {
                    return new SafetyHandoffWaitResult
                    {
                        Outcome = WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                        Detail = "DeclaredSafetyHandoffReceiptMissingOrCorrupt"
                    };
                }
                if (!string.Equals(
                        receipt.HandoffId,
                        closing.SafetyHandoffId,
                        StringComparison.Ordinal) ||
                    receipt.SessionGeneration != closing.SessionGeneration ||
                    receipt.SessionLease != closing.SessionLease ||
                    !string.Equals(
                        receipt.StopSafetyTransactionId,
                        closing.StopSafetyTransactionId,
                        StringComparison.OrdinalIgnoreCase) ||
                    receipt.RelaunchDisposition !=
                        WatchdogRelaunchDisposition.PreserveApprovedPermit ||
                    receipt.RelaunchPermitGeneration != permitGeneration ||
                    !WatchdogTakeoverPermitBindingPolicy.Matches(
                        _relaunchCoordinator.Snapshot,
                        _args.SessionId,
                        receipt.RelaunchPermitGeneration,
                        receipt.RelaunchPermitId,
                        receipt.RelaunchPermitNonceSha256))
                {
                    return new SafetyHandoffWaitResult
                    {
                        Outcome = WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                        Detail = "SafetyHandoffIdentityOrPermitMismatch"
                    };
                }
                if (receipt.State == WatchdogSafetyHandoffState.Failed)
                {
                    return new SafetyHandoffWaitResult
                    {
                        Outcome = WatchdogSafetyHandoffWaitOutcome.Failed,
                        Detail = $"Code={receipt.FailureCode};Detail={receipt.Detail}"
                    };
                }
                if (receipt.State == WatchdogSafetyHandoffState.Completed)
                {
                    var snapshot = WatchdogSafetyConfigSnapshotStore.Validate(
                        _args.JournalDirectory,
                        receipt.HandoffId,
                        receipt.ConfigSnapshotPath,
                        receipt.ConfigSnapshotManifestPath,
                        receipt.ConfigSnapshotManifestSha256);
                    WatchdogSafetyHandoffWaitOutcome handoffOutcome;
                    WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                        closing,
                        receipt,
                        exactClosingPermitBinding: true,
                        exactReceiptIdentity: true,
                        configSnapshotValid: snapshot?.Succeeded == true,
                        timedOut: false,
                        out handoffOutcome);
                    return new SafetyHandoffWaitResult
                    {
                        Outcome = handoffOutcome,
                        ReadyTimestamp = handoffOutcome ==
                                         WatchdogSafetyHandoffWaitOutcome.Completed
                            ? Stopwatch.GetTimestamp()
                            : 0,
                        Detail = handoffOutcome ==
                                 WatchdogSafetyHandoffWaitOutcome.Completed
                            ? "ExactSafetyHandoffCompleted"
                            : handoffOutcome == WatchdogSafetyHandoffWaitOutcome.Failed
                                ? "SafetyHandoffCompletedWithIncompleteSafetyEvidence"
                                : "SafetyConfigSnapshotInvalid:" +
                                  (snapshot?.Error ?? "Unavailable")
                    };
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            return new SafetyHandoffWaitResult
            {
                Outcome = WatchdogSafetyHandoffWaitOutcome.TimedOut,
                Detail = $"PermitGeneration={permitGeneration};" +
                         $"TimeoutSeconds={effective.TotalSeconds:F0};" +
                         $"AuthoritySource={(supervisorAuthorityRequired ? "Supervisor" : "EvidenceMirror")};" +
                         $"LastAuthorityReadFailure={lastAuthorityReadFailure}"
            };
        }

        private void BlockSafetyPrerequisite(
            long permitGeneration,
            SafetyHandoffWaitResult result)
        {
            var outcome = result?.Outcome ??
                          WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt;
            var detail = result?.Detail ?? "SafetyPrerequisiteResultMissing";
            var fingerprint = RecoveryFailurePolicy.BuildFingerprint(
                "SafetyPrerequisite:" + outcome + ":" + detail);
            int failureCount;
            lock (_journalGate)
            {
                RecordStageFailureOnceLocked(
                    RecoveryFailureStageKind.SafetyPrerequisite,
                    "SafetyPrerequisite",
                    permitGeneration,
                    fingerprint);
                if (_journal.SafetyPrerequisiteFirstFailureUtcTicks <= 0)
                    _journal.SafetyPrerequisiteFirstFailureUtcTicks =
                        DateTime.UtcNow.Ticks;
                failureCount = _journal.SafetyPrerequisiteFailureCount;
                _journal.LastReason =
                    $"SafetyPrerequisite:{outcome}:{detail}";
                try { TryPersistJournalSnapshotLocked(); } catch { }
            }
            Record(
                "RecoverySafetyPrerequisiteBlocked",
                $"PermitGeneration={permitGeneration};Outcome={outcome};" +
                $"FailureCount={failureCount};MainLaunchBudgetConsumed=False;" +
                $"Detail={detail}");
            ScheduleSafetyPrerequisiteRetry(
                permitGeneration,
                outcome,
                detail);
        }

        private void ScheduleSafetyPrerequisiteRetry(
            long permitGeneration,
            WatchdogSafetyHandoffWaitOutcome outcome,
            string detail)
        {
            if (Interlocked.CompareExchange(
                    ref _safetyPrerequisiteRetryStarted,
                    1,
                    0) != 0)
                return;
            int attempt;
            int delaySeconds;
            lock (_journalGate)
            {
                attempt = Math.Max(0, _journal.SafetyPrerequisiteRetryAttempt);
                var schedule = new[] { 1, 5, 15, 30, 60, 300 };
                delaySeconds = schedule[Math.Min(attempt, schedule.Length - 1)];
                _journal.SafetyPrerequisiteRetryAttempt = attempt + 1;
                _journal.SafetyPrerequisiteNextRetryUtcTicks =
                    DateTime.UtcNow.AddSeconds(delaySeconds).Ticks;
                try { TryPersistJournalSnapshotLocked(); } catch { }
            }
            _transitionWindow.Show(
                "设备保持断能，等待安全前置复核",
                $"首发故障：{outcome}；停止结果：安全回执未放行；" +
                $"恢复门禁：schema 6 Supervisor 权威证据；当前动作：退避复核；" +
                $"安全前置失败计数：{_journal.SafetyPrerequisiteFailureCount}；" +
                $"下次重试：{DateTime.Now.AddSeconds(delaySeconds):yyyy-MM-dd HH:mm:ss}\r\n" +
                detail,
                delaySeconds,
                attempt + 1);
            Record(
                "SafetyPrerequisiteRetryScheduled",
                $"PermitGeneration={permitGeneration};Attempt={attempt + 1};" +
                $"DelaySeconds={delaySeconds};Outcome={outcome}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(
                            TimeSpan.FromSeconds(delaySeconds),
                            _stop.Token)
                        .ConfigureAwait(false);
                    if (_journal.ManualStopRequested || IsSessionRevoked() ||
                        _stop.IsCancellationRequested ||
                        !IsConsumableRelaunchPermit(permitGeneration))
                        return;
                    lock (_journalGate)
                    {
                        _journal.SafetyPrerequisiteNextRetryUtcTicks = 0;
                        try { TryPersistJournalSnapshotLocked(); } catch { }
                    }
                    Interlocked.Exchange(
                        ref _safetyPrerequisiteRetryStarted,
                        0);
                    BeginRelaunchAfterExit(permitGeneration);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    Interlocked.Exchange(
                        ref _safetyPrerequisiteRetryStarted,
                        0);
                }
            });
        }

        private async Task RelaunchLoopAsync(
            string reason,
            long approvedPermitGeneration,
            bool initialSafetyReplacement = false,
            long recoveryReadyTimestamp = 0)
        {
            if (IsRecoveryBlocked() || _journal.ManualStopRequested || IsSessionRevoked()) return;
            if (Interlocked.CompareExchange(ref _relaunchStarted, 1, 0) != 0) return;
            var permitGeneration = approvedPermitGeneration;
            var launchOrdinal = 0;
            try
            {
                while (!_journal.ManualStopRequested &&
                    !IsSessionRevoked() &&
                       !_stop.IsCancellationRequested)
                {
                    if (IsRecoveryBlocked()) return;
                    // The loop is a consumer only.  Every caller must first
                    // reserve and durably persist a permit; it must never
                    // manufacture a second permit from inside this retry
                    // loop.
                    if (permitGeneration <= 0) return;

                    if (!TryConsumeRelaunchPermit(permitGeneration, out var attempt))
                        return;

                    launchOrdinal++;
                    if (WatchdogRecoveryReadinessPolicy.ShouldApplyProcessBackoff(
                            initialSafetyReplacement,
                            launchOrdinal))
                    {
                        var delay = RecoveryFailurePolicy.SelectProcessRelaunchDelaySeconds(attempt);
                        Record(
                            "RecoveryBackoff",
                            $"Attempt={attempt};DelaySeconds={delay};Reason={reason}");
                        await DelayWithTransitionCountdownAsync(delay, attempt, reason)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        Record(
                            "RecoveryInitialBackoffSuppressed",
                            $"Attempt={attempt};Reason={reason};SafetyReadyTimestamp=" +
                            recoveryReadyTimestamp);
                    }
                    if (_journal.ManualStopRequested ||
                        IsSessionRevoked() ||
                        IsRecoveryBlocked())
                        return;
                    DurableRelaunchPermitIdentity launchIdentity = null;
                    var launchIntentCommitted = false;
                    Process started = null;
                    GuardedProcessOwnerReceipt launchOwner = null;
                    try
                    {
                        _transitionWindow.Show(
                            "正在启动试验程序",
                            "正在创建新的主程序进程并恢复安全检查点",
                            0,
                            attempt);
                        var previousPid = _journal.CurrentPid;
                        // Freeze the recovery intent from the verified initial
                        // process before the first launch intent. A recovery
                        // child may report every channel disabled while it is
                        // rebuilding state; that observation must never rewrite
                        // the original recovery scope.
                        var frozenIntent = TryGetOrFreezeRecoveryChannelIntent();
                        if (!frozenIntent.Succeeded)
                        {
                            var failure = frozenIntent.FailureReason ??
                                "FrozenExcludedChannelsUnavailable";
                            EnterRelaunchCircuitOpen(
                                RecoveryFailurePolicy.BuildFingerprint(failure),
                                Math.Max(1, _journal.ConsecutiveStartupFailures),
                                failure);
                            return;
                        }
                        var excluded = frozenIntent.ExcludedChannels;
                        var permitRecord = _relaunchCoordinator.Snapshot;
                        if (permitRecord == null ||
                            permitRecord.Generation != permitGeneration ||
                            string.IsNullOrWhiteSpace(permitRecord.PermitId) ||
                            string.IsNullOrWhiteSpace(permitRecord.PermitNonce))
                            return;
                        var arguments = string.Format(CultureInfo.InvariantCulture,
                             "--watchdog-recover {0} --watchdog-pipe {1} --previous-pid {2} --recovery-attempt {3} --exclude-channels {4} " +
                             "--sidecar-pid {5} --sidecar-start-ticks {6} --sidecar-instance-nonce {7} " +
                             "--relaunch-generation {8} --relaunch-permit-id {9} --relaunch-permit-nonce {10} " +
                             "--journal-directory {11}",
                             Quote(_args.SessionId), Quote(_args.PipeName), previousPid, attempt,
                             Quote(string.Join(",", excluded)),
                             _sidecarProcessId,
                             _sidecarProcessStartUtcTicks,
                             Quote(_sidecarInstanceNonce),
                             permitRecord.Generation,
                             Quote(permitRecord.PermitId),
                             Quote(permitRecord.PermitNonce),
                             Quote(_args.JournalDirectory));
                        lock (_processLaunchGate)
                        {
                            if (_journal.ManualStopRequested ||
                                !RecoveryFailurePolicy.CanLaunchMainProcess(_journal.RecoveryBlocked) ||
                                RecoveryTransitionPolicy.MustSuppressAutomaticRestart(
                                    IsTransitionOperatorStopInProgress(),
                                    IsSessionRevoked()))
                            {
                                Record(
                                    "RecoveryProcessLaunchSuppressed",
                                    _journal.RecoveryBlocked
                                        ? "RecoveryBlocked"
                                        : "OperatorTransitionStopOrSessionRevoked");
                                return;
                            }
                            if (!TryBeginLaunchIntent(
                                    permitGeneration,
                                    out launchIdentity,
                                    out attempt,
                                    arguments))
                                return;
                            launchIntentCommitted = true;
                            launchOwner = TryStartCommittedLaunch(permitGeneration);
                            started = launchOwner.Process;
                            if (started == null) throw new InvalidOperationException("Guarded process start returned null.");
                            _journal.CurrentPid = started.Id;
                            _journal.CurrentProcessStartUtcTicks = started.StartTime.ToUniversalTime().Ticks;
                            _journal.OrphanPauseTriggered = false;
                            _journal.PowerDisableTriggered = false;
                        }
                        var processStartBoundaryTimestamp = Stopwatch.GetTimestamp();
                        if (initialSafetyReplacement && launchOrdinal == 1 &&
                            WatchdogRecoveryReadinessPolicy.IsSlaExceeded(
                                recoveryReadyTimestamp,
                                processStartBoundaryTimestamp,
                                Stopwatch.Frequency,
                                WatchdogRecoveryReadinessPolicy.InitialLaunchSlaSeconds))
                        {
                            var elapsedMs = recoveryReadyTimestamp > 0
                                ? (processStartBoundaryTimestamp - recoveryReadyTimestamp) *
                                  1000.0 / Stopwatch.Frequency
                                : double.PositiveInfinity;
                            Record(
                                "RecoveryProcessLaunchSlaExceeded",
                                $"Session={permitRecord.SessionId};" +
                                $"PermitGeneration={permitRecord.Generation};" +
                                $"PermitId={permitRecord.PermitId};" +
                                $"PermitNonceSha256={Sha256Text(permitRecord.PermitNonce)};" +
                                $"Attempt={attempt};" +
                                $"ElapsedMs={elapsedMs:F0};SlaMs=" +
                                WatchdogRecoveryReadinessPolicy.InitialLaunchSlaSeconds * 1000);
                        }
                        if (!TryCommitStarted(launchIdentity, launchOwner, out var startedFailure))
                        {
                            launchOwner.KillExactAndDispose();
                            BlockLaunchOutcomeUnknown(
                                "StartedCommitFailed:" + startedFailure);
                            return;
                        }
                        _attached = false;
                        Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                        Record("RecoveryProcessLaunched", $"PID={started.Id};Attempt={attempt};Permit={permitGeneration}");
                        var attachDeadlineTicks = processStartBoundaryTimestamp +
                                                  (long)WatchdogRecoveryReadinessPolicy
                                                      .RecoveryAttachSlaSeconds *
                                                  Stopwatch.Frequency;
                        string bootstrapFailureReason = null;
                        while (!_attached && !_journal.ManualStopRequested && !IsSessionRevoked() &&
                               Stopwatch.GetTimestamp() < attachDeadlineTicks)
                        {
                            var bootstrapObservation = ObserveBootstrapOutcome(
                                launchIdentity,
                                launchOwner,
                                attempt,
                                out var observedFailureReason);
                            if (bootstrapObservation == RecoveryBootstrapObservation.RejectedNoWork)
                                return;
                            if (bootstrapObservation == RecoveryBootstrapObservation.StartupFailed)
                            {
                                bootstrapFailureReason = observedFailureReason;
                                break;
                            }
                            if (started.HasExited)
                            {
                                var outcomeDeadline = Stopwatch.GetTimestamp() +
                                                      5L * Stopwatch.Frequency;
                                while (Stopwatch.GetTimestamp() < outcomeDeadline)
                                {
                                    bootstrapObservation = ObserveBootstrapOutcome(
                                        launchIdentity,
                                        launchOwner,
                                        attempt,
                                        out observedFailureReason);
                                    if (bootstrapObservation == RecoveryBootstrapObservation.RejectedNoWork)
                                        return;
                                    if (bootstrapObservation == RecoveryBootstrapObservation.StartupFailed)
                                    {
                                        bootstrapFailureReason = observedFailureReason;
                                        break;
                                    }
                                    await Task.Delay(100).ConfigureAwait(false);
                                }
                                break;
                            }
                            var remainingTicks = Math.Max(
                                0,
                                attachDeadlineTicks - Stopwatch.GetTimestamp());
                            var remaining = Math.Max(
                                1,
                                (int)Math.Ceiling(
                                    remainingTicks / (double)Stopwatch.Frequency));
                            _transitionWindow.Show(
                                "主程序正在加载",
                                "等待监控界面连接 Watchdog 恢复会话",
                                remaining,
                                attempt);
                            await Task.Delay(250).ConfigureAwait(false);
                        }
                        if (_attached)
                        {
                            Interlocked.Exchange(ref _takeoverStarted, 0);
                            try { launchOwner?.Dispose(); } catch { }
                            return;
                        }
                        var terminalObservation = ObserveBootstrapOutcome(
                            launchIdentity,
                            launchOwner,
                            attempt,
                            out var terminalFailureReason);
                        if (terminalObservation == RecoveryBootstrapObservation.RejectedNoWork)
                            return;
                        if (terminalObservation == RecoveryBootstrapObservation.StartupFailed)
                            bootstrapFailureReason = terminalFailureReason;
                        if (string.IsNullOrWhiteSpace(bootstrapFailureReason) &&
                            Stopwatch.GetTimestamp() >= attachDeadlineTicks)
                        {
                            Record(
                                "RecoveryAttachSlaExceeded",
                                $"Session={permitRecord.SessionId};" +
                                $"PermitGeneration={permitRecord.Generation};" +
                                $"PermitId={permitRecord.PermitId};" +
                                $"PermitNonceSha256={Sha256Text(permitRecord.PermitNonce)};" +
                                $"PID={started.Id};ProcessStartUtcTicks=" +
                                $"{_journal.CurrentProcessStartUtcTicks};" +
                                $"Attempt={attempt};" +
                                $"SlaSeconds=" +
                                WatchdogRecoveryReadinessPolicy.RecoveryAttachSlaSeconds);
                        }
                        if (launchOwner != null) launchOwner.KillExactAndDispose();
                        else _processLauncher.KillExact(started);
                        // RegisterRecoveryFailure is the single strict-V4
                        // failure transaction and closes the active permit.
                        var failureCode = string.IsNullOrWhiteSpace(bootstrapFailureReason)
                            ? "RecoveryAttachFailed"
                            : "RecoveryBootstrapStartupFailed";
                        var failureDetail = string.IsNullOrWhiteSpace(bootstrapFailureReason)
                            ? failureCode
                            : bootstrapFailureReason;
                        Record(
                            failureCode,
                            $"Attempt={attempt};Permit={permitGeneration};Reason={failureDetail}");
                        var failureDecision = RegisterRecoveryFailure(
                            failureCode,
                            RecoveryFailurePolicy.Classify(
                                failureCode,
                                false,
                                failureDetail));
                        if (failureDecision.SafeIdleRecoveryBlocked ||
                            (!failureDecision.ProcessRelaunchAllowed &&
                             !failureDecision.RelaunchPermitAlreadyPending))
                        {
                            EnterRelaunchCircuitOpen(
                                failureDecision.Fingerprint,
                                failureDecision.ConsecutiveCount,
                                failureCode);
                            return;
                        }
                        permitGeneration = failureDecision.RelaunchPermitGeneration;
                        if (permitGeneration <= 0 && failureDecision.RelaunchPermitAlreadyPending)
                            permitGeneration = GetActiveRelaunchPermit();
                        if (permitGeneration <= 0) return;
                        _transitionWindow.Show(
                            "本次启动未能连接",
                            "主程序未在 15 秒内连接，将按退避策略再次尝试",
                            0,
                            attempt);
                    }
                    catch (RecoveryLaunchRejectedNoWorkException ex)
                    {
                        Record("RecoveryLaunchRejectedNoWork", ex.Message);
                        _transitionWindow.Hide();
                        PublishTerminal("RecoveryRejectedNoWork", ex.Message);
                        _stop.Cancel();
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (launchIntentCommitted)
                        {
                            if (launchOwner != null) launchOwner.KillExactAndDispose();
                            else _processLauncher.KillExact(started);
                            BlockLaunchOutcomeUnknown(
                                "RecoveryLaunchOutcomeUnknown:" + ex.GetBaseException().Message);
                            return;
                        }
                        Record("RecoveryLaunchFailed", ex.Message);
                        // A pre-Start failure leaves an Approved permit in
                        // the journal.  Close that exact permit before
                        // evaluating the next failure; otherwise a retry
                        // could consume an action whose launch body never ran.
                        // RegisterRecoveryFailure is the single strict-V4
                        // failure transaction and closes the active permit.
                        var failureDecision = RegisterRecoveryFailure(
                            "RecoveryLaunchFailed:" + ex.GetBaseException().Message,
                            RecoveryFailurePolicy.Classify(
                                "RecoveryLaunchFailed",
                                false,
                                ex.GetBaseException().ToString()));
                        if (failureDecision.SafeIdleRecoveryBlocked ||
                            (!failureDecision.ProcessRelaunchAllowed &&
                             !failureDecision.RelaunchPermitAlreadyPending))
                        {
                            EnterRelaunchCircuitOpen(
                                failureDecision.Fingerprint,
                                failureDecision.ConsecutiveCount,
                                "RecoveryLaunchFailed");
                            return;
                        }
                        permitGeneration = failureDecision.RelaunchPermitGeneration;
                        if (permitGeneration <= 0 && failureDecision.RelaunchPermitAlreadyPending)
                            permitGeneration = GetActiveRelaunchPermit();
                        if (permitGeneration <= 0) return;
                        _transitionWindow.Show(
                            "本次启动失败",
                            "将按退避策略重试：" + ex.GetBaseException().Message,
                            0,
                            attempt);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _relaunchStarted, 0);
            }
        }

        private FrozenChannelIntentResolution TryGetOrFreezeRecoveryChannelIntent()
        {
            lock (_journalGate)
            {
                var result = WatchdogRecoveryChannelIntentPolicy.ResolveAndPersist(
                    _journal,
                    TryPersistJournalSnapshotLocked);
                if (!result.Succeeded)
                {
                    _journal.RecoveryFailureCode = result.FailureReason;
                    MarkRecoveryBlockedLocked(result.FailureReason);
                    try { TryPersistJournalSnapshotLocked(); } catch { }
                    return result;
                }

                return result;
            }
        }

        private bool AdvanceReplacementState(
            long permitGeneration,
            string permitId,
            RecoveryReplacementState state,
            string detail)
        {
            var result = RecoveryReplacementTransactionStore.Advance(
                _args.JournalDirectory,
                _args.SessionId,
                permitGeneration,
                permitId,
                state,
                detail);
            if (result?.Succeeded == true)
            {
                if (!result.AlreadyApplied)
                    Record(
                        "RecoveryReplacementAdvanced",
                        $"PermitGeneration={permitGeneration};State={state};" +
                        $"Revision={result.Transaction?.Revision};Detail={detail}");
                return true;
            }
            Record(
                "RecoveryReplacementAdvanceFailed",
                $"PermitGeneration={permitGeneration};State={state};" +
                $"Reason={result?.Reason ?? "Unknown"}");
            _unattendedAlarmSink.Publish(
                "P0",
                "RecoveryReplacementEvidenceFailed",
                result?.Reason ?? detail ?? string.Empty,
                RecoveryFailureDomain.EvidenceBinding,
                null);
            return false;
        }

        private bool IsRecoveryBlocked()
        {
            lock (_journalGate) return _journal.RecoveryBlocked;
        }

        private long GetActiveRelaunchPermit()
        {
            var record = _relaunchCoordinator?.Snapshot;
            if (record == null || record.State != DurableRelaunchPermitState.Approved)
                return 0;
            return record.Generation;
        }

        private bool IsConsumableRelaunchPermit(long generation)
        {
            var record = _relaunchCoordinator?.Snapshot;
            return generation > 0 && record != null &&
                   record.Generation == generation &&
                   record.State == DurableRelaunchPermitState.Approved;
        }

        private long ApproveRelaunchPermit(string reason)
        {
            RecoveryFailureReport report;
            RecoveryFailureClassification classification;
            lock (_journalGate)
            {
                if (_journal.RecoveryBlocked ||
                    _journal.ManualStopRequested ||
                    IsSessionRevoked())
                    return 0;
                classification = RecoveryFailurePolicy.Classify(
                    _journal.RecoveryFailureCode,
                    _journal.RecoveryFailurePermanent,
                    reason);
                var maximum = _journal.RecoveryFailureMaxProcessRelaunches > 0
                    ? _journal.RecoveryFailureMaxProcessRelaunches
                    : classification.MaximumProcessRelaunches;
                if (classification.Permanent ||
                    maximum <= 0 ||
                    _journal.ConsecutiveStartupFailures >= maximum)
                {
                    MarkRecoveryBlockedLocked(
                        "RecoveryRelaunchBudgetExhausted:" + (reason ?? string.Empty));
                    try { TryPersistJournalSnapshotLocked(); } catch { }
                    return 0;
                }
                report = new RecoveryFailureReport
                {
                    RootCode = string.IsNullOrWhiteSpace(_journal.RecoveryFailureCode)
                        ? "WatchdogTakeover"
                        : _journal.RecoveryFailureCode,
                    DeviceOrChannelGroup = _journal.DeviceOrChannelGroup,
                    RunId = _journal.RunId,
                    RunEpoch = _journal.LastHeartbeat?.RunEpoch ?? 0,
                    RecoveryStage = _journal.RecoveryStage,
                    RecoveryProgressToken = _journal.RecoveryProgressToken,
                    RecoveryProcessSource = _journal.RecoveryProcessSource
                };
            }
            var correlation = Guid.NewGuid().ToString("N");
            var decision = RegisterRecoveryFailure(
                report,
                classification,
                correlation,
                Sha256Text(correlation + "|" + (reason ?? string.Empty)),
                correlation,
                Interlocked.Read(ref _connectionGeneration));
            if (decision.DurableDecisionRetryPending)
                return 0;
            if (decision.SafeIdleRecoveryBlocked ||
                (!decision.ProcessRelaunchAllowed &&
                 !decision.RelaunchPermitAlreadyPending))
                return 0;
            var generation = decision.RelaunchPermitGeneration > 0
                ? decision.RelaunchPermitGeneration
                : GetActiveRelaunchPermit();
            var approved = _relaunchCoordinator?.Snapshot;
            if (generation <= 0 || approved == null ||
                approved.Generation != generation ||
                approved.State != DurableRelaunchPermitState.Approved)
            {
                Record(
                    "RelaunchPermitNotConsumable",
                    $"DecisionGeneration={generation};" +
                    $"State={approved?.State.ToString() ?? "Missing"};" +
                    $"AuthorityGeneration={approved?.Generation ?? 0};Reason={reason}");
                return 0;
            }
            if (TryGetRecoveryNoWorkReason(out var noWorkReason))
            {
                var rejected = CommitRejectedNoWork(
                    approved.Identity,
                    "PermitIssuance:" + noWorkReason);
                if (rejected)
                    Record(
                        "RelaunchPermitRejectedNoWork",
                        $"Generation={generation};Reason={noWorkReason}");
                else
                    BlockLaunchOutcomeUnknown(
                        "RejectedNoWorkCommitFailedAtPermitIssuance:" + noWorkReason);
                return 0;
            }
            return generation;
        }

        private bool TryConsumeRelaunchPermit(long permitGeneration, out int attempt)
        {
            attempt = 0;
            lock (_journalGate)
            {
                var record = _relaunchCoordinator?.Snapshot;
                if (permitGeneration <= 0 || _journal.RecoveryBlocked ||
                    record == null || record.Generation != permitGeneration ||
                    record.State != DurableRelaunchPermitState.Approved)
                    return false;

                attempt = Math.Max(1, _journal.ConsecutiveStartupFailures + 1);
                _journal.RecoveryAttempt = attempt;
                // The strict relaunch file is the action authority.  This
                // full-session snapshot is an audit mirror; a transient
                // mirror failure must not mutate or revoke the already
                // durable Approved permit.
                try { TryPersistJournalSnapshotLocked(); } catch { }
                return true;
            }
        }

        private bool TryBeginLaunchIntent(
            long permitGeneration,
            out DurableRelaunchPermitIdentity identity,
            out int attempt,
            string launchArguments = null)
        {
            identity = null;
            attempt = 0;
            DurableRelaunchPermitRecord record;
            lock (_journalGate)
            {
                record = _relaunchCoordinator?.Snapshot;
                if (record == null ||
                    record.Generation != permitGeneration ||
                    record.State != DurableRelaunchPermitState.Approved ||
                    _journal.RecoveryBlocked ||
                    _journal.ManualStopRequested ||
                    IsSessionRevoked())
                    return false;
                identity = record.Identity;
            }

            var frozenIdentity = identity;
            var result = ExecuteAuthorityTransitionWithBusyRetry(
                () => _relaunchCoordinator.BeginLaunch(
                    frozenIdentity,
                    launchArguments ?? string.Empty));
            if (result != null &&
                result.TransitionStatus == DurableAuthorityTransitionStatus.Busy)
            {
                Record("LaunchIntentDeferred", result.Reason ?? "AuthorityMutexBusy");
                identity = null;
                return false;
            }
            lock (_journalGate)
            {
                ApplyDurablePermitLocked(result.Record);
                if (!result.Succeeded || result.Blocked ||
                    _relaunchCoordinator.Snapshot.State != DurableRelaunchPermitState.LaunchIntent)
                {
                    identity = null;
                    MarkRecoveryBlockedLocked(
                        result.Reason ?? "LaunchIntentPersistenceFailed");
                    try { TryPersistJournalSnapshotLocked(); } catch { }
                    return false;
                }
                if (!AdvanceReplacementState(
                        permitGeneration,
                        frozenIdentity.PermitId,
                        RecoveryReplacementState.MainLaunchIntent,
                        "Durable main launch intent committed"))
                {
                    identity = null;
                    MarkRecoveryBlockedLocked(
                        "ReplacementTransactionPersistFailed:MainLaunchIntent");
                    try { TryPersistJournalSnapshotLocked(); } catch { }
                    return false;
                }
                attempt = Math.Max(1, _journal.ConsecutiveStartupFailures + 1);
                _journal.RecoveryAttempt = attempt;
                try { TryPersistJournalSnapshotLocked(); } catch { }
                return true;
            }
        }

        private bool TryCommitStarted(
            DurableRelaunchPermitIdentity identity,
            GuardedProcessOwnerReceipt owner,
            out string failure)
        {
            failure = string.Empty;
            var process = owner?.Process;
            if (identity == null || process == null)
            {
                failure = "StartedIdentityMissing";
                return false;
            }
            var startTicks = owner.StartUtcTicks;
            if (startTicks <= 0)
            {
                failure = "StartedIdentityReadFailed";
                return false;
            }
            var result = ExecuteAuthorityTransitionWithBusyRetry(
                () => _relaunchCoordinator.CommitStarted(
                    identity,
                    process.Id,
                    startTicks));
            if (result != null &&
                result.TransitionStatus == DurableAuthorityTransitionStatus.Busy)
            {
                failure = result.Reason ?? "AuthorityMutexBusy";
                return false;
            }
            lock (_journalGate)
            {
                ApplyDurablePermitLocked(result.Record);
                if (!result.Succeeded || result.Blocked ||
                    _relaunchCoordinator.Snapshot.State != DurableRelaunchPermitState.Started)
                {
                    failure = result.Reason ?? "StartedCommitFailed";
                    return false;
                }
                if (!AdvanceReplacementState(
                        identity.Generation,
                        identity.PermitId,
                        RecoveryReplacementState.MainStarted,
                        "Main Process.Start and exact identity committed"))
                {
                    failure = "ReplacementTransactionPersistFailed:MainStarted";
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// The single Host callsite for an OS launch.  A strict V4 capability
        /// is read back by the authority immediately before it crosses the
        /// guarded OS-process boundary; no caller can provide a raw
        /// ProcessStartInfo or bypass the one-shot intent consume.
        /// </summary>
        private GuardedProcessOwnerReceipt TryStartCommittedLaunch(long generation)
        {
            if (generation <= 0)
                throw new InvalidOperationException("LaunchGenerationMissing");
            var capability = _relaunchCoordinator.GetLaunchCapability(generation);
            if (capability == null)
                throw new InvalidOperationException("LaunchCapabilityMissing");
            if (TryGetRecoveryNoWorkReason(out var noWorkReason))
            {
                var current = _relaunchCoordinator.Snapshot;
                if (!CommitRejectedNoWork(
                        current?.Identity,
                        "ProcessStart:" + noWorkReason))
                    throw new InvalidOperationException(
                        "RejectedNoWorkCommitFailed:" + noWorkReason);
                throw new RecoveryLaunchRejectedNoWorkException(
                    noWorkReason);
            }
            var owner = _processLauncher.Start(capability);
            if (owner == null || owner.Process == null)
                throw new InvalidOperationException("GuardedProcessStartReturnedNull");
            return owner;
        }

        private bool CommitRejectedNoWork(
            DurableRelaunchPermitIdentity identity,
            string reason)
        {
            var rejected = ExecuteAuthorityTransitionWithBusyRetry(
                () => _relaunchCoordinator.RejectNoWork(identity, reason));
            if (rejected?.Record != null)
                lock (_journalGate) ApplyDurablePermitLocked(rejected.Record);
            return rejected?.Succeeded == true &&
                   rejected.Record?.State == DurableRelaunchPermitState.RejectedNoWork;
        }

        /// <summary>
        /// Reads the durable checkpoint again at both permit publication and
        /// the guarded Process.Start boundary.  A newer disarm, a different
        /// run/session, or an unreadable previously-known checkpoint is a
        /// neutral NoWork terminal: it must never re-authorize main-process
        /// creation and must not consume the recovery failure budget.
        /// </summary>
        private bool TryGetRecoveryNoWorkReason(out string reason)
        {
            reason = null;
            if (IsSessionRevoked())
            {
                reason = "ClosingFenceOrRevocationObserved";
                return true;
            }

            WatchdogCheckpointMirror mirror;
            lock (_journalGate) mirror = _journal?.LastCheckpointMirror;
            if (mirror == null) return false;
            if (!mirror.Armed)
            {
                reason = "CheckpointMirrorDisarmed";
                return true;
            }

            var sourcePath = mirror.SourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath)) return false;
            DurableJsonReadResult<DurableCheckpointLaunchObservation> read;
            try
            {
                read = DurableJsonFileStore.ReadLatestValid<DurableCheckpointLaunchObservation>(
                    checkpoint => checkpoint?.Revision ?? 0,
                    sourcePath,
                    sourcePath + ".bak");
            }
            catch (Exception ex)
            {
                reason = "DurableCheckpointReadFailed:" + ex.GetBaseException().Message;
                return true;
            }
            var checkpoint = read?.Value;
            if (checkpoint == null)
            {
                reason = "DurableCheckpointUnavailable:" +
                         (read?.Status.ToString() ?? "Missing");
                return true;
            }
            if (checkpoint.Revision < mirror.Revision)
            {
                reason = "DurableCheckpointRevisionRegressed";
                return true;
            }
            if (!checkpoint.Armed)
            {
                reason = "DurableCheckpointDisarmed";
                return true;
            }
            if (!string.Equals(
                    checkpoint.WatchdogSessionId,
                    _args.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    checkpoint.RunId,
                    mirror.RunId,
                    StringComparison.OrdinalIgnoreCase) ||
                checkpoint.RunEpoch != mirror.RunEpoch)
            {
                reason = "DurableCheckpointIdentityChanged";
                return true;
            }
            return false;
        }

        private RecoveryBootstrapObservation ObserveBootstrapOutcome(
            DurableRelaunchPermitIdentity identity,
            GuardedProcessOwnerReceipt owner,
            int attempt,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (identity == null || owner?.Process == null)
                return RecoveryBootstrapObservation.None;
            var record = _relaunchCoordinator.Snapshot;
            if (record == null || record.Generation != identity.Generation ||
                !string.Equals(record.PermitId, identity.PermitId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(record.PermitNonce))
                return RecoveryBootstrapObservation.None;
            RecoveryBootstrapReceipt receipt;
            if (!RecoveryBootstrapOutcomeStore.TryReadVerified(
                    _args.JournalDirectory,
                    _args.SessionId,
                    identity.Generation,
                    identity.PermitId,
                    record.PermitNonce,
                    out receipt))
                return RecoveryBootstrapObservation.None;
            if (receipt.ChildProcessId != owner.ProcessId ||
                receipt.ChildProcessStartUtcTicks != owner.StartUtcTicks)
            {
                Record(
                    "RecoveryBootstrapReceiptRejected",
                    $"ProcessIdentityMismatch;ReceiptPid={receipt.ChildProcessId};" +
                    $"ExpectedPid={owner.ProcessId};Outcome={receipt.Outcome}");
                return RecoveryBootstrapObservation.None;
            }
            if (receipt.Outcome == RecoveryBootstrapOutcome.StartupFailed)
            {
                failureReason = string.IsNullOrWhiteSpace(receipt.Reason)
                    ? "Recovery child reported StartupFailed before UI attach."
                    : receipt.Reason;
                Record(
                    "RecoveryBootstrapStartupFailed",
                    $"Attempt={attempt};Permit={identity.Generation};" +
                    $"CheckpointRevision={receipt.CheckpointRevision};Reason={failureReason}");
                return RecoveryBootstrapObservation.StartupFailed;
            }
            if (receipt.Outcome != RecoveryBootstrapOutcome.RejectedNoWork)
                return RecoveryBootstrapObservation.None;

            var rejected = ExecuteAuthorityTransitionWithBusyRetry(
                () => _relaunchCoordinator.RejectNoWork(
                    identity,
                    string.IsNullOrWhiteSpace(receipt.Reason)
                        ? "RecoveryBootstrapRejectedNoWork"
                        : receipt.Reason));
            if (rejected?.Record != null)
                lock (_journalGate) ApplyDurablePermitLocked(rejected.Record);
            if (rejected?.Succeeded != true ||
                rejected.Record?.State != DurableRelaunchPermitState.RejectedNoWork)
            {
                BlockLaunchOutcomeUnknown(
                    "RejectedNoWorkCommitFailed:" + (rejected?.Reason ?? "Unknown"));
                return RecoveryBootstrapObservation.RejectedNoWork;
            }

            try
            {
                if (!owner.Process.HasExited && !owner.Process.WaitForExit(5000))
                    owner.KillExactAndDispose();
                else
                    owner.Dispose();
            }
            catch { owner.KillExactAndDispose(); }
            Record(
                "RecoveryBootstrapRejectedNoWork",
                $"Attempt={attempt};Permit={identity.Generation};" +
                $"CheckpointRevision={receipt.CheckpointRevision};Reason={receipt.Reason}");
            _transitionWindow.Hide();
            Interlocked.Exchange(ref _transitionActive, 0);
            PublishTerminal("RecoveryRejectedNoWork", receipt.Reason);
            _stop.Cancel();
            return RecoveryBootstrapObservation.RejectedNoWork;
        }

        private bool TryCommitAttachedPermit(WatchdogRunSession session, out string failure)
        {
            failure = string.Empty;
            if (session == null || !session.RecoveryProcess ||
                session.RelaunchGeneration <= 0 ||
                string.IsNullOrWhiteSpace(session.RelaunchPermitId) ||
                string.IsNullOrWhiteSpace(session.RelaunchPermitNonce))
            {
                failure = "RecoveryAttachPermitMissing";
                return false;
            }
            var record = _relaunchCoordinator.Snapshot;
            if (record == null ||
                record.Generation != session.RelaunchGeneration ||
                !string.Equals(record.PermitId, session.RelaunchPermitId, StringComparison.Ordinal) ||
                !string.Equals(record.PermitNonce, session.RelaunchPermitNonce, StringComparison.Ordinal) ||
                (record.State != DurableRelaunchPermitState.Started &&
                 record.State != DurableRelaunchPermitState.Attached))
            {
                failure = "RecoveryAttachPermitIdentityMismatch";
                return false;
            }
            if (session.ProcessId <= 0 || session.ProcessStartUtcTicks <= 0)
            {
                failure = "RecoveryAttachProcessIdentityMissing";
                return false;
            }
            if (ProbeProcessIdentity(
                    session.ProcessId,
                    session.ProcessStartUtcTicks) !=
                DurableRelaunchProcessObservation.Alive)
            {
                failure = "RecoveryAttachProcessNotAliveOrReused";
                return false;
            }
            if (record.State == DurableRelaunchPermitState.Attached)
            {
                if (record.ProcessId != session.ProcessId ||
                    record.ProcessStartUtcTicks != session.ProcessStartUtcTicks)
                {
                    failure = "RecoveryAttachProcessIdentityMismatch";
                    return false;
                }
                return true;
            }
            var result = ExecuteAuthorityTransitionWithBusyRetry(
                () => _relaunchCoordinator.CommitAttached(
                    record.Identity,
                    session.ProcessId,
                    session.ProcessStartUtcTicks));
            if (result != null &&
                result.TransitionStatus == DurableAuthorityTransitionStatus.Busy)
            {
                failure = result.Reason ?? "AuthorityMutexBusy";
                return false;
            }
            lock (_journalGate)
            {
                ApplyDurablePermitLocked(result.Record);
                if (!result.Succeeded || result.Blocked)
                {
                    failure = result.Reason ?? "AttachedCommitFailed";
                    return false;
                }
                if (!AdvanceReplacementState(
                        record.Generation,
                        record.PermitId,
                        RecoveryReplacementState.Attached,
                        "Authenticated recovery process attached"))
                {
                    failure = "ReplacementTransactionPersistFailed:Attached";
                    return false;
                }
                return true;
            }
        }

        private bool TryValidateRecoveryReconnectPermit(
            WatchdogRunSession session,
            out string failure)
        {
            failure = string.Empty;
            if (session == null || !session.RecoveryProcess ||
                session.RelaunchGeneration <= 0 ||
                string.IsNullOrWhiteSpace(session.RelaunchPermitId) ||
                string.IsNullOrWhiteSpace(session.RelaunchPermitNonce))
            {
                failure = "RecoveryReconnectPermitMissing";
                return false;
            }

            var record = _relaunchCoordinator.Snapshot;
            if (record == null ||
                (record.State != DurableRelaunchPermitState.Attached &&
                 record.State != DurableRelaunchPermitState.Committed) ||
                record.Generation != session.RelaunchGeneration ||
                !string.Equals(record.PermitId, session.RelaunchPermitId, StringComparison.Ordinal) ||
                !string.Equals(record.PermitNonce, session.RelaunchPermitNonce, StringComparison.Ordinal) ||
                record.ProcessId != session.ProcessId ||
                record.ProcessStartUtcTicks != session.ProcessStartUtcTicks)
            {
                failure = "RecoveryReconnectPermitIdentityMismatch";
                return false;
            }

            if (ProbeProcessIdentity(
                    session.ProcessId,
                    session.ProcessStartUtcTicks) !=
                DurableRelaunchProcessObservation.Alive)
            {
                failure = "RecoveryReconnectProcessNotAliveOrReused";
                return false;
            }

            return true;
        }

        private bool TryCommitRecoveryBatch(
            string runId,
            long runEpoch,
            string recoveryStage,
            string progressToken,
            long commitGeneration)
        {
            if (commitGeneration <= 0) return false;
            var record = _relaunchCoordinator?.Snapshot;
            if (record == null)
                return false;
            if (record.State == DurableRelaunchPermitState.Committed)
            {
                return record.RecoveryCommitGeneration >= commitGeneration &&
                       string.Equals(record.RunId, runId, StringComparison.Ordinal) &&
                       record.RunEpoch == runEpoch &&
                       string.Equals(record.RecoveryStage, recoveryStage, StringComparison.Ordinal) &&
                       string.Equals(record.ProgressToken, progressToken, StringComparison.Ordinal);
            }
            if (record.State != DurableRelaunchPermitState.Attached)
                return false;
            var result = ExecuteAuthorityTransitionWithBusyRetry(
                () => _relaunchCoordinator.CommitRecoveryBatch(
                    record.Identity,
                    string.IsNullOrWhiteSpace(runId) ? record.RunId : runId,
                    runEpoch > 0 ? runEpoch : record.RunEpoch,
                    string.IsNullOrWhiteSpace(recoveryStage)
                        ? record.RecoveryStage
                        : recoveryStage,
                    string.IsNullOrWhiteSpace(progressToken) ? record.ProgressToken : progressToken,
                    commitGeneration));
            if (result != null &&
                result.TransitionStatus == DurableAuthorityTransitionStatus.Busy)
            {
                RecordRecoveryCommitDeferred(
                    commitGeneration,
                    "Authority",
                    result.Reason ?? "AuthorityMutexBusy");
                return false;
            }
            lock (_journalGate)
            {
                ApplyDurablePermitLocked(result.Record);
                if (!result.Succeeded || result.Blocked)
                {
                    _journal.LastReason = result.Reason ?? "RecoveryBatchCommitFailed";
                    if (result.Blocked ||
                        result.TransitionStatus == DurableAuthorityTransitionStatus.Unproven)
                    {
                        _journal.RecoveryBlocked = true;
                        _journal.RecoveryFailurePermanent = true;
                    }
                    try { TryPersistJournalSnapshotLocked(); } catch { }
                    return false;
                }
                if (!AdvanceReplacementState(
                        record.Generation,
                        record.PermitId,
                        RecoveryReplacementState.CheckpointCommitted,
                        "Recovery checkpoint committed"))
                {
                    _journal.LastReason =
                        "ReplacementTransactionPersistFailed:CheckpointCommitted";
                    _journal.RecoveryBlocked = true;
                    _journal.RecoveryFailurePermanent = true;
                    try { TryPersistJournalSnapshotLocked(); } catch { }
                    return false;
                }
                return true;
            }
        }

        private bool TryAcceptRecoveryBatchCommit(
            long commitGeneration,
            string evidence,
            string detail,
            WatchdogHeartbeat heartbeat,
            string progressToken = null)
        {
            if (heartbeat == null ||
                string.IsNullOrWhiteSpace(heartbeat.RunId) ||
                heartbeat.RunEpoch <= 0 ||
                string.IsNullOrWhiteSpace(heartbeat.RecoveryStage))
            {
                RecordRecoveryCommitDeferred(
                    commitGeneration,
                    evidence,
                    "ContextMissing");
                return false;
            }
            var token = string.IsNullOrWhiteSpace(progressToken)
                ? heartbeat.RecoveryProgressVersion.ToString(CultureInfo.InvariantCulture)
                : progressToken;
            if (!TryCommitRecoveryBatch(
                    heartbeat.RunId,
                    heartbeat.RunEpoch,
                    heartbeat.RecoveryStage,
                    token,
                    commitGeneration))
            {
                RecordRecoveryCommitDeferred(
                    commitGeneration,
                    evidence,
                    $"Rejected;RunId={heartbeat.RunId};RunEpoch={heartbeat.RunEpoch};" +
                    $"Stage={heartbeat.RecoveryStage}");
                return false;
            }
            ClearRecoveryCommitRetry(commitGeneration, evidence);
            ObserveRecoveryBatchCommit(
                commitGeneration,
                evidence,
                detail,
                heartbeat.RunId);
            WatchdogRecoveryCommitMarker.Archive(
                _args.JournalDirectory,
                _args.SessionId,
                commitGeneration,
                "Accepted");
            return true;
        }

        private void RecordRecoveryCommitDeferred(
            long generation,
            string evidence,
            string reason)
        {
            var key = generation.ToString(CultureInfo.InvariantCulture) + "|" +
                      (evidence ?? string.Empty);
            var now = DateTime.UtcNow;
            var shouldLog = false;
            var count = 0;
            lock (_recoveryCommitRetryGate)
            {
                if (!_recoveryCommitRetries.TryGetValue(key, out var state))
                {
                    state = new RecoveryCommitRetryState();
                    _recoveryCommitRetries[key] = state;
                }
                state.DeferredCount++;
                count = state.DeferredCount;
                if (state.LastLoggedUtc == default ||
                    now - state.LastLoggedUtc >= TimeSpan.FromMinutes(1))
                {
                    state.LastLoggedUtc = now;
                    shouldLog = true;
                }
                if (_recoveryCommitRetries.Count > 128)
                {
                    foreach (var stale in _recoveryCommitRetries
                                 .OrderBy(pair => pair.Value.LastLoggedUtc)
                                 .Take(_recoveryCommitRetries.Count - 128)
                                 .Select(pair => pair.Key)
                                 .ToArray())
                        _recoveryCommitRetries.Remove(stale);
                }
            }
            if (shouldLog)
                Record(
                    "RecoveryBatchCommitDeferred",
                    $"Evidence={evidence};Generation={generation};Reason={reason};" +
                    $"DeferredCount={count};NextSummarySeconds=60");
        }

        private void ClearRecoveryCommitRetry(long generation, string evidence)
        {
            var key = generation.ToString(CultureInfo.InvariantCulture) + "|" +
                      (evidence ?? string.Empty);
            lock (_recoveryCommitRetryGate)
                _recoveryCommitRetries.Remove(key);
        }

        private DurableRelaunchResult ExecuteAuthorityTransitionWithBusyRetry(
            Func<DurableRelaunchResult> action)
        {
            DurableRelaunchResult result = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                result = action?.Invoke();
                if (result == null ||
                    result.TransitionStatus != DurableAuthorityTransitionStatus.Busy)
                    return result;
                if (attempt < 2) Thread.Sleep(100 * (attempt + 1));
            }
            return result;
        }

        private void BlockLaunchOutcomeUnknown(string reason)
        {
            string fingerprint;
            int consecutive;
            lock (_journalGate)
            {
                fingerprint = _journal.RecoveryFailureFingerprint;
                consecutive = Math.Max(1, _journal.ConsecutiveStartupFailures);
            }
            EnterRelaunchCircuitOpen(
                fingerprint,
                consecutive,
                reason ?? "LaunchOutcomeUnknown");
        }

        private static DurableRelaunchProcessObservation ProbeProcessIdentity(
            int processId,
            long processStartUtcTicks)
        {
            if (processId <= 0 || processStartUtcTicks <= 0)
                return DurableRelaunchProcessObservation.Unknown;
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                        return DurableRelaunchProcessObservation.Dead;
                    var actual = process.StartTime.ToUniversalTime().Ticks;
                    return actual == processStartUtcTicks
                        ? DurableRelaunchProcessObservation.Alive
                        : DurableRelaunchProcessObservation.IdentityMismatch;
                }
            }
            catch (ArgumentException)
            {
                return DurableRelaunchProcessObservation.Dead;
            }
            catch
            {
                return DurableRelaunchProcessObservation.Unknown;
            }
        }

        private void MarkRecoveryBlockedLocked(string reason)
        {
            _journal.RecoveryBlocked = true;
            _journal.RecoveryFailurePermanent = true;
            _journal.LastReason = reason ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_journal.RecoveryFailureFingerprint))
                _journal.RecoveryFailureFingerprint = RecoveryFailurePolicy.BuildFingerprint(
                    _journal.RecoveryFailureCode ?? reason);
            _journal.RecoveryBlockedUtcTicks = DateTime.UtcNow.Ticks;
        }

        private void EnterRelaunchCircuitOpen(string fingerprint, int consecutiveCount, string detail)
        {
            Interlocked.Exchange(ref _relaunchStarted, 0);
            Interlocked.Exchange(ref _transitionActive, 1);
            DurableRelaunchResult durableBlock = null;
            try
            {
                durableBlock = ExecuteAuthorityTransitionWithBusyRetry(
                    () => _relaunchCoordinator.Block(
                        string.IsNullOrWhiteSpace(detail)
                            ? "ProcessRelaunchCircuitOpen"
                            : detail));
            }
            catch { }
            lock (_journalGate)
            {
                _journal.RecoveryBlocked = true;
                _journal.RecoveryFailureFingerprint = fingerprint ?? string.Empty;
                _journal.ConsecutiveStartupFailures = Math.Max(
                    1,
                    consecutiveCount);
                _journal.RecoveryBlockedUtcTicks = DateTime.UtcNow.Ticks;
                if (durableBlock?.Record != null)
                    ApplyDurablePermitLocked(durableBlock.Record);
                try { TryPersistJournalSnapshotLocked(); } catch { }
            }
            Record(
                "SafeIdleRecoveryBlocked",
                $"ProcessRelaunchCircuitOpen;Count={consecutiveCount};Fingerprint={fingerprint};Detail={detail}");
            ScheduleNextCircuitHalfOpen();
            _unattendedAlarmSink.Publish(
                "P0",
                "MainLaunchCircuitOpen",
                detail ?? string.Empty,
                RecoveryFailureDomain.MainLaunch,
                null);
            ShowRecoveryBlockedTransition(detail);
        }

        internal static bool IsAutomaticHalfOpenEligible(
            string failureCode,
            bool permanent)
        {
            if (permanent || string.IsNullOrWhiteSpace(failureCode)) return false;
            return failureCode.StartsWith("RecoveryLaunchFailed", StringComparison.Ordinal) ||
                   failureCode.StartsWith("RecoveryAttachFailed", StringComparison.Ordinal) ||
                   failureCode.StartsWith("RecoveryBootstrapStartupFailed", StringComparison.Ordinal) ||
                   failureCode.StartsWith("RecoveryProcessExitedBeforeBatchCommit", StringComparison.Ordinal);
        }

        internal static bool ShouldProbeCircuitHalfOpen(bool recoveryBlocked, bool attached)
        {
            // Attachment describes the old main process.  A circuit probe replaces that process,
            // so an already-exited (_attached=false) owner must never prevent the half-open timer.
            return recoveryBlocked;
        }

        internal static int CalculateCircuitHalfOpenDelaySeconds(
            int attempt,
            bool lastKnownGoodActive)
        {
            if (lastKnownGoodActive) return 30 * 60;
            return attempt <= 0 ? 30 : attempt == 1 ? 60 : 300;
        }

        private bool ScheduleNextCircuitHalfOpen()
        {
            int attempt;
            string failureCode;
            bool permanent;
            lock (_journalGate)
            {
                attempt = Math.Max(0, _journal.CircuitProbeAttempt);
                failureCode = _journal.RecoveryFailureCode;
                permanent = _journal.RecoveryFailurePermanent;
            }
            if (!IsAutomaticHalfOpenEligible(failureCode, permanent))
            {
                Interlocked.Exchange(ref _nextCircuitHalfOpenTimestamp, 0);
                Interlocked.Exchange(ref _circuitHalfOpenStarted, 0);
                return false;
            }
            var activeDirectory = Path.GetFileName(
                Path.GetDirectoryName(
                    _relaunchCoordinator.ActiveExecutablePath ?? string.Empty) ??
                string.Empty);
            var seconds = CalculateCircuitHalfOpenDelaySeconds(
                attempt,
                string.Equals(
                    activeDirectory,
                    "LastKnownGood",
                    StringComparison.OrdinalIgnoreCase));
            Interlocked.Exchange(
                ref _nextCircuitHalfOpenTimestamp,
                Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency);
            return true;
        }

        private void TryBeginAutomaticCircuitHalfOpen()
        {
            var due = Interlocked.Read(ref _nextCircuitHalfOpenTimestamp);
            if (due <= 0 || Stopwatch.GetTimestamp() < due ||
                Interlocked.CompareExchange(ref _circuitHalfOpenStarted, 1, 0) != 0)
                return;

            string failureCode;
            string fingerprint;
            int count;
            bool permanent;
            lock (_journalGate)
            {
                failureCode = _journal.RecoveryFailureCode;
                fingerprint = _journal.RecoveryFailureFingerprint;
                count = _journal.ConsecutiveStartupFailures;
                permanent = _journal.RecoveryFailurePermanent;
            }
            if (!IsAutomaticHalfOpenEligible(failureCode, permanent))
            {
                Interlocked.Exchange(ref _nextCircuitHalfOpenTimestamp, 0);
                Interlocked.Exchange(ref _circuitHalfOpenStarted, 0);
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var opened = _relaunchCoordinator.TryAutomaticHalfOpen(
                        fingerprint,
                        count);
                    if (opened?.Succeeded != true ||
                        opened.Record?.State != DurableRelaunchPermitState.Approved)
                    {
                        Record(
                            "RecoveryCircuitHalfOpenDeferred",
                            opened?.Reason ?? "AutomaticHalfOpenFailed");
                        lock (_journalGate) _journal.CircuitProbeAttempt++;
                        ScheduleNextCircuitHalfOpen();
                        return;
                    }

                    var permit = opened.Record;
                    if (!TryCreateHalfOpenSafetyHandoff(permit, out var safetyReceipt,
                            out var safetyFailure))
                    {
                        _relaunchCoordinator.Block(
                            "HalfOpenSafetySnapshotFailed:" + safetyFailure);
                        Record("RecoveryCircuitHalfOpenSafetyFailed", safetyFailure);
                        lock (_journalGate) _journal.CircuitProbeAttempt++;
                        ScheduleNextCircuitHalfOpen();
                        return;
                    }

                    lock (_journalGate)
                    {
                        ApplyDurablePermitLocked(permit);
                        _journal.RecoveryBlocked = false;
                        _journal.RecoveryFailurePermanent = false;
                        _journal.CircuitProbeAttempt++;
                        _journal.LastReason = "AutomaticHalfOpenApproved";
                        try { TryPersistJournalSnapshotLocked(); } catch { }
                    }
                    Interlocked.Exchange(ref _transitionActive, 1);
                    _transitionWindow.Show(
                        "自动恢复正在重试",
                        "设备保持断能，正在执行新的安全确认和主程序半开探测。",
                        0,
                        Math.Max(1, count + 1));
                    Record(
                        "RecoveryCircuitHalfOpenApproved",
                        $"Generation={permit.Generation};PermitId={permit.PermitId};" +
                        $"PreviousFailureCount={count}");
                    BeginSafetyHandoff(safetyReceipt);
                    BeginRelaunchAfterExit(permit.Generation);
                }
                catch (Exception ex)
                {
                    Record(
                        "RecoveryCircuitHalfOpenFailed",
                        ex.GetBaseException().Message);
                    ScheduleNextCircuitHalfOpen();
                }
                finally
                {
                    Interlocked.Exchange(ref _circuitHalfOpenStarted, 0);
                }
            });
        }

        private bool TryCreateHalfOpenSafetyHandoff(
            DurableRelaunchPermitRecord permit,
            out WatchdogSafetyHandoffReceipt receipt,
            out string failure)
        {
            receipt = null;
            failure = string.Empty;
            try
            {
                WatchdogSafetyHandoffReceipt previous;
                if (permit == null ||
                    !WatchdogSafetyHandoffReceiptStore.TryRead(
                        _args.JournalDirectory,
                        _args.SessionId,
                        out previous) ||
                    previous?.IsSafetyCompleted != true)
                    throw new InvalidDataException("PreviousSafetyEvidenceMissing");
                var priorSnapshot = WatchdogSafetyConfigSnapshotStore.Validate(
                    _args.JournalDirectory,
                    previous.HandoffId,
                    previous.ConfigSnapshotPath,
                    previous.ConfigSnapshotManifestPath,
                    previous.ConfigSnapshotManifestSha256);
                if (priorSnapshot?.Succeeded != true)
                    throw new InvalidDataException(
                        "PreviousSafetySnapshotInvalid:" + priorSnapshot?.Error);
                var mainExecutablePath = _relaunchCoordinator.ActiveExecutablePath;
                var safetyAgentExecutablePath = Path.Combine(
                    Path.GetDirectoryName(mainExecutablePath) ?? string.Empty,
                    "MTTFTest.SafetyAgent.exe");
                if (!File.Exists(mainExecutablePath) ||
                    !File.Exists(safetyAgentExecutablePath))
                    throw new FileNotFoundException("HalfOpenExecutableMissing");
                var mainSha = SupervisorProtocol.ComputeSha256(mainExecutablePath);
                var agentSha = SupervisorProtocol.ComputeSha256(
                    safetyAgentExecutablePath);
                if (string.Equals(mainExecutablePath, previous.MainExecutablePath,
                        StringComparison.OrdinalIgnoreCase) &&
                    (!Sha256Equals(mainSha, previous.MainExecutableSha256) ||
                     !Sha256Equals(agentSha, previous.SafetyAgentExecutableSha256)))
                    throw new InvalidDataException("HalfOpenExecutableHashChanged");

                var handoffId = Guid.NewGuid().ToString("N");
                var created = WatchdogSafetyConfigSnapshotStore.Create(
                    _args.JournalDirectory,
                    handoffId,
                    priorSnapshot.ConfigDirectory,
                    null,
                    priorSnapshot.Manifest.BuildIdentity,
                    priorSnapshot.Runtime,
                    _args.SessionId,
                    previous.SessionGeneration,
                    previous.SessionLease,
                    permit.Generation,
                    permit.PermitId,
                    mainSha,
                    agentSha);
                if (created?.Succeeded != true)
                    throw new InvalidDataException(
                        "HalfOpenSafetySnapshotCreateFailed:" + created?.Error);

                receipt = new WatchdogSafetyHandoffReceipt
                {
                    SchemaVersion = SupervisorProtocol.SchemaVersion,
                    SessionId = _args.SessionId,
                    SessionGeneration = previous.SessionGeneration,
                    SessionLease = previous.SessionLease,
                    HandoffId = handoffId,
                    Nonce = Guid.NewGuid().ToString("N"),
                    StopSafetyTransactionId = Guid.NewGuid().ToString("N"),
                    RunId = previous.RunId,
                    RunEpoch = previous.RunEpoch,
                    Revision = previous.Revision + 1,
                    State = WatchdogSafetyHandoffState.Accepted,
                    Stage = WatchdogSafetyStage.None,
                    PersistenceDrained = true,
                    LogicalQuiescent = true,
                    HardwareResourcesReleased = true,
                    ExecutionAuthorizationRevoked = true,
                    CallbacksIsolated = true,
                    DataAuditState = WatchdogDataAuditState.Drained,
                    SidecarProcessId = _sidecarProcessId,
                    SidecarProcessStartUtcTicks = _sidecarProcessStartUtcTicks,
                    ProjectDirectory = previous.ProjectDirectory,
                    MainExecutablePath = mainExecutablePath,
                    MainExecutableSha256 = mainSha,
                    SafetyAgentExecutablePath = safetyAgentExecutablePath,
                    SafetyAgentExecutableSha256 = agentSha,
                    ConfigSnapshotPath = created.ConfigDirectory,
                    ConfigSnapshotManifestPath = created.ManifestPath,
                    ConfigSnapshotManifestSha256 = created.ManifestSha256,
                    ConfigSnapshotSchemaVersion = 2,
                    RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                    RelaunchPermitGeneration = permit.Generation,
                    RelaunchPermitId = permit.PermitId,
                    RelaunchPermitNonceSha256 =
                        WatchdogTakeoverPermitBindingPolicy.HashNonce(permit.PermitNonce),
                    Detail = "AutomaticHalfOpenSafetyRequested"
                };
                WatchdogSafetyHandoffReceiptStore.WriteThrough(
                    _args.JournalDirectory,
                    receipt);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetBaseException().Message;
                receipt = null;
                _unattendedAlarmSink.Publish(
                    "P0",
                    "AutomaticHalfOpenSafetyFailed",
                    failure,
                    RecoveryFailureDomain.EvidenceBinding,
                    null);
                return false;
            }
        }

        private void ShowRecoveryBlockedTransition(string detail)
        {
            Interlocked.Exchange(ref _transitionActive, 1);
            _transitionWindow.BeginTransition();
            _transitionWindow.Show(
                "自动恢复已阻断，设备保持安全",
                $"恢复失败已进入持久终态（Code={_journal.RecoveryFailureCode ?? "Unknown"}，" +
                $"Count={Math.Max(1, _journal.ConsecutiveStartupFailures)}）。" +
                (IsAutomaticHalfOpenEligible(
                    _journal.RecoveryFailureCode,
                    _journal.RecoveryFailurePermanent)
                    ? "设备持续断能；冷却后将自动执行安全复核和半开重试，无需人工点击。"
                    : "证据或配置无法安全验证，系统保持断能并持续告警。") +
                "可点击下方按钮停止并关闭。\r\n" +
                (detail ?? string.Empty),
                0,
                _journal.RecoveryAttempt);
        }

        private RecoveryFailureDecision RegisterRecoveryFailure(
            string reason,
            RecoveryFailureClassification classification)
        {
            classification = classification ?? RecoveryFailurePolicy.Classify(null, false, reason);
            RecoveryFailureReport report;
            lock (_journalGate)
            {
                report = new RecoveryFailureReport
                {
                    RootCode = classification.Code,
                    DeviceOrChannelGroup = _journal.DeviceOrChannelGroup,
                    RunId = _journal.RunId,
                    RunEpoch = _journal.LastHeartbeat?.RunEpoch ?? 0,
                    RecoveryStage = _journal.RecoveryStage,
                    RecoveryProgressToken = _journal.RecoveryProgressToken,
                    RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource
                };
            }
            return RegisterRecoveryFailure(report, classification);
        }

        private static RecoveryFailureStageKind ResolveFailureStage(
            string failureCode,
            string recoveryStage)
        {
            var code = failureCode ?? string.Empty;
            var stage = recoveryStage ?? string.Empty;
            if (code.IndexOf("Attach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                code.IndexOf("Bootstrap", StringComparison.OrdinalIgnoreCase) >= 0)
                return RecoveryFailureStageKind.MainAttach;
            if (code.IndexOf("Launch", StringComparison.OrdinalIgnoreCase) >= 0)
                return RecoveryFailureStageKind.MainLaunch;
            if (code.IndexOf("FirstCycle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                code.IndexOf("BatchCommit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stage.IndexOf("FirstCycle", StringComparison.OrdinalIgnoreCase) >= 0)
                return RecoveryFailureStageKind.FirstCycleCommit;
            return RecoveryFailureStageKind.CheckpointResume;
        }

        private void RecordStageFailureOnceLocked(
            RecoveryFailureStageKind stage,
            string operationId,
            long permitGeneration,
            string fingerprint)
        {
            var key = string.Join(
                "|",
                operationId ?? string.Empty,
                permitGeneration.ToString(CultureInfo.InvariantCulture),
                fingerprint ?? string.Empty);
            switch (stage)
            {
                case RecoveryFailureStageKind.SafetyPrerequisite:
                    if (string.Equals(
                            _journal.LastSafetyPrerequisiteFailureKey,
                            key,
                            StringComparison.Ordinal))
                        return;
                    _journal.LastSafetyPrerequisiteFailureKey = key;
                    _journal.SafetyPrerequisiteFailureCount++;
                    break;
                case RecoveryFailureStageKind.MainLaunch:
                    if (string.Equals(
                            _journal.LastMainLaunchFailureKey,
                            key,
                            StringComparison.Ordinal))
                        return;
                    _journal.LastMainLaunchFailureKey = key;
                    _journal.MainLaunchFailureCount++;
                    break;
                case RecoveryFailureStageKind.MainAttach:
                    if (string.Equals(
                            _journal.LastMainAttachFailureKey,
                            key,
                            StringComparison.Ordinal))
                        return;
                    _journal.LastMainAttachFailureKey = key;
                    _journal.MainAttachFailureCount++;
                    break;
                case RecoveryFailureStageKind.CheckpointResume:
                    if (string.Equals(
                            _journal.LastCheckpointResumeFailureKey,
                            key,
                            StringComparison.Ordinal))
                        return;
                    _journal.LastCheckpointResumeFailureKey = key;
                    _journal.CheckpointResumeFailureCount++;
                    break;
                case RecoveryFailureStageKind.FirstCycleCommit:
                    if (string.Equals(
                            _journal.LastFirstCycleCommitFailureKey,
                            key,
                            StringComparison.Ordinal))
                        return;
                    _journal.LastFirstCycleCommitFailureKey = key;
                    _journal.FirstCycleCommitFailureCount++;
                    break;
            }
        }

        private RecoveryFailureDecision RegisterRecoveryFailure(
            RecoveryFailureReport report,
            RecoveryFailureClassification classification,
            string requestCorrelationId = null,
            string requestPayloadSha256 = null,
            string operationId = null,
            long connectionGeneration = 0,
            string requestFailureFingerprint = null)
        {
            classification = classification ?? RecoveryFailurePolicy.Classify(
                report?.RootCode,
                false,
                string.Empty);
            report = RecoveryFailurePolicy.NormalizeReport(report);
            DurableRelaunchPermitRecord strictRecord;
            int budget;
            int recoveryAttemptGeneration;
            string fingerprint;
            string seed;
            StrictRecoveryFailureReplayIdentity replayIdentity;
            // Freeze the complete request projection once under the journal
            // gate.  The cross-process authority CAS below is deliberately
            // outside this lock, preventing disk/mutex stalls from blocking
            // heartbeats or ordinary journal projection.
            lock (_journalGate)
            {
                strictRecord = _relaunchCoordinator.Snapshot;
                budget = strictRecord?.MaximumProcessRelaunches > 0
                    ? strictRecord.MaximumProcessRelaunches
                    : Math.Max(1, classification.MaximumProcessRelaunches);
                recoveryAttemptGeneration = Math.Max(1, _journal.RecoveryAttempt + 1);
                fingerprint = string.IsNullOrWhiteSpace(requestFailureFingerprint)
                    ? (string.IsNullOrWhiteSpace(report?.RootCode)
                        ? RecoveryFailurePolicy.BuildFingerprint(classification.Code)
                        : RecoveryFailurePolicy.BuildFingerprint(report.RootCode))
                    : requestFailureFingerprint;
                seed = Sha256Text(string.Join("|", _args.SessionId, fingerprint,
                    report?.RecoveryProgressToken, report?.RunId, report?.RecoveryStage));
            }
            var normalizedCorrelation = ValidGuidN(requestCorrelationId, seed.Substring(0, 32));
            var normalizedPayload = IsSha256(requestPayloadSha256)
                ? requestPayloadSha256.ToUpperInvariant() : Sha256Text(seed);
            StrictRecoveryFailureReplayIdentity.TryResolve(
                strictRecord,
                normalizedCorrelation,
                normalizedPayload,
                out replayIdentity);
            var op = new RecoveryFailureOperation
            {
                OperationId = replayIdentity?.OperationId ?? ValidGuidN(operationId, seed.Substring(0, 32)),
                SessionId = _args.SessionId,
                SessionNonce = replayIdentity?.SessionNonce ?? _sidecarInstanceNonce,
                SidecarProcessId = replayIdentity?.ReporterProcessId ?? _sidecarProcessId,
                SidecarProcessStartUtcTicks = replayIdentity?.ReporterProcessStartUtcTicks ?? _sidecarProcessStartUtcTicks,
                ConnectionGeneration = replayIdentity?.ConnectionGeneration ?? Math.Max(1, connectionGeneration > 0
                    ? connectionGeneration
                    : Interlocked.Read(ref _connectionGeneration)),
                RecoveryAttemptGeneration = replayIdentity?.RecoveryAttemptGeneration ?? recoveryAttemptGeneration,
                RequestCorrelationId = normalizedCorrelation,
                RequestPayloadSha256 = normalizedPayload,
                FailureCode = string.IsNullOrWhiteSpace(classification.Code) ? "RecoveryFailure" : classification.Code,
                FailureFingerprint = fingerprint,
                Permanent = classification.Permanent,
                DetailCode = "RecoveryAttemptFailed",
                RunId = string.IsNullOrWhiteSpace(report?.RunId) ? "watchdog-run" : report.RunId,
                RunEpoch = report?.RunEpoch > 0 ? report.RunEpoch : 1,
                RecoveryStage = string.IsNullOrWhiteSpace(report?.RecoveryStage) ? "Recovery" : report.RecoveryStage,
                RecoveryProgressToken = string.IsNullOrWhiteSpace(report?.RecoveryProgressToken) ? "watchdog-progress" : report.RecoveryProgressToken,
                RecoveryProcessSource = string.IsNullOrWhiteSpace(report?.RecoveryProcessSource) ? RecoveryFailurePolicy.RecoveryProcessSource : report.RecoveryProcessSource,
                DeviceOrChannelGroup = string.IsNullOrWhiteSpace(report?.DeviceOrChannelGroup) ? "WatchdogHost" : report.DeviceOrChannelGroup,
                MaximumProcessRelaunches = budget
            };

            // The strict authority owns the single durable read/decide/CAS.
            // Never hold _journalGate across this cross-process operation.
            var strict = _relaunchCoordinator.RegisterFailure(op);
            var authorityBusy = strict != null &&
                strict.CommitStatus == DurableAuthorityCommitStatus.Busy;
            lock (_journalGate)
            {
                if (authorityBusy)
                {
                    _journal.LastReason = strict.Reason ?? "AuthorityMutexBusy";
                    try { TryPersistJournalSnapshotLocked(); } catch { }
                }
                else
                {
                var record = _relaunchCoordinator.Snapshot;
                ApplyDurablePermitLocked(record);
                _journal.ConsecutiveStartupFailures = strict?.Record?.ConsecutiveFailures ?? _journal.ConsecutiveStartupFailures;
                _journal.RecoveryFailureCode = op.FailureCode;
                _journal.RecoveryFailurePermanent = op.Permanent;
                _journal.RecoveryFailureFingerprint = strict?.Record?.LastFailureFingerprint ?? op.FailureFingerprint;
                _journal.RootCode = report?.RootCode ?? op.FailureCode;
                _journal.DeviceOrChannelGroup = op.DeviceOrChannelGroup;
                _journal.RunId = op.RunId;
                _journal.RecoveryStage = op.RecoveryStage;
                _journal.RecoveryProgressToken = op.RecoveryProgressToken;
                _journal.RecoveryProcessSource = op.RecoveryProcessSource;
                _journal.RecoveryFailureMaxProcessRelaunches = budget;
                RecordStageFailureOnceLocked(
                    ResolveFailureStage(op.FailureCode, op.RecoveryStage),
                    op.OperationId,
                    strictRecord?.Generation ?? 0,
                    op.FailureFingerprint);
                _journal.RecoveryBlocked = strict == null || strict.Blocked || strict.Unproven || !strict.Durable;
                _journal.LastReason = strict?.Reason;
                try { TryPersistJournalSnapshotLocked(); }
                catch
                {
                    // The separate schema4 relaunch record is the action
                    // authority.  A best-effort projection failure in the
                    // full session journal is audited but cannot contradict
                    // an already durable strict decision.
                }
                }
            }

            if (!authorityBusy && strict?.Record?.ConsecutiveFailures == 2 &&
                IsPackageRollbackEligible(op.FailureCode, op.Permanent))
            {
                string rollbackReason;
                if (_relaunchCoordinator.TryActivateLastKnownGood(out rollbackReason))
                {
                    lock (_journalGate)
                    {
                        _journal.LastReason =
                            "PackageRollbackToLastKnownGood:" + rollbackReason;
                        try { TryPersistJournalSnapshotLocked(); } catch { }
                    }
                    RecordEvent(
                        "PackageRollbackActivated",
                        "FailureCode=" + op.FailureCode + ";Count=2;" +
                        "Executable=" + _relaunchCoordinator.ActiveExecutablePath);
                    // The package identity changed, so this is a new probe rather
                    // than a blind retry of the failed bytes.  The normal
                    // half-open path still re-runs the complete safety handoff.
                    Interlocked.Exchange(
                        ref _nextCircuitHalfOpenTimestamp,
                        Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref _circuitHalfOpenStarted, 0);
                }
            }

            if (strict?.Receipt != null)
            {
                // Receipts are sent after the authority gate and journal
                // mirror have completed; the wire object contains no permit
                // nonce or launch-only identity.
                Send(new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    Type = WatchdogMessageType.RecoveryAttemptFailedReceipt,
                    SessionId = _args.SessionId,
                    CorrelationId = strict.Receipt.RequestCorrelationId,
                    RecoveryFailureReceipt = strict.Receipt.Clone()
                });
            }

            var blocked = strict == null || strict.Blocked || strict.Unproven || !strict.Durable ||
                          string.Equals(strict.Receipt?.Disposition, RecoveryFailureDispositions.CircuitOpen, StringComparison.Ordinal) ||
                          string.Equals(strict.Receipt?.Disposition, RecoveryFailureDispositions.IdentityConflict, StringComparison.Ordinal);
            var already = string.Equals(strict?.Receipt?.Disposition, RecoveryFailureDispositions.RelaunchAlreadyPending, StringComparison.Ordinal);
            return new RecoveryFailureDecision
            {
                Fingerprint = strict?.Record?.LastFailureFingerprint ?? op.FailureFingerprint,
                ConsecutiveCount = strict?.Record?.ConsecutiveFailures ?? 0,
                ProcessRelaunchAllowed = strict?.ActionAllowed == true && !blocked,
                RelaunchPermitAlreadyPending = already,
                DurableDecisionRetryPending = authorityBusy,
                RelaunchPermitGeneration = strict?.Record?.Generation ?? 0,
                SafeIdleRecoveryBlocked = blocked,
                RelaunchBudgetExhausted = blocked,
                Report = report
            };
        }

        private static bool IsPackageRollbackEligible(string failureCode, bool permanent)
        {
            if (permanent) return false;
            var code = failureCode ?? string.Empty;
            return code.IndexOf("Hardware", StringComparison.OrdinalIgnoreCase) < 0 &&
                   code.IndexOf("Hydraulic", StringComparison.OrdinalIgnoreCase) < 0 &&
                   code.IndexOf("Caliper", StringComparison.OrdinalIgnoreCase) < 0 &&
                   code.IndexOf("Circuit", StringComparison.OrdinalIgnoreCase) < 0 &&
                   code.IndexOf("SafetyPrerequisite", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string ValidGuidN(string value, string fallback)
        {
            Guid parsed;
            return Guid.TryParseExact(value ?? string.Empty, "N", out parsed)
                ? parsed.ToString("N") : fallback;
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            return value.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'));
        }

        private static string Sha256Text(string value)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty).ToUpperInvariant();
        }

        private RecoveryFailureReport BuildFailureReport(
            WatchdogMessage message,
            RecoveryFailureClassification classification)
        {
            var heartbeat = message?.Heartbeat ?? _journal.LastHeartbeat;
            var report = new RecoveryFailureReport
            {
                RootCode = string.IsNullOrWhiteSpace(message?.RootCode)
                    ? classification?.Code ?? message?.RecoveryFailureCode
                    : message.RootCode,
                DeviceOrChannelGroup = message?.DeviceOrChannelGroup,
                RunId = string.IsNullOrWhiteSpace(message?.RunId)
                    ? heartbeat?.RunId
                    : message.RunId,
                RunEpoch = heartbeat?.RunEpoch ?? 0,
                RecoveryStage = string.IsNullOrWhiteSpace(message?.RecoveryStage)
                    ? heartbeat?.RecoveryStage
                    : message.RecoveryStage,
                RecoveryProgressToken = message?.RecoveryProgressToken,
                RecoveryProcessSource = message?.RecoveryProcessSource
            };
            if (string.IsNullOrWhiteSpace(report.DeviceOrChannelGroup))
                report.DeviceOrChannelGroup = heartbeat?.RecoveryIncident;
            if (string.IsNullOrWhiteSpace(report.DeviceOrChannelGroup))
                report.DeviceOrChannelGroup = string.Join(",", heartbeat?.RecoveryEligibleChannels ?? Array.Empty<int>());
            if (string.IsNullOrWhiteSpace(report.RunId)) report.RunId = _journal.RunId;
            if (string.IsNullOrWhiteSpace(report.RecoveryStage)) report.RecoveryStage = _journal.RecoveryStage;
            if (string.IsNullOrWhiteSpace(report.RecoveryProgressToken) && heartbeat != null)
                report.RecoveryProgressToken = heartbeat.RecoveryProgressVersion.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(report.RecoveryProgressToken))
                report.RecoveryProgressToken = _journal.RecoveryProgressToken;
            if (string.IsNullOrWhiteSpace(report.RecoveryProcessSource))
                report.RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource;
            return RecoveryFailurePolicy.NormalizeReport(report);
        }

        private void CommitRecoveryAttempt(long commitGeneration, string committedRunId)
        {
            var clearTransientAlarm = false;
            lock (_journalGate)
            {
                var hasNewRun = !string.IsNullOrWhiteSpace(committedRunId) &&
                                !string.Equals(
                                    committedRunId,
                                    _journal.RunId,
                                    StringComparison.OrdinalIgnoreCase);
                _journal.RecoveryAttempt = 0;
                _journal.CircuitProbeAttempt = 0;
                // Recovery budget is cleared only after a commit belonging to
                // a genuinely new RunId.  A duplicate commit/heartbeat from
                // the failed run is audit evidence, not a successful reset.
                if (hasNewRun || string.IsNullOrWhiteSpace(_journal.RecoveryFailureFingerprint))
                {
                    clearTransientAlarm = true;
                    _journal.ConsecutiveStartupFailures = 0;
                    _journal.SafetyPrerequisiteFailureCount = 0;
                    _journal.MainLaunchFailureCount = 0;
                    _journal.MainAttachFailureCount = 0;
                    _journal.CheckpointResumeFailureCount = 0;
                    _journal.FirstCycleCommitFailureCount = 0;
                    _journal.LastSafetyPrerequisiteFailureKey = string.Empty;
                    _journal.LastMainLaunchFailureKey = string.Empty;
                    _journal.LastMainAttachFailureKey = string.Empty;
                    _journal.LastCheckpointResumeFailureKey = string.Empty;
                    _journal.LastFirstCycleCommitFailureKey = string.Empty;
                    _journal.SafetyPrerequisiteRetryAttempt = 0;
                    _journal.SafetyPrerequisiteFirstFailureUtcTicks = 0;
                    _journal.SafetyPrerequisiteNextRetryUtcTicks = 0;
                    _journal.RecoveryBlocked = false;
                    _journal.RecoveryFailureCode = string.Empty;
                    _journal.RecoveryFailurePermanent = false;
                    _journal.RecoveryFailureFingerprint = string.Empty;
                    _journal.RootCode = string.Empty;
                    _journal.DeviceOrChannelGroup = string.Empty;
                    _journal.RecoveryProgressToken = string.Empty;
                    _journal.RecoveryProcessSource = string.Empty;
                    _journal.RecoveryFailureMaxProcessRelaunches = 0;
                    _journal.RecoveryFirstFailureUtcTicks = 0;
                    _journal.RecoveryLastFailureUtcTicks = 0;
                    _journal.RecoveryBlockedUtcTicks = 0;
                    _journal.LastRecoveryCommitRunId = committedRunId;
                }
                _journal.LastRecoveryBatchCommitGeneration = Math.Max(
                    _journal.LastRecoveryBatchCommitGeneration,
                    commitGeneration);
            }
            if (clearTransientAlarm)
                SupervisorP0AlarmClient.TryClearTransient(
                    "RecoveryFirstCycleCommitted",
                    "Generation=" + commitGeneration.ToString(
                        CultureInfo.InvariantCulture) + ";RunId=" +
                    (committedRunId ?? string.Empty));
        }

        private void ObserveRecoveryBatchCommit(
            long commitGeneration,
            string evidence,
            string detail,
            string committedRunId = null)
        {
            if (commitGeneration <= _journal.LastRecoveryBatchCommitGeneration) return;
            // A marker carries only the generation.  Do not consume it while
            // a failure budget is active: the subsequent heartbeat must supply
            // the authoritative new RunId before the budget can be cleared.
            if (!string.IsNullOrWhiteSpace(_journal.RecoveryFailureFingerprint) &&
                string.IsNullOrWhiteSpace(committedRunId))
            {
                if (Interlocked.Exchange(
                        ref _pendingCommitGenerationAwaitingRunIdentity,
                        commitGeneration) != commitGeneration)
                {
                    Record(
                        "RecoveryBatchCommitAwaitingRunIdentity",
                        $"Evidence={evidence};Generation={commitGeneration};{detail ?? string.Empty}");
                }
                return;
            }
            Interlocked.Exchange(ref _pendingCommitGenerationAwaitingRunIdentity, 0);
            CommitRecoveryAttempt(commitGeneration, committedRunId);
            Record(
                "RecoveryBatchCommitted",
                $"Evidence={evidence};Generation={commitGeneration};{detail ?? string.Empty}");
            if (Interlocked.CompareExchange(ref _transitionActive, 0, 0) == 0) return;
            try { _transitionWindow.Hide(); } catch { }
            Interlocked.Exchange(ref _transitionActive, 0);
            RecordEvent(
                "RecoveryTransitionClosed",
                $"Evidence={evidence};Generation={commitGeneration}");
        }

        private async Task DelayWithTransitionCountdownAsync(int delaySeconds, int attempt, string reason)
        {
            var started = Stopwatch.GetTimestamp();
            while (!_journal.ManualStopRequested && !IsSessionRevoked() && !_stop.IsCancellationRequested)
            {
                var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                var remaining = Math.Max(0, (int)Math.Ceiling(delaySeconds - elapsed));
                if (remaining <= 0) return;
                _transitionWindow.Show(
                    "系统将在安全退避后自动重启",
                    "恢复原因：" + DescribeRecoveryReason(reason),
                    remaining,
                    attempt);
                await Task.Delay(Math.Min(1000, remaining * 1000)).ConfigureAwait(false);
            }
        }

        private static string DescribeRecoveryReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "未知异常";
            if (reason.StartsWith("Heartbeat", StringComparison.OrdinalIgnoreCase)) return "主程序心跳无响应";
            if (reason.StartsWith("ProcessExited", StringComparison.OrdinalIgnoreCase)) return "主程序意外退出";
            if (reason.StartsWith("FormalProgress", StringComparison.OrdinalIgnoreCase)) return "试验控制进度停滞";
            if (reason.StartsWith("ExternalRecovery", StringComparison.OrdinalIgnoreCase)) return "内部恢复流程停滞";
            if (reason.StartsWith("PowerDisable", StringComparison.OrdinalIgnoreCase)) return "程控电源断能确认超时";
            if (reason.Equals("StopCompleted", StringComparison.OrdinalIgnoreCase))
                return "安全停止已完成，正在等待进程替换";
            if (reason.Equals("StopRequested", StringComparison.OrdinalIgnoreCase))
                return "正在执行安全停止";
            if (reason.IndexOf("StopSafetyTimeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("StopSafetyTimedOut", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("StopHardDeadline", StringComparison.OrdinalIgnoreCase) >= 0)
                return "安全停止流程超时";
            return reason;
        }

        private void TrackManualPauseProgress(WatchdogHeartbeat heartbeat)
        {
            var commanded = heartbeat != null && WatchdogTakeoverPolicy.IsManualPauseCommanded(
                heartbeat.ManualPauseActive,
                heartbeat.ManualPausePending);
            var signature = commanded
                ? string.Join("|", new[]
                {
                    heartbeat.ManualPauseStage ?? string.Empty,
                    heartbeat.ManualPauseProgressVersion.ToString(CultureInfo.InvariantCulture),
                    heartbeat.ManualPauseSafetyFault ? "Fault" : "Healthy",
                    string.Join(",", (heartbeat.ManualPauseEnergizedChannels ?? Array.Empty<int>())
                        .Distinct()
                        .OrderBy(channel => channel))
                })
                : string.Empty;
            lock (_manualPauseProgressGate)
            {
                if (string.Equals(signature, _manualPauseProgressSignature, StringComparison.Ordinal)) return;
                _manualPauseProgressSignature = signature;
                Interlocked.Exchange(ref _manualPauseProgressTimestamp, Stopwatch.GetTimestamp());
            }
        }

        private void OnTransitionPromptDismissRequested()
        {
            RecordEvent(
                "RecoveryTransitionPromptDismissed",
                "ProcessPreserved=True;AutomaticRecoveryUnchanged=True");
            try { _transitionWindow.Hide(); } catch { }
        }

        private void OnTransitionMuteP0BuzzerRequested()
        {
            var muted = SupervisorP0AlarmClient.TryMuteBuzzer(
                "OperatorMuteFromRecoveryWindow",
                "RecoveryTransitionWindow");
            RecordEvent(
                muted ? "P0AlarmBuzzerMutedByOperator" : "P0AlarmBuzzerMuteRejected",
                "FaultLightAndLatchPreserved=True;SupervisorAccepted=" + muted);
        }

        private void OnTransitionOperatorStopRequested()
        {
            lock (_processLaunchGate)
            {
                if (Interlocked.CompareExchange(ref _operatorTransitionStopStarted, 1, 0) != 0) return;
                _journal.ManualStopRequested = true;
            }
            Interlocked.Exchange(ref _manualStopIntentTimestamp, Stopwatch.GetTimestamp());
            Record("OperatorCanceledAutomaticRecovery", "TransitionWindowButton");
            try { WatchdogControlMarker.WriteLocal(_args.SessionId, OperatorTransitionStopMarkerReason); }
            catch (Exception ex) { Record("OperatorStopLocalMarkerFailed", ex.Message); }
            // 本机撤权先同步落地；项目盘副本不得阻塞过渡窗按钮线程。
            _ = Task.Run(() =>
            {
                try { WatchdogControlMarker.WriteProject(
                    _args.JournalDirectory,
                    _args.SessionId,
                    OperatorTransitionStopMarkerReason); }
                catch (Exception ex) { Record("OperatorStopProjectMarkerFailed", ex.Message); }
            });
            Send(
                WatchdogMessageType.RequestStopAll,
                "OperatorCanceledAutomaticRecovery",
                Guid.NewGuid().ToString("N"));
            _ = Task.Run(CompleteTransitionOperatorStopAsync);
        }

        private bool IsTransitionOperatorStopInProgress()
        {
            return Interlocked.CompareExchange(ref _operatorTransitionStopStarted, 0, 0) != 0;
        }

        private async Task CompleteTransitionOperatorStopAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && IsCurrentProcessAlive() &&
                   !_operatorStopAcknowledged.Task.IsCompleted)
                await Task.Delay(250).ConfigureAwait(false);

            if (IsCurrentProcessAlive())
            {
                try
                {
                    using (var process = Process.GetProcessById(_journal.CurrentPid))
                    {
                        if (WatchdogProcessIdentityPolicy.CanKillOldProcess(
                                sessionRevoked: false,
                                manualStopRequested: true,
                                currentIdentityMatches: MatchesCurrentProcess(process)))
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                            Record("OperatorStopProcessTerminated", $"PID={process.Id}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Record("OperatorStopTerminationFailed", ex.GetBaseException().Message);
                }
            }

            try { WatchdogControlMarker.WriteLocal(_args.SessionId, OperatorTransitionStopMarkerReason); }
            catch (Exception ex) { Record("OperatorStopLocalMarkerFailed", ex.Message); }
            try { WatchdogControlMarker.WriteProject(
                _args.JournalDirectory,
                _args.SessionId,
                OperatorTransitionStopMarkerReason); }
            catch (Exception ex) { Record("OperatorStopProjectMarkerFailed", ex.Message); }
            PublishTerminal("OperatorCanceledRecovery", OperatorTransitionStopMarkerReason);
            try { _transitionWindow.Hide(); } catch { }
            _stop.Cancel();
        }

        private bool IsCurrentProcessAlive()
        {
            try { using (var process = Process.GetProcessById(_journal.CurrentPid)) return MatchesCurrentProcess(process) && !process.HasExited; }
            catch { return false; }
        }

        private async Task CaptureMiniDumpBeforeTerminationAsync(Process process, string reason)
        {
            Record("MiniDumpRequested", $"PID={process.Id};Reason={reason}");
            await MiniDumpCapture.TryCaptureAsync(
                    process,
                    _args.JournalDirectory,
                    _args.SessionId,
                    TimeSpan.FromSeconds(3),
                    message => RecordEvent("MiniDump", message),
                    fullMemory: true)
                .ConfigureAwait(false);
        }

        private void CaptureFirstLivenessEvidence(
            int currentProcessId,
            long currentProcessStartUtcTicks,
            string reason)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using (var process = Process.GetProcessById(currentProcessId))
                    {
                        if (!MatchesExactProcess(
                                process,
                                currentProcessStartUtcTicks))
                            return;
                        WriteFirstLivenessSnapshot(process, reason);
                        await MiniDumpCapture.TryCaptureAsync(
                                process,
                                _args.JournalDirectory,
                                _args.SessionId,
                                TimeSpan.FromSeconds(2),
                                message => RecordEvent(
                                    "FirstLivenessMiniDump",
                                    message),
                                fullMemory: false)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    RecordEvent(
                        "FirstLivenessEvidenceFailed",
                        reason + ":" + ex.GetBaseException().Message);
                }
            });
        }

        private void WriteFirstLivenessSnapshot(Process process, string reason)
        {
            try
            {
                WatchdogHeartbeat heartbeat;
                lock (_journalGate) heartbeat = _journal.LastHeartbeat;
                var threads = new List<Dictionary<string, object>>();
                foreach (ProcessThread thread in process.Threads)
                {
                    try
                    {
                        threads.Add(new Dictionary<string, object>
                        {
                            ["Id"] = thread.Id,
                            ["State"] = thread.ThreadState.ToString(),
                            ["WaitReason"] = thread.ThreadState ==
                                System.Diagnostics.ThreadState.Wait
                                    ? thread.WaitReason.ToString()
                                    : string.Empty,
                            ["StartUtcTicks"] = thread.StartTime
                                .ToUniversalTime().Ticks,
                            ["TotalProcessorTimeMs"] =
                                thread.TotalProcessorTime.TotalMilliseconds
                        });
                    }
                    catch { }
                    finally { thread.Dispose(); }
                    if (threads.Count >= 256) break;
                }
                var evidence = new Dictionary<string, object>
                {
                    ["SchemaVersion"] = SupervisorProtocol.SchemaVersion,
                    ["SessionId"] = _args.SessionId,
                    ["CapturedUtc"] = DateTime.UtcNow.ToString("O"),
                    ["Reason"] = reason ?? string.Empty,
                    ["ProcessId"] = process.Id,
                    ["ProcessStartUtcTicks"] = process.StartTime
                        .ToUniversalTime().Ticks,
                    ["MainExecutablePath"] = _args.ExecutablePath,
                    ["MainExecutableSha256"] = File.Exists(_args.ExecutablePath)
                        ? SupervisorProtocol.ComputeSha256(_args.ExecutablePath)
                        : string.Empty,
                    ["WorkingSetBytes"] = process.WorkingSet64,
                    ["PrivateMemoryBytes"] = process.PrivateMemorySize64,
                    ["HandleCount"] = process.HandleCount,
                    ["ThreadCount"] = threads.Count,
                    ["Threads"] = threads,
                    ["SemanticHeartbeat"] = heartbeat,
                    ["WindowsEventLog"] = CaptureRecentWindowsEvents()
                };
                var directory = Path.Combine(
                    _args.JournalDirectory,
                    "WatchdogEvidence");
                var path = Path.Combine(
                    directory,
                    "session-" + _args.SessionId + "-first-liveness-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json");
                DurableJsonFileStore.WriteAtomicWithBackup(
                    path,
                    new UTF8Encoding(false).GetBytes(Json.Serialize(evidence)));
                RecordEvent("FirstLivenessEvidenceFrozen", path);
            }
            catch (Exception ex)
            {
                RecordEvent(
                    "FirstLivenessSnapshotFailed",
                    ex.GetBaseException().Message);
            }
        }

        private static List<Dictionary<string, object>> CaptureRecentWindowsEvents()
        {
            var result = new List<Dictionary<string, object>>();
            var cutoff = DateTime.Now.AddMinutes(-10);
            foreach (var logName in new[] { "Application", "System" })
            {
                try
                {
                    using (var log = new EventLog(logName))
                    {
                        for (var index = log.Entries.Count - 1;
                             index >= 0 && result.Count < 40;
                             index--)
                        {
                            var entry = log.Entries[index];
                            if (entry.TimeGenerated < cutoff) break;
                            if (entry.EntryType != EventLogEntryType.Error &&
                                entry.EntryType != EventLogEntryType.Warning)
                                continue;
                            var message = entry.Message ?? string.Empty;
                            result.Add(new Dictionary<string, object>
                            {
                                ["Log"] = logName,
                                ["Utc"] = entry.TimeGenerated.ToUniversalTime()
                                    .ToString("O"),
                                ["Type"] = entry.EntryType.ToString(),
                                ["Source"] = entry.Source ?? string.Empty,
                                ["EventId"] = entry.InstanceId,
                                ["Message"] = message.Length <= 2000
                                    ? message
                                    : message.Substring(0, 2000)
                            });
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        private static double SemanticSnapshotAgeSeconds(
            WatchdogHeartbeat heartbeat,
            DateTime nowUtc)
        {
            if (heartbeat == null) return double.PositiveInfinity;
            if (heartbeat.SnapshotRevision <= 0 ||
                heartbeat.SnapshotCapturedUtcTicks <= 0)
                return 0;
            return Math.Max(
                0,
                (nowUtc.Ticks - heartbeat.SnapshotCapturedUtcTicks) /
                (double)TimeSpan.TicksPerSecond);
        }

        private static int[] GetRecoveryEligibleChannels(WatchdogHeartbeat heartbeat)
        {
            if (heartbeat == null) return Array.Empty<int>();
            // New clients publish the post-policy set.  Older clients only
            // have EligibleChannels, which remains a compatibility fallback.
            return heartbeat.RecoveryEligibleChannels != null && heartbeat.RecoveryEligibleChannels.Length > 0
                ? heartbeat.RecoveryEligibleChannels
                : heartbeat.EligibleChannels ?? Array.Empty<int>();
        }

        private bool IsSessionRevoked()
        {
            if (WatchdogControlMarker.IsRevoked(
                    _args.JournalDirectory,
                    _args.SessionId))
                return true;
            WatchdogClosingTombstone closing;
            if (!WatchdogClosingTombstoneStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out closing))
                return false;
            return !MatchesPreservedTakeoverPermit(closing);
        }

        private bool MatchesPreservedTakeoverPermit(
            WatchdogClosingTombstone closing)
        {
            if (closing?.PreservesApprovedPermit != true) return false;
            if (WatchdogTakeoverPermitBindingPolicy.Matches(
                    _relaunchCoordinator.Snapshot,
                    _args.SessionId,
                    closing.RelaunchPermitGeneration,
                    closing.RelaunchPermitId,
                    closing.RelaunchPermitNonceSha256))
                return true;

            // The fence owns the exact old main PID, not every later process
            // in the same durable recovery session.  Once a different PID is
            // attached, the old fence remains auditable but cannot revoke a
            // later permit generation.
            WatchdogApplicationExitReceipt exit;
            return WatchdogApplicationExitReceiptStore.TryRead(
                       _args.JournalDirectory,
                       _args.SessionId,
                       out exit) &&
                   exit?.PreservesApprovedPermit == true &&
                   string.Equals(
                       exit.TakeoverTransactionId,
                       closing.TakeoverTransactionId,
                       StringComparison.Ordinal) &&
                   _journal.CurrentPid > 0 &&
                   _journal.CurrentPid != exit.MainProcessId;
        }

        internal static WatchdogCloseFenceAction EvaluateCloseFenceAction(
            bool legacyRevoked,
            WatchdogClosingTombstone closing,
            bool activeConnection,
            bool currentProcessAlive)
        {
            if (legacyRevoked)
                return WatchdogCloseFenceAction.TerminateSession;
            if (closing == null)
                return WatchdogCloseFenceAction.None;
            if (closing.PreservesApprovedPermit)
                return WatchdogCloseFenceAction.None;
            if (closing.IsSafetyTerminal)
                return WatchdogCloseFenceAction.TerminateSession;
            if (closing.State != WatchdogClosingTombstoneState.Closing &&
                closing.State != WatchdogClosingTombstoneState.Terminal)
                return WatchdogCloseFenceAction.TerminateSession;
            // A v1/v2 Closing record is durable stop intent, not proof of
            // completion.  Pipe loss or main exit must not turn intent into a
            // false terminal; the sidecar waits for Terminal or safety handoff.
            return WatchdogCloseFenceAction.SuppressRelaunch;
        }

        private bool ObserveDurableSafetyState()
        {
            WatchdogClosingTombstone closing;
            WatchdogClosingTombstoneStore.TryRead(
                _args.JournalDirectory,
                _args.SessionId,
                out closing);
            if (closing != null)
            {
                ObserveClosingFence(closing);
                if (closing.IsSafetyTerminal)
                {
                    if (MatchesPreservedTakeoverPermit(closing))
                    {
                        Record(
                            "DurableTakeoverSafetyTerminalObserved",
                            $"StateVersion={closing.StateVersion};" +
                            $"PermitGeneration={closing.RelaunchPermitGeneration}");
                        BeginRelaunchAfterExit(closing.RelaunchPermitGeneration);
                        return true;
                    }
                    Record("DurableClosingTerminalObserved",
                        $"StateVersion={closing.StateVersion};SafetyStage={closing.EffectiveSafetyStage}");
                    PublishTerminal("DurableClosingTerminalObserved", closing.TerminalReason);
                    if (!ObserveApplicationExitIntent())
                        _stop.Cancel();
                    return true;
                }
            }

            WatchdogManualPauseReceipt pause;
            if (WatchdogManualPauseReceiptStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out pause) &&
                (pause.Stage == WatchdogManualPauseStage.Completed ||
                 pause.Stage == WatchdogManualPauseStage.Resumed))
            {
                _transitionWindow.SetPreserveMainProcessPreferred(
                    RecoveryTransitionPolicy.ShouldPreferPreserveProcess(
                        pause.Stage,
                        IsCurrentProcessAlive()));
                CancelAutomaticTakeover("DurableManualPause" + pause.Stage);
                Record("DurableManualPauseProgressObserved",
                    $"Revision={pause.Revision};Stage={pause.Stage}");
                return !_attached;
            }
            return false;
        }

        private bool ObserveSafetyHandoffProgress()
        {
            WatchdogSafetyHandoffReceipt handoff;
            if (!WatchdogSafetyHandoffReceiptStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out handoff) || handoff == null)
                return false;

            WatchdogClosingTombstone closing;
            WatchdogClosingTombstoneStore.TryRead(
                _args.JournalDirectory,
                _args.SessionId,
                out closing);
            if (closing != null &&
                (closing.SessionGeneration != handoff.SessionGeneration ||
                 closing.SessionLease != handoff.SessionLease))
                return false;

            var started = BeginSafetyHandoff(handoff);
            return started || !handoff.IsTerminal;
        }

        private bool ObserveApplicationExitIntent()
        {
            WatchdogApplicationExitReceipt receipt;
            if (!WatchdogApplicationExitReceiptStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out receipt) ||
                receipt == null || !receipt.IsValidFor(_args.SessionId))
                return false;

            int processId;
            long processStartUtcTicks;
            lock (_journalGate)
            {
                processId = _journal.CurrentPid;
                processStartUtcTicks = _journal.CurrentProcessStartUtcTicks;
            }
            if (processId != receipt.MainProcessId ||
                processStartUtcTicks != receipt.MainProcessStartUtcTicks)
            {
                RecordRepeatedObservation(
                    "ApplicationExitIntentIdentityMismatch",
                    string.Join("|", new[]
                    {
                        receipt.ExitIntentId ?? string.Empty,
                        receipt.MainProcessId.ToString(CultureInfo.InvariantCulture),
                        receipt.MainProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture),
                        processId.ToString(CultureInfo.InvariantCulture),
                        processStartUtcTicks.ToString(CultureInfo.InvariantCulture)
                    }),
                    $"IntentPid={receipt.MainProcessId};CurrentPid={processId};" +
                    $"IntentStart={receipt.MainProcessStartUtcTicks};CurrentStart={processStartUtcTicks}");
                return false;
            }

            var preserveApprovedPermit = receipt.PreservesApprovedPermit;
            if (preserveApprovedPermit)
            {
                var authority = _relaunchCoordinator.Snapshot;
                if (!WatchdogTakeoverPermitBindingPolicy.Matches(
                        authority,
                        _args.SessionId,
                        receipt.RelaunchPermitGeneration,
                        receipt.RelaunchPermitId,
                        receipt.RelaunchPermitNonceSha256))
                {
                    RecordRepeatedObservation(
                        "TakeoverExitPermitIdentityMismatch",
                        string.Join("|", new[]
                        {
                            receipt.ExitIntentId ?? string.Empty,
                            receipt.RelaunchPermitGeneration.ToString(CultureInfo.InvariantCulture),
                            receipt.RelaunchPermitId ?? string.Empty,
                            authority?.Fingerprint ?? string.Empty
                        }),
                        $"Intent={receipt.ExitIntentId};" +
                        $"Generation={receipt.RelaunchPermitGeneration};" +
                        $"PermitId={receipt.RelaunchPermitId}");
                    return false;
                }
            }

            WatchdogClosingTombstone closing;
            if (WatchdogClosingTombstoneStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out closing) &&
                (closing.SessionGeneration != receipt.SessionGeneration ||
                 closing.SessionLease != receipt.SessionLease))
            {
                RecordRepeatedObservation(
                    "ApplicationExitIntentLeaseMismatch",
                    string.Join("|", new[]
                    {
                        receipt.ExitIntentId ?? string.Empty,
                        receipt.SessionGeneration.ToString(CultureInfo.InvariantCulture),
                        receipt.SessionLease.ToString(CultureInfo.InvariantCulture),
                        closing.SessionGeneration.ToString(CultureInfo.InvariantCulture),
                        closing.SessionLease.ToString(CultureInfo.InvariantCulture)
                    }),
                    receipt.ExitIntentId);
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _applicationExitDeadlineStarted,
                    1,
                    0) == 0)
            {
                if (preserveApprovedPermit)
                    Record(
                        "TakeoverReplacementExitObserved",
                        $"Intent={receipt.ExitIntentId};" +
                        $"Transaction={receipt.TakeoverTransactionId};" +
                        $"PermitGeneration={receipt.RelaunchPermitGeneration}");
                else
                {
                    CancelAutomaticTakeover("ApplicationExitRequested");
                    _journal.ManualStopRequested = true;
                }
                _ = Task.Run(() => RunApplicationExitDeadlineAsync(receipt.ExitIntentId));
            }
            return true;
        }

        private async Task RunApplicationExitDeadlineAsync(string exitIntentId)
        {
            try
            {
                WatchdogApplicationExitReceipt receipt;
                if (!TryReadExactApplicationExit(exitIntentId, out receipt)) return;

                await DelayUntilUtcTicks(receipt.DiagnosticDeadlineUtcTicks).ConfigureAwait(false);
                if (IsExactProcessAlive(
                        receipt.MainProcessId,
                        receipt.MainProcessStartUtcTicks))
                {
                    Record(
                        "ApplicationExitDiagnosticsDeadline",
                        $"Intent={receipt.ExitIntentId};PID={receipt.MainProcessId}");
                    try
                    {
                        using (var process = Process.GetProcessById(receipt.MainProcessId))
                        {
                            if (MatchesExactProcess(process, receipt.MainProcessStartUtcTicks))
                            {
                                var capture = CaptureMiniDumpBeforeTerminationAsync(
                                    process,
                                    "ApplicationExitDiagnosticsDeadline");
                                await Task.WhenAny(capture, Task.Delay(2000)).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Record("ApplicationExitDiagnosticFailed", ex.GetBaseException().Message);
                    }
                    UpdateApplicationExitReceipt(
                        exitIntentId,
                        WatchdogApplicationExitState.DiagnosticsCaptured,
                        false,
                        "BoundedDiagnosticsCaptured");
                }

                if (!TryReadExactApplicationExit(exitIntentId, out receipt)) return;
                await DelayUntilUtcTicks(receipt.HardDeadlineUtcTicks).ConfigureAwait(false);
                if (!IsExactProcessAlive(
                        receipt.MainProcessId,
                        receipt.MainProcessStartUtcTicks))
                {
                    UpdateApplicationExitReceipt(
                        exitIntentId,
                        WatchdogApplicationExitState.GracefulCompleted,
                        false,
                        "MainProcessExitedBeforeDeadline");
                    CompleteApplicationExitSupervision(receipt);
                    return;
                }

                using (var process = Process.GetProcessById(receipt.MainProcessId))
                {
                    if (!MatchesExactProcess(process, receipt.MainProcessStartUtcTicks))
                    {
                        Record("ApplicationExitDeadlineIdentityMismatch", receipt.ExitIntentId);
                        return;
                    }
                    UpdateApplicationExitReceipt(
                        exitIntentId,
                        WatchdogApplicationExitState.ForcedDeadlineExit,
                        true,
                        "ExactMainProcessTerminatedAt30SecondDeadline");
                    process.Kill();
                    Record(
                        "ApplicationExitDeadlineTerminationRequested",
                        $"Intent={receipt.ExitIntentId};PID={receipt.MainProcessId}");
                }
                CompleteApplicationExitSupervision(receipt);
            }
            catch (Exception ex)
            {
                Record("ApplicationExitDeadlineSupervisorFailed", ex.GetBaseException().Message);
                Interlocked.Exchange(ref _applicationExitDeadlineStarted, 0);
            }
        }

        internal static bool ShouldTerminateSidecarAfterApplicationExit(
            WatchdogApplicationExitReceipt receipt) =>
            receipt?.PreservesApprovedPermit != true;

        private void CompleteApplicationExitSupervision(
            WatchdogApplicationExitReceipt receipt)
        {
            if (ShouldTerminateSidecarAfterApplicationExit(receipt))
            {
                _stop.Cancel();
                return;
            }
            Record(
                "TakeoverReplacementOldProcessExited",
                $"Intent={receipt.ExitIntentId};" +
                $"PermitGeneration={receipt.RelaunchPermitGeneration}");
            BeginRelaunchAfterExit(receipt.RelaunchPermitGeneration);
        }

        private static async Task DelayUntilUtcTicks(long deadlineUtcTicks)
        {
            while (DateTime.UtcNow.Ticks < deadlineUtcTicks)
            {
                var remaining = TimeSpan.FromTicks(
                    Math.Max(0, deadlineUtcTicks - DateTime.UtcNow.Ticks));
                await Task.Delay(remaining > TimeSpan.FromSeconds(1)
                        ? TimeSpan.FromSeconds(1)
                        : remaining)
                    .ConfigureAwait(false);
            }
        }

        private bool TryReadExactApplicationExit(
            string exitIntentId,
            out WatchdogApplicationExitReceipt receipt)
        {
            if (!WatchdogApplicationExitReceiptStore.TryRead(
                    _args.JournalDirectory,
                    _args.SessionId,
                    out receipt))
                return false;
            return string.Equals(
                receipt.ExitIntentId,
                exitIntentId,
                StringComparison.Ordinal);
        }

        private void UpdateApplicationExitReceipt(
            string exitIntentId,
            WatchdogApplicationExitState state,
            bool terminationRequested,
            string detail)
        {
            try
            {
                WatchdogApplicationExitReceipt receipt;
                if (!TryReadExactApplicationExit(exitIntentId, out receipt) ||
                    state < receipt.State)
                    return;
                WatchdogClosingTombstone closing;
                if (WatchdogClosingTombstoneStore.TryRead(
                        _args.JournalDirectory,
                        _args.SessionId,
                        out closing) &&
                    closing.SessionGeneration == receipt.SessionGeneration &&
                    closing.SessionLease == receipt.SessionLease)
                {
                    receipt.MotorsOff = closing.MotorsOff;
                    receipt.PowerOff = closing.PowerOff;
                    receipt.PressureSafe = closing.PressureSafe;
                    receipt.PersistenceDrained = closing.PersistenceDrained;
                    receipt.LogicalQuiescent = closing.LogicalQuiescent;
                    receipt.DataContinuityVerified = closing.DataContinuityVerified;
                    receipt.StopSafetyTransactionId = closing.StopSafetyTransactionId;
                }
                receipt.State = state;
                receipt.ProcessTerminationRequested |= terminationRequested;
                receipt.Revision++;
                receipt.Detail = detail ?? string.Empty;
                WatchdogApplicationExitReceiptStore.WriteThrough(
                    _args.JournalDirectory,
                    receipt);
            }
            catch (Exception ex)
            {
                Record("ApplicationExitReceiptUpdateFailed", ex.GetBaseException().Message);
            }
        }

        private bool DurableStopOrPauseCompletionSupersedesTakeover()
        {
            WatchdogClosingTombstone closing;
            if (WatchdogClosingTombstoneStore.TryRead(
                    _args.JournalDirectory, _args.SessionId, out closing))
            {
                ObserveClosingFence(closing);
                return true;
            }
            WatchdogManualPauseReceipt pause;
            return WatchdogManualPauseReceiptStore.TryRead(
                       _args.JournalDirectory, _args.SessionId, out pause) &&
                   (pause.Stage == WatchdogManualPauseStage.Completed ||
                    pause.Stage == WatchdogManualPauseStage.Resumed);
        }

        private bool BeginSafetyHandoff(WatchdogSafetyHandoffReceipt receipt)
        {
            if (receipt == null || receipt.IsTerminal ||
                !receipt.IsValidFor(_args.SessionId))
                return false;
            if (Interlocked.CompareExchange(ref _safetyHandoffStarted, 1, 0) != 0)
                return false;

            var authorityToken = GetSafetyAuthorityToken(
                receipt.HandoffId,
                receipt.RelaunchPermitGeneration,
                receipt.RelaunchPermitId);
            if (authorityToken != null)
            {
                WatchdogSafetyHandoffReceipt authoritativeReceipt;
                string authorityReadFailure;
                if (!TryReadExactSafetyHandoff(
                        receipt.HandoffId,
                        receipt.Nonce,
                        authorityToken,
                        out authoritativeReceipt,
                        out authorityReadFailure))
                {
                    RecordRepeatedObservation(
                        "SupervisorSafetyAuthorityReadFailed",
                        receipt.HandoffId,
                        authorityReadFailure);
                    _unattendedAlarmSink.Publish(
                        "P0",
                        "SupervisorSafetyAuthorityReadFailed",
                        authorityReadFailure,
                        RecoveryFailureDomain.EvidenceBinding,
                        receipt);
                    Interlocked.Exchange(ref _safetyHandoffStarted, 0);
                    return true;
                }

                receipt = authoritativeReceipt;
                if (receipt.IsTerminal)
                {
                    RecordRepeatedObservation(
                        "SupervisorSafetyAuthorityTerminalObserved",
                        receipt.HandoffId + "|" +
                        receipt.Revision.ToString(CultureInfo.InvariantCulture),
                        $"State={receipt.State};Revision={receipt.Revision};" +
                        "Project mirror ignored in favor of Supervisor authority.");
                    Interlocked.Exchange(ref _safetyHandoffStarted, 0);
                    return true;
                }
            }

            var snapshot = WatchdogSafetyConfigSnapshotStore.Validate(
                _args.JournalDirectory,
                receipt.HandoffId,
                receipt.ConfigSnapshotPath,
                receipt.ConfigSnapshotManifestPath,
                receipt.ConfigSnapshotManifestSha256);
            if (snapshot?.Succeeded != true)
            {
                FailSafetyHandoff(
                    receipt,
                    "SafetyConfigSnapshotInvalid",
                    snapshot?.Error ?? "SnapshotValidationUnavailable");
                Interlocked.Exchange(ref _safetyHandoffStarted, 0);
                return true;
            }
            if (receipt.RelaunchDisposition ==
                    WatchdogRelaunchDisposition.PreserveApprovedPermit)
            {
                if (!WatchdogTakeoverPermitBindingPolicy.Matches(
                        _relaunchCoordinator.Snapshot,
                        _args.SessionId,
                        receipt.RelaunchPermitGeneration,
                        receipt.RelaunchPermitId,
                        receipt.RelaunchPermitNonceSha256))
                {
                    FailSafetyHandoff(
                        receipt,
                        "SafetyHandoffPermitMismatch",
                        "Typed handoff does not match durable authority.");
                    Interlocked.Exchange(ref _safetyHandoffStarted, 0);
                    return true;
                }
            }
            else
            {
                CancelAutomaticTakeover("SafetyHandoffObserved");
                _journal.ManualStopRequested = true;
            }
            _ = Task.Run(() => RunSafetyHandoffAsync(receipt.HandoffId, receipt.Nonce));
            return true;
        }

        private void RecordRepeatedObservation(string eventType, string identity, string reason)
        {
            var recoveryFingerprint = string.Empty;
            lock (_journalGate)
                recoveryFingerprint = _journal.RecoveryFailureFingerprint ?? string.Empty;
            var authority = _relaunchCoordinator.Snapshot;
            var observationFingerprint = DurableJsonFileStore.ComputeSha256(
                new UTF8Encoding(false).GetBytes(
                    (identity ?? string.Empty) + "|" + recoveryFingerprint));
            var key = string.Join("|", new[]
            {
                eventType ?? string.Empty,
                _args.SessionId ?? string.Empty,
                (authority?.Generation ?? 0).ToString(CultureInfo.InvariantCulture),
                authority?.PermitId ?? string.Empty,
                observationFingerprint
            });
            var decision = _repeatedEventSummarizer.Observe(key, Stopwatch.GetTimestamp());
            if (decision.EmitFirst)
            {
                RecordEvent(eventType, reason);
                return;
            }
            if (decision.EmitSummary)
                RecordEvent(
                    (eventType ?? "RepeatedObservation") + "Summary",
                    (reason ?? string.Empty) + ";SuppressedCount=" +
                    decision.SuppressedCount.ToString(CultureInfo.InvariantCulture) +
                    ";WindowMs=30000");
        }

        private void FailSafetyHandoff(
            WatchdogSafetyHandoffReceipt receipt,
            string code,
            string detail)
        {
            try
            {
                if (receipt == null || receipt.IsTerminal) return;
                receipt.State = WatchdogSafetyHandoffState.Failed;
                receipt.FailureCode = code ?? "SafetyHandoffFailed";
                receipt.FailureDomain = RecoveryFailureDomain.EvidenceBinding;
                receipt.Detail = detail ?? string.Empty;
                receipt.Revision++;
                WatchdogSafetyHandoffReceiptStore.WriteThrough(
                    _args.JournalDirectory,
                    receipt);
            }
            catch { }
            Record(
                "SafetyHandoffTerminalFailure",
                $"Code={code};Detail={detail};HandoffId={receipt?.HandoffId}");
            _unattendedAlarmSink.Publish(
                "P0",
                code ?? "SafetyHandoffFailed",
                detail ?? string.Empty,
                RecoveryFailureDomain.EvidenceBinding,
                receipt);
        }

        private async Task RunSafetyHandoffAsync(string handoffId, string nonce)
        {
            try
            {
                SupervisorSafetyAuthorityToken authorityToken = null;
                WatchdogSafetyHandoffReceipt receipt;
                if (!TryReadExactSafetyHandoff(handoffId, nonce, out receipt)) return;
                authorityToken = GetSafetyAuthorityToken(
                    handoffId,
                    receipt.RelaunchPermitGeneration,
                    receipt.RelaunchPermitId);
                if (receipt.State == WatchdogSafetyHandoffState.Failed)
                    return;
                if (receipt.State == WatchdogSafetyHandoffState.Requested)
                {
                    receipt.State = WatchdogSafetyHandoffState.Accepted;
                    receipt.SidecarProcessId = _sidecarProcessId;
                    receipt.SidecarProcessStartUtcTicks = _sidecarProcessStartUtcTicks;
                    receipt.Revision++;
                    receipt.Detail = "SidecarAcceptedExactSafetyHandoff";
                    WatchdogSafetyHandoffReceiptStore.WriteThrough(_args.JournalDirectory, receipt);
                    SendSafetyHandoff(WatchdogMessageType.SafetyHandoffAccepted, receipt);
                    Record("SafetyHandoffAccepted", $"HandoffId={handoffId};Revision={receipt.Revision}");
                }

                while (!_stop.IsCancellationRequested)
                {
                    if (authorityToken == null)
                    {
                        authorityToken = GetSafetyAuthorityToken(
                            handoffId,
                            receipt.RelaunchPermitGeneration,
                            receipt.RelaunchPermitId);
                    }
                    string authorityReadFailure;
                    if (!TryReadExactSafetyHandoff(
                            handoffId,
                            nonce,
                            authorityToken,
                            out receipt,
                            out authorityReadFailure))
                    {
                        if (authorityToken == null) return;
                        Record(
                            "SupervisorSafetyAuthorityReadFailed",
                            authorityReadFailure);
                        await Task.Delay(1000).ConfigureAwait(false);
                        continue;
                    }
                    if (receipt.State == WatchdogSafetyHandoffState.Completed &&
                        receipt.IsSafetyCompleted)
                    {
                        if (receipt.RelaunchDisposition ==
                                WatchdogRelaunchDisposition.PreserveApprovedPermit &&
                            !AdvanceReplacementState(
                                receipt.RelaunchPermitGeneration,
                                receipt.RelaunchPermitId,
                                RecoveryReplacementState.SafetyCompleted,
                                "SafetyAgent completed all physical safety stages"))
                        {
                            FailSafetyHandoff(
                                receipt,
                                "ReplacementTransactionPersistFailed",
                                "SafetyCompleted could not be durably committed.");
                            return;
                        }
                        SendSafetyHandoff(WatchdogMessageType.SafetyHandoffCompleted, receipt);
                        if (receipt.RelaunchDisposition ==
                            WatchdogRelaunchDisposition.PreserveApprovedPermit)
                        {
                            Record(
                                "TakeoverSafetyHandoffCompleted",
                                $"HandoffId={handoffId};" +
                                $"PermitGeneration={receipt.RelaunchPermitGeneration}");
                            BeginRelaunchAfterExit(receipt.RelaunchPermitGeneration);
                        }
                        else
                        {
                            PublishTerminal("SafetyHandoffCompleted", receipt.Detail);
                            _stop.Cancel();
                        }
                        return;
                    }
                    if (receipt.State == WatchdogSafetyHandoffState.Failed)
                    {
                        Record(
                            "SafetyHandoffFailed",
                            $"Code={receipt.FailureCode};Detail={receipt.Detail}");
                        _unattendedAlarmSink.Publish(
                            "P0",
                            receipt.FailureCode,
                            receipt.Detail,
                            receipt.FailureDomain,
                            receipt);
                        return;
                    }

                    while (IsCurrentProcessAlive() && !_stop.IsCancellationRequested)
                        await Task.Delay(250).ConfigureAwait(false);
                    if (_stop.IsCancellationRequested) return;

                    if (receipt.State == WatchdogSafetyHandoffState.WorkerStarted &&
                        ProbeProcessIdentity(
                            receipt.WorkerProcessId,
                            receipt.WorkerProcessStartUtcTicks) == DurableRelaunchProcessObservation.Alive)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        continue;
                    }

                    Process worker = null;
                    try
                    {
                        var executable = receipt.SafetyAgentExecutablePath;
                        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                            throw new FileNotFoundException("SafetyAgentExecutableMissing", executable);
                        if (!ExecutableSha256Matches(
                                executable,
                                receipt.SafetyAgentExecutableSha256))
                            throw new InvalidDataException("SafetyAgentExecutableHashMismatch");
                        // Persist the attempt before Process.Start.  The independent agent may
                        // advance the receipt immediately after it starts; writing an older
                        // WorkerStarted receipt afterwards would otherwise race and regress its
                        // stage evidence.
                        receipt.AttemptCount++;
                        receipt.Revision++;
                        receipt.Detail = "SafetyAgentLaunchIntent";
                        receipt.FailureCode = string.Empty;
                        receipt.FailureDomain = RecoveryFailureDomain.None;
                        WatchdogSafetyHandoffReceiptStore.WriteThrough(
                            _args.JournalDirectory, receipt);
                        var arguments = string.Format(
                            CultureInfo.InvariantCulture,
                            "--session-id {0} --handoff-id {1} --handoff-nonce {2} " +
                            "--journal-directory {3}",
                            Quote(_args.SessionId),
                            Quote(handoffId),
                            Quote(nonce),
                            Quote(_args.JournalDirectory));
                        var formalMarker = Path.Combine(
                            Path.GetDirectoryName(executable) ?? string.Empty,
                            "MTTFTest.UnattendedMode.required");
                        if (File.Exists(formalMarker))
                        {
                            SupervisorSafetyAuthorityToken launchedAuthorityToken;
                            worker = SupervisorSafetyAgentLaunchClient.Start(
                                receipt,
                                executable,
                                arguments,
                                authorityToken,
                                out launchedAuthorityToken);
                            RememberSafetyAuthorityToken(launchedAuthorityToken);
                            authorityToken = launchedAuthorityToken;
                        }
                        else
                        {
                            worker = Process.Start(new ProcessStartInfo
                            {
                                FileName = executable,
                                Arguments = arguments,
                                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            });
                        }
                        if (worker == null) throw new InvalidOperationException("SafetyWorkerStartReturnedNull");
                        if (receipt.RelaunchDisposition ==
                            WatchdogRelaunchDisposition.PreserveApprovedPermit)
                        {
                            if (!AdvanceReplacementState(
                                    receipt.RelaunchPermitGeneration,
                                    receipt.RelaunchPermitId,
                                    RecoveryReplacementState.Approved,
                                    "SafetyAgent observer joined approved transaction") ||
                                !AdvanceReplacementState(
                                    receipt.RelaunchPermitGeneration,
                                    receipt.RelaunchPermitId,
                                    RecoveryReplacementState.OldProcessExitProven,
                                    "SafetyAgent launch observed exact old process exit") ||
                                !AdvanceReplacementState(
                                    receipt.RelaunchPermitGeneration,
                                    receipt.RelaunchPermitId,
                                    RecoveryReplacementState.SafetyAgentRunning,
                                    "Independent SafetyAgent process started"))
                            {
                                try { if (!worker.HasExited) worker.Kill(); } catch { }
                                throw new InvalidDataException(
                                    "ReplacementTransactionPersistFailed:SafetyAgentRunning");
                            }
                        }
                        Record("SafetyHandoffWorkerStarted",
                            $"PID={worker.Id};Attempt={receipt.AttemptCount};HandoffId={handoffId}");
                        await Task.Run(() => worker.WaitForExit()).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Record("SafetyHandoffWorkerFailed", ex.GetBaseException().Message);
                    }
                    finally
                    {
                        try { worker?.Dispose(); } catch { }
                    }

                    string postWorkerReadFailure;
                    if (TryReadExactSafetyHandoff(
                            handoffId,
                            nonce,
                            authorityToken,
                            out receipt,
                            out postWorkerReadFailure) &&
                        receipt.State == WatchdogSafetyHandoffState.Completed &&
                        receipt.IsSafetyCompleted)
                        continue;
                    if (authorityToken != null &&
                        !string.IsNullOrWhiteSpace(postWorkerReadFailure))
                        Record(
                            "SupervisorSafetyAuthorityReadFailed",
                            postWorkerReadFailure);
                    if (receipt != null &&
                        receipt.FailureDomain != RecoveryFailureDomain.None &&
                        receipt.AttemptCount == 1)
                    {
                        _unattendedAlarmSink.Publish(
                            "P0",
                            receipt.FailureCode,
                            receipt.Detail,
                            receipt.FailureDomain,
                            receipt);
                    }
                    var attempt = Math.Max(1, receipt?.AttemptCount ?? 1);
                    var delay = attempt == 1 ? 1000 :
                        attempt == 2 ? 5000 : attempt == 3 ? 15000 : 30000;
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Record("SafetyHandoffSupervisorFailed", ex.GetBaseException().Message);
            }
            finally
            {
                Interlocked.Exchange(ref _safetyHandoffStarted, 0);
            }
        }

        private bool TryReadExactSafetyHandoff(
            string handoffId,
            string nonce,
            out WatchdogSafetyHandoffReceipt receipt)
        {
            if (!WatchdogSafetyHandoffReceiptStore.TryRead(
                    _args.JournalDirectory, _args.SessionId, out receipt))
                return false;
            return string.Equals(receipt.HandoffId, handoffId, StringComparison.Ordinal) &&
                   string.Equals(receipt.Nonce, nonce, StringComparison.Ordinal);
        }

        private void RememberSafetyAuthorityToken(
            SupervisorSafetyAuthorityToken authorityToken)
        {
            if (authorityToken?.IsValid() != true)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityTokenInvalid");
            lock (_safetyAuthorityTokenGate)
            {
                if (_safetyAuthorityToken == null)
                {
                    _safetyAuthorityToken = CloneSafetyAuthorityToken(
                        authorityToken);
                    return;
                }
                if (!SafetyAuthorityTokensMatch(
                        _safetyAuthorityToken,
                        authorityToken))
                    throw new InvalidDataException(
                        "SupervisorSafetyAuthorityTokenChanged");
            }
        }

        private SupervisorSafetyAuthorityToken GetSafetyAuthorityToken(
            string handoffId,
            long permitGeneration,
            string permitId)
        {
            lock (_safetyAuthorityTokenGate)
            {
                var token = _safetyAuthorityToken;
                if (token?.IsValid() != true ||
                    !string.Equals(token.SessionId, _args.SessionId,
                        StringComparison.Ordinal) ||
                    !string.Equals(token.HandoffId, handoffId,
                        StringComparison.Ordinal) ||
                    token.PermitGeneration != permitGeneration ||
                    !string.Equals(token.PermitId, permitId,
                        StringComparison.Ordinal))
                    return null;
                return CloneSafetyAuthorityToken(token);
            }
        }

        private static SupervisorSafetyAuthorityToken CloneSafetyAuthorityToken(
            SupervisorSafetyAuthorityToken token)
        {
            if (token == null) return null;
            return new SupervisorSafetyAuthorityToken
            {
                SchemaVersion = token.SchemaVersion,
                AuthorityId = token.AuthorityId,
                SessionId = token.SessionId,
                HandoffId = token.HandoffId,
                PermitGeneration = token.PermitGeneration,
                PermitId = token.PermitId,
                ReceiptRevision = token.ReceiptRevision,
                ReceiptCanonicalSha256 = token.ReceiptCanonicalSha256
            };
        }

        private static bool SafetyAuthorityTokensMatch(
            SupervisorSafetyAuthorityToken left,
            SupervisorSafetyAuthorityToken right)
        {
            return left?.IsValid() == true && right?.IsValid() == true &&
                   left.SchemaVersion == right.SchemaVersion &&
                   string.Equals(left.AuthorityId, right.AuthorityId,
                       StringComparison.Ordinal) &&
                   string.Equals(left.SessionId, right.SessionId,
                       StringComparison.Ordinal) &&
                   string.Equals(left.HandoffId, right.HandoffId,
                       StringComparison.Ordinal) &&
                   left.PermitGeneration == right.PermitGeneration &&
                   string.Equals(left.PermitId, right.PermitId,
                       StringComparison.Ordinal) &&
                   left.ReceiptRevision == right.ReceiptRevision &&
                   string.Equals(left.ReceiptCanonicalSha256,
                       right.ReceiptCanonicalSha256,
                       StringComparison.Ordinal);
        }

        private bool TryReadExactSafetyHandoff(
            string handoffId,
            string nonce,
            SupervisorSafetyAuthorityToken authorityToken,
            out WatchdogSafetyHandoffReceipt receipt,
            out string failure)
        {
            failure = string.Empty;
            if (authorityToken == null)
            {
                if (TryReadExactSafetyHandoff(handoffId, nonce, out receipt))
                    return true;
                failure = "LocalSafetyHandoffReceiptMissingOrIdentityMismatch";
                return false;
            }

            if (!SupervisorSafetyAgentLaunchClient.TryReadAuthority(
                    authorityToken,
                    out receipt,
                    out failure))
                return false;
            if (!string.Equals(receipt.HandoffId, handoffId,
                    StringComparison.Ordinal))
            {
                failure = "SupervisorSafetyAuthorityReadHandoffIdMismatch";
                receipt = null;
                return false;
            }
            if (!string.Equals(receipt.Nonce, nonce, StringComparison.Ordinal))
            {
                failure = "SupervisorSafetyAuthorityReadNonceMismatch";
                receipt = null;
                return false;
            }
            return true;
        }

        private void SendSafetyHandoff(string type, WatchdogSafetyHandoffReceipt receipt)
        {
            if (receipt == null) return;
            Send(new WatchdogMessage
            {
                Type = type,
                SessionId = _args.SessionId,
                CorrelationId = receipt.HandoffId,
                SafetyHandoff = new WatchdogSafetyHandoff
                {
                    SessionId = receipt.SessionId,
                    SessionGeneration = receipt.SessionGeneration,
                    SessionLease = receipt.SessionLease,
                    HandoffId = receipt.HandoffId,
                    Nonce = receipt.Nonce,
                    StopSafetyTransactionId = receipt.StopSafetyTransactionId,
                    RunId = receipt.RunId,
                    RunEpoch = receipt.RunEpoch,
                    SidecarProcessId = receipt.SidecarProcessId,
                    SidecarProcessStartUtcTicks = receipt.SidecarProcessStartUtcTicks,
                    WorkerProcessId = receipt.WorkerProcessId,
                    WorkerProcessStartUtcTicks = receipt.WorkerProcessStartUtcTicks,
                    Stage = receipt.Stage,
                    MotorsOff = receipt.MotorsOff,
                    PowerOff = receipt.PowerOff,
                    PressureSafe = receipt.PressureSafe,
                    PersistenceDrained = receipt.PersistenceDrained,
                    LogicalQuiescent = receipt.LogicalQuiescent,
                    HardwareResourcesReleased = receipt.HardwareResourcesReleased,
                    ExecutionAuthorizationRevoked = receipt.ExecutionAuthorizationRevoked,
                    CallbacksIsolated = receipt.CallbacksIsolated,
                    ConfigSnapshotManifestSha256 = receipt.ConfigSnapshotManifestSha256,
                    RelaunchDisposition = receipt.RelaunchDisposition,
                    RelaunchPermitGeneration = receipt.RelaunchPermitGeneration,
                    RelaunchPermitId = receipt.RelaunchPermitId,
                    RelaunchPermitNonceSha256 = receipt.RelaunchPermitNonceSha256,
                    FailureCode = receipt.FailureCode,
                    FailureDomain = receipt.FailureDomain,
                    TimestampUtcTicks = receipt.UpdatedUtcTicks
                }
            });
        }

        private WatchdogCloseFenceAction CaptureCloseFenceAction(
            bool activeConnection,
            bool currentProcessAlive,
            out WatchdogClosingTombstone closing,
            out bool legacyRevoked)
        {
            legacyRevoked = WatchdogControlMarker.IsRevoked(
                _args.JournalDirectory,
                _args.SessionId);
            WatchdogClosingTombstoneStore.TryRead(
                _args.JournalDirectory,
                _args.SessionId,
                out closing);
            if (closing?.PreservesApprovedPermit == true)
                return MatchesPreservedTakeoverPermit(closing)
                    ? WatchdogCloseFenceAction.None
                    : WatchdogCloseFenceAction.SuppressRelaunch;
            return EvaluateCloseFenceAction(
                legacyRevoked,
                closing,
                activeConnection,
                currentProcessAlive);
        }

        private void ObserveClosingFence(WatchdogClosingTombstone closing)
        {
            if (closing == null) return;
            if (Interlocked.Read(ref _observedClosingFenceVersion) ==
                closing.StateVersion)
                return;
            Interlocked.Exchange(
                ref _observedClosingFenceVersion,
                closing.StateVersion);
            if (MatchesPreservedTakeoverPermit(closing))
            {
                Record(
                    "TakeoverReplacementFenceObserved",
                    $"StateVersion={closing.StateVersion};" +
                    $"Transaction={closing.TakeoverTransactionId};" +
                    $"PermitGeneration={closing.RelaunchPermitGeneration};" +
                    "Action=PreserveApprovedPermit");
                SaveJournal();
                return;
            }
            _journal.ManualStopRequested = true;
            CancelAutomaticTakeover("ClosingFenceObserved");
            var revoked = _relaunchCoordinator.Revoke("ClosingFenceObserved");
            if (revoked?.Record != null)
                lock (_journalGate) ApplyDurablePermitLocked(revoked.Record);
            Record(
                "ClosingFenceObserved",
                $"StateVersion={closing.StateVersion};" +
                $"Generation={closing.SessionGeneration};Lease={closing.SessionLease};" +
                "Action=SuppressRelaunchAndAwaitStopCompleted");
            SaveJournal();
        }

        private bool MatchesCurrentProcess(Process process)
        {
            try
            {
                return WatchdogProcessIdentityPolicy.Matches(
                    _journal.CurrentPid,
                    _journal.CurrentProcessStartUtcTicks,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks);
            }
            catch { return false; }
        }

        private void Send(string type, string reason, string correlationId)
        {
            Send(new WatchdogMessage
            {
                Type = type,
                SessionId = _args.SessionId,
                Reason = reason,
                CorrelationId = correlationId
            });
        }

        private void Send(WatchdogMessage message)
        {
            WatchdogHostSendQueueOwner owner;
            lock (_gate)
            {
                owner = _sendOwner;
            }
            if (owner == null) return;
            try
            {
                var payload = WatchdogProtocol.Serialize(message);
                var request = new WatchdogHostSendRequest(
                    message?.Type,
                    WatchdogWireFrame.Encode(payload),
                    IsLifecycleSend(message?.Type));
                if (!owner.TryEnqueue(request))
                {
                    RecordEvent(
                        "HostTransportAdmissionBusy",
                        $"Type={message?.Type ?? "Unknown"};ConnectionGeneration={owner.ConnectionGeneration}");
                    if (request.Lifecycle)
                        RetireConnection(owner, "LifecycleAdmissionBusy:" + request.MessageType);
                    return;
                }
                var waitMs = SendWriteTimeoutMs + WatchdogTransportPolicy.SendGateWaitMs;
                if (!request.Completion.Task.Wait(waitMs))
                {
                    RetireConnection(owner, "SendCompletionTimeout:" + request.MessageType);
                    RecordEvent(
                        "HostTransportWriteTimeout",
                        $"Type={request.MessageType};ConnectionGeneration={owner.ConnectionGeneration}");
                    return;
                }
                if (!request.Completion.Task.Result)
                    RecordEvent(
                        "HostTransportSendRejected",
                        $"Type={request.MessageType};ConnectionGeneration={owner.ConnectionGeneration}");
            }
            catch (Exception ex)
            {
                Record("SendFailed", (message?.Type ?? "Unknown") + ":" + ex.GetBaseException().Message);
                RetireConnection(owner, "SendFailed:" + (message?.Type ?? "Unknown"));
            }
        }

        private async Task RunSendQueueAsync(WatchdogHostSendQueueOwner owner)
        {
            try
            {
                while (!owner.Lifetime.IsCancellationRequested)
                {
                    WatchdogHostSendRequest request;
                    if (!owner.TryDequeue(out request))
                    {
                        await owner.Signal.WaitAsync(owner.Lifetime.Token).ConfigureAwait(false);
                        continue;
                    }
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_sendOwner, owner))
                        {
                            request.Completion.TrySetResult(false);
                            continue;
                        }
                    }

                    var queueWaitMs = (Stopwatch.GetTimestamp() - request.EnqueuedTimestamp) *
                                      1000.0 / Stopwatch.Frequency;
                    var writeTask = owner.Writer.WriteLineAsync(request.Payload);
                    owner.InflightWriteTask = writeTask;
                    var completed = await Task.WhenAny(
                            writeTask,
                            Task.Delay(SendWriteTimeoutMs, owner.Lifetime.Token))
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(completed, writeTask))
                    {
                        request.Completion.TrySetResult(false);
                        RecordEvent(
                            "HostTransportWriteTimeout",
                            $"Type={request.MessageType};ConnectionGeneration={owner.ConnectionGeneration};" +
                            $"QueueWaitMs={queueWaitMs:F3}");
                        RetireConnection(owner, "WriterTimeout:" + request.MessageType);
                        return;
                    }
                    await writeTask.ConfigureAwait(false);
                    owner.InflightWriteTask = null;
                    request.Completion.TrySetResult(true);
                }
            }
            catch (OperationCanceledException)
            {
                // Exact connection retirement.
            }
            catch (Exception ex)
            {
                RecordEvent(
                    "HostTransportWriteFailed",
                    $"ConnectionGeneration={owner.ConnectionGeneration};{ex.GetBaseException().Message}");
                RetireConnection(owner, "WriterFailed");
            }
            finally
            {
                owner.StopAccepting();
            }
        }

        private static bool IsLifecycleSend(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.Attached, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RecoveryAttemptFailedReceipt, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RequestStopAll, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ExternalRecoveryRequired, StringComparison.Ordinal);
        }

        private void RetireConnection(
            WatchdogHostSendQueueOwner expectedOwner,
            string reason)
        {
            if (expectedOwner == null) return;
            lock (_gate)
            {
                if (!ReferenceEquals(_sendOwner, expectedOwner)) return;
                _sendOwner = null;
            }
            expectedOwner.StopAccepting();
            RecordEvent(
                "HostTransportConnectionRetireRequested",
                $"ConnectionGeneration={expectedOwner.ConnectionGeneration};Reason={reason}");
            try { expectedOwner.Pipe.Dispose(); } catch { }
        }

        private void Record(string state, string reason)
        {
            lock (_journalGate)
            {
                _journal.State = state;
                _journal.LastReason = reason ?? string.Empty;
            }
            SaveJournal();
            RecordEvent(state, reason);
        }

        private void SaveJournal()
        {
            string content;
            lock (_journalGate)
            {
                content = BuildJournalSnapshotLocked();
            }
            _journalStore.PublishSnapshot(content);
        }

        private string CaptureJournalSnapshotForPermit()
        {
            lock (_journalGate)
                return BuildJournalSnapshotLocked();
        }

        private void ApplyDurablePermitLocked(DurableRelaunchPermitRecord record)
        {
            if (record == null) return;
            _journal.RelaunchState = record.State.ToString();
            _journal.RelaunchGeneration = record.Generation;
            _journal.RelaunchPermitGeneration = record.Generation;
            _journal.RelaunchPermitId = record.PermitId;
            _journal.RelaunchPermitNonce = record.PermitNonce;
            _journal.RelaunchFingerprint = record.Fingerprint;
            _journal.RelaunchProgressToken = record.ProgressToken;
            _journal.RelaunchProcessSource = record.ProcessSource;
            _journal.RelaunchRunId = record.RunId;
            _journal.RelaunchRecoveryStage = record.RecoveryStage;
            _journal.RelaunchProcessId = record.ProcessId;
            _journal.RelaunchProcessStartUtcTicks = record.ProcessStartUtcTicks;
            _journal.RelaunchConsecutiveFailures = record.ConsecutiveFailures;
            _journal.RelaunchMaximumProcessRelaunches = record.MaximumProcessRelaunches;
            _journal.RelaunchRecoveryCommitGeneration = record.RecoveryCommitGeneration;
            _journal.RelaunchFailureCode = record.LastFailureCode;
            _journal.RelaunchFailureReason = record.LastFailureReason;
            _journal.RecoveryBlocked = record.State == DurableRelaunchPermitState.Blocked;
            _journal.ConsecutiveStartupFailures = record.ConsecutiveFailures;
            _journal.RecoveryFailureMaxProcessRelaunches = record.MaximumProcessRelaunches;
            _journal.RecoveryFailureCode = record.LastFailureCode;
            _journal.RecoveryFailurePermanent = record.State == DurableRelaunchPermitState.Blocked;
            if (record.State == DurableRelaunchPermitState.RejectedNoWork)
            {
                // NoWork is a neutral terminal for one stale checkpoint
                // identity.  It must close the relaunch permit without
                // poisoning the still-live authority or suppressing its next
                // in-process recovery commit.
                _journal.RecoveryBlocked = false;
                _journal.RecoveryFailurePermanent = false;
                _journal.RecoveryFailureCode = string.Empty;
                _journal.RecoveryFailureFingerprint = string.Empty;
                _journal.RootCode = string.Empty;
                Interlocked.Exchange(ref _pendingCommitGenerationAwaitingRunIdentity, 0);
            }
            if (record.State != DurableRelaunchPermitState.RejectedNoWork &&
                !string.IsNullOrWhiteSpace(record.Fingerprint))
                _journal.RecoveryFailureFingerprint = record.Fingerprint;
            if (!string.IsNullOrWhiteSpace(record.ProgressToken))
                _journal.RecoveryProgressToken = record.ProgressToken;
            if (!string.IsNullOrWhiteSpace(record.ProcessSource))
                _journal.RecoveryProcessSource = record.ProcessSource;
            if (!string.IsNullOrWhiteSpace(record.RunId))
                _journal.RunId = record.RunId;
            if (!string.IsNullOrWhiteSpace(record.RecoveryStage))
                _journal.RecoveryStage = record.RecoveryStage;
            if (record.ProcessId > 0)
                _journal.CurrentPid = record.ProcessId;
            if (record.ProcessStartUtcTicks > 0)
                _journal.CurrentProcessStartUtcTicks = record.ProcessStartUtcTicks;
        }

        private string BuildJournalSnapshotLocked()
        {
            _journal.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            _journal.EventSequence = Interlocked.Read(ref _eventSequence);
            _journal.DroppedEventCount = _journalStore.DroppedEventCount;
            return Json.Serialize(_journal);
        }

        private bool TryPersistJournalSnapshotLocked()
        {
            return _journalStore.TryPublishSnapshotSynchronously(
                BuildJournalSnapshotLocked());
        }

        private void RecordHeartbeatCheckpoint()
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastHeartbeatCheckpointTimestamp);
            if (previous != 0 &&
                (now - previous) / (double)Stopwatch.Frequency < _args.JournalPolicy.HeartbeatCheckpointSeconds)
                return;
            if (Interlocked.CompareExchange(ref _lastHeartbeatCheckpointTimestamp, now, previous) != previous)
                return;
            RecordEvent("HeartbeatCheckpoint", "Periodic", true);
        }

        private void RecordEvent(string eventType, string reason, bool checkpoint = false)
        {
            WatchdogJournalEvent value;
            lock (_journalGate)
            {
                var heartbeat = _journal.LastHeartbeat;
                value = new WatchdogJournalEvent
                {
                    EventSequence = Interlocked.Increment(ref _eventSequence),
                    EventType = eventType ?? string.Empty,
                    State = _journal.State,
                    Reason = reason ?? string.Empty,
                    ProcessId = _journal.CurrentPid,
                    ProcessStartUtcTicks = _journal.CurrentProcessStartUtcTicks,
                    HeartbeatSequence = _journal.LastHeartbeatSequence,
                    AckSequence = _journal.LastHeartbeatAckSequence,
                    RecoveryAttempt = _journal.RecoveryAttempt,
                    RootCode = _journal.RootCode ?? _journal.RecoveryFailureCode,
                    DeviceOrChannelGroup = _journal.DeviceOrChannelGroup,
                    RecoveryProgressToken = _journal.RecoveryProgressToken,
                    RecoveryProcessSource = _journal.RecoveryProcessSource,
                    RecoveryFailureFingerprint = _journal.RecoveryFailureFingerprint,
                    RelaunchState = _journal.RelaunchState,
                    RelaunchGeneration = _journal.RelaunchPermitGeneration,
                    RelaunchPermitId = _journal.RelaunchPermitId,
                    RelaunchPermitNonce = _journal.RelaunchPermitNonce,
                    RelaunchFingerprint = _journal.RelaunchFingerprint,
                    RelaunchProgressToken = _journal.RelaunchProgressToken,
                    RelaunchProcessSource = _journal.RelaunchProcessSource,
                    RelaunchRunId = _journal.RelaunchRunId,
                    RelaunchRecoveryStage = _journal.RelaunchRecoveryStage,
                    RelaunchProcessId = _journal.RelaunchProcessId,
                    RelaunchProcessStartUtcTicks = _journal.RelaunchProcessStartUtcTicks,
                    RelaunchConsecutiveFailures = _journal.RelaunchConsecutiveFailures,
                    RelaunchMaximumProcessRelaunches = _journal.RelaunchMaximumProcessRelaunches,
                    RelaunchRecoveryCommitGeneration = _journal.RelaunchRecoveryCommitGeneration,
                    RelaunchFailureCode = _journal.RelaunchFailureCode,
                    RelaunchFailureReason = _journal.RelaunchFailureReason,
                    ManualStopRequested = _journal.ManualStopRequested,
                    RunId = heartbeat?.RunId ?? _journal.RunId,
                    RunEpoch = heartbeat?.RunEpoch ?? 0,
                    OrphanPaused = heartbeat?.OrphanPaused == true,
                    PowerDisablePending = heartbeat?.PowerDisablePending == true,
                    RecoveryStage = heartbeat?.RecoveryStage ?? _journal.RecoveryStage,
                    RecoveryIncident = heartbeat?.RecoveryIncident,
                    RecoveryContext = heartbeat?.RecoveryContext,
                    EnabledChannels = heartbeat?.EnabledChannels ?? Array.Empty<int>(),
                    EligibleChannels = GetRecoveryEligibleChannels(heartbeat),
                    CompletedChannels = heartbeat?.CompletedChannels ?? Array.Empty<int>(),
                    PermanentAlarmedChannels = heartbeat?.PermanentAlarmedChannels ?? Array.Empty<int>()
                };
            }
            _journalStore.Record(value, checkpoint);
        }

        private void PublishTerminal(string state, string reason)
        {
            if (Interlocked.CompareExchange(ref _terminalPublished, 1, 0) != 0) return;
            _journalStore.PublishTerminal(state, reason);
        }

        private static double ElapsedSeconds(long since) => (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;
        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        private static string SafeName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars()) value = (value ?? string.Empty).Replace(invalid, '_');
            return value;
        }

        internal static void WriteEmergencyLog(string message, string[] rawArgs = null)
        {
            try
            {
                var directory = WatchdogArguments.ReadRaw(rawArgs, "--journal-directory");
                var session = WatchdogArguments.ReadRaw(rawArgs, "--session");
                if (string.IsNullOrWhiteSpace(directory) || !Guid.TryParseExact(session, "N", out _)) return;
                using (var process = Process.GetCurrentProcess())
                using (var store = new WatchdogJournalStore(
                           directory,
                           session,
                           "sidecar",
                           new WatchdogJournalPolicy(),
                           process.Id,
                           process.StartTime.ToUniversalTime().Ticks))
                {
                    store.RecordError(message);
                    store.Flush(TimeSpan.FromSeconds(1));
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try { _unattendedAlarmSink.Dispose(); } catch { }
            try { _transitionWindow.Hide(); } catch { }
            try { _transitionWindow.Dispose(); } catch { }
            try { _journalStore.Flush(TimeSpan.FromSeconds(2)); } catch { }
            try { _journalStore.Dispose(); } catch { }
            try { _stop.Dispose(); } catch { }
        }
    }
}
