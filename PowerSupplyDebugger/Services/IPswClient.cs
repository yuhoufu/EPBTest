using PowerSupplyDebugger.Models;

namespace PowerSupplyDebugger.Services;

public interface IPswClient : IAsyncDisposable
{
    PowerSupplyEndpoint Endpoint { get; }
    bool IsConnected { get; }
    string Identity { get; }
    bool IsVerifiedPsw { get; }
    PswCapabilities Capabilities { get; }

    Task<PowerSupplySnapshot> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<PowerSupplySnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
    Task<double> SetVoltageAsync(double value, CancellationToken cancellationToken = default);
    Task<double> SetCurrentAsync(double value, CancellationToken cancellationToken = default);
    Task<double> SetOvpAsync(double value, CancellationToken cancellationToken = default);
    Task<double> SetOcpAsync(double value, CancellationToken cancellationToken = default);
    Task<PowerSupply.Core.PswOutputCommandResult> SetOutputAndReadBackAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
    Task<bool> SetOutputAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ReadErrorQueueAsync(CancellationToken cancellationToken = default);
    Task<string?> SendRawAsync(string command, bool expectResponse, CancellationToken cancellationToken = default);
}
