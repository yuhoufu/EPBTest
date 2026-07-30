using PowerSupplyDebugger.Models;
using Core = PowerSupply.Core;

namespace PowerSupplyDebugger.Services;

/// <summary>
/// 调试器适配层。实际 TCP、超时、身份校验和 SCPI 读写由与 EPB 主程序共用的
/// PowerSupply.Core 实现，避免两套协议行为漂移。
/// </summary>
public sealed class PswTcpClient : IPswClient
{
    private readonly Core.PswTcpClient _core;

    public PswTcpClient(PowerSupplyEndpoint endpoint, ILogService log)
    {
        Endpoint = endpoint.Clone();
        Endpoint.Validate();
        _core = new Core.PswTcpClient(
            new Core.PswEndpoint
            {
                Id = endpoint.Id,
                DisplayName = endpoint.DisplayName,
                Host = endpoint.Host,
                Port = endpoint.Port,
                Terminator = endpoint.Terminator
            },
            new LogAdapter(log, endpoint));
    }

    public PowerSupplyEndpoint Endpoint { get; }
    public bool IsConnected => _core.IsConnected;
    public string Identity => _core.Identity;
    public bool IsVerifiedPsw => _core.IsVerifiedPsw;
    public PswCapabilities Capabilities => Convert(_core.Capabilities);

    public async Task<PowerSupplySnapshot> ConnectAsync(CancellationToken cancellationToken = default) =>
        Convert(await _core.ConnectAsync(cancellationToken).ConfigureAwait(false));

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _core.DisconnectAsync(cancellationToken);

    public async Task<PowerSupplySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) =>
        Convert(await _core.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false));

    public Task<double> SetVoltageAsync(double value, CancellationToken cancellationToken = default) =>
        _core.SetVoltageAsync(value, cancellationToken);

    public Task<double> SetCurrentAsync(double value, CancellationToken cancellationToken = default) =>
        _core.SetCurrentAsync(value, cancellationToken);

    public Task<double> SetOvpAsync(double value, CancellationToken cancellationToken = default) =>
        _core.SetOvpAsync(value, cancellationToken);

    public Task<double> SetOcpAsync(double value, CancellationToken cancellationToken = default) =>
        _core.SetOcpAsync(value, cancellationToken);

    public Task<bool> SetOutputAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _core.SetOutputAsync(enabled, cancellationToken);

    public Task<IReadOnlyList<string>> ReadErrorQueueAsync(CancellationToken cancellationToken = default) =>
        _core.ReadErrorQueueAsync(cancellationToken);

    public async Task<string?> SendRawAsync(
        string command,
        bool expectResponse,
        CancellationToken cancellationToken = default) =>
        await _core.SendRawAsync(command, expectResponse, cancellationToken).ConfigureAwait(false);

    private static PowerSupplySnapshot Convert(Core.PswSnapshot snapshot) =>
        new()
        {
            Timestamp = new DateTimeOffset(snapshot.TimestampUtc, TimeSpan.Zero),
            IsConnected = snapshot.IsConnected,
            Identity = snapshot.Identity,
            IsVerifiedPsw = snapshot.IsVerifiedPsw,
            Capabilities = Convert(snapshot.Capabilities),
            OutputEnabled = snapshot.OutputEnabled,
            SetVoltage = snapshot.SetVoltage,
            SetCurrent = snapshot.SetCurrent,
            Ovp = snapshot.Ovp,
            Ocp = snapshot.Ocp,
            MeasuredVoltage = snapshot.MeasuredVoltage,
            MeasuredCurrent = snapshot.MeasuredCurrent,
            MeasuredPower = snapshot.MeasuredPower,
            ProtectionTripped = snapshot.ProtectionTripped,
            OperationStatus = snapshot.OperationStatus,
            QuestionableStatus = snapshot.QuestionableStatus,
            ControlState = "共享通信内核",
            LastError = snapshot.LastError
        };

    private static PswCapabilities Convert(Core.PswCapabilities capabilities) =>
        new()
        {
            Model = capabilities.Model,
            MaxVoltage = capabilities.MaxVoltage,
            MaxCurrent = capabilities.MaxCurrent,
            MinOvp = capabilities.MinOvp,
            MaxOvp = capabilities.MaxOvp,
            MinOcp = capabilities.MinOcp,
            MaxOcp = capabilities.MaxOcp
        };

    public ValueTask DisposeAsync()
    {
        _core.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class LogAdapter : Core.IPswLog
    {
        private readonly ILogService _log;
        private readonly PowerSupplyEndpoint _endpoint;

        public LogAdapter(ILogService log, PowerSupplyEndpoint endpoint)
        {
            _log = log;
            _endpoint = endpoint;
        }

        public void Write(Core.PswLogEntry entry)
        {
            var direction = entry.Direction switch
            {
                Core.PswLogDirection.Transmit => ScpiLogDirection.Transmit,
                Core.PswLogDirection.Receive => ScpiLogDirection.Receive,
                Core.PswLogDirection.Error => ScpiLogDirection.Error,
                _ => ScpiLogDirection.Information
            };
            _log.WriteAsync(
                    new ScpiLogEntry(
                        DateTimeOffset.UtcNow,
                        entry.SupplyId,
                        _endpoint.DisplayName,
                        direction,
                        entry.Message),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
    }
}
