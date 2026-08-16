using System;
using System.Diagnostics;
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
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _journalGate = new object();
        private readonly WatchdogJournalStore _journalStore;
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
        private int _heartbeatSuspectLogged;
        private int _terminalPublished;
        private long _manualStopIntentTimestamp;
        private int _manualStopEmergencyResent;
        private int _manualStopTakeoverStarted;
        private int _physicalStopConfirmed;
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
            var startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            _journal = new WatchdogJournal
            {
                SessionId = args.SessionId,
                ExecutablePath = args.ExecutablePath,
                PipeName = args.PipeName,
                CurrentPid = args.ParentPid,
                CurrentProcessStartUtcTicks = args.ParentStartTicks,
                State = "Starting",
                UpdatedUtc = startedUtc,
                StartedUtc = startedUtc
            };
            SaveJournal();
            RecordEvent("Starting", "SidecarStarted");
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
                    }
                    _attached = true;
                    _takeoverStarted = 0;
                    _relaunchStarted = 0;
                    _lastProgressVersion = 0;
                    Interlocked.Exchange(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref _lastCompletedCycleCount, -1);
                    Interlocked.Exchange(ref _lastFormalProgressTimestamp, Stopwatch.GetTimestamp());
                    _journal.OrphanPauseTriggered = false;
                    _journal.PowerDisableTriggered = false;
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                    Record("Attached", message.Session?.RecoveryProcess == true ? "RecoveryProcess" : "MainProcess");
                    Send(WatchdogMessageType.Attached, "Attached", message.CorrelationId);
                    break;
                case WatchdogMessageType.Heartbeat:
                    if (message.Heartbeat == null) break;
                    _attached = true;
                    Interlocked.Exchange(ref _heartbeatSuspectLogged, 0);
                    _journal.CurrentPid = message.Heartbeat.ProcessId;
                    _journal.CurrentProcessStartUtcTicks = message.Heartbeat.ProcessStartUtcTicks;
                    _journal.LastHeartbeat = message.Heartbeat;
                    _lastHeartbeatSequence = message.Heartbeat.Sequence;
                    _journal.LastHeartbeatSequence = message.Heartbeat.Sequence;
                    _journal.LastHeartbeatUtcTicks = DateTime.UtcNow.Ticks;
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
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
                case WatchdogMessageType.BatchStartFailed:
                    Record("BatchStartFailed", message.Reason);
                    BeginTakeover("BatchStartFailed:" + message.Reason);
                    break;
                case WatchdogMessageType.RecoveryAttemptFailed:
                    Record("RecoveryAttemptFailed", message.Reason);
                    _attached = false;
                    BeginRelaunchAfterExit();
                    break;
                case WatchdogMessageType.StopCompleted:
                    Record("StopCompleted", message.StopSummary?.Detail ?? message.Reason);
                    if (_journal.ManualStopRequested)
                    {
                        PublishTerminal("ManualStopCompleted", message.StopSummary?.Detail ?? message.Reason);
                        _stop.Cancel();
                    }
                    else
                        BeginRelaunchAfterExit();
                    break;
                case WatchdogMessageType.PhysicalStopConfirmed:
                    Interlocked.Exchange(ref _physicalStopConfirmed, 1);
                    Record("PhysicalStopConfirmed", message.Reason);
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
                    var stageSinceUtc = heartbeat?.StopAllActive == true && heartbeat.StopStageStartedUtc > 0
                        ? heartbeat.StopStageStartedUtc
                        : heartbeat?.PowerDisablePending == true && heartbeat.PowerDisableSince > 0
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
                    var logicalResidue = heartbeat != null && !heartbeat.RunActive &&
                        (heartbeat.TimerCount > 0 || heartbeat.RunnerCount > 0 ||
                         heartbeat.StopCtsCount > 0 || heartbeat.CyclePauseCtsCount > 0 ||
                         heartbeat.DaqRecoveryCount > 0 || heartbeat.SoftwareRecoveryCount > 0 ||
                         heartbeat.RecoveryOwnerCount > 0);
                    var inconsistentRecovery = heartbeat != null && !heartbeat.RecoveryActive &&
                        (heartbeat.DaqRecoveryCount > 0 || heartbeat.SoftwareRecoveryCount > 0 ||
                         heartbeat.RecoveryOwnerCount > 0);
                    var formalProgressStalled = heartbeat != null &&
                        heartbeat.RunActive &&
                        string.Equals(heartbeat.Phase, "Formal", StringComparison.OrdinalIgnoreCase) &&
                        (heartbeat.TimerCount > 0 || heartbeat.RunnerCount > 0 ||
                         heartbeat.EnergizedChannelCount > 0) &&
                        ElapsedSeconds(Interlocked.Read(ref _lastFormalProgressTimestamp)) >=
                        WatchdogTakeoverPolicy.SelectFormalProgressTimeoutSeconds(
                            heartbeat.ExpectedCyclePeriodMs);
                    var shouldTakeover = WatchdogTakeoverPolicy.ShouldTakeover(
                        IsSessionRevoked(),
                        _journal.ManualStopRequested,
                        Interlocked.CompareExchange(ref _takeoverStarted, 0, 0) != 0,
                        processAlive,
                        heartbeatAge,
                        heartbeat?.RecoveryActive == true,
                        heartbeat?.OrphanPaused == true,
                        heartbeat?.PowerDisablePending == true,
                        stageAgeSeconds,
                        eligibleChannels.Length > 0,
                        heartbeat?.StopAllActive == true,
                        logicalResidue,
                        inconsistentRecovery,
                        formalProgressStalled);
                    if (shouldTakeover)
                    {
                        var reason = !processAlive || heartbeatAge >= 5
                            ? (heartbeatAge >= 5 ? "HeartbeatUnresponsive" : "ProcessExitedUnexpectedly")
                            : heartbeat?.StopAllActive == true && stageAgeSeconds >= 5
                                ? "StopStageNoProgress5s"
                                : heartbeat?.PowerDisablePending == true && stageAgeSeconds >= 5
                                ? "PowerDisablePendingTimeout"
                                : heartbeat?.OrphanPaused == true && stageAgeSeconds >= 5
                                    ? "OrphanPausedTimeout"
                                    : formalProgressStalled
                                        ? "FormalProgressStalled"
                                    : "ExternalRecoveryStageStalled";
                        BeginTakeover(reason);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Record("MonitorError", ex.Message); }
            }
        }

        private void BeginTakeover(string reason)
        {
            if (_journal.ManualStopRequested || IsSessionRevoked() ||
                Interlocked.CompareExchange(ref _takeoverStarted, 1, 0) != 0) return;
            Record("TakeoverRequested", reason);
            Send(WatchdogMessageType.RequestStopAll, reason, Guid.NewGuid().ToString("N"));
            _ = Task.Run(() => TakeoverAsync(reason));
        }

        private void BeginManualStopTakeover(string reason)
        {
            if (!_journal.ManualStopRequested || IsSessionRevoked() ||
                Interlocked.CompareExchange(ref _manualStopTakeoverStarted, 1, 0) != 0)
                return;
            Record("ManualStopTakeoverRequested", reason);
            _ = Task.Run(() => ManualStopTakeoverAsync(reason));
        }

        private async Task ManualStopTakeoverAsync(string reason)
        {
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
                            await MiniDumpCapture.TryCaptureAsync(
                                    process,
                                    _args.JournalDirectory,
                                    _args.SessionId,
                                    TimeSpan.FromSeconds(3),
                                    message => RecordEvent("MiniDump", message))
                                .ConfigureAwait(false);
                            process.Kill();
                            process.WaitForExit(5000);
                            Record("ManualStopOldProcessTerminated", reason);
                        }
                    }
                }
                catch (Exception ex) { Record("ManualStopTerminationFailed", ex.Message); }
            }
            LaunchIdleRestart(reason);
            PublishTerminal("ManualStopIdleRestartLaunched", reason);
            _stop.Cancel();
        }

        private void LaunchIdleRestart(string reason)
        {
            var arguments = string.Format(
                CultureInfo.InvariantCulture,
                "--watchdog-idle-restart {0} --previous-pid {1}",
                Quote(_args.SessionId),
                _journal.CurrentPid);
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = _journal.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(_journal.ExecutablePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false
            });
            if (started == null) throw new InvalidOperationException("Idle restart Process.Start returned null.");
            Record("IdleProcessLaunched", $"PID={started.Id};Reason={reason};AutoResume=false");
        }

        private async Task TakeoverAsync(string reason)
        {
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
            if (_journal.ManualStopRequested || IsSessionRevoked()) return;
            _ = Task.Run(async () =>
            {
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
            if (Interlocked.CompareExchange(ref _relaunchStarted, 1, 0) != 0) return;
            while (!_journal.ManualStopRequested && !IsSessionRevoked() && !_stop.IsCancellationRequested)
            {
                _journal.RecoveryAttempt++;
                var attempt = _journal.RecoveryAttempt;
                var delay = attempt == 1 ? 5 : attempt == 2 ? 15 : attempt == 3 ? 30 : 60;
                Record("RecoveryBackoff", $"Attempt={attempt};DelaySeconds={delay};Reason={reason}");
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                if (_journal.ManualStopRequested || IsSessionRevoked()) return;
                try
                {
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
                    var started = Process.Start(new ProcessStartInfo
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
                    _attached = false;
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                    Record("RecoveryProcessLaunched", $"PID={started.Id};Attempt={attempt}");
                    var attachDeadline = DateTime.UtcNow.AddSeconds(20);
                    while (!_attached && !_journal.ManualStopRequested && !IsSessionRevoked() &&
                           DateTime.UtcNow < attachDeadline && !started.HasExited)
                        await Task.Delay(250).ConfigureAwait(false);
                    if (_attached)
                    {
                        Interlocked.Exchange(ref _takeoverStarted, 0);
                        Interlocked.Exchange(ref _relaunchStarted, 0);
                        return;
                    }
                    try { if (!started.HasExited) started.Kill(); } catch { }
                    Record("RecoveryAttachFailed", $"Attempt={attempt}");
                }
                catch (Exception ex) { Record("RecoveryLaunchFailed", ex.Message); }
            }
            Interlocked.Exchange(ref _relaunchStarted, 0);
        }

        private bool IsCurrentProcessAlive()
        {
            try { using (var process = Process.GetProcessById(_journal.CurrentPid)) return MatchesCurrentProcess(process) && !process.HasExited; }
            catch { return false; }
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
            try { _journalStore.Flush(TimeSpan.FromSeconds(2)); } catch { }
            try { _journalStore.Dispose(); } catch { }
            try { _stop.Dispose(); } catch { }
        }
    }
}
