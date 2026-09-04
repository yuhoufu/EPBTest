using System;
using System.Globalization;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // Passive AI scaling metadata. No device handle, file path, output or permit.
    public sealed class EngineDaqConfiguration
    {
        public EngineDaqChannelConfiguration[] Channels { get; set; } = Array.Empty<EngineDaqChannelConfiguration>();

        public bool IsStructurallyValid() => Channels != null && Channels.Length > 0 && Channels.Length <= 64 &&
            Channels.All(c => c?.IsStructurallyValid() == true) &&
            Channels.Select(c => c.Sequence).Distinct().Count() == Channels.Length &&
            Channels.Select(c => c.PhysicalChannel).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Channels.Length &&
            Channels.Where(c => c.Enabled).Select(c => c.ParameterName).Distinct(StringComparer.Ordinal).Count() == Channels.Count(c => c.Enabled);

        public EngineDaqConfiguration Clone() => new EngineDaqConfiguration
        { Channels = Channels?.Select(c => c?.Clone()).ToArray() };

        public string ComputeSha256()
        {
            // Length-delimited fields avoid delimiter injection and reflection-order hashes.
            var fields = Channels.OrderBy(c => c.Sequence).SelectMany(c => new[]
            {
                c.Sequence.ToString(CultureInfo.InvariantCulture), c.PhysicalChannel, c.ParameterName, c.Unit,
                c.Slope.ToString("R", CultureInfo.InvariantCulture), c.Intercept.ToString("R", CultureInfo.InvariantCulture),
                c.ParameterType, c.Enabled ? "1" : "0", c.ZeroOffset.ToString("R", CultureInfo.InvariantCulture)
            });
            return SupervisorProtocol.ComputeTextSha256("DaqConfigurationV1\n" + string.Concat(fields.Select(f =>
                f.Length.ToString(CultureInfo.InvariantCulture) + ":" + f)));
        }
    }

    public sealed class EngineDaqChannelConfiguration
    {
        public int Sequence { get; set; }
        public string PhysicalChannel { get; set; } = string.Empty;
        public string ParameterName { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public double Slope { get; set; }
        public double Intercept { get; set; }
        public string ParameterType { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public double ZeroOffset { get; set; }
        public EngineDaqChannelConfiguration Clone() => (EngineDaqChannelConfiguration)MemberwiseClone();
        public bool IsStructurallyValid() => Sequence > 0 && Sequence <= 4096 &&
            TextValid(PhysicalChannel, 128) && TextValid(ParameterName, 128) && TextValid(Unit, 32) &&
            (ParameterType == "电流" || ParameterType == "管路压力" || ParameterType == "夹紧力") &&
            EngineUiContract.IsFinite(Slope) && Slope != 0 && EngineUiContract.IsFinite(Intercept) && EngineUiContract.IsFinite(ZeroOffset);
        private static bool TextValid(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
            value.Length <= maximum && value.Trim() == value && !value.Any(char.IsControl);
    }
}
