using System.Net;

namespace PowerSupplyDebugger.Models;

public sealed class PowerSupplyEndpoint
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 2268;
    public string Terminator { get; set; } = "\\r\\n";

    public string ResolveTerminator() => Terminator
        .Replace("\\r", "\r", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal);

    public void Validate()
    {
        if (Id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Id), "设备编号必须大于 0。");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("设备名称不能为空。", nameof(DisplayName));
        }

        if (string.IsNullOrWhiteSpace(Host) ||
            (!IPAddress.TryParse(Host, out _) && Uri.CheckHostName(Host) == UriHostNameType.Unknown))
        {
            throw new ArgumentException("IP 地址或主机名格式无效。", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "端口必须在 1～65535 范围内。");
        }

        if (ResolveTerminator() is not ("\r\n" or "\n" or "\r"))
        {
            throw new ArgumentException("终止符只允许 CRLF、LF 或 CR。", nameof(Terminator));
        }
    }

    public PowerSupplyEndpoint Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Host = Host,
        Port = Port,
        Terminator = Terminator
    };

    public static IReadOnlyList<PowerSupplyEndpoint> CreateDefaults() =>
    [
        new() { Id = 1, DisplayName = "电源 1", Host = "192.168.1.101", Port = 2268 },
        new() { Id = 2, DisplayName = "电源 2", Host = "192.168.1.102", Port = 2268 },
        new() { Id = 3, DisplayName = "电源 3", Host = "192.168.1.103", Port = 2268 },
        new() { Id = 4, DisplayName = "电源 4", Host = "192.168.1.104", Port = 2268 }
    ];
}
