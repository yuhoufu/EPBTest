using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Config;

namespace AdaptiveControlTests
{
    // Test-only device response. It never publishes a controller event or writes a trial record.
    internal sealed class SimulatedEpbPlant
    {
        private readonly object _gate = new object();
        private readonly Func<long> _clock;
        internal SimulatedEpbPlant(Func<long> clock = null) { _clock = clock ?? Stopwatch.GetTimestamp; }
        private readonly Dictionary<string, bool> _digital = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _analog = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, long> _motorStarted = new Dictionary<int, long>();
        private readonly Dictionary<int, int> _direction = new Dictionary<int, int>();
        private readonly Dictionary<int, PowerSupplyCoordinatorTests.FakePswClient> _power = new Dictionary<int, PowerSupplyCoordinatorTests.FakePswClient>();
        private readonly Dictionary<int, double> _pressure = new Dictionary<int, double>();
        private readonly Dictionary<int, long> _pressureTick = new Dictionary<int, long>();
        private DoConfig _do;
        private AoConfig _ao;
        internal int EnergizationEdges { get; private set; }

        internal void Configure(DoConfig config) { lock (_gate) _do = config; }
        internal void Configure(AoConfig config) { lock (_gate) _ao = config; }
        internal void AttachPower(int group, PowerSupplyCoordinatorTests.FakePswClient power)
        { lock (_gate) _power[group] = power; }
        internal void WriteAnalog(string physical, double voltage)
        { lock (_gate) _analog[physical] = voltage; }

        internal void WriteDigital(IReadOnlyList<string> lines, bool[] states)
        {
            lock (_gate)
            {
                if (lines.Count != states.Length) throw new InvalidOperationException("Digital vector length mismatch");
                for (var index = 0; index < lines.Count; index++) _digital[lines[index]] = states[index];
                foreach (var record in _do.Epb.Where(record => record.Enabled))
                {
                    var forward = IsOn(record.Pos);
                    var reverse = IsOn(record.Neg);
                    if (forward && reverse) throw new InvalidOperationException("Simulated motor driven in both directions");
                    var direction = forward ? 1 : reverse ? -1 : 0;
                    _direction.TryGetValue(record.Channel, out var previous);
                    if (direction != previous)
                    {
                        _direction[record.Channel] = direction;
                        _motorStarted[record.Channel] = _clock();
                        if (direction != 0) EnergizationEdges++;
                    }
                }
            }
        }

        private bool IsOn(string line) => line != null && _digital.TryGetValue(line, out var on) && on;

        internal double[,] Read(AiConfigDetailRecord[] records, int samples, double rate)
        {
            var values = new double[records.Length, samples];
            lock (_gate)
            {
                var tick = _clock();
                for (var row = 0; row < records.Length; row++)
                {
                    var record = records[row];
                    if (record.变换斜率 == 0) throw new InvalidOperationException("AI simulation requires nonzero calibration slope");
                    for (var column = 0; column < samples; column++)
                    {
                        var sampleTick = tick - (long)((samples - column - 1) * Stopwatch.Frequency / rate);
                        var engineering = EngineeringValue(record.参数名, sampleTick);
                        values[row, column] = (engineering - record.变换截距) / record.变换斜率 + record.零位漂移;
                    }
                }
            }
            return values;
        }

        private double EngineeringValue(string parameter, long tick)
        {
            if (parameter.StartsWith("EPB", StringComparison.OrdinalIgnoreCase) && parameter.EndsWith("_current", StringComparison.OrdinalIgnoreCase))
            {
                var channel = int.Parse(parameter.Substring(3, parameter.Length - 11));
                var record = _do.Epb.First(item => item.Channel == channel);
                if (!_direction.TryGetValue(channel, out var direction) || direction == 0) return 0;
                var group = record.PowerGroup ?? ((channel - 1) / 3 + 1);
                if (!_power.TryGetValue(group, out var supply) || !supply.OutputEnabled) return 0;
                var age = Math.Max(0, (tick - _motorStarted[channel]) / (double)Stopwatch.Frequency);
                // A finite travel phase followed by a rising load; the real controller must cut power.
                if (direction < 0)
                    return Math.Min(22, 0.8 + (age < 1.2 ? 15 * Math.Exp(-age / 0.08) : (age - 1.2) * 35));
                return Math.Min(22, 0.8 + Math.Max(0, age - 0.15) * 35);
            }
            if (parameter.StartsWith("Pressure_", StringComparison.OrdinalIgnoreCase))
            {
                var id = int.Parse(parameter.Substring(9));
                var valve = _do.Pressure.FirstOrDefault(item => item.Id == id);
                var device = _ao.Devices.Values.FirstOrDefault(item => item.Name == "Cylinder" + id);
                var target = 0.0;
                if (valve != null && device != null && IsOn(valve.Physical) && _analog.TryGetValue(device.PhysicalChannel, out var voltage))
                    target = Math.Max(0, voltage * device.ScaleK + device.Offset);
                _pressure.TryGetValue(id, out var pressure);
                _pressureTick.TryGetValue(id, out var previous);
                var elapsed = previous == 0 ? 0 : Math.Max(0, (tick - previous) / (double)Stopwatch.Frequency);
                pressure += (target - pressure) * (1 - Math.Exp(-15 * elapsed));
                _pressure[id] = pressure;
                _pressureTick[id] = Math.Max(tick, previous);
                return pressure;
            }
            return 0;
        }
    }
}
