using PowerSupplyDebugger.Models;
using PowerSupplyDebugger.Services;

namespace PowerSupplyDebugger.Tests;

internal sealed class TestLogService : ILogService
{
    public event EventHandler<ScpiLogEntry>? EntryWritten;
    public List<ScpiLogEntry> Entries { get; } = [];

    public Task WriteAsync(ScpiLogEntry entry, CancellationToken cancellationToken = default)
    {
        lock (Entries)
        {
            Entries.Add(entry);
        }

        EntryWritten?.Invoke(this, entry);
        return Task.CompletedTask;
    }
}
