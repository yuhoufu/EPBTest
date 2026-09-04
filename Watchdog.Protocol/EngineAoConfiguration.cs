using System;
using System.Globalization;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // Read-only AO metadata. Calibration commands carry points, never coefficients,
    // wiring changes, file paths, execution permits or production counters.
    public sealed class EngineAoConfiguration
    {
        public double MinVoltage { get; set; }
        public double MaxVoltage { get; set; }
        public double MinPressure { get; set; }
        public double MaxPressure { get; set; }
        public EngineAoDeviceConfiguration[] Devices { get; set; } = Array.Empty<EngineAoDeviceConfiguration>();
        public bool IsStructurallyValid() => new[] { MinVoltage, MaxVoltage, MinPressure, MaxPressure }.All(EngineUiContract.IsFinite) &&
            MinVoltage >= -10 && MinVoltage <= 0 && MaxVoltage > 0 && MaxVoltage <= 10 &&
            MinPressure == 0 && MaxPressure > 0 && MaxPressure <= 1000 && Devices?.Length == 2 &&
            Devices.All(d => d?.IsStructurallyValid() == true && d.Points.All(p => p.Voltage >= MinVoltage && p.Voltage <= MaxVoltage)) &&
            Devices.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count() == 2 &&
            Devices.Select(d => d.PhysicalChannel).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2;
        public EngineAoConfiguration Clone() => new EngineAoConfiguration
        { MinVoltage = MinVoltage, MaxVoltage = MaxVoltage, MinPressure = MinPressure, MaxPressure = MaxPressure,
            Devices = Devices?.Select(d => d?.Clone()).ToArray() };
        public string ComputeSha256() => AoCalibrationCommit.Hash("AoConfigurationV1", new[]
        {
            AoCalibrationCommit.Number(MinVoltage), AoCalibrationCommit.Number(MaxVoltage),
            AoCalibrationCommit.Number(MinPressure), AoCalibrationCommit.Number(MaxPressure)
        }.Concat(Devices.OrderBy(d => d.Name, StringComparer.Ordinal).SelectMany(d => new[]
        { d.Name, d.PhysicalChannel, AoCalibrationCommit.Number(d.ScaleK), AoCalibrationCommit.Number(d.Offset),
            AoCalibrationCommit.PointsHash(d.Points) })).ToArray());
    }

    public sealed class EngineAoDeviceConfiguration
    {
        public string Name { get; set; } = string.Empty;
        public string PhysicalChannel { get; set; } = string.Empty;
        public double ScaleK { get; set; }
        public double Offset { get; set; }
        public EngineAoCalibrationPoint[] Points { get; set; } = Array.Empty<EngineAoCalibrationPoint>();
        public bool IsStructurallyValid() => (Name == "Cylinder1" || Name == "Cylinder2") &&
            !string.IsNullOrWhiteSpace(PhysicalChannel) && PhysicalChannel.Length <= 128 && PhysicalChannel.Trim() == PhysicalChannel &&
            !PhysicalChannel.Any(char.IsControl) && EngineUiContract.IsFinite(ScaleK) && ScaleK > 1e-9 &&
            EngineUiContract.IsFinite(Offset) && Points != null && Points.Length <= 256 && Points.All(p => p?.IsStructurallyValid() == true);
        public EngineAoDeviceConfiguration Clone() => new EngineAoDeviceConfiguration
        { Name = Name, PhysicalChannel = PhysicalChannel, ScaleK = ScaleK, Offset = Offset, Points = Points?.Select(p => p?.Clone()).ToArray() };
    }

    public sealed class EngineAoCalibrationPoint
    {
        public double Voltage { get; set; }
        public double Pressure { get; set; }
        public double? CommandPressure { get; set; }
        public bool IsStructurallyValid() => EngineUiContract.IsFinite(Voltage) && Voltage >= -10 && Voltage <= 10 &&
            EngineUiContract.IsFinite(Pressure) && Pressure >= 0 && Pressure <= 1000 &&
            (!CommandPressure.HasValue || EngineUiContract.IsFinite(CommandPressure.Value) && CommandPressure >= 0 && CommandPressure <= 1000);
        public EngineAoCalibrationPoint Clone() => (EngineAoCalibrationPoint)MemberwiseClone();
    }

    public sealed class AoCalibrationCommit
    {
        public string DeviceName { get; set; } = string.Empty;
        public EngineAoCalibrationPoint[] Points { get; set; } = Array.Empty<EngineAoCalibrationPoint>();
        public bool IsStructurallyValid()
        {
            if ((DeviceName != "Cylinder1" && DeviceName != "Cylinder2") || Points == null || Points.Length < 2 ||
                Points.Length > 256 || Points.Any(p => p?.IsStructurallyValid() != true)) return false;
            var sorted = Points.OrderBy(p => p.Voltage).ToArray();
            return sorted.Skip(1).Select((point, index) => point.Voltage > sorted[index].Voltage &&
                point.Pressure > sorted[index].Pressure).All(valid => valid);
        }
        public AoCalibrationCommit Clone() => new AoCalibrationCommit
        { DeviceName = DeviceName, Points = Points?.Select(p => p?.Clone()).ToArray() };
        public string ComputeSha256() => Hash("AoCalibrationV1", DeviceName, PointsHash(Points));
        internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        internal static string PointsHash(EngineAoCalibrationPoint[] points) => Hash("AoPointsV1", points.OrderBy(p => p.Voltage)
            .ThenBy(p => p.Pressure).SelectMany(p => new[] { Number(p.Voltage), Number(p.Pressure),
                p.CommandPressure.HasValue ? Number(p.CommandPressure.Value) : "absent" }).ToArray());
        internal static string Hash(string prefix, params string[] fields) => SupervisorProtocol.ComputeTextSha256(prefix + "\n" +
            string.Concat(fields.Select(f => f.Length.ToString(CultureInfo.InvariantCulture) + ":" + f)));
    }
}
