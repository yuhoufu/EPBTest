using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PowerSupplyDebugger.Models;

namespace PowerSupplyDebugger.Services;

public sealed class PswTcpClient : IPswClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ILogService _log;
    private TcpClient? _tcpClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public PswTcpClient(PowerSupplyEndpoint endpoint, ILogService log)
    {
        Endpoint = endpoint.Clone();
        Endpoint.Validate();
        _log = log;
    }

    public PowerSupplyEndpoint Endpoint { get; }
    public bool IsConnected => _tcpClient?.Connected == true && _reader is not null && _writer is not null;
    public string Identity { get; private set; } = string.Empty;
    public bool IsVerifiedPsw { get; private set; }
    public PswCapabilities Capabilities { get; private set; } = new();

    public async Task<PowerSupplySnapshot> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisposeTransport();
            Identity = string.Empty;
            IsVerifiedPsw = false;
            Capabilities = new PswCapabilities();

            var client = new TcpClient
            {
                NoDelay = true,
                ReceiveTimeout = checked((int)CommandTimeout.TotalMilliseconds),
                SendTimeout = checked((int)CommandTimeout.TotalMilliseconds)
            };

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(Endpoint.Host, Endpoint.Port, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw new TimeoutException(
                    $"连接 {Endpoint.Host}:{Endpoint.Port} 超时（{ConnectTimeout.TotalSeconds:0} 秒）。");
            }

            _tcpClient = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            _writer = new StreamWriter(stream, new ASCIIEncoding(), 1024, true)
            {
                AutoFlush = true,
                NewLine = Endpoint.ResolveTerminator()
            };

            await LogAsync(ScpiLogDirection.Information, $"已连接 {Endpoint.Host}:{Endpoint.Port}", cancellationToken)
                .ConfigureAwait(false);

            Identity = await QueryCoreAsync("*IDN?", cancellationToken).ConfigureAwait(false);
            IsVerifiedPsw = PswProtocol.IsVerifiedIdentity(Identity);
            Capabilities = PswCapabilities.FromIdentity(Identity);

            if (IsVerifiedPsw)
            {
                Capabilities = await ReadProtectionRangesCoreAsync(Capabilities, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await ReadSnapshotCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogFailureSafeAsync($"连接或身份读取失败：{ex.Message}").ConfigureAwait(false);
            DisposeTransport();
            throw;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tcpClient is not null)
            {
                await LogAsync(ScpiLogDirection.Information, "已断开连接", cancellationToken).ConfigureAwait(false);
            }

            DisposeTransport();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public Task<PowerSupplySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(ReadSnapshotCoreAsync, cancellationToken);

    public Task<double> SetVoltageAsync(double value, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        PswProtocol.ValidateSetpoint(value, Capabilities.MaxVoltage, "VSET");
        return SetAndReadBackAsync("SOUR:VOLT", "SOUR:VOLT?", value, cancellationToken);
    }

    public Task<double> SetCurrentAsync(double value, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        PswProtocol.ValidateSetpoint(value, Capabilities.MaxCurrent, "ISET");
        return SetAndReadBackAsync("SOUR:CURR", "SOUR:CURR?", value, cancellationToken);
    }

    public Task<double> SetOvpAsync(double value, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (Capabilities is not { MinOvp: { } minimum, MaxOvp: { } maximum })
        {
            throw new InvalidOperationException("设备未返回可信的 OVP 范围，禁止写入。");
        }

        PswProtocol.ValidateRange(value, minimum, maximum, "OVP");
        return SetAndReadBackAsync("SOUR:VOLT:PROT", "SOUR:VOLT:PROT?", value, cancellationToken);
    }

    public Task<double> SetOcpAsync(double value, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (Capabilities is not { MinOcp: { } minimum, MaxOcp: { } maximum })
        {
            throw new InvalidOperationException("设备未返回可信的 OCP 范围，禁止写入。");
        }

        PswProtocol.ValidateRange(value, minimum, maximum, "OCP");
        return SetAndReadBackAsync("SOUR:CURR:PROT", "SOUR:CURR:PROT?", value, cancellationToken);
    }

    public Task<bool> SetOutputAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        return ExecuteLockedAsync(
            async token =>
            {
                await WriteCoreAsync(enabled ? "OUTP ON" : "OUTP OFF", token).ConfigureAwait(false);
                var actual = PswProtocol.ParseBoolean(
                    await QueryCoreAsync("OUTP?", token).ConfigureAwait(false),
                    "OUTP?");
                if (actual != enabled)
                {
                    throw new InvalidOperationException(
                        $"输出状态回读不一致：期望 {(enabled ? "ON" : "OFF")}，实际 {(actual ? "ON" : "OFF")}。");
                }

                return actual;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> ReadErrorQueueAsync(CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(
            async token =>
            {
                var errors = new List<string>();
                for (var i = 0; i < 32; i++)
                {
                    var response = await QueryCoreAsync("SYST:ERR?", token).ConfigureAwait(false);
                    errors.Add(response);
                    var trimmed = response.TrimStart();
                    if (trimmed.StartsWith("0,", StringComparison.Ordinal) ||
                        trimmed.StartsWith("+0,", StringComparison.Ordinal) ||
                        trimmed == "0")
                    {
                        break;
                    }
                }

                return (IReadOnlyList<string>)errors;
            },
            cancellationToken);

    public Task<string?> SendRawAsync(
        string command,
        bool expectResponse,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\r') || command.Contains('\n'))
        {
            throw new ArgumentException("原始命令必须为单行非空 SCPI 文本。", nameof(command));
        }

        if (!expectResponse)
        {
            EnsureWritable();
        }

        return ExecuteLockedAsync(
            async token =>
            {
                if (expectResponse)
                {
                    return await QueryCoreAsync(command.Trim(), token).ConfigureAwait(false);
                }

                await WriteCoreAsync(command.Trim(), token).ConfigureAwait(false);
                return null;
            },
            cancellationToken);
    }

    private async Task<PowerSupplySnapshot> ReadSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();

        var output = PswProtocol.ParseBoolean(
            await QueryCoreAsync("OUTP?", cancellationToken).ConfigureAwait(false),
            "OUTP?");
        var setVoltage = PswProtocol.ParseNumber(
            await QueryCoreAsync("SOUR:VOLT?", cancellationToken).ConfigureAwait(false),
            "SOUR:VOLT?");
        var setCurrent = PswProtocol.ParseNumber(
            await QueryCoreAsync("SOUR:CURR?", cancellationToken).ConfigureAwait(false),
            "SOUR:CURR?");
        var measurement = PswProtocol.ParseMeasurement(
            await QueryCoreAsync("MEAS:ALL:DC?", cancellationToken).ConfigureAwait(false));
        var operation = PswProtocol.ParseInteger(
            await QueryCoreAsync("STAT:OPER:COND?", cancellationToken).ConfigureAwait(false),
            "STAT:OPER:COND?");
        var questionable = PswProtocol.ParseInteger(
            await QueryCoreAsync("STAT:QUES:COND?", cancellationToken).ConfigureAwait(false),
            "STAT:QUES:COND?");
        var protectionTripped = PswProtocol.ParseBoolean(
            await QueryCoreAsync("OUTP:PROT:TRIP?", cancellationToken).ConfigureAwait(false),
            "OUTP:PROT:TRIP?");
        var ovp = await QueryOptionalNumberCoreAsync("SOUR:VOLT:PROT?", cancellationToken).ConfigureAwait(false);
        var ocp = await QueryOptionalNumberCoreAsync("SOUR:CURR:PROT?", cancellationToken).ConfigureAwait(false);
        var controlState = SupportsRemoteStateQuery(Identity)
            ? await QueryCoreAsync("SYST:COMM:RLST?", cancellationToken).ConfigureAwait(false)
            : "固件低于 1.60 或版本未知";

        return new PowerSupplySnapshot
        {
            IsConnected = true,
            Identity = Identity,
            IsVerifiedPsw = IsVerifiedPsw,
            Capabilities = Capabilities,
            OutputEnabled = output,
            SetVoltage = setVoltage,
            SetCurrent = setCurrent,
            Ovp = ovp,
            Ocp = ocp,
            MeasuredVoltage = measurement.Voltage,
            MeasuredCurrent = measurement.Current,
            MeasuredPower = measurement.Power,
            ProtectionTripped = protectionTripped,
            OperationStatus = operation,
            QuestionableStatus = questionable,
            ControlState = controlState
        };
    }

    private async Task<PswCapabilities> ReadProtectionRangesCoreAsync(
        PswCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var minOvp = await QueryOptionalNumberCoreAsync("SOUR:VOLT:PROT? MIN", cancellationToken)
            .ConfigureAwait(false);
        var maxOvp = await QueryOptionalNumberCoreAsync("SOUR:VOLT:PROT? MAX", cancellationToken)
            .ConfigureAwait(false);
        var minOcp = await QueryOptionalNumberCoreAsync("SOUR:CURR:PROT? MIN", cancellationToken)
            .ConfigureAwait(false);
        var maxOcp = await QueryOptionalNumberCoreAsync("SOUR:CURR:PROT? MAX", cancellationToken)
            .ConfigureAwait(false);
        return capabilities.WithProtectionRanges(minOvp, maxOvp, minOcp, maxOcp);
    }

    private async Task<double?> QueryOptionalNumberCoreAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            return PswProtocol.ParseNumber(
                await QueryCoreAsync(command, cancellationToken).ConfigureAwait(false),
                command);
        }
        catch (InvalidDataException ex)
        {
            await LogAsync(ScpiLogDirection.Information, $"可选能力查询不可用：{ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    private Task<double> SetAndReadBackAsync(
        string setCommand,
        string queryCommand,
        double value,
        CancellationToken cancellationToken) =>
        ExecuteLockedAsync(
            async token =>
            {
                await WriteCoreAsync($"{setCommand} {PswProtocol.FormatNumber(value)}", token).ConfigureAwait(false);
                var actual = PswProtocol.ParseNumber(
                    await QueryCoreAsync(queryCommand, token).ConfigureAwait(false),
                    queryCommand);
                var tolerance = Math.Max(0.0001, Math.Abs(value) * 0.0001);
                if (Math.Abs(actual - value) > tolerance)
                {
                    throw new InvalidOperationException(
                        $"{setCommand} 回读不一致：期望 {value:0.####}，实际 {actual:0.####}。");
                }

                return actual;
            },
            cancellationToken);

    private async Task<string> QueryCoreAsync(string command, CancellationToken cancellationToken)
    {
        await WriteCoreAsync(command, cancellationToken).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        string? response;
        try
        {
            response = await _reader!.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{command} 响应超时（{CommandTimeout.TotalSeconds:0} 秒）。");
        }

        if (response is null)
        {
            throw new IOException($"{command} 查询期间连接被远端关闭。");
        }

        response = response.TrimEnd('\r', '\n');
        await LogAsync(ScpiLogDirection.Receive, response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task WriteCoreAsync(string command, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await LogAsync(ScpiLogDirection.Transmit, command, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);
        try
        {
            await _writer!.WriteLineAsync(command.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await _writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{command} 发送超时（{CommandTimeout.TotalSeconds:0} 秒）。");
        }
    }

    private async Task<T> ExecuteLockedAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogFailureSafeAsync(ex.Message).ConfigureAwait(false);
            if (ex is IOException or SocketException or TimeoutException)
            {
                DisposeTransport();
            }

            throw;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("设备尚未连接。");
        }
    }

    private void EnsureWritable()
    {
        EnsureConnected();
        if (!IsVerifiedPsw)
        {
            throw new InvalidOperationException("设备身份未验证为 GW Instek PSW，禁止写入。");
        }
    }

    private Task LogAsync(
        ScpiLogDirection direction,
        string message,
        CancellationToken cancellationToken) =>
        _log.WriteAsync(
            new ScpiLogEntry(DateTimeOffset.Now, Endpoint.Id, Endpoint.DisplayName, direction, message),
            cancellationToken);

    private async Task LogFailureSafeAsync(string message)
    {
        try
        {
            await LogAsync(ScpiLogDirection.Error, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Logging must never hide the original communication failure.
        }
    }

    private static bool SupportsRemoteStateQuery(string identity)
    {
        var matches = Regex.Matches(identity, @"(?<!\d)(\d+)\.(\d+)(?!\d)");
        if (matches.Count == 0)
        {
            return false;
        }

        var match = matches[^1];
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        return major > 1 || (major == 1 && minor >= 60);
    }

    private void DisposeTransport()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _tcpClient?.Dispose();
        _writer = null;
        _reader = null;
        _tcpClient = null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            DisposeTransport();
        }

        _commandGate.Dispose();
    }
}
