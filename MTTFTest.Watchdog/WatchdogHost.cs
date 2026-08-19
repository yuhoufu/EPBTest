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
        public int RecoveryFailureMaxProcessRelaunches { get; set; }
        public long RecoveryFirstFailureUtcTicks { get; set; }
        public long RecoveryLastFailureUtcTicks { get; set; }
        public long RecoveryBlockedUtcTicks { get; set; }
        public long LastRecoveryBatchCommitGeneration { get; set; }
        public bool ManualStopRequested { get; set; }
        public string State { get; set; }
        public string LastReason { get; set; }
        public long LastHeartbeatSequence { get; set; }
        public long LastHeartbeatAckSequence { get; set; }
        public long LastHeartbeatUtcTicks { get; set; }
        public bool OrphanPauseTriggered { get; set; }
        public bool PowerDisableTriggered { get; set; }
        public string UpdatedUtc { get; set; }
        public string StartedUtc { get; set; }
        public long EventSequence { get; set; }
        public long DroppedEventCount { get; set; }
        public WatchdogHeartbeat LastHeartbeat { get; set; }
        public WatchdogCheckpointMirror LastCheckpointMirror { get; set; }
    }

    internal sealed class WatchdogArguments
    {
        public int ParentPid;
        public long ParentStartTicks;
        public string SessionId;
        public string PipeName;
        public string ExecutablePath;
        public string JournalDirectory;
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
                string.IsNullOrWhiteSpace(result.JournalDirectory))
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
        private readonly WatchdogJournalStore _journalStore;
        private readonly RecoveryTransitionWindow _transitionWindow;
        private StreamWriter _writer;
        private WatchdogJournal _journal;
        private long _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        private long _lastProgressTimestamp = Stopwatch.GetTimestamp();
        private long _lastProgressVersion;
        private long _lastCompletedCycleCount = -1;
        private long _lastFormalProgressTimestamp = Stopwatch.GetTimestamp();
        private long _lastHeartbeatSequence;
        private long _lastHeartbeatAckSequence;
        private long _eventSequence;
        private long _lastHeartbeatCheckpointTimestamp;
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
        private readonly TaskCompletionSource<bool> _operatorStopAcknowledged =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _attached;

        private WatchdogHost(WatchdogArguments args)
        {
            _args = args;
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
                RecoveryFailureMaxProcessRelaunches =
                    previous?.RecoveryFailureMaxProcessRelaunches ?? 0,
                RecoveryFirstFailureUtcTicks = previous?.RecoveryFirstFailureUtcTicks ?? 0,
                RecoveryLastFailureUtcTicks = previous?.RecoveryLastFailureUtcTicks ?? 0,
                RecoveryBlockedUtcTicks = previous?.RecoveryBlockedUtcTicks ?? 0,
                LastReason = previous?.LastReason,
                State = previous?.RecoveryBlocked == true
                    ? "SafeIdleRecoveryBlocked"
                    : "Starting",
                UpdatedUtc = startedUtc,
                StartedUtc = startedUtc
            };
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
                return previous != null &&
                       string.Equals(previous.SessionId, args.SessionId, StringComparison.OrdinalIgnoreCase)
                    ? previous
                    : null;
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
            }
            try { await monitor.ConfigureAwait(false); } catch { }
            return 0;
        }

        private async Task ServeConnectionAsync(Stream pipe, CancellationToken token)
        {
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
            {
                lock (_gate) _writer = writer;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        WatchdogMessage message;
                        try { message = WatchdogProtocol.Deserialize(line); }
                        catch (Exception ex) { Record("InvalidMessage", ex.Message); continue; }
                        if (message == null ||
                            message.ProtocolVersion < WatchdogProtocol.MinimumCompatibleVersion ||
                            message.ProtocolVersion > WatchdogProtocol.Version ||
                            !string.Equals(message.SessionId, _args.SessionId, StringComparison.Ordinal))
                            continue;
                        await HandleMessageAsync(message).ConfigureAwait(false);
                    }
                }
                finally
                {
                    lock (_gate) if (ReferenceEquals(_writer, writer)) _writer = null;
                }
            }
        }

        private Task HandleMessageAsync(WatchdogMessage message)
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
                    if (message.Session != null)
                    {
                        _journal.CurrentPid = message.Session.ProcessId;
                        _journal.CurrentProcessStartUtcTicks = message.Session.ProcessStartUtcTicks;
                        _journal.ExecutablePath = Path.GetFullPath(message.Session.ExecutablePath);
                        _journal.RecoveryAttempt = Math.Max(_journal.RecoveryAttempt, message.Session.RecoveryAttempt);
                        _journal.RelaunchGeneration = Math.Max(
                            _journal.RelaunchGeneration,
                            message.Session.RelaunchGeneration);
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
                        break;
                    }
                    _takeoverStarted = 0;
                    _relaunchStarted = 0;
                    _lastProgressVersion = 0;
                    _channelProgressTracker.Reset();
                    Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref _lastCompletedCycleCount, -1);
                    Interlocked.Exchange(ref _lastFormalProgressTimestamp, Stopwatch.GetTimestamp());
                    _journal.OrphanPauseTriggered = false;
                    _journal.PowerDisableTriggered = false;
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                    Record("Attached", message.Session?.RecoveryProcess == true ? "RecoveryProcess" : "MainProcess");
                    if (message.Session?.RecoveryProcess == true)
                    {
                        Interlocked.Exchange(ref _transitionActive, 1);
                        _transitionWindow.Show(
                            "主程序已重新启动",
                            "正在连接恢复会话并加载安全检查点",
                            0,
                            _journal.RecoveryAttempt);
                    }
                    Send(WatchdogMessageType.Attached, "Attached", message.CorrelationId);
                    break;
                case WatchdogMessageType.MainUiReady:
                    if (_journal.RecoveryBlocked)
                        RecordEvent("MainUiReadyRejectedByDurableBlock", message.Reason);
                    else
                        Record("MainUiReady", message.Reason ?? "MainWindowShown");
                    if (!_journal.RecoveryBlocked &&
                        Interlocked.CompareExchange(ref _transitionActive, 0, 0) != 0)
                    {
                        _transitionWindow.Hide();
                        Interlocked.Exchange(ref _transitionActive, 0);
                    }
                    break;
                case WatchdogMessageType.Heartbeat:
                    if (message.Heartbeat == null) break;
                    _attached = true;
                    Interlocked.Exchange(ref _heartbeatSuspectLogged, 0);
                    _journal.CurrentPid = message.Heartbeat.ProcessId;
                    _journal.CurrentProcessStartUtcTicks = message.Heartbeat.ProcessStartUtcTicks;
                    _journal.LastHeartbeat = message.Heartbeat;
                    if (message.Heartbeat.RecoveryBatchCommitGeneration >
                        _journal.LastRecoveryBatchCommitGeneration)
                    {
                        CommitRecoveryAttempt(message.Heartbeat.RecoveryBatchCommitGeneration);
                        RecordEvent(
                            "RecoveryBatchCommitHeartbeatObserved",
                            $"Generation={message.Heartbeat.RecoveryBatchCommitGeneration}");
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
                        _journal.LastCheckpointMirror = message.CheckpointMirror;
                    Record("RecoveryCheckpointValidated", message.Reason);
                    break;
                case WatchdogMessageType.SafetyPreflightPassed:
                    Record("SafetyPreflightPassed", message.Reason);
                    break;
                case WatchdogMessageType.RecoveryBatchCommitted:
                    CommitRecoveryAttempt(
                        message.RecoveryCommitGeneration > 0
                            ? message.RecoveryCommitGeneration
                            : Math.Max(1, _journal.LastRecoveryBatchCommitGeneration + 1));
                    Record("RecoveryBatchCommitted", message.Reason);
                    break;
                case WatchdogMessageType.BatchStartFailed:
                    Record("BatchStartFailed", message.Reason);
                    BeginTakeover("BatchStartFailed:" + message.Reason);
                    break;
                case WatchdogMessageType.RecoveryAttemptFailed:
                    var classification = RecoveryFailurePolicy.Classify(
                        message.RecoveryFailureCode,
                        message.RecoveryFailurePermanent,
                        message.Reason);
                    Record(
                        "RecoveryAttemptFailed",
                        $"Code={classification.Code};Permanent={classification.Permanent};" +
                        $"ContextSha256={message.RecoveryFailureContextSha256};" +
                        (message.Reason ?? string.Empty));
                    _attached = false;
                    var failureDecision = RegisterRecoveryFailure(
                        message.Reason,
                        classification);
                    if (classification.Permanent ||
                        failureDecision.ConsecutiveCount >=
                        Math.Max(1, classification.MaximumProcessRelaunches))
                        EnterRelaunchCircuitOpen(
                            failureDecision.Fingerprint,
                            failureDecision.ConsecutiveCount,
                            message.RecoveryFailureDetail ?? message.Reason);
                    else
                        BeginRelaunchAfterExit();
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
                    Interlocked.CompareExchange(
                        ref _manualStopIntentTimestamp,
                        Stopwatch.GetTimestamp(),
                        0);
                    Record("ManualStopIntent", message.Reason);
                    break;
                case WatchdogMessageType.RunStopped:
                case WatchdogMessageType.RunCompleted:
                case WatchdogMessageType.ApplicationClosing:
                case WatchdogMessageType.ShutdownExpected:
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
            }
            return Task.CompletedTask;
        }

        private async Task MonitorAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    if (!_attached) continue;
                    if (IsSessionRevoked())
                    {
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
                        if (manualAge >= 15)
                        {
                            BeginManualStopTakeover("ManualStopTimeout15s");
                            continue;
                        }
                    }
                    var logicalResidue = heartbeat != null && !manualPauseCommanded && !heartbeat.RunActive &&
                        (heartbeat.TimerCount > 0 || heartbeat.RunnerCount > 0 ||
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
                    var channelSupervisionReason = EvaluateChannelSupervision(
                        heartbeat,
                        manualPauseCommanded,
                        eligibleChannels);
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
                        DateTime.UtcNow.Ticks,
                        manualPauseNoProgressSeconds);
                    var shouldTakeover = WatchdogTakeoverPolicy.ShouldTakeover(
                        IsSessionRevoked(),
                        _journal.ManualStopRequested,
                        Interlocked.CompareExchange(ref _takeoverStarted, 0, 0) != 0,
                        processAlive,
                        heartbeatAge,
                        WatchdogRecoveryTelemetryPolicy.ShouldTreatAsRecoveryActive(heartbeat),
                        heartbeat?.OrphanPaused == true,
                        heartbeat?.PowerOffUnconfirmed == true,
                        stageAgeSeconds,
                        eligibleChannels.Length > 0,
                        heartbeat?.StopAllActive == true,
                        logicalResidue,
                        inconsistentRecovery,
                        formalProgressStalled || channelSupervisionFailed,
                        manualPauseCommanded,
                        manualPauseUnsafe,
                        heartbeat?.RecoveryHardDeadlineUtc ?? 0,
                        DateTime.UtcNow.Ticks,
                        ElapsedSeconds(Interlocked.Read(ref _lastProgressTimestamp)));
                    if (shouldTakeover)
                    {
                        var reason = !processAlive || heartbeatAge >= 5
                            ? (heartbeatAge >= 5 ? "HeartbeatUnresponsive" : "ProcessExitedUnexpectedly")
                            : heartbeat?.StopAllActive == true && stageAgeSeconds >= 5
                                ? "StopStageNoProgress5s"
                                : heartbeat?.PowerOffUnconfirmed == true && stageAgeSeconds >= 5
                                ? "PowerOffUnconfirmedTimeout"
                                : heartbeat?.OrphanPaused == true && stageAgeSeconds >= 5
                                    ? "OrphanPausedTimeout"
                                    : channelSupervisionFailed
                                        ? channelSupervisionReason
                                    : formalProgressStalled
                                        ? "FormalProgressStalledAggregateFallback"
                                    : "ExternalRecoveryStageStalled";
                        if (manualPauseCommanded)
                            BeginManualPauseSafetyTakeover(
                                heartbeat?.ManualPauseSafetyFault == true
                                    ? "ManualPauseSafetyFault:" + heartbeat.ManualPauseSafetyFaultReason
                                    : "ManualPauseHardDeadlineExceeded");
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
            Record("TakeoverRequested", reason);
            Interlocked.Exchange(ref _transitionActive, 1);
            _transitionWindow.Show(
                "检测到异常，正在安全接管",
                "正在请求原程序关闭全部输出。原因：" + DescribeRecoveryReason(reason),
                0,
                _journal.RecoveryAttempt + 1);
            Send(WatchdogMessageType.RequestStopAll, reason, Guid.NewGuid().ToString("N"));
            _ = Task.Run(() => TakeoverAsync(reason));
        }

        private string EvaluateChannelSupervision(
            WatchdogHeartbeat heartbeat,
            bool manualPauseCommanded,
            int[] eligibleChannels)
        {
            return _channelProgressTracker.Evaluate(
                heartbeat,
                eligibleChannels,
                manualPauseCommanded,
                Stopwatch.GetTimestamp(),
                Stopwatch.Frequency);
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
            Interlocked.Exchange(ref _transitionActive, 1);
            Record("ManualPauseSafetyTakeoverRequested", reason);
            _transitionWindow.Show(
                "人工暂停安全确认异常",
                "仅执行全断能并安全重开到空闲态，不会自动续跑。原因：" + DescribeRecoveryReason(reason),
                0,
                0);
            Send(WatchdogMessageType.RequestStopAll, "ManualPauseSafety:" + reason, Guid.NewGuid().ToString("N"));
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
                            await CaptureMiniDumpBeforeTerminationAsync(process, "ManualStop:" + reason)
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
            if (!LaunchIdleRestart(reason)) return;
            PublishTerminal("ManualStopIdleRestartLaunched", reason);
            _stop.Cancel();
        }

        private bool LaunchIdleRestart(string reason)
        {
            Process started;
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
                    "--watchdog-idle-restart {0} --previous-pid {1}",
                    Quote(_args.SessionId),
                    _journal.CurrentPid);
                started = Process.Start(new ProcessStartInfo
                {
                    FileName = _journal.ExecutablePath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(_journal.ExecutablePath) ?? Environment.CurrentDirectory,
                    UseShellExecute = false
                });
                if (started == null) throw new InvalidOperationException("Idle restart Process.Start returned null.");
                _journal.CurrentPid = started.Id;
                _journal.CurrentProcessStartUtcTicks = started.StartTime.ToUniversalTime().Ticks;
            }
            Record("IdleProcessLaunched", $"PID={started.Id};Reason={reason};AutoResume=false");
            return true;
        }

        private async Task TakeoverAsync(string reason)
        {
            _transitionWindow.Show(
                "正在确认设备安全状态",
                "等待原程序完成全断能并退出。原因：" + DescribeRecoveryReason(reason),
                0,
                _journal.RecoveryAttempt + 1);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!_journal.ManualStopRequested && !IsSessionRevoked() && DateTime.UtcNow < deadline)
            {
                if (!IsCurrentProcessAlive()) break;
                await Task.Delay(250).ConfigureAwait(false);
            }
            if (_journal.ManualStopRequested || IsSessionRevoked()) return;
            if (!IsSessionRevoked() && !_journal.ManualStopRequested && IsCurrentProcessAlive())
            {
                try
                {
                    using (var process = Process.GetProcessById(_journal.CurrentPid))
                    {
                        if (WatchdogProcessIdentityPolicy.CanKillOldProcess(
                                IsSessionRevoked(),
                                _journal.ManualStopRequested,
                                MatchesCurrentProcess(process)))
                        {
                            await CaptureMiniDumpBeforeTerminationAsync(process, "AutomaticTakeover:" + reason)
                                .ConfigureAwait(false);
                            process.Kill();
                            process.WaitForExit(5000);
                            Record("OldProcessTerminated", reason);
                        }
                    }
                }
                catch (Exception ex) { Record("OldProcessTerminationFailed", ex.Message); }
            }
            await RelaunchLoopAsync(reason).ConfigureAwait(false);
        }

        private void BeginRelaunchAfterExit()
        {
            if (_journal.RecoveryBlocked || _journal.ManualStopRequested || IsSessionRevoked()) return;
            _ = Task.Run(async () =>
            {
                if (_journal.RecoveryAttempt > _journal.ConsecutiveStartupFailures)
                    RegisterRecoveryFailure(
                        "RecoveryProcessExitedBeforeBatchCommit",
                        RecoveryFailurePolicy.Classify(
                            "RecoveryProcessExitedBeforeBatchCommit",
                            false,
                            "RecoveryProcessExitedBeforeBatchCommit"));
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
                    await RelaunchLoopAsync("StopCompleted").ConfigureAwait(false);
            });
        }

        private async Task RelaunchLoopAsync(string reason)
        {
            if (!RecoveryFailurePolicy.CanLaunchMainProcess(_journal.RecoveryBlocked)) return;
            if (Interlocked.CompareExchange(ref _relaunchStarted, 1, 0) != 0) return;
            while (!_journal.ManualStopRequested && !IsSessionRevoked() && !_stop.IsCancellationRequested)
            {
                if (!RecoveryFailurePolicy.CanLaunchMainProcess(_journal.RecoveryBlocked))
                    return;
                var activeClassification = RecoveryFailurePolicy.Classify(
                    _journal.RecoveryFailureCode,
                    _journal.RecoveryFailurePermanent,
                    reason);
                var maximumRelaunches = _journal.RecoveryFailureMaxProcessRelaunches > 0
                    ? _journal.RecoveryFailureMaxProcessRelaunches
                    : Math.Max(1, activeClassification.MaximumProcessRelaunches);
                if (activeClassification.Permanent ||
                    _journal.ConsecutiveStartupFailures >= maximumRelaunches)
                {
                    EnterRelaunchCircuitOpen(
                        string.IsNullOrWhiteSpace(_journal.RecoveryFailureFingerprint)
                            ? RecoveryFailurePolicy.BuildFingerprint(reason)
                            : _journal.RecoveryFailureFingerprint,
                        _journal.ConsecutiveStartupFailures,
                        reason);
                    return;
                }
                var attempt = _journal.ConsecutiveStartupFailures + 1;
                lock (_journalGate)
                {
                    _journal.RecoveryAttempt = attempt;
                    _journal.RelaunchGeneration++;
                }
                var delay = RecoveryFailurePolicy.SelectProcessRelaunchDelaySeconds(attempt);
                Record("RecoveryBackoff", $"Attempt={attempt};DelaySeconds={delay};Reason={reason}");
                await DelayWithTransitionCountdownAsync(delay, attempt, reason).ConfigureAwait(false);
                if (_journal.ManualStopRequested ||
                    IsSessionRevoked() ||
                    !RecoveryFailurePolicy.CanLaunchMainProcess(_journal.RecoveryBlocked))
                    return;
                try
                {
                    _transitionWindow.Show(
                        "正在启动试验程序",
                        "正在创建新的主程序进程并恢复安全检查点",
                        0,
                        attempt);
                    var previousPid = _journal.CurrentPid;
                    // AlarmStopped/InterlockStopped 可能是可恢复的软件或基础设施故障，
                    // 不能仅凭旧进程运行态永久排除；持久禁用会落为 NotEnabled，
                    // 与人工禁用和已完成通道一起排除。
                    var excluded = (_journal.LastHeartbeat?.ManuallyDisabledChannels ?? Array.Empty<int>())
                        .Concat(_journal.LastHeartbeat?.CompletedChannels ?? Array.Empty<int>())
                        .Concat(_journal.LastHeartbeat?.PermanentAlarmedChannels ?? Array.Empty<int>())
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray();
                    var arguments = string.Format(CultureInfo.InvariantCulture,
                        "--watchdog-recover {0} --watchdog-pipe {1} --previous-pid {2} --recovery-attempt {3} --exclude-channels {4}",
                        Quote(_args.SessionId), Quote(_args.PipeName), previousPid, attempt,
                        Quote(string.Join(",", excluded)));
                    Process started;
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
                        started = Process.Start(new ProcessStartInfo
                        {
                            FileName = _journal.ExecutablePath,
                            Arguments = arguments,
                            WorkingDirectory = Path.GetDirectoryName(_journal.ExecutablePath) ?? Environment.CurrentDirectory,
                            UseShellExecute = false
                        });
                        if (started == null) throw new InvalidOperationException("Process.Start returned null.");
                        _journal.CurrentPid = started.Id;
                        _journal.CurrentProcessStartUtcTicks = started.StartTime.ToUniversalTime().Ticks;
                        _journal.OrphanPauseTriggered = false;
                        _journal.PowerDisableTriggered = false;
                    }
                    _attached = false;
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                    Record("RecoveryProcessLaunched", $"PID={started.Id};Attempt={attempt}");
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
                        Interlocked.Exchange(ref _relaunchStarted, 0);
                        return;
                    }
                    try { if (!started.HasExited) started.Kill(); } catch { }
                    Record("RecoveryAttachFailed", $"Attempt={attempt}");
                    RegisterRecoveryFailure(
                        "RecoveryAttachFailed",
                        RecoveryFailurePolicy.Classify(
                            "RecoveryAttachFailed",
                            false,
                            "RecoveryAttachFailed"));
                    _transitionWindow.Show(
                        "本次启动未能连接",
                        "主程序未在 20 秒内连接，将按退避策略再次尝试",
                        0,
                        attempt);
                }
                catch (Exception ex)
                {
                    Record("RecoveryLaunchFailed", ex.Message);
                    RegisterRecoveryFailure(
                        "RecoveryLaunchFailed:" + ex.GetBaseException().Message,
                        RecoveryFailurePolicy.Classify(
                            "RecoveryLaunchFailed",
                            false,
                            ex.GetBaseException().ToString()));
                    _transitionWindow.Show(
                        "本次启动失败",
                        "将按退避策略重试：" + ex.GetBaseException().Message,
                        0,
                        attempt);
                }
            }
            Interlocked.Exchange(ref _relaunchStarted, 0);
        }

        private void EnterRelaunchCircuitOpen(string fingerprint, int consecutiveCount, string detail)
        {
            Interlocked.Exchange(ref _relaunchStarted, 0);
            Interlocked.Exchange(ref _transitionActive, 1);
            lock (_journalGate)
            {
                _journal.RecoveryBlocked = true;
                _journal.RecoveryFailureFingerprint = fingerprint ?? string.Empty;
                _journal.ConsecutiveStartupFailures = Math.Max(
                    1,
                    consecutiveCount);
                _journal.RecoveryBlockedUtcTicks = DateTime.UtcNow.Ticks;
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
            var fingerprint = RecoveryFailurePolicy.BuildFingerprint(
                classification.Code + ":" + (reason ?? string.Empty));
            int consecutiveCount;
            lock (_journalGate)
            {
                var sameFingerprint = string.Equals(
                    _journal.RecoveryFailureFingerprint,
                    fingerprint,
                    StringComparison.Ordinal);
                consecutiveCount = sameFingerprint
                    ? Math.Max(1, _journal.ConsecutiveStartupFailures + 1)
                    : 1;
                var nowTicks = DateTime.UtcNow.Ticks;
                _journal.ConsecutiveStartupFailures = consecutiveCount;
                _journal.RecoveryFailureCode = classification.Code;
                _journal.RecoveryFailurePermanent = classification.Permanent;
                _journal.RecoveryFailureFingerprint = fingerprint;
                _journal.RecoveryFailureMaxProcessRelaunches =
                    Math.Max(0, classification.MaximumProcessRelaunches);
                if (!sameFingerprint || _journal.RecoveryFirstFailureUtcTicks <= 0)
                    _journal.RecoveryFirstFailureUtcTicks = nowTicks;
                _journal.RecoveryLastFailureUtcTicks = nowTicks;
            }
            SaveJournal();
            return new RecoveryFailureDecision
            {
                Fingerprint = fingerprint,
                ConsecutiveCount = consecutiveCount,
                ProcessRelaunchAllowed = !classification.Permanent &&
                    consecutiveCount < Math.Max(1, classification.MaximumProcessRelaunches)
            };
        }

        private void CommitRecoveryAttempt(long commitGeneration)
        {
            lock (_journalGate)
            {
                _journal.RecoveryAttempt = 0;
                _journal.ConsecutiveStartupFailures = 0;
                _journal.CircuitProbeAttempt = 0;
                _journal.RecoveryBlocked = false;
                _journal.RecoveryFailureCode = string.Empty;
                _journal.RecoveryFailurePermanent = false;
                _journal.RecoveryFailureFingerprint = string.Empty;
                _journal.RecoveryFailureMaxProcessRelaunches = 0;
                _journal.RecoveryFirstFailureUtcTicks = 0;
                _journal.RecoveryLastFailureUtcTicks = 0;
                _journal.RecoveryBlockedUtcTicks = 0;
                _journal.LastRecoveryBatchCommitGeneration = Math.Max(
                    _journal.LastRecoveryBatchCommitGeneration,
                    commitGeneration);
            }
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

            const string markerReason = "OperatorCanceledAutomaticRecoveryFromTransitionWindow";
            try { WatchdogControlMarker.WriteLocal(_args.SessionId, markerReason); }
            catch (Exception ex) { Record("OperatorStopLocalMarkerFailed", ex.Message); }
            try { WatchdogControlMarker.WriteProject(_args.JournalDirectory, _args.SessionId, markerReason); }
            catch (Exception ex) { Record("OperatorStopProjectMarkerFailed", ex.Message); }
            PublishTerminal("OperatorRecoveryCanceled", markerReason);
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
            StreamWriter writer;
            lock (_gate)
            {
                writer = _writer;
            }
            if (writer == null) return;
            try
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_writer, writer)) return;
                    writer.WriteLine(WatchdogProtocol.Serialize(message));
                }
            }
            catch (Exception ex)
            {
                // A failed write is a transport state transition, not a
                // best-effort notification.  The main process has its own
                // ACK monitor; recording here makes the sidecar failure
                // diagnosable even when the pipe is already gone.
                Record("SendFailed", (message?.Type ?? "Unknown") + ":" + ex.GetBaseException().Message);
                lock (_gate) if (ReferenceEquals(_writer, writer)) _writer = null;
            }
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
                _journal.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                _journal.EventSequence = Interlocked.Read(ref _eventSequence);
                _journal.DroppedEventCount = _journalStore.DroppedEventCount;
                content = Json.Serialize(_journal);
            }
            _journalStore.PublishSnapshot(content);
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
                    ManualStopRequested = _journal.ManualStopRequested,
                    RunId = heartbeat?.RunId,
                    RunEpoch = heartbeat?.RunEpoch ?? 0,
                    OrphanPaused = heartbeat?.OrphanPaused == true,
                    PowerDisablePending = heartbeat?.PowerDisablePending == true,
                    RecoveryStage = heartbeat?.RecoveryStage,
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
