using System.Text;
using PowerSupplyDebugger.Models;

namespace PowerSupplyDebugger.Services;

public sealed class FileLogService : ILogService, IDisposable
{
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLogService(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EPBTest",
            "PowerSupplyDebugger",
            "Logs");
        Directory.CreateDirectory(_logDirectory);
    }

    public event EventHandler<ScpiLogEntry>? EntryWritten;

    public async Task WriteAsync(ScpiLogEntry entry, CancellationToken cancellationToken = default)
    {
        EntryWritten?.Invoke(this, entry);

        var path = Path.Combine(_logDirectory, $"{entry.Timestamp:yyyy-MM-dd}.log");
        var line = $"{entry.Timestamp:O}\t{entry.DeviceId}\t{entry.DeviceName}\t{entry.Direction}\t{Sanitize(entry.Message)}{Environment.NewLine}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Sanitize(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);

    public void Dispose() => _gate.Dispose();
}
