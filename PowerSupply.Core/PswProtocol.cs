using System;
using System.Globalization;
using System.IO;

namespace PowerSupply.Core
{
    public static class PswProtocol
    {
        public static bool IsVerifiedIdentity(string identity)
        {
            var caps = PswCapabilities.FromIdentity(identity);
            var text = (identity ?? string.Empty).ToUpperInvariant();
            return caps.CanWriteSetpoints &&
                   (text.Contains("GW INSTEK") || text.Contains("GOOD WILL") || text.Contains("PSW"));
        }

        public static double ParseNumber(string response, string command)
        {
            if (!double.TryParse((response ?? string.Empty).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value) ||
                double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException($"{command} 返回的数值无效：{response}");
            return value;
        }

        public static int ParseInteger(string response, string command)
        {
            var value = ParseNumber(response, command);
            if (value < int.MinValue || value > int.MaxValue)
                throw new InvalidDataException($"{command} 返回值超出整数范围：{response}");
            return checked((int)value);
        }

        public static bool ParseBoolean(string response, string command)
        {
            var value = (response ?? string.Empty).Trim().ToUpperInvariant();
            if (value == "1" || value == "ON") return true;
            if (value == "0" || value == "OFF") return false;
            throw new InvalidDataException($"{command} 返回的开关状态无效：{response}");
        }

        public static Tuple<double, double, double> ParseMeasurement(string response)
        {
            var parts = (response ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) throw new InvalidDataException($"MEAS:ALL:DC? 返回格式无效：{response}");
            var voltage = ParseNumber(parts[0], "MEAS:ALL:DC?");
            var current = ParseNumber(parts[1], "MEAS:ALL:DC?");
            var power = parts.Length >= 3 ? ParseNumber(parts[2], "MEAS:ALL:DC?") : voltage * current;
            return Tuple.Create(voltage, current, power);
        }

        public static string FormatNumber(double value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }

        public static void ValidateSetpoint(double value, double maximum, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > maximum)
                throw new ArgumentOutOfRangeException(name, $"{name} 必须在 0～{maximum:0.###} 范围内。");
        }

        public static void ValidateRange(double value, double minimum, double maximum, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, $"{name} 必须在 {minimum:0.###}～{maximum:0.###} 范围内。");
        }
    }
}
