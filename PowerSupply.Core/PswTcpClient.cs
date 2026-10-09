using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowerSupply.Core
{
    public sealed class PswTcpClient : IPswClient
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly IPswLog _log;
        private readonly int _connectTimeoutMs;
        private readonly int _commandTimeoutMs;
        private readonly object _phaseTraceGate = new object();
        private readonly Queue<string> _recentPowerPhases = new Queue<string>();
        private const int RecentPowerPhaseCapacity = 256;
        private DateTime _lastPowerPhaseAggregateUtc = DateTime.UtcNow;
        private long _successfulPowerPhaseCount;
        private double _maximumSuccessfulPowerPhaseMs;
        private static readonly TimeSpan ProtectionSetpointRefreshInterval = TimeSpan.FromMinutes(1);
        private double? _cachedOvp;
        private double? _cachedOcp;
        private DateTime _lastProtectionSetpointReadUtc = DateTime.MinValue;
        private TcpClient _client;
        private NetworkStream _stream;
        private StreamWriter _writer;

        public PswTcpClient(PswEndpoint endpoint, IPswLog log = null, int connectTimeoutMs = 3000, int commandTimeoutMs = 2000)
        {
            Endpoint = (endpoint ?? throw new ArgumentNullException(nameof(endpoint))).Clone();
            Endpoint.Validate();
            _log = log ?? NullPswLog.Instance;
            _connectTimeoutMs = Math.Max(100, connectTimeoutMs);
            _commandTimeoutMs = Math.Max(100, commandTimeoutMs);
        }

        public PswEndpoint Endpoint { get; }
        public bool IsConnected => _client != null && _client.Connected && _stream != null && _writer != null;
        public string Identity { get; private set; } = string.Empty;
        public bool IsVerifiedPsw { get; private set; }
        public PswCapabilities Capabilities { get; private set; } = new PswCapabilities();

        public async Task<PswSnapshot> ConnectAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            var sessionStarted = Stopwatch.GetTimestamp();
            LogPowerPhase("ConnectSession", "Started", 0, Endpoint.Id.ToString());
            try
            {
                DisposeTransport();
                var client = new TcpClient { NoDelay = true };
                _client = client;
                IPAddress address;
                if (!IPAddress.TryParse(Endpoint.Host, out address))
                {
                    var dnsStarted = Stopwatch.GetTimestamp();
                    LogPowerPhase("DnsResolve", "Started", 0, Endpoint.Host);
                    try
                    {
                        var addresses = await AwaitWithTimeout(
                                Dns.GetHostAddressesAsync(Endpoint.Host),
                                _connectTimeoutMs,
                                token,
                                $"DNS {Endpoint.Host}")
                            .ConfigureAwait(false);
                        address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork) ??
                                  addresses.FirstOrDefault();
                        if (address == null) throw new SocketException((int)SocketError.HostNotFound);
                        LogPowerPhase(
                            "DnsResolve",
                            "Completed",
                            ElapsedMs(dnsStarted),
                            address.ToString());
                    }
                    catch (Exception ex)
                    {
                        LogPowerPhase(
                            "DnsResolve",
                            PhaseFailureState(ex),
                            ElapsedMs(dnsStarted),
                            FailureDetail(Endpoint.Host, ex));
                        throw;
                    }
                }
                else
                {
                    LogPowerPhase("DnsResolve", "SkippedIpLiteral", 0, address.ToString());
                }
                var connectStarted = Stopwatch.GetTimestamp();
                LogPowerPhase("TcpConnect", "Started", 0, address + ":" + Endpoint.Port);
                try
                {
                    await AwaitWithTimeout(client.ConnectAsync(address, Endpoint.Port), _connectTimeoutMs, token,
                        $"连接 {Endpoint.Host}:{Endpoint.Port}").ConfigureAwait(false);
                    LogPowerPhase(
                        "TcpConnect",
                        "Completed",
                        ElapsedMs(connectStarted),
                        address + ":" + Endpoint.Port);
                }
                catch (Exception ex)
                {
                    LogPowerPhase(
                        "TcpConnect",
                        PhaseFailureState(ex),
                        ElapsedMs(connectStarted),
                        FailureDetail(address + ":" + Endpoint.Port, ex));
                    throw;
                }
                var stream = client.GetStream();
                _stream = stream;
                _writer = new StreamWriter(stream, Encoding.ASCII, 1024, true)
                {
                    AutoFlush = true,
                    NewLine = Endpoint.ResolveTerminator()
                };
                Log(PswLogDirection.Information, $"已连接 {Endpoint.Host}:{Endpoint.Port}");
                Identity = await QueryCoreAsync("*IDN?", token).ConfigureAwait(false);
                IsVerifiedPsw = PswProtocol.IsVerifiedIdentity(Identity);
                Capabilities = PswCapabilities.FromIdentity(Identity);
                if (IsVerifiedPsw)
                    Capabilities = await ReadProtectionRangesCoreAsync(token).ConfigureAwait(false);
                var snapshot = await ReadSnapshotCoreAsync(token).ConfigureAwait(false);
                LogPowerPhase(
                    "ConnectSession",
                    "Completed",
                    ElapsedMs(sessionStarted),
                    "Identity=" + Identity);
                return snapshot;
            }
            catch (Exception ex)
            {
                LogPowerPhase(
                    "ConnectSession",
                    PhaseFailureState(ex),
                    ElapsedMs(sessionStarted),
                    FailureDetail("Connect", ex));
                DisposeTransport();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DisconnectAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { DisposeTransport(); }
            finally { _gate.Release(); }
        }

        public Task<PswSnapshot> ReadSnapshotAsync(CancellationToken token) =>
            ExecuteLockedAsync(ReadSnapshotCoreAsync, token);

        public Task<double> SetVoltageAsync(double value, CancellationToken token)
        {
            EnsureWritable();
            PswProtocol.ValidateSetpoint(value, Capabilities.MaxVoltage, "VSET");
            return SetAndReadBackAsync("SOUR:VOLT", "SOUR:VOLT?", value, token);
        }

        public Task<double> SetCurrentAsync(double value, CancellationToken token)
        {
            EnsureWritable();
            PswProtocol.ValidateSetpoint(value, Capabilities.MaxCurrent, "ISET");
            return SetAndReadBackAsync("SOUR:CURR", "SOUR:CURR?", value, token);
        }

        public Task<double> SetOvpAsync(double value, CancellationToken token)
        {
            EnsureWritable();
            if (!Capabilities.CanWriteOvp) throw new InvalidOperationException("设备未返回可信的 OVP 范围。");
            PswProtocol.ValidateRange(value, Capabilities.MinOvp.Value, Capabilities.MaxOvp.Value, "OVP");
            return SetAndReadBackAsync(
                "SOUR:VOLT:PROT",
                "SOUR:VOLT:PROT?",
                value,
                token,
                actual =>
                {
                    _cachedOvp = actual;
                    _lastProtectionSetpointReadUtc = DateTime.UtcNow;
                });
        }

        public Task<double> SetOcpAsync(double value, CancellationToken token)
        {
            EnsureWritable();
            if (!Capabilities.CanWriteOcp) throw new InvalidOperationException("设备未返回可信的 OCP 范围。");
            PswProtocol.ValidateRange(value, Capabilities.MinOcp.Value, Capabilities.MaxOcp.Value, "OCP");
            return SetAndReadBackAsync(
                "SOUR:CURR:PROT",
                "SOUR:CURR:PROT?",
                value,
                token,
                actual =>
                {
                    _cachedOcp = actual;
                    _lastProtectionSetpointReadUtc = DateTime.UtcNow;
                });
        }

        public async Task<PswOutputCommandResult> SetOutputAndReadBackAsync(
            bool enabled,
            CancellationToken token)
        {
            var started = Stopwatch.GetTimestamp();
            var result = new PswOutputCommandResult
            {
                SupplyId = Endpoint.Id,
                Endpoint = Endpoint.Host + ":" + Endpoint.Port,
                RequestedState = enabled ? PswOutputState.On : PswOutputState.Off,
                ObservedState = PswOutputState.Unknown,
                StartedUtc = DateTime.UtcNow
            };
            try
            {
                EnsureWritable();
                result.Identity = Identity ?? string.Empty;
                result.IdentityVerified = IsVerifiedPsw;
                return await ExecuteLockedAsync(async ct =>
                {
                    try
                    {
                        result.FailureStage = "WriteOutput";
                        await WriteCoreAsync(enabled ? "OUTP ON" : "OUTP OFF", ct)
                            .ConfigureAwait(false);
                        result.CommandWritten = true;

                        result.FailureStage = "ReadBackOutput";
                        var actual = PswProtocol.ParseBoolean(
                            await QueryCoreAsync("OUTP?", ct).ConfigureAwait(false),
                            "OUTP?");
                        result.ObservedState = actual
                            ? PswOutputState.On
                            : PswOutputState.Off;
                        result.ReadBackVerified = actual == enabled;
                        if (!result.ReadBackVerified)
                        {
                            result.FailureCode = "OutputReadBackMismatch";
                            result.Detail = "Requested=" + result.RequestedState +
                                            ";Observed=" + result.ObservedState;
                        }
                        else
                        {
                            result.FailureStage = string.Empty;
                        }
                        return CompleteOutputResult(result, started);
                    }
                    catch (Exception ex)
                    {
                        result.FailureCode = ex is OperationCanceledException
                            ? "OutputCommandCanceled"
                            : ex is TimeoutException
                                ? "OutputCommandTimeout"
                                : "OutputCommandFailed";
                        result.Detail = ex.GetBaseException().Message;
                        return CompleteOutputResult(result, started);
                    }
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(result.FailureStage))
                    result.FailureStage = "Preflight";
                result.FailureCode = ex is OperationCanceledException
                    ? "OutputCommandCanceled"
                    : ex is TimeoutException
                        ? "OutputCommandTimeout"
                        : "OutputCommandFailed";
                result.Detail = ex.GetBaseException().Message;
                return CompleteOutputResult(result, started);
            }
        }

        [Obsolete("Use SetOutputAndReadBackAsync. The bool return is the observed output state, not an operation-success flag.")]
        public async Task<bool> SetOutputAsync(bool enabled, CancellationToken token)
        {
            var result = await SetOutputAndReadBackAsync(enabled, token).ConfigureAwait(false);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.Detail)
                        ? result.FailureCode
                        : result.FailureCode + ":" + result.Detail);
            return result.ObservedState == PswOutputState.On;
        }

        private static PswOutputCommandResult CompleteOutputResult(
            PswOutputCommandResult result,
            long started)
        {
            result.CompletedUtc = DateTime.UtcNow;
            result.DurationMs = ElapsedMs(started);
            return result;
        }

        public Task<IReadOnlyList<string>> ReadErrorQueueAsync(CancellationToken token)
        {
            return ExecuteLockedAsync<IReadOnlyList<string>>(async ct =>
            {
                var errors = new List<string>();
                for (var i = 0; i < 32; i++)
                {
                    var response = await QueryCoreAsync("SYST:ERR?", ct).ConfigureAwait(false);
                    errors.Add(response);
                    var value = response.TrimStart();
                    if (value == "0" || value.StartsWith("0,") || value.StartsWith("+0,")) break;
                }
                return errors;
            }, token);
        }

        public Task<string> SendRawAsync(string command, bool expectResponse, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("原始命令必须为单行非空 SCPI 文本。", nameof(command));
            if (!expectResponse) EnsureWritable();
            return ExecuteLockedAsync(async ct =>
            {
                if (expectResponse) return await QueryCoreAsync(command.Trim(), ct).ConfigureAwait(false);
                await WriteCoreAsync(command.Trim(), ct).ConfigureAwait(false);
                return null;
            }, token);
        }

        private async Task<PswSnapshot> ReadSnapshotCoreAsync(CancellationToken token)
        {
            EnsureConnected();
            var pollStartedUtc = DateTime.UtcNow;
            var pollStartedTicks = Stopwatch.GetTimestamp();
            var output = PswProtocol.ParseBoolean(await QueryCoreAsync("OUTP?", token).ConfigureAwait(false), "OUTP?");
            var setV = PswProtocol.ParseNumber(await QueryCoreAsync("SOUR:VOLT?", token).ConfigureAwait(false), "SOUR:VOLT?");
            var setI = PswProtocol.ParseNumber(await QueryCoreAsync("SOUR:CURR?", token).ConfigureAwait(false), "SOUR:CURR?");
            var measurement = PswProtocol.ParseMeasurement(await QueryCoreAsync("MEAS:ALL:DC?", token).ConfigureAwait(false));
            var operation = PswProtocol.ParseInteger(await QueryCoreAsync("STAT:OPER:COND?", token).ConfigureAwait(false), "STAT:OPER:COND?");
            var questionable = PswProtocol.ParseInteger(await QueryCoreAsync("STAT:QUES:COND?", token).ConfigureAwait(false), "STAT:QUES:COND?");
            var tripped = PswProtocol.ParseBoolean(await QueryCoreAsync("OUTP:PROT:TRIP?", token).ConfigureAwait(false), "OUTP:PROT:TRIP?");
            if (!_cachedOvp.HasValue || !_cachedOcp.HasValue ||
                DateTime.UtcNow - _lastProtectionSetpointReadUtc >= ProtectionSetpointRefreshInterval)
            {
                _cachedOvp = await QueryOptionalNumberCoreAsync("SOUR:VOLT:PROT?", token).ConfigureAwait(false);
                _cachedOcp = await QueryOptionalNumberCoreAsync("SOUR:CURR:PROT?", token).ConfigureAwait(false);
                _lastProtectionSetpointReadUtc = DateTime.UtcNow;
            }
            var completedUtc = DateTime.UtcNow;
            return new PswSnapshot
            {
                PollStartedUtc = pollStartedUtc,
                PollCompletedUtc = completedUtc,
                PollDurationMs = ElapsedMs(pollStartedTicks),
                TimestampUtc = completedUtc,
                SupplyId = Endpoint.Id,
                IsConnected = true,
                Identity = Identity,
                IsVerifiedPsw = IsVerifiedPsw,
                Capabilities = Capabilities,
                OutputEnabled = output,
                SetVoltage = setV,
                SetCurrent = setI,
                Ovp = _cachedOvp,
                Ocp = _cachedOcp,
                MeasuredVoltage = measurement.Item1,
                MeasuredCurrent = measurement.Item2,
                MeasuredPower = measurement.Item3,
                OperationStatus = operation,
                QuestionableStatus = questionable,
                ProtectionTripped = tripped
            };
        }

        private async Task<PswCapabilities> ReadProtectionRangesCoreAsync(CancellationToken token)
        {
            var minOvp = await QueryOptionalNumberCoreAsync("SOUR:VOLT:PROT? MIN", token).ConfigureAwait(false);
            var maxOvp = await QueryOptionalNumberCoreAsync("SOUR:VOLT:PROT? MAX", token).ConfigureAwait(false);
            var minOcp = await QueryOptionalNumberCoreAsync("SOUR:CURR:PROT? MIN", token).ConfigureAwait(false);
            var maxOcp = await QueryOptionalNumberCoreAsync("SOUR:CURR:PROT? MAX", token).ConfigureAwait(false);
            return Capabilities.WithProtectionRanges(minOvp, maxOvp, minOcp, maxOcp);
        }

        private async Task<double?> QueryOptionalNumberCoreAsync(string command, CancellationToken token)
        {
            try { return PswProtocol.ParseNumber(await QueryCoreAsync(command, token).ConfigureAwait(false), command); }
            catch (InvalidDataException) { return null; }
        }

        private Task<double> SetAndReadBackAsync(
            string set,
            string query,
            double value,
            CancellationToken token,
            Action<double> onVerified = null)
        {
            return ExecuteLockedAsync(async ct =>
            {
                await WriteCoreAsync($"{set} {PswProtocol.FormatNumber(value)}", ct).ConfigureAwait(false);
                var actual = PswProtocol.ParseNumber(await QueryCoreAsync(query, ct).ConfigureAwait(false), query);
                var tolerance = Math.Max(0.0001, Math.Abs(value) * 0.0001);
                if (Math.Abs(actual - value) > tolerance)
                    throw new InvalidOperationException($"{set} 回读不一致：期望 {value:0.####}，实际 {actual:0.####}。");
                onVerified?.Invoke(actual);
                return actual;
            }, token);
        }

        private async Task<string> QueryCoreAsync(string command, CancellationToken token)
        {
            await WriteCoreAsync(command, token).ConfigureAwait(false);
            var response = await ReadResponseLineCoreAsync(command, token).ConfigureAwait(false);
            if (response == null) throw new IOException($"{command} 查询期间连接被远端关闭。");
            return response.TrimEnd('\r', '\n');
        }

        private async Task WriteCoreAsync(string command, CancellationToken token)
        {
            EnsureConnected();
            token.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            LogPowerPhase("ScpiWrite", "Started", 0, command);
            try
            {
                await AwaitWithTimeout(_writer.WriteLineAsync(command), _commandTimeoutMs, token, command).ConfigureAwait(false);
                await AwaitWithTimeout(_writer.FlushAsync(), _commandTimeoutMs, token, command).ConfigureAwait(false);
                LogPowerPhase("ScpiWrite", "Completed", ElapsedMs(started), command);
            }
            catch (Exception ex)
            {
                LogPowerPhase(
                    "ScpiWrite",
                    PhaseFailureState(ex),
                    ElapsedMs(started),
                    FailureDetail(command, ex));
                throw;
            }
        }

        private async Task<string> ReadResponseLineCoreAsync(string command, CancellationToken token)
        {
            EnsureConnected();
            var started = Stopwatch.GetTimestamp();
            var firstByteLogged = false;
            var bytes = new List<byte>(128);
            LogPowerPhase("ScpiResponse", "WaitStarted", 0, command);
            try
            {
                while (true)
                {
                    var remaining = _commandTimeoutMs - (int)Math.Ceiling(ElapsedMs(started));
                    if (remaining <= 0)
                        throw new TimeoutException($"{command} 响应超时（{_commandTimeoutMs} ms）。");
                    var one = new byte[1];
                    var read = await AwaitWithTimeout(
                            _stream.ReadAsync(one, 0, 1),
                            remaining,
                            token,
                            command + " response")
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        LogPowerPhase(
                            firstByteLogged ? "ScpiResponseLine" : "ScpiResponseFirstByte",
                            "Failed",
                            ElapsedMs(started),
                            command + ";Error=RemoteClosedConnection");
                        return null;
                    }
                    if (!firstByteLogged)
                    {
                        firstByteLogged = true;
                        LogPowerPhase("ScpiResponseFirstByte", "Completed", ElapsedMs(started), command);
                    }
                    if (one[0] == (byte)'\n') break;
                    if (one[0] != (byte)'\r') bytes.Add(one[0]);
                    if (bytes.Count > 65536)
                        throw new InvalidDataException(command + " 响应行超过 65536 字节。");
                }
                var response = Encoding.ASCII.GetString(bytes.ToArray());
                LogPowerPhase(
                    "ScpiResponseLine",
                    "Completed",
                    ElapsedMs(started),
                    command + ";Bytes=" + bytes.Count);
                return response;
            }
            catch (Exception ex)
            {
                LogPowerPhase(
                    firstByteLogged ? "ScpiResponseLine" : "ScpiResponseFirstByte",
                    PhaseFailureState(ex),
                    ElapsedMs(started),
                    FailureDetail(command + ";Bytes=" + bytes.Count, ex));
                throw;
            }
        }

        private async Task<T> ExecuteLockedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // StreamReader.ReadLineAsync 在 netstandard2.0 中无法真正取消。若把调用方的
                // CancellationToken 传入超时包装，取消只会让包装任务提前退出，底层读取仍
                // 占用 StreamReader；下一条命令随后会触发“流正在由其上的前一操作使用”，
                // 并可能让残留读取吞掉下一条 SCPI 响应。拿到 I/O 锁后必须让当前事务在
                // 自身命令超时范围内完整结束；调用方取消仍可中止等待 I/O 锁。
                return await action(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // TcpClient.Connected 只反映最近一次 I/O 的缓存状态；远端 FIN/RST 后它仍可能为 true。
                // 一旦读写、套接字或超时异常发生，必须主动丢弃传输层，避免上层误判仍在线。
                if (ex is IOException || ex is SocketException || ex is TimeoutException || !IsConnected)
                    DisposeTransport();
                throw;
            }
            finally { _gate.Release(); }
        }

        private static async Task<T> AwaitWithTimeout<T>(Task<T> task, int timeoutMs, CancellationToken token, string operation)
        {
            var timeout = Task.Delay(timeoutMs, token);
            var completed = await Task.WhenAny(task, timeout).ConfigureAwait(false);
            if (completed != task)
            {
                ObserveLateFault(task);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"{operation} 超时（{timeoutMs} ms）。");
            }
            return await task.ConfigureAwait(false);
        }

        private static async Task AwaitWithTimeout(Task task, int timeoutMs, CancellationToken token, string operation)
        {
            var timeout = Task.Delay(timeoutMs, token);
            var completed = await Task.WhenAny(task, timeout).ConfigureAwait(false);
            if (completed != task)
            {
                ObserveLateFault(task);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"{operation} 超时（{timeoutMs} ms）。");
            }
            await task.ConfigureAwait(false);
        }

        /// <summary>
        /// netstandard2.0 的 StreamReader.ReadLineAsync 无法接收 CancellationToken。
        /// 超时后上层会关闭整条传输连接，使原 I/O Task 稍后以 Socket/Disposed 异常结束；
        /// 必须继续观察该 Task 的 Exception，避免由终结器线程触发 UnobservedTaskException。
        /// </summary>
        private static void ObserveLateFault(Task task)
        {
            if (task == null) return;
            _ = task.ContinueWith(
                completed =>
                {
                    // 访问 Exception 即完成“观察”；异常仍由当前事务的 TimeoutException 表达。
                    var ignored = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void EnsureConnected()
        {
            if (!IsConnected) throw new InvalidOperationException("设备尚未连接。");
        }

        private void EnsureWritable()
        {
            EnsureConnected();
            if (!IsVerifiedPsw) throw new InvalidOperationException("设备身份未验证为 GW Instek PSW，禁止写入。");
        }

        private void Log(PswLogDirection direction, string message)
        {
            try { _log.Write(new PswLogEntry(DateTime.UtcNow, Endpoint.Id, direction, message)); }
            catch { }
        }

        private void LogPowerPhase(string phase, string state, double elapsedMs, string detail)
        {
            var message =
                $"PowerCommPhase Phase={phase} State={state} ElapsedMs={elapsedMs:F3} " +
                $"Endpoint={Endpoint.Host}:{Endpoint.Port} Detail={detail}";
            var failure = string.Equals(state, "TimedOut", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(state, "Canceled", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);
            string aggregate = null;
            string recentTrace = null;
            lock (_phaseTraceGate)
            {
                _recentPowerPhases.Enqueue(DateTime.UtcNow.ToString("O") + " " + message);
                while (_recentPowerPhases.Count > RecentPowerPhaseCapacity)
                    _recentPowerPhases.Dequeue();
                if (!failure)
                {
                    _successfulPowerPhaseCount++;
                    _maximumSuccessfulPowerPhaseMs = Math.Max(_maximumSuccessfulPowerPhaseMs, elapsedMs);
                    var now = DateTime.UtcNow;
                    if ((now - _lastPowerPhaseAggregateUtc).TotalSeconds >= 60)
                    {
                        aggregate =
                            $"PowerCommAggregate WindowSeconds={(now - _lastPowerPhaseAggregateUtc).TotalSeconds:F0} " +
                            $"SuccessPhases={_successfulPowerPhaseCount} MaxElapsedMs={_maximumSuccessfulPowerPhaseMs:F3} " +
                            $"Endpoint={Endpoint.Host}:{Endpoint.Port}";
                        _lastPowerPhaseAggregateUtc = now;
                        _successfulPowerPhaseCount = 0;
                        _maximumSuccessfulPowerPhaseMs = 0;
                    }
                }
                else
                    recentTrace = string.Join(" || ", _recentPowerPhases.ToArray());
            }
            // 成功阶段只保留内存环形缓冲，并每分钟输出一条聚合；失败立即输出且附带
            // 最近阶段，避免正常2kHz运行把逐命令日志放大为每分钟数MiB。
            if (!string.IsNullOrWhiteSpace(aggregate))
                Log(PswLogDirection.Information, aggregate);
            if (failure)
            {
                Log(PswLogDirection.Error, message);
                Log(PswLogDirection.Error, "PowerCommRecentTrace " + recentTrace);
            }
        }

        private static double ElapsedMs(long startedTicks) =>
            (Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency;

        private static string PhaseFailureState(Exception exception) =>
            exception is TimeoutException ? "TimedOut" :
            exception is OperationCanceledException ? "Canceled" : "Failed";

        private static string FailureDetail(string detail, Exception exception)
        {
            var error = exception?.GetBaseException();
            var message = (error?.Message ?? "Unknown")
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            return (detail ?? string.Empty) + ";Error=" +
                   (error?.GetType().Name ?? "Unknown") + ":" + message;
        }

        private void DisposeTransport()
        {
            try { _writer?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _writer = null;
            _stream = null;
            _client = null;
            _lastProtectionSetpointReadUtc = DateTime.MinValue;
        }

        public void Dispose()
        {
            DisposeTransport();
            _gate.Dispose();
        }
    }
}
