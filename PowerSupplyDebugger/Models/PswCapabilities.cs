namespace PowerSupplyDebugger.Models;

public sealed record PswCapabilities
{
    public string Model { get; init; } = "未识别";
    public double MaxVoltage { get; init; }
    public double MaxCurrent { get; init; }
    public double? MinOvp { get; init; }
    public double? MaxOvp { get; init; }
    public double? MinOcp { get; init; }
    public double? MaxOcp { get; init; }

    public bool CanWriteSetpoints => MaxVoltage > 0 && MaxCurrent > 0;
    public bool CanWriteOvp => MinOvp.HasValue && MaxOvp.HasValue;
    public bool CanWriteOcp => MinOcp.HasValue && MaxOcp.HasValue;

    public static PswCapabilities FromIdentity(string identity)
    {
        var normalized = identity.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Contains("PSW-30-72", StringComparison.Ordinal) ||
            normalized.Contains("PSW30-72", StringComparison.Ordinal))
        {
            return new PswCapabilities { Model = "PSW 30-72", MaxVoltage = 30, MaxCurrent = 72 };
        }

        if (normalized.Contains("PSW-30-108", StringComparison.Ordinal) ||
            normalized.Contains("PSW30-108", StringComparison.Ordinal))
        {
            return new PswCapabilities { Model = "PSW 30-108", MaxVoltage = 30, MaxCurrent = 108 };
        }

        return new PswCapabilities();
    }

    public PswCapabilities WithProtectionRanges(
        double? minOvp,
        double? maxOvp,
        double? minOcp,
        double? maxOcp) => this with
    {
        MinOvp = IsValidRange(minOvp, maxOvp) ? minOvp : null,
        MaxOvp = IsValidRange(minOvp, maxOvp) ? maxOvp : null,
        MinOcp = IsValidRange(minOcp, maxOcp) ? minOcp : null,
        MaxOcp = IsValidRange(minOcp, maxOcp) ? maxOcp : null
    };

    private static bool IsValidRange(double? minimum, double? maximum) =>
        minimum.HasValue &&
        maximum.HasValue &&
        minimum.Value >= 0 &&
        maximum.Value > minimum.Value;
}
