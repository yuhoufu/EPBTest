using PswOutputState = PowerSupply.Core.PswOutputState;
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
    public async Task TypedOutputResultTreatsObservedOffAsSuccess()
    {
        await using var server = new FakePswServer(outputEnabled: true);
        server.Start();
        await using var client = CreateClient(server);
        await client.ConnectAsync();
        server.ClearCommands();

        var result = await client.SetOutputAndReadBackAsync(false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.CommandWritten);
        Assert.True(result.ReadBackVerified);
        Assert.Equal(PswOutputState.Off, result.RequestedState);
        Assert.Equal(PswOutputState.Off, result.ObservedState);
        Assert.Equal(["OUTP OFF", "OUTP?"], server.Commands.ToArray());
    }

    [Fact]
    public async Task TypedOutputResultReportsObservedOnWithoutBooleanInversion()
    {
        await using var server = new FakePswServer(outputEnabled: true)
        {
            IgnoreOutputWrites = true
        };
        server.Start();
        await using var client = CreateClient(server);
        await client.ConnectAsync();

        var result = await client.SetOutputAndReadBackAsync(false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.CommandWritten);
        Assert.False(result.ReadBackVerified);
        Assert.Equal(PswOutputState.On, result.ObservedState);
        Assert.Equal("OutputReadBackMismatch", result.FailureCode);
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

    [Fact]
    public async Task CancellationDuringQueryFinishesTransactionBeforeNextCommand()
    {
        await using var server = new FakePswServer(outputEnabled: true);
        server.Start();
        await using var client = CreateClient(server);
        await client.ConnectAsync();
        server.ClearCommands();
        server.DelayResponseFor = "OUTP?";
        server.ResponseDelayMs = 250;

        using var cancellation = new CancellationTokenSource();
        var readTask = client.ReadSnapshotAsync(cancellation.Token);
        await WaitUntilAsync(
            () => server.Commands.Contains("OUTP?"),
            TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var snapshot = await readTask;
        Assert.True(snapshot.OutputEnabled);

        server.DelayResponseFor = null;
        Assert.False(await client.SetOutputAsync(false));
        Assert.True(client.IsConnected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等待模拟电源收到命令超时。");
            }

            await Task.Delay(10);
        }
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
