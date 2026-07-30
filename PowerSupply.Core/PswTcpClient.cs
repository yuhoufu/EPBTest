using System;
using System.Collections.Generic;
using System.IO;
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
        private TcpClient _client;
        private StreamReader _reader;
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
        public bool IsConnected => _client != null && _client.Connected && _reader != null && _writer != null;
        public string Identity { get; private set; } = string.Empty;
        public bool IsVerifiedPsw { get; private set; }
        public PswCapabilities Capabilities { get; private set; } = new PswCapabilities();

        public async Task<PswSnapshot> ConnectAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                DisposeTransport();
                var client = new TcpClient { NoDelay = true };
                _client = client;
                await AwaitWithTimeout(client.ConnectAsync(Endpoint.Host, Endpoint.Port), _connectTimeoutMs, token,
                    $"连接 {Endpoint.Host}:{Endpoint.Port}").ConfigureAwait(false);
                var stream = client.GetStream();
                _reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
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
                return await ReadSnapshotCoreAsync(token).ConfigureAwait(false);
            }
            catch
            {
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
            return SetAndReadBackAsync("SOUR:VOLT:PROT", "SOUR:VOLT:PROT?", value, token);
        }

        public Task<double> SetOcpAsync(double value, CancellationToken token)
        {
            EnsureWritable();
            if (!Capabilities.CanWriteOcp) throw new InvalidOperationException("设备未返回可信的 OCP 范围。");
            PswProtocol.ValidateRange(value, Capabilities.MinOcp.Value, Capabilities.MaxOcp.Value, "OCP");
            return SetAndReadBackAsync("SOUR:CURR:PROT", "SOUR:CURR:PROT?", value, token);
        }

        public Task<bool> SetOutputAsync(bool enabled, CancellationToken token)
        {
            EnsureWritable();
            return ExecuteLockedAsync(async ct =>
            {
                await WriteCoreAsync(enabled ? "OUTP ON" : "OUTP OFF", ct).ConfigureAwait(false);
                var actual = PswProtocol.ParseBoolean(await QueryCoreAsync("OUTP?", ct).ConfigureAwait(false), "OUTP?");
                if (actual != enabled) throw new InvalidOperationException("输出状态回读不一致。");
                return actual;
            }, token);
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
            var output = PswProtocol.ParseBoolean(await QueryCoreAsync("OUTP?", token).ConfigureAwait(false), "OUTP?");
            var setV = PswProtocol.ParseNumber(await QueryCoreAsync("SOUR:VOLT?", token).ConfigureAwait(false), "SOUR:VOLT?");
            var setI = PswProtocol.ParseNumber(await QueryCoreAsync("SOUR:CURR?", token).ConfigureAwait(false), "SOUR:CURR?");
            var measurement = PswProtocol.ParseMeasurement(await QueryCoreAsync("MEAS:ALL:DC?", token).ConfigureAwait(false));
            var operation = PswProtocol.ParseInteger(await QueryCoreAsync("STAT:OPER:COND?", token).ConfigureAwait(false), "STAT:OPER:COND?");
            var questionable = PswProtocol.ParseInteger(await QueryCoreAsync("STAT:QUES:COND?", token).ConfigureAwait(false), "STAT:QUES:COND?");
            var tripped = PswProtocol.ParseBoolean(await QueryCoreAsync("OUTP:PROT:TRIP?", token).ConfigureAwait(false), "OUTP:PROT:TRIP?");
            return new PswSnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                SupplyId = Endpoint.Id,
                IsConnected = true,
                Identity = Identity,
                IsVerifiedPsw = IsVerifiedPsw,
                Capabilities = Capabilities,
                OutputEnabled = output,
                SetVoltage = setV,
                SetCurrent = setI,
                Ovp = await QueryOptionalNumberCoreAsync("SOUR:VOLT:PROT?", token).ConfigureAwait(false),
                Ocp = await QueryOptionalNumberCoreAsync("SOUR:CURR:PROT?", token).ConfigureAwait(false),
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

        private Task<double> SetAndReadBackAsync(string set, string query, double value, CancellationToken token)
        {
            return ExecuteLockedAsync(async ct =>
            {
                await WriteCoreAsync($"{set} {PswProtocol.FormatNumber(value)}", ct).ConfigureAwait(false);
                var actual = PswProtocol.ParseNumber(await QueryCoreAsync(query, ct).ConfigureAwait(false), query);
                var tolerance = Math.Max(0.0001, Math.Abs(value) * 0.0001);
                if (Math.Abs(actual - value) > tolerance)
                    throw new InvalidOperationException($"{set} 回读不一致：期望 {value:0.####}，实际 {actual:0.####}。");
                return actual;
            }, token);
        }

        private async Task<string> QueryCoreAsync(string command, CancellationToken token)
        {
            await WriteCoreAsync(command, token).ConfigureAwait(false);
            var response = await AwaitWithTimeout(_reader.ReadLineAsync(), _commandTimeoutMs, token, command).ConfigureAwait(false);
            if (response == null) throw new IOException($"{command} 查询期间连接被远端关闭。");
            Log(PswLogDirection.Receive, response);
            return response.TrimEnd('\r', '\n');
        }

        private async Task WriteCoreAsync(string command, CancellationToken token)
        {
            EnsureConnected();
            token.ThrowIfCancellationRequested();
            Log(PswLogDirection.Transmit, command);
            await AwaitWithTimeout(_writer.WriteLineAsync(command), _commandTimeoutMs, token, command).ConfigureAwait(false);
            await AwaitWithTimeout(_writer.FlushAsync(), _commandTimeoutMs, token, command).ConfigureAwait(false);
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
                token.ThrowIfCancellationRequested();
                throw new TimeoutException($"{operation} 超时（{timeoutMs} ms）。");
            }
            await task.ConfigureAwait(false);
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

        private void DisposeTransport()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _writer = null;
            _reader = null;
            _client = null;
        }

        public void Dispose()
        {
            DisposeTransport();
            _gate.Dispose();
        }
    }
}
