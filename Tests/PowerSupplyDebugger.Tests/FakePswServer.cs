using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PowerSupplyDebugger.Tests;

internal sealed class FakePswServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Task> _connections = [];
    private Task? _acceptTask;
    private readonly object _stateLock = new();
    private double _voltage = 12.5;
    private double _current = 3.25;
    private double _ovp = 31;
    private double _ocp = 73;
    private bool _outputEnabled;

    public FakePswServer(string identity = "GW-INSTEK,PSW-30-72,SN0001,1.70", bool outputEnabled = true)
    {
        Identity = identity;
        _outputEnabled = outputEnabled;
    }

    public string Identity { get; set; }
    public string? SuppressResponseFor { get; set; }
    public string? DelayResponseFor { get; set; }
    public int ResponseDelayMs { get; set; }
    public string? CloseConnectionFor { get; set; }
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentQueue<string> RawFrames { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_cancellation.Token);
    }

    public void ClearCommands()
    {
        while (Commands.TryDequeue(out _))
        {
        }

        while (RawFrames.TryDequeue(out _))
        {
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                var task = HandleClientAsync(client, cancellationToken);
                lock (_connections)
                {
                    _connections.Add(task);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var frame = new List<byte>();
            var buffer = new byte[1];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                frame.Add(buffer[0]);
                if (buffer[0] != (byte)'\n')
                {
                    continue;
                }

                var raw = Encoding.ASCII.GetString([.. frame]);
                frame.Clear();
                RawFrames.Enqueue(raw);
                var command = raw.TrimEnd('\r', '\n');
                Commands.Enqueue(command);

                if (string.Equals(command, CloseConnectionFor, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                ApplyWrite(command);
                if (!command.Contains('?') ||
                    string.Equals(command, SuppressResponseFor, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(command, DelayResponseFor, StringComparison.OrdinalIgnoreCase) &&
                    ResponseDelayMs > 0)
                {
                    await Task.Delay(ResponseDelayMs, cancellationToken);
                }

                var response = GetResponse(command) + "\r\n";
                var bytes = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
    }

    private void ApplyWrite(string command)
    {
        lock (_stateLock)
        {
            if (TryReadArgument(command, "SOUR:VOLT ", out var voltage))
            {
                _voltage = voltage;
            }
            else if (TryReadArgument(command, "SOUR:CURR ", out var current))
            {
                _current = current;
            }
            else if (TryReadArgument(command, "SOUR:VOLT:PROT ", out var ovp))
            {
                _ovp = ovp;
            }
            else if (TryReadArgument(command, "SOUR:CURR:PROT ", out var ocp))
            {
                _ocp = ocp;
            }
            else if (command.Equals("OUTP ON", StringComparison.OrdinalIgnoreCase))
            {
                _outputEnabled = true;
            }
            else if (command.Equals("OUTP OFF", StringComparison.OrdinalIgnoreCase))
            {
                _outputEnabled = false;
            }
        }
    }

    private string GetResponse(string command)
    {
        lock (_stateLock)
        {
            return command.ToUpperInvariant() switch
            {
                "*IDN?" => Identity,
                "SOUR:VOLT:PROT? MIN" => "1",
                "SOUR:VOLT:PROT? MAX" => "33",
                "SOUR:CURR:PROT? MIN" => "1",
                "SOUR:CURR:PROT? MAX" => "79.2",
                "OUTP?" => _outputEnabled ? "1" : "0",
                "SOUR:VOLT?" => Format(_voltage),
                "SOUR:CURR?" => Format(_current),
                "MEAS:ALL:DC?" => $"{Format(_voltage)},{Format(_current)},{Format(_voltage * _current)}",
                "STAT:OPER:COND?" => "256",
                "STAT:QUES:COND?" => "0",
                "OUTP:PROT:TRIP?" => "0",
                "SOUR:VOLT:PROT?" => Format(_ovp),
                "SOUR:CURR:PROT?" => Format(_ocp),
                "SYST:COMM:RLST?" => "REM",
                "SYST:ERR?" => "0,\"No error\"",
                _ => "0"
            };
        }
    }

    private static bool TryReadArgument(string command, string prefix, out double value)
    {
        value = 0;
        return command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               double.TryParse(command[prefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (SocketException)
            {
            }
        }

        Task[] connections;
        lock (_connections)
        {
            connections = [.. _connections];
        }

        try
        {
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _cancellation.Dispose();
    }
}
