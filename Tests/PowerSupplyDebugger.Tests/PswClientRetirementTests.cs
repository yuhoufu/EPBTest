using PowerSupply.Core;

namespace PowerSupplyDebugger.Tests;

public sealed class PswClientRetirementTests
{
    [Fact]
    public async Task RetiredClientCannotReconnectOrIssueCommands()
    {
        await using var server = new FakePswServer();
        server.Start();
        using var client = CreateClient(server);
        Assert.False(client.CaptureRetirement().FullyReleased);
        client.Dispose(); client.Dispose();
        Assert.True(client.CaptureRetirement().FullyReleased);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendRawAsync("OUTP ON", false, CancellationToken.None));
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task DisposeAbortsStalledReadAndRejectsAlreadyQueuedWrite()
    {
        await using var server = new FakePswServer();
        server.Start();
        using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        server.ClearCommands(); server.SuppressResponseFor = "SOUR:CURR?";
        var read = client.SendRawAsync("SOUR:CURR?", true, CancellationToken.None);
        await WaitUntilAsync(() => server.Commands.Contains("SOUR:CURR?"));
        var write = client.SetVoltageAsync(19, CancellationToken.None);
        Assert.True(client.CaptureRetirement().PendingOperations >= 2);

        client.Dispose();
        Assert.False(client.IsConnected);
        Assert.NotNull(await Record.ExceptionAsync(() => read));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => write);
        await WaitUntilAsync(() => client.CaptureRetirement().FullyReleased);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("SOUR:VOLT ", StringComparison.Ordinal));
        Assert.Equal(0, client.CaptureRetirement().PendingTransportTasks);
    }

    [Fact]
    public async Task DisposeDuringIdentityReadCannotResurrectConnection()
    {
        await using var server = new FakePswServer { SuppressResponseFor = "*IDN?" };
        server.Start();
        using var client = CreateClient(server);
        var connect = client.ConnectAsync(CancellationToken.None);
        await WaitUntilAsync(() => server.Commands.Contains("*IDN?"));
        client.Dispose();
        Assert.NotNull(await Record.ExceptionAsync(() => connect));
        await WaitUntilAsync(() => client.CaptureRetirement().FullyReleased);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Equal(new[] { "*IDN?" }, server.Commands.ToArray());
    }

    [Fact]
    public async Task RetiredSocketDoesNotHideBlockedOperationCallback()
    {
        await using var server = new FakePswServer(); server.Start();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var log = new CallbackLog(entry =>
        {
            if (entry.Message.StartsWith("已连接", StringComparison.Ordinal))
            { entered.Set(); release.Wait(); }
        });
        using var client = CreateClient(server, log);
        var connect = Task.Run(() => client.ConnectAsync(CancellationToken.None));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            client.Dispose();
            var pending = client.CaptureRetirement();
            Assert.True(pending.TransportReleased);
            Assert.False(pending.OperationsExited);
            Assert.False(pending.FullyReleased);
            release.Set();
            Assert.NotNull(await Record.ExceptionAsync(() => connect));
            await WaitUntilAsync(() => client.CaptureRetirement().FullyReleased);
            Assert.Empty(server.Commands);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task TimedOutNativeReadMustExitBeforeTransportIsReusable()
    {
        await using var server = new FakePswServer(); server.Start();
        using var client = CreateClient(server);
        await client.ConnectAsync(CancellationToken.None);
        server.SuppressResponseFor = "SOUR:CURR?";
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SendRawAsync("SOUR:CURR?", true, CancellationToken.None));
        await WaitUntilAsync(() => client.CaptureRetirement().PendingTransportTasks == 0);
        Assert.False(client.IsConnected);
        server.SuppressResponseFor = null;
        await client.ConnectAsync(CancellationToken.None);
        client.Dispose();
        await WaitUntilAsync(() => client.CaptureRetirement().FullyReleased);
    }

    private static PswTcpClient CreateClient(FakePswServer server, IPswLog? log = null) =>
        new(new PswEndpoint { Id = 1, DisplayName = "退休边界模拟电源", Host = "127.0.0.1",
            Port = server.Port, Terminator = "\\r\\n" }, log, 1000, 500);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(5), "resource did not reach expected state");
            await Task.Delay(10);
        }
    }

    private sealed class CallbackLog(Action<PswLogEntry> write) : IPswLog
    {
        public void Write(PswLogEntry entry) => write(entry);
    }
}
