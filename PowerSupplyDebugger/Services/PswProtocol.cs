using System.Globalization;
using System.IO;
using PowerSupplyDebugger.Models;

namespace PowerSupplyDebugger.Services;

public static class PswProtocol
{
    public static bool IsVerifiedIdentity(string identity)
    {
        var normalized = identity.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return normalized.Contains("GW-INSTEK", StringComparison.Ordinal) &&
               (normalized.Contains("PSW-30-72", StringComparison.Ordinal) ||
                normalized.Contains("PSW30-72", StringComparison.Ordinal) ||
                normalized.Contains("PSW-30-108", StringComparison.Ordinal) ||
                normalized.Contains("PSW30-108", StringComparison.Ordinal));
    }

    public static double ParseNumber(string response, string command)
    {
        if (!double.TryParse(
                response.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) ||
            double.IsNaN(value) ||
            double.IsInfinity(value))
        {
            throw new InvalidDataException($"{command} 返回的数值无效：{response}");
        }

        return value;
    }

    public static int ParseInteger(string response, string command)
    {
        var value = ParseNumber(response, command);
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new InvalidDataException($"{command} 返回值超出整数范围：{response}");
        }

        return checked((int)value);
    }

    public static bool ParseBoolean(string response, string command) =>
        response.Trim().ToUpperInvariant() switch
        {
            "1" or "ON" => true,
            "0" or "OFF" => false,
            _ => throw new InvalidDataException($"{command} 返回的开关状态无效：{response}")
        };

    public static (double Voltage, double Current, double Power) ParseMeasurement(string response)
    {
        var parts = response.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidDataException($"MEAS:ALL:DC? 返回格式无效：{response}");
        }

        var voltage = ParseNumber(parts[0], "MEAS:ALL:DC?");
        var current = ParseNumber(parts[1], "MEAS:ALL:DC?");
        var power = parts.Length >= 3
            ? ParseNumber(parts[2], "MEAS:ALL:DC?")
            : voltage * current;
        return (voltage, current, power);
    }

    public static string FormatNumber(double value) =>
        value.ToString("0.########", CultureInfo.InvariantCulture);

    public static void ValidateSetpoint(double value, double maximum, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"{parameterName} 必须在 0～{maximum:0.###} 范围内。");
        }
    }

    public static void ValidateRange(double value, double minimum, double maximum, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"{parameterName} 必须在 {minimum:0.###}～{maximum:0.###} 范围内。");
        }
    }
}
