using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NationalInstruments.DAQmx;
using NIDaqTask = NationalInstruments.DAQmx.Task;

namespace MTTFTest.SafetyHardware
{
    public sealed class SafetyPhysicalChannel
    {
        public string Name { get; private set; }
        public string PhysicalChannel { get; private set; }
        public double Scale { get; private set; }
        public double Offset { get; private set; }
        public double ZeroDrift { get; private set; }
        public bool IsCurrent { get; private set; }

        public static SafetyPhysicalChannel[] Load(string configDirectory, IEnumerable<string> pressureNames)
        {
            var test = XDocument.Load(Path.Combine(configDirectory, "TestConfig.xml"));
            var required = new HashSet<string>(pressureNames, StringComparer.Ordinal);
            var currentNames = new HashSet<string>(StringComparer.Ordinal);
            // EpbCycleRunnerConfig also contains Record elements, but those
            // describe timing, not enabled physical feedback channels.
            var records = test.Root?.Element("EpbRecords")?.Elements("Record");
            if (records == null)
                throw new SafetyHardwareConfigurationException("PhysicalFeedbackCurrentChannelsMissing");
            foreach (var record in records)
            {
                bool enabled;
                if (!bool.TryParse((string)record.Element("Enabled"), out enabled))
                    throw new SafetyHardwareConfigurationException("PhysicalFeedbackChannelEnabledInvalid");
                if (!enabled) continue;
                int id;
                if (!int.TryParse((string)record.Element("Id"), out id) || id < 1 || id > 12 ||
                    !currentNames.Add("EPB" + id.ToString(CultureInfo.InvariantCulture) + "_current"))
                    throw new SafetyHardwareConfigurationException("PhysicalFeedbackChannelIdInvalid");
            }
            if (currentNames.Count == 0)
                throw new SafetyHardwareConfigurationException("PhysicalFeedbackCurrentChannelsMissing");
            required.UnionWith(currentNames);
            var inputs = XDocument.Load(Path.Combine(configDirectory, "AIConfig.xml"));
            var result = new List<SafetyPhysicalChannel>();
            foreach (var record in inputs.Descendants("Records"))
            {
                var name = ((string)record.Element("参数名") ?? string.Empty).Trim();
                if (!required.Contains(name)) continue;
                var enabled = ((string)record.Element("是否启用") ?? string.Empty).Trim();
                if (enabled != "1" && !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
                    throw new SafetyHardwareConfigurationException("PhysicalFeedbackInputDisabled:" + name);
                result.Add(new SafetyPhysicalChannel
                {
                    Name = name,
                    PhysicalChannel = ((string)record.Element("物理通道") ?? string.Empty).Trim(),
                    Scale = Number(record, "变换斜率"),
                    Offset = Number(record, "变换截距"),
                    ZeroDrift = Number(record, "零位漂移"),
                    IsCurrent = currentNames.Contains(name)
                });
            }
            if (result.Count != required.Count || result.Select(x => x.Name).Distinct().Count() != required.Count ||
                result.Select(x => x.PhysicalChannel).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count ||
                result.Any(x => string.IsNullOrWhiteSpace(SafetyHardwareConfiguration.DeviceName(x.PhysicalChannel)) || x.Scale == 0))
                throw new SafetyHardwareConfigurationException("PhysicalFeedbackInputsMissingOrDuplicate");
            return result.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        }

        private static double Number(XElement record, string name)
        {
            double result;
            if (!double.TryParse((string)record.Element(name), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
                double.IsNaN(result) || double.IsInfinity(result))
                throw new SafetyHardwareConfigurationException("PhysicalFeedbackCalibrationInvalid:" + name);
            return result;
        }
    }

    /// <summary>
    /// One finite AI task per device, shared by current and pressure inputs.
    /// A fresh acquisition starts on every frame. No buffered samples from a
    /// previous frame can be re-timestamped as new evidence.
    /// </summary>
    public sealed class SafetyPhysicalFeedbackProbe : IDisposable
    {
        private sealed class Group
        {
            internal NIDaqTask Task;
            internal AnalogMultiChannelReader Reader;
            internal SafetyPhysicalChannel[] Channels;
        }

        private readonly List<Group> _groups = new List<Group>();
        private readonly int _samples;

        public SafetyPhysicalFeedbackProbe(SafetyPhysicalChannel[] channels, double sampleRateHz, int samples, int timeoutMs)
        {
            _samples = samples;
            try
            {
                foreach (var source in channels.GroupBy(x => SafetyHardwareConfiguration.DeviceName(x.PhysicalChannel), StringComparer.OrdinalIgnoreCase))
                {
                    var group = new Group { Task = new NIDaqTask(), Channels = source.ToArray() };
                    _groups.Add(group);
                    foreach (var channel in group.Channels)
                        group.Task.AIChannels.CreateVoltageChannel(channel.PhysicalChannel, string.Empty,
                            AITerminalConfiguration.Rse, -10, 10, AIVoltageUnits.Volts);
                    group.Task.Timing.ConfigureSampleClock(string.Empty, sampleRateHz, SampleClockActiveEdge.Rising,
                        SampleQuantityMode.FiniteSamples, samples);
                    group.Task.Stream.Timeout = Math.Max(1, timeoutMs);
                    group.Reader = new AnalogMultiChannelReader(group.Task.Stream);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Dictionary<string, SafetyFeedbackSample> Read()
        {
            var frame = new Dictionary<string, SafetyFeedbackSample>(StringComparer.Ordinal);
            foreach (var group in _groups)
            {
                var acquiredMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                group.Task.Start();
                try
                {
                    var values = group.Reader.ReadMultiSample(_samples);
                    if (values.GetLength(0) != group.Channels.Length || values.GetLength(1) != _samples)
                        throw new SafetyHardwareUnavailableException("PhysicalFeedbackIncompleteBatch");
                    for (var row = 0; row < group.Channels.Length; row++)
                    {
                        var channel = group.Channels[row];
                        var worst = double.NegativeInfinity;
                        for (var column = 0; column < _samples; column++)
                        {
                            var raw = values[row, column];
                            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= -10 || raw >= 10)
                            {
                                worst = double.NaN;
                                break;
                            }
                            var value = (raw - channel.ZeroDrift) * channel.Scale + channel.Offset;
                            if (double.IsNaN(value) || double.IsInfinity(value)) { worst = double.NaN; break; }
                            worst = Math.Max(worst, channel.IsCurrent ? Math.Abs(value) : value);
                        }
                        frame.Add(channel.Name, new SafetyFeedbackSample(worst, acquiredMs));
                    }
                }
                finally { group.Task.Stop(); }
            }
            return frame;
        }

        public void Dispose()
        {
            foreach (var group in _groups) try { group.Task.Dispose(); } catch { }
            _groups.Clear();
        }
    }
}
