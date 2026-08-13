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
        public WatchdogHeartbeat LastHeartbeat { get; set; }
    }

    internal sealed class WatchdogArguments
    {
        public int ParentPid;
        public long ParentStartTicks;
        public string SessionId;
        public string PipeName;
        public string ExecutablePath;

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
                ExecutablePath = Read("--executable")
            };
            if (pid <= 0 || ticks <= 0 || string.IsNullOrWhiteSpace(result.SessionId) ||
                string.IsNullOrWhiteSpace(result.PipeName) || string.IsNullOrWhiteSpace(result.ExecutablePath))
                throw new ArgumentException("Watchdog startup arguments are incomplete.");
            result.ExecutablePath = Path.GetFullPath(result.ExecutablePath);
            return result;
        }
    }

    internal sealed class WatchdogHost
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private readonly WatchdogArguments _args;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _journalGate = new object();
        private StreamWriter _writer;
        private WatchdogJournal _journal;
        private long _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
        private long _lastProgressTimestamp = Stopwatch.GetTimestamp();
        private long _lastProgressVersion;
        private long _lastHeartbeatSequence;
        private long _lastHeartbeatAckSequence;
        private int _takeoverStarted;
        private int _relaunchStarted;
        private bool _attached;

        private WatchdogHost(WatchdogArguments args)
        {
            _args = args;
            _journal = new WatchdogJournal
            {
                SessionId = args.SessionId,
                ExecutablePath = args.ExecutablePath,
                PipeName = args.PipeName,
                CurrentPid = args.ParentPid,
                CurrentProcessStartUtcTicks = args.ParentStartTicks,
                State = "Starting",
                UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            SaveJournal();
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
                try { return new WatchdogHost(args).RunAsync().GetAwaiter().GetResult(); }
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
                        if (message == null || message.ProtocolVersion != WatchdogProtocol.Version ||
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
                    _journal.OrphanPauseTriggered = false;
                    _journal.PowerDisableTriggered = false;
                    Interlocked.Exchange(ref _lastHeartbeatTimestamp, Stopwatch.GetTimestamp());
                    Record("Attached", message.Session?.RecoveryProcess == true ? "RecoveryProcess" : "MainProcess");
                    Send(WatchdogMessageType.Attached, "Attached", message.CorrelationId);
                    break;
                case WatchdogMessageType.Heartbeat:
                    if (message.Heartbeat == null) break;
                    _attached = true;
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
                    SaveJournal();
                    _lastHeartbeatAckSequence = message.Heartbeat.Sequence;
                    _journal.LastHeartbeatAckSequence = _lastHeartbeatAckSequence;
                    Send(new WatchdogMessage
                    {
                        Type = WatchdogMessageType.HeartbeatAck,
                        SessionId = _args.SessionId,
                        AckSequence = message.Heartbeat.Sequence,
                        CorrelationId = message.CorrelationId
                    });
                    if (_journal.ManualStopRequested && !message.Heartbeat.RunActive)
                        _stop.Cancel();
                    break;
                case WatchdogMessageType.Pong:
                    break;
                case WatchdogMessageType.ExternalRecoveryRequired:
                    BeginTakeover("ExternalRecoveryRequired:" + message.Reason);
                    break;
                case WatchdogMessageType.StopCompleted:
                    Record("StopCompleted", message.StopSummary?.Detail ?? message.Reason);
                    BeginRelaunchAfterExit();
                    break;
                case WatchdogMessageType.ManualStopRequested:
                    _journal.ManualStopRequested = true;
                    Record("ManualStopRequested", message.Reason);
                    // 人工停止已由主程序先写入跨进程撤权标记。Watchdog 此时的
                    // 唯一职责是永久放弃本 Session 的 Kill/重启资格，不应再等待
                    // StopAll 的持久化或逻辑收口；否则 StopAll 自身卡住会遗留一个
                    // 没有任何作用的后台 sidecar。主程序继续独立执行幂等 StopAll。
                    _stop.Cancel();
                    break;
                case WatchdogMessageType.RunStopped:
                case WatchdogMessageType.RunCompleted:
                case WatchdogMessageType.ApplicationClosing:
                case WatchdogMessageType.ShutdownExpected:
                    _journal.ManualStopRequested = true;
                    Record(message.Type, message.Reason);
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
                    if (!_attached || _journal.ManualStopRequested) continue;
                    if (IsSessionRevoked())
                    {
                        _journal.ManualStopRequested = true;
                        Record("SessionRevoked", "RevocationMarker");
                        _stop.Cancel();
                        continue;
                    }
                    var heartbeatAge = ElapsedSeconds(Interlocked.Read(ref _lastHeartbeatTimestamp));
                    if (heartbeatAge >= 3 && heartbeatAge < 5)
                        Send(WatchdogMessageType.Ping, "HeartbeatSuspect", null);
                    var heartbeat = _journal.LastHeartbeat;
                    var eligibleChannels = GetRecoveryEligibleChannels(heartbeat);
                    var processAlive = IsCurrentProcessAlive();
                    var stageSinceUtc = heartbeat?.PowerDisablePending == true && heartbeat.PowerDisableSince > 0
                        ? heartbeat.PowerDisableSince
                        : heartbeat?.PauseSince ?? 0;
                    var stageAgeSeconds = stageSinceUtc > 0
                        ? Math.Max(0, (DateTime.UtcNow.Ticks - stageSinceUtc) / (double)TimeSpan.TicksPerSecond)
                        : ElapsedSeconds(Interlocked.Read(ref _lastProgressTimestamp));
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
                        eligibleChannels.Length > 0);
                    if (shouldTakeover)
                    {
                        var reason = !processAlive || heartbeatAge >= 5
                            ? (heartbeatAge >= 5 ? "HeartbeatUnresponsive" : "ProcessExitedUnexpectedly")
                            : heartbeat?.PowerDisablePending == true && stageAgeSeconds >= 5
                                ? "PowerDisablePendingTimeout"
                                : heartbeat?.OrphanPaused == true && stageAgeSeconds >= 5
                                    ? "OrphanPausedTimeout"
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
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MTTFTest", "Watchdog");
                return File.Exists(Path.Combine(directory, "session-" + SafeName(_args.SessionId) + ".revoked"));
            }
            catch { return false; }
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
            _journal.State = state;
            _journal.LastReason = reason ?? string.Empty;
            SaveJournal();
        }

        private void SaveJournal()
        {
            try
            {
                lock (_journalGate)
                    _journal.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTTFTest", "Watchdog");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "session-" + SafeName(_args.SessionId) + ".json");
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                string content;
                lock (_journalGate) content = Json.Serialize(_journal);
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            catch (Exception ex) { WriteEmergencyLog("JournalSaveFailed: " + ex); }
        }

        private static double ElapsedSeconds(long since) => (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;
        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        private static string SafeName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars()) value = (value ?? string.Empty).Replace(invalid, '_');
            return value;
        }

        internal static void WriteEmergencyLog(string message)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTTFTest", "Watchdog");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "watchdog-error.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
