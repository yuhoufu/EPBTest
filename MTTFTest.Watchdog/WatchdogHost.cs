using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
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

    internal sealed class WatchdogHost : IDisposable
    {
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
        private int _takeoverStarted;
        private int _relaunchStarted;
        private int _recoveryBlockedStopRequested;
        private int _heartbeatSuspectLogged;
        private int _unstructuredRecoveryLogged;
        private int _terminalPublished;
        private long _manualStopIntentTimestamp;
        private long _manualPauseStartedTimestamp;
        private long _manualPauseProgressTimestamp = Stopwatch.GetTimestamp();
        private readonly object _manualPauseProgressGate = new object();
        private string _manualPauseProgressSignature = string.Empty;
        private int _manualStopEmergencyResent;
        private int _manualStopTakeoverStarted;
        private int _manualPauseSafetyTakeoverStarted;
        private int _physicalStopConfirmed;
        private int _transitionActive;
        private int _operatorTransitionStopStarted;
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

        private WatchdogHost(WatchdogArguments args)
        {
            _args = args;
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
            _transitionWindow = new RecoveryTransitionWindow(OnTransitionOperatorStopRequested);
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
                     previous.SchemaVersion != WatchdogJournalPolicy.CurrentSchemaVersion))
                    return null;
                // V2 did not carry the structured report fields, but its
                // durable RecoveryBlocked bit and legacy code/fingerprint are
                // authoritative and must survive the additive migration.
                if (previous.SchemaVersion == 2)
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
                        return host.RunAsync().GetAwaiter().GetResult();
                }
                finally { try { singleton.ReleaseMutex(); } catch { } }
            }
        }

        private async Task<int> RunAsync()
        {
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
                    if (IsSessionRevoked())
                    {
                        _journal.ManualStopRequested = true;
                        Record("SessionRevoked", "AttachRevocationMarker");
                        PublishTerminal("SessionRevoked", "AttachRevocationMarker");
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
                    if (message.Session?.RecoveryProcess == true)
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
                    if (!sameAuthorityReconnect)
                    {
                        CancelAutomaticTakeover("ValidatedNewProcessAttached");
                        _takeoverStarted = 0;
                        _activeTakeoverCorrelationId = string.Empty;
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
                        Record("AttachedReconnect",
                            "SameAuthority;ConnectionGeneration=" + connectionGeneration);
                    }
                    if (message.Session?.RecoveryProcess == true)
                    {
                        Interlocked.Exchange(ref _transitionActive, 1);
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
                    _attached = true;
                    Interlocked.Exchange(ref _heartbeatSuspectLogged, 0);
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
                        BeginRelaunchAfterExit();
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
                    Record(message.Type, message.Reason);
                    if (Interlocked.CompareExchange(ref _operatorTransitionStopStarted, 0, 0) != 0)
                    {
                        _operatorStopAcknowledged.TrySetResult(true);
                        break;
                    }
                    PublishTerminal(message.Type, message.Reason);
                    _stop.Cancel();
                    break;
                case WatchdogMessageType.ApplicationClosing:
                    _journal.ManualStopRequested = true;
                    CancelAutomaticTakeover("ApplicationClosing");
                    Record(message.Type, message.Reason);
                    PublishTerminal("ExpectedApplicationExit", message.Reason);
                    _stop.Cancel();
                    break;
                case WatchdogMessageType.ShutdownExpected:
                    _journal.ManualStopRequested = true;
                    CancelAutomaticTakeover("ShutdownExpected");
                    Record(message.Type, message.Reason);
                    PublishTerminal("ExpectedApplicationExit", message.Reason);
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
                    if (!_attached) continue;
                    if (WatchdogRecoveryCommitMarker.TryRead(
                            _args.JournalDirectory,
                            _args.SessionId,
                            out var durableCommitGeneration) &&
                        durableCommitGeneration > _journal.LastRecoveryBatchCommitGeneration)
                    {
                        var commitHeartbeat = _journal.LastHeartbeat;
                        // The marker proves only that the client durably reached
                        // the boundary.  Wait for a heartbeat carrying the same
                        // generation and full new-run context before mutating the
                        // strict authority.
                        if (commitHeartbeat != null &&
                            commitHeartbeat.RecoveryBatchCommitGeneration >= durableCommitGeneration)
                            TryAcceptRecoveryBatchCommit(
                                durableCommitGeneration,
                                "DurableMarker",
                                $"Generation={durableCommitGeneration}",
                                commitHeartbeat);
                    }
                    if (IsSessionRevoked())
                    {
                        // 过渡窗人工停止会先写耐久撤权 marker，让主程序在管道失效时
                        // 也能主动全断能。此时 Sidecar 必须继续等待该停止收口，不能
                        // 被自己刚写的 marker 提前结束。
                        if (IsTransitionOperatorStopInProgress()) continue;
                        _journal.ManualStopRequested = true;
                        Record("SessionRevoked", "RevocationMarker");
                        PublishTerminal("SessionRevoked", "RevocationMarker");
                        _stop.Cancel();
                        continue;
                    }
                    if (_journal.RecoveryBlocked)
                    {
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
                        heartbeatAge,
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
                        : heartbeatAge;
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

                    var stopActive = heartbeat?.StopAllActive == true ||
                        heartbeat?.StopTakeoverRequired == true ||
                        heartbeat?.StopTimedOut == true;
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
                        logicalResidue && !stopActive,
                        inconsistentRecovery && !stopActive,
                        (formalProgressStalled || channelSupervisionFailed) && !stopActive,
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
                        var reason = !processAlive || heartbeatAgeForTakeover >= 5
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
            _transitionWindow.Show(
                "检测到异常，正在安全接管",
                "正在请求原程序关闭全部输出。原因：" + DescribeRecoveryReason(reason),
                0,
                _journal.RecoveryAttempt + 1);
            Send(WatchdogMessageType.RequestStopAll, reason, correlationId);
            _ = Task.Run(() => TakeoverAsync(reason, transaction));
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
            if (IsTransitionOperatorStopInProgress() || IsSessionRevoked()) return;
            if (IsCurrentProcessAlive())
            {
                try
                {
                    using (var process = Process.GetProcessById(_journal.CurrentPid))
                    {
                        if (MatchesCurrentProcess(process))
                        {
                            await CaptureMiniDumpBeforeTerminationAsync(
                                    process,
                                    "ManualPauseSafety:" + reason)
                                .ConfigureAwait(false);
                            process.Kill();
                            process.WaitForExit(5000);
                            Record("ManualPauseOldProcessTerminated", reason);
                        }
                    }
                }
                catch (Exception ex) { Record("ManualPauseTerminationFailed", ex.Message); }
            }
            if (IsTransitionOperatorStopInProgress() || IsSessionRevoked()) return;
            if (!LaunchIdleRestart("ManualPauseSafety:" + reason)) return;
            PublishTerminal("ManualPauseIdleRestartLaunched", reason);
            _stop.Cancel();
        }

        private async Task ManualStopTakeoverAsync(string reason)
        {
            if (IsTransitionOperatorStopInProgress() || IsSessionRevoked()) return;
            if (IsCurrentProcessAlive())
            {
                try
                {
                    using (var process = Process.GetProcessById(_journal.CurrentPid))
                    {
                        if (WatchdogProcessIdentityPolicy.CanKillOldProcess(
                                IsSessionRevoked(),
                                manualStopRequested: true,
                                MatchesCurrentProcess(process)))
                        {
                            await CaptureMiniDumpBeforeTerminationAsync(
                                    process,
                                    "ManualStopSafety:" + reason)
                                .ConfigureAwait(false);
                            process.Kill();
                            process.WaitForExit(5000);
                            Record("ManualStopOldProcessTerminated", reason);
                        }
                    }
                }
                catch (Exception ex) { Record("ManualStopTerminationFailed", ex.Message); }
            }
            if (IsTransitionOperatorStopInProgress() || IsSessionRevoked()) return;
            if (!LaunchIdleRestart("ManualStopSafety:" + reason)) return;
            PublishTerminal("ManualStopIdleRestartLaunched", reason);
            _stop.Cancel();
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
            TakeoverTransactionLease transaction)
        {
            try
            {
                _transitionWindow.Show(
                    "正在确认设备安全状态",
                    "等待原程序完成全断能并退出。原因：" + DescribeRecoveryReason(reason),
                    0,
                    _journal.RecoveryAttempt + 1);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!_journal.ManualStopRequested &&
                       !IsSessionRevoked() &&
                       DateTime.UtcNow < deadline)
                {
                    if (!IsCurrentProcessAlive()) break;
                    await Task.Delay(250, transaction.CancellationToken).ConfigureAwait(false);
                }
                if (_journal.ManualStopRequested ||
                    IsSessionRevoked() ||
                    !_automaticTakeover.IsAuthorized(transaction))
                    return;

                Process oldProcess = null;
                try
                {
                    if (IsCurrentProcessAlive())
                    {
                        oldProcess = Process.GetProcessById(_journal.CurrentPid);
                        if (!WatchdogProcessIdentityPolicy.CanKillOldProcess(
                                IsSessionRevoked(),
                                _journal.ManualStopRequested,
                                MatchesCurrentProcess(oldProcess)))
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
                                    oldProcess.WaitForExit(5000);
                                    if (oldProcess.HasExited)
                                        Record("OldProcessTerminated", reason);
                                    return oldProcess.HasExited;
                                },
                            permit => RelaunchLoopAsync(reason, permit),
                            (current, requested, failure) => Record(
                                requested == TakeoverTransactionStage.RelaunchPermit &&
                                string.Equals(failure, "PermitNotConsumable", StringComparison.Ordinal)
                                    ? "TakeoverAbortedNoConsumablePermit"
                                    : "TakeoverStageRegression",
                                $"Current={current};Requested={requested};" +
                                $"Failure={failure};Reason={reason}"))
                        .ConfigureAwait(false);
                    if (!pipeline.Succeeded) return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Record("OldProcessTerminationFailed", ex.Message);
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
            _ = Task.Run(async () =>
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
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline && !IsSessionRevoked() && IsCurrentProcessAlive())
                    await Task.Delay(250).ConfigureAwait(false);
                if (!IsSessionRevoked() && !_journal.ManualStopRequested && IsCurrentProcessAlive())
                {
                    try
                    {
                        using (var process = Process.GetProcessById(_journal.CurrentPid))
                        {
                            var canKill = WatchdogProcessIdentityPolicy.CanKillOldProcess(
                                IsSessionRevoked(),
                                _journal.ManualStopRequested,
                                MatchesCurrentProcess(process));
                            if (canKill)
                            {
                                await CaptureMiniDumpBeforeTerminationAsync(
                                        process,
                                        "StopCompletedButProcessAlive")
                                    .ConfigureAwait(false);
                                process.Kill();
                                process.WaitForExit(5000);
                                Record("OldProcessTerminated", "StopCompleted");
                            }
                        }
                    }
                    catch (Exception ex) { Record("OldProcessTerminationFailed", ex.Message); }
                }
                if (!_journal.ManualStopRequested && !IsSessionRevoked())
                    await RelaunchLoopAsync("StopCompleted", permitGeneration).ConfigureAwait(false);
            });
        }

        private async Task RelaunchLoopAsync(string reason, long approvedPermitGeneration)
        {
            if (IsRecoveryBlocked() || _journal.ManualStopRequested || IsSessionRevoked()) return;
            if (Interlocked.CompareExchange(ref _relaunchStarted, 1, 0) != 0) return;
            var permitGeneration = approvedPermitGeneration;
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

                    var delay = RecoveryFailurePolicy.SelectProcessRelaunchDelaySeconds(attempt);
                    Record("RecoveryBackoff", $"Attempt={attempt};DelaySeconds={delay};Reason={reason}");
                    await DelayWithTransitionCountdownAsync(delay, attempt, reason).ConfigureAwait(false);
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
                             "--relaunch-generation {8} --relaunch-permit-id {9} --relaunch-permit-nonce {10}",
                             Quote(_args.SessionId), Quote(_args.PipeName), previousPid, attempt,
                             Quote(string.Join(",", excluded)),
                             _sidecarProcessId,
                             _sidecarProcessStartUtcTicks,
                             Quote(_sidecarInstanceNonce),
                             permitRecord.Generation,
                             Quote(permitRecord.PermitId),
                             Quote(permitRecord.PermitNonce));
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
                        var attachDeadline = DateTime.UtcNow.AddSeconds(20);
                        while (!_attached && !_journal.ManualStopRequested && !IsSessionRevoked() &&
                               DateTime.UtcNow < attachDeadline && !started.HasExited)
                        {
                            var remaining = Math.Max(1, (int)Math.Ceiling((attachDeadline - DateTime.UtcNow).TotalSeconds));
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
                        if (launchOwner != null) launchOwner.KillExactAndDispose();
                        else _processLauncher.KillExact(started);
                        // RegisterRecoveryFailure is the single strict-V4
                        // failure transaction and closes the active permit.
                        Record("RecoveryAttachFailed", $"Attempt={attempt};Permit={permitGeneration}");
                        var failureDecision = RegisterRecoveryFailure(
                            "RecoveryAttachFailed",
                            RecoveryFailurePolicy.Classify(
                                "RecoveryAttachFailed",
                                false,
                                "RecoveryAttachFailed"));
                        if (failureDecision.SafeIdleRecoveryBlocked ||
                            (!failureDecision.ProcessRelaunchAllowed &&
                             !failureDecision.RelaunchPermitAlreadyPending))
                        {
                            EnterRelaunchCircuitOpen(
                                failureDecision.Fingerprint,
                                failureDecision.ConsecutiveCount,
                                "RecoveryAttachFailed");
                            return;
                        }
                        permitGeneration = failureDecision.RelaunchPermitGeneration;
                        if (permitGeneration <= 0 && failureDecision.RelaunchPermitAlreadyPending)
                            permitGeneration = GetActiveRelaunchPermit();
                        if (permitGeneration <= 0) return;
                        _transitionWindow.Show(
                            "本次启动未能连接",
                            "主程序未在 20 秒内连接，将按退避策略再次尝试",
                            0,
                            attempt);
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
            var owner = _processLauncher.Start(capability);
            if (owner == null || owner.Process == null)
                throw new InvalidOperationException("GuardedProcessStartReturnedNull");
            return owner;
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
                return true;
            }
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
                Record("RecoveryBatchCommitDeferred",
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
                Record(
                    "RecoveryBatchCommitDeferred",
                    $"Evidence={evidence};Generation={commitGeneration};ContextMissing");
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
                Record(
                    "RecoveryBatchCommitRejected",
                    $"Evidence={evidence};Generation={commitGeneration};" +
                    $"RunId={heartbeat.RunId};RunEpoch={heartbeat.RunEpoch};" +
                    $"Stage={heartbeat.RecoveryStage}");
                return false;
            }
            ObserveRecoveryBatchCommit(
                commitGeneration,
                evidence,
                detail,
                heartbeat.RunId);
            return true;
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
            ShowRecoveryBlockedTransition(detail);
        }

        private void ShowRecoveryBlockedTransition(string detail)
        {
            _transitionWindow.Show(
                "自动恢复已阻断，设备保持安全",
                $"恢复失败已进入持久终态（Code={_journal.RecoveryFailureCode ?? "Unknown"}，" +
                $"Count={Math.Max(1, _journal.ConsecutiveStartupFailures)}）。" +
                "不会再启动主程序；请处理配置/程序或设备问题后由操作员重新开始新会话。" +
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

            if (strict?.Receipt != null)
            {
                // Receipts are sent after the authority gate and journal
                // mirror have completed; the wire object contains no permit
                // nonce or launch-only identity.
                Send(new WatchdogMessage
                {
                    ProtocolVersion = 3,
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
                    _journal.ConsecutiveStartupFailures = 0;
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
            if (reason.StartsWith("Stop", StringComparison.OrdinalIgnoreCase)) return "安全停止流程超时";
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
                    message => RecordEvent("MiniDump", message))
                .ConfigureAwait(false);
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
            return WatchdogControlMarker.IsRevoked(_args.JournalDirectory, _args.SessionId);
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
            if (!string.IsNullOrWhiteSpace(record.Fingerprint))
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
            try { _transitionWindow.Hide(); } catch { }
            try { _transitionWindow.Dispose(); } catch { }
            try { _journalStore.Flush(TimeSpan.FromSeconds(2)); } catch { }
            try { _journalStore.Dispose(); } catch { }
            try { _stop.Dispose(); } catch { }
        }
    }
}
