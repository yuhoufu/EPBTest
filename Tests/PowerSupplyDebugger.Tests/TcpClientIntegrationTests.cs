using PowerSupplyDebugger.Models;
using PowerSupplyDebugger.Services;

namespace PowerSupplyDebugger.Tests;

public sealed class TcpClientIntegrationTests
{
    [Fact]
    public async Task ConnectIsReadOnlyPreservesOutputAndUsesCrLf()
    {
        await using var server = new FakePswServer(outputEnabled: true);
        server.Start();
        await using var client = CreateClient(server);

        var snapshot = await client.ConnectAsync();

        Assert.True(snapshot.IsConnected);
        Assert.True(snapshot.IsVerifiedPsw);
        Assert.True(snapshot.OutputEnabled);
        Assert.Equal(12.5, snapshot.SetVoltage);
        Assert.Equal(3.25, snapshot.SetCurrent);
        Assert.All(server.RawFrames, frame => Assert.EndsWith("\r\n", frame, StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, command => !command.Contains('?'));
        Assert.DoesNotContain(server.Commands, command => command.Equals("SYST:ERR?", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnverifiedIdentityKeepsConnectionReadOnly()
    {
        await using var server = new FakePswServer("ACME,MODEL-1,SN,1.00", false);
        server.Start();
        await using var client = CreateClient(server);
        var snapshot = await client.ConnectAsync();

        Assert.False(snapshot.IsVerifiedPsw);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetVoltageAsync(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetOutputAsync(true));
        Assert.DoesNotContain(server.Commands, command => command == "SOUR:VOLT 10" || command == "OUTP ON");
    }

    [Fact]
    public async Task WritesAreSerializedAndImmediatelyReadBack()
    {
        await using var server = new FakePswServer(outputEnabled: false);
        server.Start();
        await using var client = CreateClient(server);
        await client.ConnectAsync();
        server.ClearCommands();

        await Task.WhenAll(client.SetVoltageAsync(18.25), client.SetCurrentAsync(6.5));

        var commands = server.Commands.ToArray();
        Assert.Equal(4, commands.Length);
        Assert.True(
            commands.SequenceEqual(["SOUR:VOLT 18.25", "SOUR:VOLT?", "SOUR:CURR 6.5", "SOUR:CURR?"]) ||
            commands.SequenceEqual(["SOUR:CURR 6.5", "SOUR:CURR?", "SOUR:VOLT 18.25", "SOUR:VOLT?"]));
    }

    [Fact]
    public async Task OutputWriteRequiresMatchingReadBack()
    {
        await using var server = new FakePswServer(outputEnabled: false);
        server.Start();
        await using var client = CreateClient(server);
        await client.ConnectAsync();
        server.ClearCommands();

        Assert.True(await client.SetOutputAsync(true));
        Assert.Equal(["OUTP ON", "OUTP?"], server.Commands.ToArray());
    }

    [Fact]
    public async Task FourDevicesCanConnectConcurrently()
    {
        var servers = Enumerable.Range(0, 4)
            .Select(_ => new FakePswServer(outputEnabled: false))
            .ToArray();
        foreach (var server in servers)
        {
            server.Start();
        }

        var clients = servers.Select(CreateClient).ToArray();
        try
        {
            var snapshots = await Task.WhenAll(clients.Select(client => client.ConnectAsync()));
            Assert.Equal(4, snapshots.Length);
            Assert.All(snapshots, snapshot => Assert.True(snapshot.IsVerifiedPsw));
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }

            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ResponseTimeoutDisconnectsClient()
    {
        await using var server = new FakePswServer(outputEnabled: false)
        {
            SuppressResponseFor = "MEAS:ALL:DC?"
        };
        server.Start();
        await using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync());
        Assert.Contains("MEAS:ALL:DC?", exception.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task RemoteCloseIsReportedAndConnectionIsDropped()
    {
        await using var server = new FakePswServer(outputEnabled: false);
        server.Start();
        await using var client = CreateClient(server);
        await client.ConnectAsync();
        server.CloseConnectionFor = "OUTP?";

        await Assert.ThrowsAsync<IOException>(() => client.ReadSnapshotAsync());
        Assert.False(client.IsConnected);
    }

    private static PswTcpClient CreateClient(FakePswServer server) =>
        new(
            new PowerSupplyEndpoint
            {
                Id = 1,
                DisplayName = "模拟电源",
                Host = "127.0.0.1",
                Port = server.Port,
                Terminator = "\\r\\n"
            },
            new TestLogService());
}
