using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PowerSupply.Core
{
    public interface IPswClient : IDisposable
    {
        PswEndpoint Endpoint { get; }
        bool IsConnected { get; }
        string Identity { get; }
        bool IsVerifiedPsw { get; }
        PswCapabilities Capabilities { get; }
        Task<PswSnapshot> ConnectAsync(CancellationToken token);
        Task DisconnectAsync(CancellationToken token);
        Task<PswSnapshot> ReadSnapshotAsync(CancellationToken token);
        Task<double> SetVoltageAsync(double value, CancellationToken token);
        Task<double> SetCurrentAsync(double value, CancellationToken token);
        Task<double> SetOvpAsync(double value, CancellationToken token);
        Task<double> SetOcpAsync(double value, CancellationToken token);
        Task<PswOutputCommandResult> SetOutputAndReadBackAsync(
            bool enabled,
            CancellationToken token);
        [Obsolete("Use SetOutputAndReadBackAsync. The bool return is the observed output state, not an operation-success flag.")]
        Task<bool> SetOutputAsync(bool enabled, CancellationToken token);
        Task<IReadOnlyList<string>> ReadErrorQueueAsync(CancellationToken token);
        Task<string> SendRawAsync(string command, bool expectResponse, CancellationToken token);
    }
}
