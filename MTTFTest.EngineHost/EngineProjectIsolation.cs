using System;
using System.Linq;
using Config;

namespace MTTFTest.EngineHost
{
    internal static class EngineProjectIsolation
    {
        internal static int[] Resolve(string scope)
        {
            var parts = (scope ?? string.Empty).Split(':');
            var number = parts.Length == 2 ? parts[1] : string.Empty;
            if (parts.Length == 2 && parts[0].Equals("DAQ", StringComparison.OrdinalIgnoreCase) && number.StartsWith("Dev", StringComparison.OrdinalIgnoreCase))
                number = number.Substring(3);
            if (parts.Length == 2 && int.TryParse(number, out var id) && number == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                if (parts[0].Equals("Channel", StringComparison.OrdinalIgnoreCase) && id >= 1 && id <= 12) return new[] { id };
                if (parts[0].Equals("Power", StringComparison.OrdinalIgnoreCase) && id >= 1 && id <= 4)
                    return Enumerable.Range((id - 1) * 3 + 1, 3).ToArray();
                if ((parts[0].Equals("Hydraulic", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("DAQ", StringComparison.OrdinalIgnoreCase)) && id >= 1 && id <= 2)
                    return Enumerable.Range((id - 1) * 6 + 1, 6).ToArray();
            }
            // Unknown/shared topology is never treated as an empty isolation.
            return Enumerable.Range(1, 12).ToArray();
        }

        internal static bool Apply(TestConfig configuration, string[] scopes)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            var changed = false;
            foreach (var scope in scopes ?? Array.Empty<string>())
            foreach (var channel in Resolve(scope))
            {
                var record = configuration.GetEpbRecord(channel);
                if (record.Enabled) { record.Enabled = false; changed = true; }
                if (record.PermanentAlarmLatched) continue;
                record.PermanentAlarmLatched = true;
                record.PermanentAlarmCode = "V3RecoveryKernelIsolation";
                record.PermanentAlarmReason = "ProjectInheritedIsolation:" + scope;
                record.PermanentAlarmUtc = DateTime.UtcNow;
                changed = true;
            }
            return changed;
        }
    }
}
