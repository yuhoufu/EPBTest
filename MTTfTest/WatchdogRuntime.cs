using System;
using System.Collections.Generic;
using System.Configuration;
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
        public bool StartIdle { get; set; }

        public static WatchdogRecoveryIntent Parse(string[] args)
        {
            string Read(string name)
            {
                for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return string.Empty;
            }
            var idleSession = Read("--watchdog-idle-restart");
            var session = string.IsNullOrWhiteSpace(idleSession)
                ? Read("--watchdog-recover")
                : idleSession;
            if (string.IsNullOrWhiteSpace(session)) return null;
            int.TryParse(Read("--previous-pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var previousPid);
            int.TryParse(Read("--recovery-attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt);
            var pipe = Read("--watchdog-pipe");
            var startIdle = !string.IsNullOrWhiteSpace(idleSession);
            if (!startIdle && string.IsNullOrWhiteSpace(pipe)) return null;
            return new WatchdogRecoveryIntent
            {
                SessionId = session,
                PipeName = pipe,
                PreviousPid = previousPid,
                RecoveryAttempt = Math.Max(1, attempt),
                StartIdle = startIdle,
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
        public string JournalPolicyLog { get; set; }
    }

    internal static class WatchdogRuntime
    {
        private static readonly object Gate = new object();
        private static readonly object HeartbeatCaptureGate = new object();
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
        private static WatchdogJournalPolicy _journalPolicy = new WatchdogJournalPolicy();
        private static WatchdogJournalStore _clientJournal;
        private static long _clientEventSequence;
        private static string _activeTakeoverCorrelationId;
        private static string _activeTakeoverReason;

        internal static event Action<string, string> StopAllRequested;
        internal static event Action<string, string> TransportLost;
        internal static event Action<string, string> TransportError;
        internal static bool IsAttached { get { lock (Gate) return _pipe?.IsConnected == true; } }
        internal static string SessionId { get { lock (Gate) return _sessionId; } }

        internal static bool IsActiveTakeoverCancellation(Exception exception)
        {
            if (!(exception?.GetBaseException() is OperationCanceledException)) return false;
            lock (Gate)
                return !string.IsNullOrWhiteSpace(_activeTakeoverCorrelationId);
        }

        internal static void ConfigureJournalExportPath(string directory)
        {
            string resolved = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    resolved = WatchdogJournalPaths.ValidateProjectDirectory(directory);
            }
            catch
            {
                lock (Gate) _journalExportDirectory = null;
                throw;
            }
            lock (Gate)
            {
                _journalExportDirectory = resolved;
            }
        }

        internal static async Task<WatchdogAttachResult> StartSessionAsync(int[] selectedChannels)
        {
            ShutdownLocalClient();
            var policyWarnings = new List<string>();
            var policy = WatchdogJournalPolicy.Load(ConfigurationManager.AppSettings, policyWarnings.Add);
            string journalDirectory;
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
                _activeTakeoverCorrelationId = string.Empty;
                _activeTakeoverReason = string.Empty;
                Interlocked.Exchange(ref _clientEventSequence, 0);
                _journalPolicy = policy;
                journalDirectory = _journalExportDirectory;
            }
            if (string.IsNullOrWhiteSpace(journalDirectory))
                return new WatchdogAttachResult
                {
                    Warning = "独立看门狗项目Journal路径缺失或非法；已拒绝启动不可审计的Sidecar，本轮仅使用进程内恢复。"
                };
            var sessionId = Guid.NewGuid().ToString("N");
            var pipeName = "MTTFTest.Watchdog." + sessionId;
            var executable = Process.GetCurrentProcess().MainModule?.FileName ?? Assembly.GetEntryAssembly()?.Location;
            var watchdog = Path.Combine(Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory, "MTTFTest.Watchdog.exe");
            lock (Gate) { _mainExecutable = executable; _watchdogExecutable = watchdog; }
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    var createdJournal = new WatchdogJournalStore(
                        journalDirectory,
                        sessionId,
                        "client",
                        policy,
                        current.Id,
                        current.StartTime.ToUniversalTime().Ticks);
                    lock (Gate) _clientJournal = createdJournal;
                }
                RecordClientEvent("JournalConfigured", policy.ToStartupLogLine());
                foreach (var warning in policyWarnings) RecordClientEvent("PolicyWarning", warning);
                if (!File.Exists(watchdog))
                {
                    _clientJournal.RecordError("未找到 MTTFTest.Watchdog.exe，本轮仅使用进程内恢复。Path=" + watchdog);
                    _clientJournal.Flush(TimeSpan.FromSeconds(1));
                    var missingJournal = _clientJournal;
                    lock (Gate) _clientJournal = null;
                    try { missingJournal.Dispose(); } catch { }
                    return new WatchdogAttachResult
                    {
                        Warning = "未找到 MTTFTest.Watchdog.exe，本轮仅使用进程内恢复。",
                        JournalPolicyLog = policy.ToStartupLogLine()
                    };
                }
            }
            catch (Exception ex)
            {
                return new WatchdogAttachResult
                {
                    Warning = "独立看门狗项目Journal初始化失败；已拒绝启动不可审计的Sidecar，本轮仅使用进程内恢复：" +
                              ex.GetBaseException().Message
                };
            }

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
                            Arguments = BuildSidecarArguments(
                                current.Id,
                                current.StartTime.ToUniversalTime().Ticks,
                                sessionId,
                                pipeName,
                                executable,
                                journalDirectory,
                                policy),
                            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        lock (Gate) _watchdogProcess = sidecar;
                        RecordClientEvent("SidecarLaunched", "PID=" + (sidecar?.Id ?? 0));
                    }
                    await ConnectAsync(sessionId, pipeName, selectedChannels, false, 0).ConfigureAwait(false);
                    return new WatchdogAttachResult
                    {
                        Attached = true,
                        SessionId = sessionId,
                        Warning = policyWarnings.Count == 0 ? null : string.Join(" | ", policyWarnings),
                        JournalPolicyLog = policy.ToStartupLogLine()
                    };
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
            try { _clientJournal?.RecordError("Sidecar startup failed: " + lastError); } catch { }
            try { _clientJournal?.Flush(TimeSpan.FromSeconds(1)); } catch { }
            WatchdogJournalStore failedJournal;
            lock (Gate)
            {
                failedJournal = _clientJournal;
                _clientJournal = null;
            }
            try { failedJournal?.Dispose(); } catch { }
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

        internal static async Task AttachRecoverySessionAsync(WatchdogRecoveryIntent intent, int[] selectedChannels)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            ShutdownLocalClient();
            var warnings = new List<string>();
            var policy = WatchdogJournalPolicy.Load(ConfigurationManager.AppSettings, warnings.Add);
            string journalDirectory;
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
                _activeTakeoverCorrelationId = string.Empty;
                _activeTakeoverReason = string.Empty;
                Interlocked.Exchange(ref _clientEventSequence, 0);
                _mainExecutable = Process.GetCurrentProcess().MainModule?.FileName ??
                                  Assembly.GetEntryAssembly()?.Location;
                _watchdogExecutable = Path.Combine(
                    Path.GetDirectoryName(_mainExecutable) ?? Environment.CurrentDirectory,
                    "MTTFTest.Watchdog.exe");
                _journalPolicy = policy;
                journalDirectory = _journalExportDirectory;
            }
            if (string.IsNullOrWhiteSpace(journalDirectory))
                throw new InvalidOperationException("Watchdog 恢复进程缺少项目Journal路径。");
            using (var process = Process.GetCurrentProcess())
            {
                var createdJournal = new WatchdogJournalStore(
                        journalDirectory,
                        intent.SessionId,
                        "client",
                        policy,
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks);
                lock (Gate) _clientJournal = createdJournal;
            }
            RecordClientEvent("RecoveryClientJournalAttached", policy.ToStartupLogLine());
            foreach (var warning in warnings) RecordClientEvent("PolicyWarning", warning);
            try
            {
                await ConnectAsync(intent.SessionId, intent.PipeName, selectedChannels, true, intent.RecoveryAttempt)
                    .ConfigureAwait(false);
            }
            catch
            {
                ShutdownLocalClient();
                throw;
            }
        }

        internal static async Task NotifyRecoveryCheckpointRejectedAsync(
            WatchdogRecoveryIntent intent,
            string reason)
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
                _selectedChannels = Array.Empty<int>();
                _recoveryProcess = true;
                _recoveryAttempt = intent.RecoveryAttempt;
                _mainExecutable = Process.GetCurrentProcess().MainModule?.FileName ??
                                  Assembly.GetEntryAssembly()?.Location;
                _watchdogExecutable = Path.Combine(
                    Path.GetDirectoryName(_mainExecutable) ?? Environment.CurrentDirectory,
                    "MTTFTest.Watchdog.exe");
            }

            // TryConsume can reject before the project journal directory is
            // available.  The sidecar is still authoritative and must receive
            // an explicit retryable terminal message over the existing pipe.
            await ConnectAsync(
                    intent.SessionId,
                    intent.PipeName,
                    Array.Empty<int>(),
                    true,
                    intent.RecoveryAttempt)
                .ConfigureAwait(false);
            NotifyRecoveryAttemptFailed(
                "CheckpointInvariant",
                true,
                "RecoveryCheckpointRejected:" + (reason ?? "Unknown"),
                reason,
                string.Empty);
            await Task.Delay(100).ConfigureAwait(false);
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
            RecordClientEvent("AttachSent", recoveryProcess ? "RecoveryProcess" : "MainProcess");
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
                    if (!SendHeartbeatSnapshot("Periodic"))
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

        /// <summary>
        ///     周期心跳与 Sidecar 主动刷新共用同一采集/序列化入口。采集串行化可避免
        ///     刷新请求和一秒周期恰好重叠时并发读取控制器集合，返回相互矛盾的快照。
        /// </summary>
        private static bool SendHeartbeatSnapshot(string trigger)
        {
            lock (HeartbeatCaptureGate)
            {
                WatchdogHeartbeat heartbeat = null;
                Func<WatchdogHeartbeat> provider;
                string session;
                lock (Gate)
                {
                    provider = _heartbeatProvider;
                    session = _sessionId;
                }
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
                if (!string.Equals(trigger, "Periodic", StringComparison.Ordinal))
                    RecordClientEvent(
                        "HeartbeatRefreshSent",
                        $"Trigger={trigger};Sequence={heartbeat.Sequence}");
                return Send(new WatchdogMessage
                {
                    Type = WatchdogMessageType.Heartbeat,
                    SessionId = session,
                    Heartbeat = heartbeat
                });
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
                    if (message.Type == WatchdogMessageType.Attached)
                    {
                        _attached?.TrySetResult(true);
                        RecordClientEvent("Attached", "HandshakeCompleted");
                    }
                    else if (message.Type == WatchdogMessageType.HeartbeatAck)
                    {
                        if (message.AckSequence >= Interlocked.Read(ref _lastHeartbeatAckSequence))
                        {
                            Interlocked.Exchange(ref _lastHeartbeatAckSequence, message.AckSequence);
                            Interlocked.Exchange(ref _lastHeartbeatAckUtcTicks, DateTime.UtcNow.Ticks);
                        }
                    }
                    else if (message.Type == WatchdogMessageType.Ping)
                    {
                        // Ping 同时承担“重新采集结构化证据”的请求语义。先回送完整
                        // Heartbeat，再回 Pong；Sidecar 只有在新快照仍保持同一资源
                        // 不变量达到硬截止时才允许接管。
                        SendHeartbeatSnapshot(message.Reason ?? "PingRefresh");
                        Send(new WatchdogMessage
                        {
                            Type = WatchdogMessageType.Pong,
                            SessionId = sessionId,
                            Reason = "Alive"
                        });
                    }
                    else if (message.Type == WatchdogMessageType.RequestStopAll)
                    {
                        lock (Gate)
                        {
                            _activeTakeoverCorrelationId = message.CorrelationId ?? string.Empty;
                            _activeTakeoverReason = message.Reason ?? string.Empty;
                        }
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
        internal static void NotifyMainUiReady(string reason)
        {
            RecordClientEvent("MainUiReady", reason ?? "MainWindowShown");
            SendSimple(WatchdogMessageType.MainUiReady, reason ?? "MainWindowShown");
        }
        internal static void NotifyRecoveryAttemptFailed(string reason)
        {
            var classification = RecoveryFailurePolicy.Classify(null, false, reason);
            NotifyRecoveryAttemptFailed(
                classification.Code,
                classification.Permanent,
                reason,
                reason,
                string.Empty);
        }

        internal static void NotifyRecoveryAttemptFailed(
            string failureCode,
            bool permanent,
            string reason,
            string detail,
            string contextSha256)
        {
            string takeoverCorrelationId;
            string takeoverReason;
            lock (Gate)
            {
                takeoverCorrelationId = _activeTakeoverCorrelationId;
                takeoverReason = _activeTakeoverReason;
            }
            var failureOwner = string.IsNullOrWhiteSpace(takeoverCorrelationId)
                ? string.Empty
                : "WatchdogTakeover";
            if (RecoveryFailurePolicy.IsSupersededByWatchdogTakeover(
                    !string.IsNullOrWhiteSpace(takeoverCorrelationId),
                    takeoverCorrelationId,
                    failureOwner,
                    takeoverCorrelationId,
                    failureCode,
                    reason,
                    detail))
            {
                failureCode = "RecoverySupersededByTakeover";
                permanent = false;
            }
            // 恢复子进程失败不是普通运行终止，不写 revocation marker；是否允许
            // 重试由结构化 failureCode/permanent 和 sidecar 的耐久预算决定。
            Volatile.Write(ref _sessionClosing, 1);
            RecordClientEvent(
                "RecoveryAttemptFailed",
                $"Code={failureCode};Permanent={permanent};ContextSha256={contextSha256};{reason}");
            Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.RecoveryAttemptFailed,
                SessionId = SessionId,
                Reason = reason,
                RecoveryFailureCode = failureCode,
                RecoveryFailurePermanent = permanent,
                RecoveryFailureDetail = detail,
                RecoveryFailureContextSha256 = contextSha256,
                RecoveryFailureOwner = failureOwner,
                RecoveryFailureCorrelationId = takeoverCorrelationId,
                CorrelationId = Guid.NewGuid().ToString("N")
            });
            if (!string.IsNullOrWhiteSpace(takeoverCorrelationId))
                RecordClientEvent(
                    "RecoveryFailureTakeoverContext",
                    $"Owner={failureOwner};CorrelationId={takeoverCorrelationId};Reason={takeoverReason}");
            FlushClientJournal();
        }
        internal static void NotifyRecoveryCheckpointValidated(
            string reason,
            WatchdogCheckpointMirror mirror)
        {
            RecordClientEvent("RecoveryCheckpointValidated", reason);
            Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.RecoveryCheckpointValidated,
                SessionId = SessionId,
                Reason = reason,
                CorrelationId = Guid.NewGuid().ToString("N"),
                CheckpointMirror = mirror
            });
            FlushClientJournal();
        }
        internal static void NotifySafetyPreflightPassed(string reason)
        {
            RecordClientEvent("SafetyPreflightPassed", reason);
            SendSimple(WatchdogMessageType.SafetyPreflightPassed, reason);
            FlushClientJournal();
        }
        internal static void NotifyRecoveryBatchCommitted(string reason, long commitGeneration)
        {
            RecordClientEvent("RecoveryBatchCommitted", reason);
            Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.RecoveryBatchCommitted,
                SessionId = SessionId,
                Reason = reason,
                CorrelationId = Guid.NewGuid().ToString("N"),
                RecoveryCommitGeneration = commitGeneration
            });
            FlushClientJournal();
        }
        internal static void NotifyBatchStartFailed(string reason)
        {
            // 启动预检失败保留原 session/checkpoint；Sidecar 将安全接管并退避重试。
            RecordClientEvent("BatchStartFailed", reason);
            SendSimple(WatchdogMessageType.BatchStartFailed, reason);
            FlushClientJournal();
        }
        internal static void NotifyManualStop(string reason)
        {
            RecordClientEvent("ManualStopIntent", reason);
            SendSimple(WatchdogMessageType.ManualStopIntent, reason);
            FlushClientJournal();
        }
        internal static void NotifyPhysicalStopConfirmed(string reason) =>
            SendSimple(WatchdogMessageType.PhysicalStopConfirmed, reason);
        internal static void NotifyRunStopped(WatchdogStopSummary summary)
        {
            Volatile.Write(ref _sessionClosing, 1);
            WriteSessionRevocationMarker("RunStopped");
            RecordClientEvent("RunStopped", summary?.Detail);
            Send(new WatchdogMessage { Type = WatchdogMessageType.RunStopped, SessionId = SessionId, StopSummary = summary });
            FlushClientJournal();
        }
        internal static void NotifyStopCompleted(WatchdogStopSummary summary, string reason)
        {
            RecordClientEvent("StopCompleted", summary?.Detail ?? reason);
            Send(new WatchdogMessage { Type = WatchdogMessageType.StopCompleted, SessionId = SessionId, Reason = reason, StopSummary = summary });
        }
        internal static void NotifyRunCompleted()
        {
            Volatile.Write(ref _sessionClosing, 1);
            WriteSessionRevocationMarker("FormalRunCompleted");
            RecordClientEvent("RunCompleted", "FormalRunCompleted");
            SendSimple(WatchdogMessageType.RunCompleted, "FormalRunCompleted");
            FlushClientJournal();
        }
        internal static void NotifyApplicationClosing()
        {
            Volatile.Write(ref _sessionClosing, 1);
            WriteSessionRevocationMarker("ApplicationClosing");
            RecordClientEvent("ApplicationClosing", "ApplicationClosing");
            SendSimple(WatchdogMessageType.ApplicationClosing, "ApplicationClosing");
            FlushClientJournal();
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
            WatchdogJournalPolicy policy;
            string journalDirectory;
            lock (Gate)
            {
                session = _sessionId;
                pipeName = _pipeName;
                executable = _mainExecutable;
                watchdog = _watchdogExecutable;
                recovery = _recoveryProcess;
                attempt = _recoveryAttempt;
                channels = _selectedChannels?.ToArray() ?? Array.Empty<int>();
                policy = _journalPolicy;
                journalDirectory = _journalExportDirectory;
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
                        Arguments = BuildSidecarArguments(
                            Process.GetCurrentProcess().Id,
                            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                            session,
                            pipeName,
                            executable,
                            journalDirectory,
                            policy),
                        WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    lock (Gate) _watchdogProcess = sidecar;
                    RecordClientEvent("SidecarRelaunched", "PID=" + (sidecar?.Id ?? 0));
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
            RecordClientEvent("TransportLost", (reason ?? string.Empty) + ":" + (detail ?? string.Empty));
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
            RecordClientEvent("TransportError", (reason ?? string.Empty) + ":" + detail);
            try { TransportError?.Invoke(reason, detail); }
            catch { }
        }

        private static void RaiseTransportError(string reason, string detail)
        {
            RecordClientEvent("TransportEvent", (reason ?? string.Empty) + ":" + (detail ?? string.Empty));
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
            lock (Gate)
            {
                session = _sessionId;
            }
            if (string.IsNullOrWhiteSpace(session)) return;
            try
            {
                WatchdogControlMarker.WriteLocal(session, reason);
            }
            catch (Exception ex) { RaiseTransportError("SessionRevocationMarkerLocal", ex); }
            WatchdogJournalStore journal;
            lock (Gate) journal = _clientJournal;
            try { journal?.PublishRevocation(reason); }
            catch (Exception ex) { RaiseTransportError("SessionRevocationMarkerProject", ex); }
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
            WatchdogJournalStore journal;
            lock (Gate)
            {
                try { _lifetime?.Cancel(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _pipe = null; _reader = null; _writer = null; _lifetime = null; _attached = null;
                _sessionId = null; _pipeName = null; _heartbeatProvider = null;
                try { _watchdogProcess?.Dispose(); } catch { }
                _watchdogProcess = null;
                journal = _clientJournal;
                _clientJournal = null;
            }
            try { journal?.Flush(TimeSpan.FromSeconds(1)); } catch { }
            try { journal?.Dispose(); } catch { }
        }

        private static void FlushClientJournal()
        {
            WatchdogJournalStore journal;
            lock (Gate) journal = _clientJournal;
            try { journal?.Flush(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { RaiseTransportError("JournalFlush", ex); }
        }

        private static void RecordClientEvent(string eventType, string detail)
        {
            WatchdogJournalStore journal;
            string session;
            int recoveryAttempt;
            lock (Gate)
            {
                journal = _clientJournal;
                session = _sessionId;
                recoveryAttempt = _recoveryAttempt;
            }
            if (journal == null) return;
            try
            {
                using (var process = Process.GetCurrentProcess())
                    journal.Record(new WatchdogJournalEvent
                    {
                        EventSequence = Interlocked.Increment(ref _clientEventSequence),
                        EventType = eventType ?? string.Empty,
                        State = Volatile.Read(ref _sessionClosing) == 0 ? "Active" : "Closing",
                        Reason = detail ?? string.Empty,
                        SessionId = session,
                        ProcessId = process.Id,
                        ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        AckSequence = Interlocked.Read(ref _lastHeartbeatAckSequence),
                        RecoveryAttempt = recoveryAttempt
                    });
            }
            catch { }
        }

        private static string BuildSidecarArguments(
            int parentPid,
            long parentStartTicks,
            string session,
            string pipe,
            string executable,
            string journalDirectory,
            WatchdogJournalPolicy policy)
        {
            policy = policy ?? new WatchdogJournalPolicy();
            return string.Format(CultureInfo.InvariantCulture,
                "--parent-pid {0} --parent-start-ticks {1} --session {2} --pipe {3} --executable {4} " +
                "--journal-directory {5} --journal-retention-days {6} --journal-retain-sessions {7} " +
                "--journal-max-total-bytes {8} --journal-max-session-bytes {9} " +
                "--journal-heartbeat-checkpoint-seconds {10} --journal-emergency-spool-max-bytes {11}",
                parentPid, parentStartTicks, Quote(session), Quote(pipe), Quote(executable),
                Quote(WatchdogJournalPaths.ValidateProjectDirectory(journalDirectory)),
                policy.RetentionDays, policy.RetainSessionCount, policy.MaxTotalBytes,
                policy.MaxSessionBytes, policy.HeartbeatCheckpointSeconds,
                policy.EmergencySpoolMaxBytes);
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
