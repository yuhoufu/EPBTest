using PowerSupplyDebugger.Models;

namespace PowerSupplyDebugger.Services;

public interface ILogService
{
    event EventHandler<ScpiLogEntry>? EntryWritten;
    Task WriteAsync(ScpiLogEntry entry, CancellationToken cancellationToken = default);
}
