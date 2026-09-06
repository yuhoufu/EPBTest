namespace PowerSupplyDebugger.Models;

public enum ScpiLogDirection
{
    Information,
    Transmit,
    Receive,
    Error
}

public sealed record ScpiLogEntry(
    DateTimeOffset Timestamp,
    int DeviceId,
    string DeviceName,
    ScpiLogDirection Direction,
    string Message)
{
    public string Display =>
        $"{Timestamp:HH:mm:ss.fff} [{DeviceName}] {Direction switch
        {
            ScpiLogDirection.Transmit => "TX",
            ScpiLogDirection.Receive => "RX",
            ScpiLogDirection.Error => "ERR",
            _ => "INFO"
        }}  {Message}";
}
