using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal sealed class WatchdogRecoveryIntent
    {
        public string SessionId { get; set; }
        public string PipeName { get; set; }
        public int PreviousPid { get; set; }
        public int RecoveryAttempt { get; set; }
        public int[] ExcludedChannels { get; set; } = Array.Empty<int>();

        public static WatchdogRecoveryIntent Parse(string[] args)
        {
            string Read(string name)
            {
                for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return string.Empty;
            }
            var session = Read("--watchdog-recover");
            if (string.IsNullOrWhiteSpace(session)) return null;
            int.TryParse(Read("--previous-pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var previousPid);
            int.TryParse(Read("--recovery-attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt);
            var pipe = Read("--watchdog-pipe");
            if (string.IsNullOrWhiteSpace(pipe)) return null;
            return new WatchdogRecoveryIntent
            {
                SessionId = session,
                PipeName = pipe,
                PreviousPid = previousPid,
                RecoveryAttempt = Math.Max(1, attempt),
                ExcludedChannels = (Read("--exclude-channels") ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
                        ? channel
                        : 0)
                    .Where(channel => channel >= 1 && channel <= 12)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray()
            };
        }
    }

    internal sealed class WatchdogAttachResult
    {
        public bool Attached { get; set; }
        public string Warning { get; set; }
        public string SessionId { get; set; }
    }

    internal static class WatchdogRuntime
    {
        private static readonly object Gate = new object();
        private static NamedPipeClientStream _pipe;
        private static StreamReader _reader;
        private static StreamWriter _writer;
        private static CancellationTokenSource _lifetime;
        private static TaskCompletionSource<bool> _attached;
        private static string _sessionId;
        private static string _pipeName;
        private static long _heartbeatSequence;
        private static Func<WatchdogHeartbeat> _heartbeatProvider;
        private static Process _watchdogProcess;
        private static string _watchdogExecutable;
        private static string _mainExecutable;
        private static int[] _selectedChannels = Array.Empty<int>();
        private static bool _recoveryProcess;
        private static int _recoveryAttempt;
        private static long _lastHeartbeatAckUtcTicks;
        private static long _lastHeartbeatAckSequence;
        private static int _transportMonitorStarted;
        private static long _transportMonitorGeneration;
        private static int _transportLostReported;
        private static int _sendFailureReported;
        private static int _reconnectAttempt;
        private static int _sessionClosing;
        private static string _journalExportDirectory;

        internal static event Action<string, string> StopAllRequested;
        internal static event Action<string, string> TransportLost;
        internal static event Action<string, string> TransportError;
        internal static bool IsAttached { get { lock (Gate) return _pipe?.IsConnected == true; } }
        internal static string SessionId { get { lock (Gate) return _sessionId; } }

        internal static void ConfigureJournalExportPath(string directory)
        {
            lock (Gate)
            {
                _journalExportDirectory = string.IsNullOrWhiteSpace(directory)
                    ? null
                    : Path.GetFullPath(directory);
            }
        }

        internal static async Task<WatchdogAttachResult> StartSessionAsync(int[] selectedChannels)
        {
            ShutdownLocalClient();
            lock (Gate)
            {
                _sessionClosing = 0;
                _transportLostReported = 0;
                _sendFailureReported = 0;
                _reconnectAttempt = 0;
                _transportMonitorStarted = 0;
                _selectedChannels = (selectedChannels ?? Array.Empty<int>()).ToArray();
                _recoveryProcess = false;
                _recoveryAttempt = 0;
            }
            var sessionId = Guid.NewGuid().ToString("N");
            var pipeName = "MTTFTest.Watchdog." + sessionId;
            var executable = Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetEntryAssembly()?.Location;
            var watchdog = Path.Combine(Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory, "MTTFTest.Watchdog.exe");
            lock (Gate) { _mainExecutable = executable; _watchdogExecutable = watchdog; }
            if (!File.Exists(watchdog))
                return new WatchdogAttachResult { Warning = "未找到 MTTFTest.Watchdog.exe，本轮仅使用进程内恢复。" };

            Exception lastError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var current = Process.GetCurrentProcess())
                    {
                        var sidecar = Process.Start(new ProcessStartInfo
                        {
                            FileName = watchdog,
                            Arguments = string.Format(CultureInfo.InvariantCulture,
                                "--parent-pid {0} --parent-start-ticks {1} --session {2} --pipe {3} --executable {4}",
                                current.Id, current.StartTime.ToUniversalTime().Ticks,
                                Quote(sessionId), Quote(pipeName), Quote(executable)),
                            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        lock (Gate) _watchdogProcess = sidecar;
                    }
                    await ConnectAsync(sessionId, pipeName, selectedChannels, false, 0).ConfigureAwait(false);
                    return new WatchdogAttachResult { Attached = true, SessionId = sessionId };
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    CloseFailedStartupSidecar();
                    lock (Gate)
                    {
                        _sessionClosing = 0;
                        _transportLostReported = 0;
                        _reconnectAttempt = 0;
                        _transportMonitorStarted = 0;
                    }
                    if (attempt == 0) await Task.Delay(500).ConfigureAwait(false);
                }
            }
            return new WatchdogAttachResult
            {
                Warning = "独立看门狗启动或握手失败，本轮仅使用进程内恢复：" + lastError?.GetBaseException().Message
            };
        }

        private static void CloseFailedStartupSidecar()
        {
            Process sidecar;
            lock (Gate) sidecar = _watchdogProcess;
            try
            {
                if (sidecar != null && !sidecar.HasExited)
                {
                    sidecar.Kill();
                    sidecar.WaitForExit(2000);
                }
            }
            catch { }
            lock (Gate)
            {
                try { _lifetime?.Cancel(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                try { _reader?.Dispose(); } catch { }
                try { _writer?.Dispose(); } catch { }
                _pipe = null; _reader = null; _writer = null; _lifetime = null; _attached = null;
                _watchdogProcess = null;
            }
        }

        internal static Task AttachRecoverySessionAsync(WatchdogRecoveryIntent intent, int[] selectedChannels)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            ShutdownLocalClient();
            lock (Gate)
            {
                _sessionClosing = 0;
                _transportLostReported = 0;
                _sendFailureReported = 0;
                _reconnectAttempt = 0;
                _transportMonitorStarted = 0;
                _selectedChannels = (selectedChannels ?? Array.Empty<int>()).ToArray();
                _recoveryProcess = true;
                _recoveryAttempt = intent.RecoveryAttempt;
                _mainExecutable = Process.GetCurrentProcess().MainModule?.FileName ??
                                  Assembly.GetEntryAssembly()?.Location;
                _watchdogExecutable = Path.Combine(
                    Path.GetDirectoryName(_mainExecutable) ?? Environment.CurrentDirectory,
                    "MTTFTest.Watchdog.exe");
            }
            return ConnectAsync(intent.SessionId, intent.PipeName, selectedChannels, true, intent.RecoveryAttempt);
        }

        private static async Task ConnectAsync(
            string sessionId,
            string pipeName,
            int[] selectedChannels,
            bool recoveryProcess,
            int recoveryAttempt)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.Run(() => pipe.Connect(5000)).ConfigureAwait(false);
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            var lifetime = new CancellationTokenSource();
            var attached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Gate)
            {
                _pipe = pipe; _reader = reader; _writer = writer; _lifetime = lifetime;
                _attached = attached; _sessionId = sessionId; _pipeName = pipeName;
                _lastHeartbeatAckUtcTicks = DateTime.UtcNow.Ticks;
                _lastHeartbeatAckSequence = 0;
                _sendFailureReported = 0;
            }
            _ = Task.Run(() => ReaderLoopAsync(lifetime.Token, reader, sessionId));
            if (!Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.Attach,
                SessionId = sessionId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Session = CreateRunSession(selectedChannels, recoveryProcess, recoveryAttempt)
            }))
                throw new IOException("Watchdog Attach 消息发送失败。");
            var timeout = Task.Delay(5000);
            if (await Task.WhenAny(attached.Task, timeout).ConfigureAwait(false) != attached.Task || !attached.Task.Result)
                throw new TimeoutException("Watchdog Attached 握手超时。");
            _ = Task.Run(() => HeartbeatLoopAsync(lifetime.Token));
            StartTransportMonitor(lifetime.Token);
        }

        private static WatchdogRunSession CreateRunSession(int[] selectedChannels, bool recovery, int attempt)
        {
            using (var process = Process.GetCurrentProcess())
                return new WatchdogRunSession
                {
                    SessionId = _sessionId,
                    PipeName = _pipeName,
                    ExecutablePath = process.MainModule?.FileName ?? Assembly.GetEntryAssembly()?.Location,
                    ProcessId = process.Id,
                    ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    RecoveryProcess = recovery,
                    RecoveryAttempt = attempt,
                    SelectedChannels = selectedChannels ?? Array.Empty<int>()
                };
        }

        internal static void SetHeartbeatProvider(Func<WatchdogHeartbeat> provider)
        {
            lock (Gate) _heartbeatProvider = provider;
        }

        private static async Task HeartbeatLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    WatchdogHeartbeat heartbeat = null;
                    Func<WatchdogHeartbeat> provider;
                    string session;
                    lock (Gate) { provider = _heartbeatProvider; session = _sessionId; }
                    Exception providerError = null;
                    try { heartbeat = provider?.Invoke(); }
                    catch (Exception ex) { providerError = ex; }
                    heartbeat ??= new WatchdogHeartbeat();
                    if (providerError != null)
                    {
                        heartbeat.RecoveryCode = "HeartbeatProviderFault";
                        heartbeat.RecoveryContext = providerError.GetBaseException().Message;
                        RaiseTransportError("HeartbeatProvider", providerError);
                    }
                    using (var process = Process.GetCurrentProcess())
                    {
                        heartbeat.Sequence = Interlocked.Increment(ref _heartbeatSequence);
                        heartbeat.SessionId = session;
                        heartbeat.ProcessId = process.Id;
                        heartbeat.ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    if (!Send(new WatchdogMessage { Type = WatchdogMessageType.Heartbeat, SessionId = session, Heartbeat = heartbeat }))
                        RaiseTransportLost("HeartbeatSendFailed", "Heartbeat未能发送。");
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    RaiseTransportError("HeartbeatLoop", ex);
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }

        private static async Task ReaderLoopAsync(
            CancellationToken token,
            StreamReader reader,
            string sessionId)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    var message = WatchdogProtocol.Deserialize(line);
                    if (message == null || !string.Equals(message.SessionId, sessionId, StringComparison.Ordinal)) continue;
                    if (message.Type == WatchdogMessageType.Attached) _attached?.TrySetResult(true);
                    else if (message.Type == WatchdogMessageType.HeartbeatAck)
                    {
                        if (message.AckSequence >= Interlocked.Read(ref _lastHeartbeatAckSequence))
                        {
                            Interlocked.Exchange(ref _lastHeartbeatAckSequence, message.AckSequence);
                            Interlocked.Exchange(ref _lastHeartbeatAckUtcTicks, DateTime.UtcNow.Ticks);
                        }
                    }
                    else if (message.Type == WatchdogMessageType.Ping)
                        Send(new WatchdogMessage { Type = WatchdogMessageType.Pong, SessionId = sessionId, Reason = "Alive" });
                    else if (message.Type == WatchdogMessageType.RequestStopAll)
                    {
                        try { StopAllRequested?.Invoke(message.Reason, message.CorrelationId); }
                        catch (Exception ex) { RaiseTransportError("StopAllRequestedHandler", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                _attached?.TrySetException(ex);
                RaiseTransportError("ReaderLoop", ex);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                RaiseTransportError("ReaderLoopClosed", string.Empty);
            }
        }

        internal static void RequestExternalRecovery(string reason) => SendSimple(WatchdogMessageType.ExternalRecoveryRequired, reason);
        internal static void NotifyManualStop(string reason)
        {
            Volatile.Write(ref _sessionClosing, 1);
            WriteSessionRevocationMarker(reason);
            SendSimple(WatchdogMessageType.ManualStopRequested, reason);
            ExportJournalSnapshot();
        }
        internal static void NotifyRunStopped(WatchdogStopSummary summary)
        {
            Volatile.Write(ref _sessionClosing, 1);
            WriteSessionRevocationMarker("RunStopped");
            Send(new WatchdogMessage { Type = WatchdogMessageType.RunStopped, SessionId = SessionId, StopSummary = summary });
            ExportJournalSnapshot();
        }
        internal static void NotifyStopCompleted(WatchdogStopSummary summary, string reason)
        {
            Send(new WatchdogMessage { Type = WatchdogMessageType.StopCompleted, SessionId = SessionId, Reason = reason, StopSummary = summary });
        }
        internal static void NotifyRunCompleted()
        {
            Volatile.Write(ref _sessionClosing, 1);
            WriteSessionRevocationMarker("FormalRunCompleted");
            SendSimple(WatchdogMessageType.RunCompleted, "FormalRunCompleted");
            ExportJournalSnapshot();
        }
        internal static void NotifyApplicationClosing()
        {
            Volatile.Write(ref _sessionClosing, 1);
            WriteSessionRevocationMarker("ApplicationClosing");
            SendSimple(WatchdogMessageType.ApplicationClosing, "ApplicationClosing");
            ExportJournalSnapshot();
        }

        private static async Task TransportMonitorAsync(CancellationToken token)
        {
            var generation = Interlocked.Read(ref _transportMonitorGeneration);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    if (Volatile.Read(ref _sessionClosing) != 0) continue;

                    var ackAgeTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastHeartbeatAckUtcTicks);
                    var ackAge = TimeSpan.FromTicks(Math.Max(0, ackAgeTicks));
                    var sidecarExited = false;
                    lock (Gate)
                    {
                        try { sidecarExited = _watchdogProcess != null && _watchdogProcess.HasExited; }
                        catch { sidecarExited = true; }
                    }
                    if (ackAge < TimeSpan.FromSeconds(3) && !sidecarExited) continue;

                    if (Interlocked.CompareExchange(ref _transportLostReported, 1, 0) == 0)
                    {
                        RaiseTransportLost(
                            sidecarExited ? "WatchdogProcessExited" : "WatchdogHeartbeatAckTimeout",
                            $"AckSequence={Interlocked.Read(ref _lastHeartbeatAckSequence)};AckAgeMs={ackAge.TotalMilliseconds:F0}");
                    }

                    if (Interlocked.CompareExchange(ref _reconnectAttempt, 1, 0) == 0)
                        await ReconnectOnceAsync(token).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { RaiseTransportError("TransportMonitor", ex); }
            }
            if (Interlocked.Read(ref _transportMonitorGeneration) == generation)
                Interlocked.Exchange(ref _transportMonitorStarted, 0);
        }

        private static async Task ReconnectOnceAsync(CancellationToken token)
        {
            string session;
            string pipeName;
            string executable;
            string watchdog;
            bool recovery;
            int attempt;
            int[] channels;
            lock (Gate)
            {
                session = _sessionId;
                pipeName = _pipeName;
                executable = _mainExecutable;
                watchdog = _watchdogExecutable;
                recovery = _recoveryProcess;
                attempt = _recoveryAttempt;
                channels = _selectedChannels?.ToArray() ?? Array.Empty<int>();
            }
            if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(pipeName) ||
                Volatile.Read(ref _sessionClosing) != 0)
                return;

            Process sidecar = null;
            try
            {
                CloseTransportOnly();
                lock (Gate)
                {
                    try { sidecar = _watchdogProcess; }
                    catch { sidecar = null; }
                }
                var sidecarAlive = false;
                try { sidecarAlive = sidecar != null && !sidecar.HasExited; } catch { }
                if (!sidecarAlive)
                {
                    if (!File.Exists(watchdog))
                        throw new FileNotFoundException("未找到独立看门狗程序。", watchdog);
                    sidecar = Process.Start(new ProcessStartInfo
                    {
                        FileName = watchdog,
                        Arguments = string.Format(CultureInfo.InvariantCulture,
                            "--parent-pid {0} --parent-start-ticks {1} --session {2} --pipe {3} --executable {4}",
                            Process.GetCurrentProcess().Id,
                            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                            Quote(session), Quote(pipeName), Quote(executable)),
                        WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    lock (Gate) _watchdogProcess = sidecar;
                }

                // The previous monitor owns the canceled transport token.  Let
                // the new pipe install a fresh monitor even while the old one
                // is unwinding its finally block.
                Interlocked.Exchange(ref _transportMonitorStarted, 0);
                await ConnectAsync(session, pipeName, channels, recovery, attempt).ConfigureAwait(false);
                lock (Gate) { _transportLostReported = 0; }
                RaiseTransportError("WatchdogReconnected", "同一Session重连成功。");
            }
            catch (Exception ex)
            {
                RaiseTransportError("WatchdogReconnectFailed", ex);
                // Do not loop here.  The sidecar owns the 3/5 second parent
                // liveness decision and will perform takeover if this process
                // can no longer publish heartbeats.
            }
        }

        private static void RaiseTransportLost(string reason, string detail)
        {
            try { TransportLost?.Invoke(reason, detail); }
            catch { }
        }

        private static void StartTransportMonitor(CancellationToken token)
        {
            Interlocked.Increment(ref _transportMonitorGeneration);
            if (Interlocked.CompareExchange(ref _transportMonitorStarted, 1, 0) == 0)
                _ = Task.Run(() => TransportMonitorAsync(token));
        }

        private static void RaiseTransportError(string reason, Exception error)
        {
            var detail = error?.GetBaseException().Message ?? string.Empty;
            try { TransportError?.Invoke(reason, detail); }
            catch { }
        }

        private static void RaiseTransportError(string reason, string detail)
        {
            try { TransportError?.Invoke(reason, detail ?? string.Empty); }
            catch { }
        }

        private static void CloseTransportOnly()
        {
            lock (Gate)
            {
                try { _lifetime?.Cancel(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _pipe = null; _reader = null; _writer = null; _lifetime = null; _attached = null;
            }
        }

        private static void WriteSessionRevocationMarker(string reason)
        {
            string session;
            lock (Gate) session = _sessionId;
            if (string.IsNullOrWhiteSpace(session)) return;
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MTTFTest", "Watchdog");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "session-" + SafeName(session) + ".revoked");
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + (reason ?? string.Empty),
                    new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            catch (Exception ex) { RaiseTransportError("SessionRevocationMarker", ex); }
        }

        private static string SafeName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars()) value = (value ?? string.Empty).Replace(invalid, '_');
            return value;
        }
        private static void SendSimple(string type, string reason) => Send(new WatchdogMessage { Type = type, SessionId = SessionId, Reason = reason, CorrelationId = Guid.NewGuid().ToString("N") });

        private static bool Send(WatchdogMessage message)
        {
            Exception error = null;
            StreamWriter writer;
            lock (Gate) writer = _writer;
            if (writer == null)
            {
                if (Volatile.Read(ref _sessionClosing) == 0 &&
                    Interlocked.CompareExchange(ref _sendFailureReported, 1, 0) == 0)
                    RaiseTransportError("SendNoWriter", message?.Type ?? "Unknown");
                return false;
            }
            try
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(_writer, writer)) return false;
                    writer.WriteLine(WatchdogProtocol.Serialize(message));
                }
                Interlocked.Exchange(ref _sendFailureReported, 0);
                return true;
            }
            catch (Exception ex) { error = ex; }
            if (Interlocked.CompareExchange(ref _sendFailureReported, 1, 0) == 0)
                RaiseTransportError("Send", error);
            return false;
        }

        internal static void ShutdownLocalClient()
        {
            Volatile.Write(ref _sessionClosing, 1);
            lock (Gate)
            {
                try { _lifetime?.Cancel(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _pipe = null; _reader = null; _writer = null; _lifetime = null; _attached = null;
                _sessionId = null; _pipeName = null; _heartbeatProvider = null;
                try { _watchdogProcess?.Dispose(); } catch { }
                _watchdogProcess = null;
            }
        }

        private static void ExportJournalSnapshot()
        {
            string session;
            string exportDirectory;
            lock (Gate)
            {
                session = _sessionId;
                exportDirectory = _journalExportDirectory;
            }
            if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(exportDirectory)) return;
            try
            {
                var localDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MTTFTest", "Watchdog");
                Directory.CreateDirectory(exportDirectory);
                foreach (var suffix in new[] { ".json", ".revoked" })
                {
                    var source = Path.Combine(localDirectory, "session-" + SafeName(session) + suffix);
                    if (!File.Exists(source)) continue;
                    var destination = Path.Combine(exportDirectory, "session-" + SafeName(session) + suffix);
                    File.Copy(source, destination, true);
                }
            }
            catch (Exception ex) { RaiseTransportError("JournalExport", ex); }
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
