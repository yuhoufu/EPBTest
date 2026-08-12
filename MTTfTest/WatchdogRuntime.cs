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

        internal static event Action<string, string> StopAllRequested;
        internal static bool IsAttached { get { lock (Gate) return _pipe?.IsConnected == true; } }
        internal static string SessionId { get { lock (Gate) return _sessionId; } }

        internal static async Task<WatchdogAttachResult> StartSessionAsync(int[] selectedChannels)
        {
            ShutdownLocalClient();
            var sessionId = Guid.NewGuid().ToString("N");
            var pipeName = "MTTFTest.Watchdog." + sessionId;
            var executable = Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetEntryAssembly()?.Location;
            var watchdog = Path.Combine(Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory, "MTTFTest.Watchdog.exe");
            if (!File.Exists(watchdog))
                return new WatchdogAttachResult { Warning = "未找到 MTTFTest.Watchdog.exe，本轮仅使用进程内恢复。" };

            Exception lastError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var current = Process.GetCurrentProcess())
                    {
                        Process.Start(new ProcessStartInfo
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
                    }
                    await ConnectAsync(sessionId, pipeName, selectedChannels, false, 0).ConfigureAwait(false);
                    return new WatchdogAttachResult { Attached = true, SessionId = sessionId };
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    ShutdownLocalClient();
                    if (attempt == 0) await Task.Delay(500).ConfigureAwait(false);
                }
            }
            return new WatchdogAttachResult
            {
                Warning = "独立看门狗启动或握手失败，本轮仅使用进程内恢复：" + lastError?.GetBaseException().Message
            };
        }

        internal static Task AttachRecoverySessionAsync(WatchdogRecoveryIntent intent, int[] selectedChannels)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            ShutdownLocalClient();
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
            }
            _ = Task.Run(() => ReaderLoopAsync(lifetime.Token));
            Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.Attach,
                SessionId = sessionId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                Session = CreateRunSession(selectedChannels, recoveryProcess, recoveryAttempt)
            });
            var timeout = Task.Delay(5000);
            if (await Task.WhenAny(attached.Task, timeout).ConfigureAwait(false) != attached.Task || !attached.Task.Result)
                throw new TimeoutException("Watchdog Attached 握手超时。");
            _ = Task.Run(() => HeartbeatLoopAsync(lifetime.Token));
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
                    try { heartbeat = provider?.Invoke(); } catch { }
                    heartbeat ??= new WatchdogHeartbeat();
                    using (var process = Process.GetCurrentProcess())
                    {
                        heartbeat.Sequence = Interlocked.Increment(ref _heartbeatSequence);
                        heartbeat.SessionId = session;
                        heartbeat.ProcessId = process.Id;
                        heartbeat.ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    Send(new WatchdogMessage { Type = WatchdogMessageType.Heartbeat, SessionId = session, Heartbeat = heartbeat });
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(250).ConfigureAwait(false); }
            }
        }

        private static async Task ReaderLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    StreamReader reader;
                    lock (Gate) reader = _reader;
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    var message = WatchdogProtocol.Deserialize(line);
                    if (message == null || !string.Equals(message.SessionId, SessionId, StringComparison.Ordinal)) continue;
                    if (message.Type == WatchdogMessageType.Attached) _attached?.TrySetResult(true);
                    else if (message.Type == WatchdogMessageType.Ping)
                        Send(new WatchdogMessage { Type = WatchdogMessageType.Pong, SessionId = SessionId, Reason = "Alive" });
                    else if (message.Type == WatchdogMessageType.RequestStopAll)
                        StopAllRequested?.Invoke(message.Reason, message.CorrelationId);
                }
            }
            catch (Exception ex) { _attached?.TrySetException(ex); }
        }

        internal static void RequestExternalRecovery(string reason) => SendSimple(WatchdogMessageType.ExternalRecoveryRequired, reason);
        internal static void NotifyManualStop(string reason) => SendSimple(WatchdogMessageType.ManualStopRequested, reason);
        internal static void NotifyRunStopped(WatchdogStopSummary summary) => Send(new WatchdogMessage { Type = WatchdogMessageType.RunStopped, SessionId = SessionId, StopSummary = summary });
        internal static void NotifyStopCompleted(WatchdogStopSummary summary, string reason) => Send(new WatchdogMessage { Type = WatchdogMessageType.StopCompleted, SessionId = SessionId, Reason = reason, StopSummary = summary });
        internal static void NotifyRunCompleted() => SendSimple(WatchdogMessageType.RunCompleted, "FormalRunCompleted");
        internal static void NotifyApplicationClosing() => SendSimple(WatchdogMessageType.ApplicationClosing, "ApplicationClosing");
        private static void SendSimple(string type, string reason) => Send(new WatchdogMessage { Type = type, SessionId = SessionId, Reason = reason, CorrelationId = Guid.NewGuid().ToString("N") });

        private static void Send(WatchdogMessage message)
        {
            lock (Gate)
            {
                if (_writer == null) return;
                try { _writer.WriteLine(WatchdogProtocol.Serialize(message)); } catch { }
            }
        }

        internal static void ShutdownLocalClient()
        {
            lock (Gate)
            {
                try { _lifetime?.Cancel(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _pipe = null; _reader = null; _writer = null; _lifetime = null; _attached = null;
                _sessionId = null; _pipeName = null; _heartbeatProvider = null;
            }
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
