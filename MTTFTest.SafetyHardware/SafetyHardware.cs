using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using NationalInstruments.DAQmx;
using PowerSupply.Core;
using NIDaqTask = NationalInstruments.DAQmx.Task;

namespace MTTFTest.SafetyHardware
{
    public sealed class SafetyHardwareConfigurationException : Exception
    {
        public SafetyHardwareConfigurationException(string message)
            : base("SafetyAgentConfigInvalid:" + (message ?? string.Empty)) { }
    }

    public sealed class SafetyHardwareUnavailableException : Exception
    {
        public SafetyHardwareUnavailableException(string message, Exception inner = null)
            : base(message ?? "SafetyHardwareUnavailable", inner) { }
    }

    public sealed class SafetyPressureChannel
    {
        public int HydraulicId { get; internal set; }
        public string Name { get; internal set; }
        public string PhysicalChannel { get; internal set; }
        public double Scale { get; internal set; }
        public double Offset { get; internal set; }
        public double ZeroDrift { get; internal set; }
    }

    public sealed class SafetyPowerSupply
    {
        public int Id { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Host { get; internal set; }
        public int Port { get; internal set; }
        public string Terminator { get; internal set; }
    }

    public sealed class SafetyHardwareConfiguration
    {
        public string[] DoPhysicalLines { get; private set; }
        public string[] AoPhysicalChannels { get; private set; }
        public SafetyPressureChannel[] PressureChannels { get; private set; }
        public SafetyPowerSupply[] PowerSupplies { get; private set; }

        public static SafetyHardwareConfiguration Load(
            string configDirectory,
            IEnumerable<string> requiredPressureChannels)
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new SafetyHardwareConfigurationException("ConfigDirectoryMissing");
            var root = Path.GetFullPath(configDirectory);
            if (!Directory.Exists(root))
                throw new SafetyHardwareConfigurationException("ConfigDirectoryMissing");

            var required = new HashSet<string>(
                requiredPressureChannels ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (required.Count == 0)
                throw new SafetyHardwareConfigurationException("PressureChannelListEmpty");

            return new SafetyHardwareConfiguration
            {
                DoPhysicalLines = LoadDo(Path.Combine(root, "DOConfig.xml")),
                AoPhysicalChannels = LoadAo(Path.Combine(root, "AOConfig.xml")),
                PressureChannels = LoadPressure(Path.Combine(root, "AIConfig.xml"), required),
                PowerSupplies = LoadPower(Path.Combine(root, "PowerSupplyConfig.xml"))
            };
        }

        private static string[] LoadDo(string path)
        {
            var root = LoadRoot(path, "DOConfig");
            var lines = new List<string>();
            var epb = root.Element("EPB");
            if (epb != null)
            {
                foreach (var record in epb.Elements("Record").Where(IsEnabled))
                {
                    lines.Add(RequiredElement(record, "正"));
                    lines.Add(RequiredElement(record, "反"));
                }
            }
            var pressure = root.Element("Pressure");
            if (pressure != null)
                lines.AddRange(pressure.Elements("Record").Where(IsEnabled)
                    .Select(record => RequiredElement(record, "物理通道")));
            return ValidatePhysicalChannels(lines, "DoPhysicalChannels");
        }

        private static string[] LoadAo(string path)
        {
            var root = LoadRoot(path, "AOConfig");
            var minimum = RequiredDouble(root.Element("MinVoltage"), "AoMinVoltage");
            var maximum = RequiredDouble(root.Element("MaxVoltage"), "AoMaxVoltage");
            if (minimum > maximum || minimum > 0 || maximum < 0)
                throw new SafetyHardwareConfigurationException("AoRangeDoesNotContainZero");
            var devices = root.Element("Devices");
            var channels = devices == null
                ? Array.Empty<string>()
                : devices.Elements("Device")
                    .Select(value => RequiredElement(value, "PhysicalChannel"));
            return ValidatePhysicalChannels(channels, "AoPhysicalChannels");
        }

        private static SafetyPressureChannel[] LoadPressure(
            string path,
            HashSet<string> required)
        {
            var root = LoadRoot(path, "AIConfigDetail");
            var result = new List<SafetyPressureChannel>();
            foreach (var record in root.Elements("Records").Where(IsEnabled))
            {
                var name = OptionalElement(record, "参数名");
                if (!required.Contains(name)) continue;
                int id;
                if (!TryParsePressureId(name, out id))
                    throw new SafetyHardwareConfigurationException("PressureChannelNameInvalid");
                result.Add(new SafetyPressureChannel
                {
                    HydraulicId = id,
                    Name = name,
                    PhysicalChannel = RequiredElement(record, "物理通道"),
                    Scale = RequiredDouble(record.Element("变换斜率"), "PressureScale"),
                    Offset = RequiredDouble(record.Element("变换截距"), "PressureOffset"),
                    ZeroDrift = RequiredDouble(record.Element("零位漂移"), "PressureZeroDrift")
                });
            }
            if (result.Count != required.Count ||
                result.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count ||
                result.Select(value => value.HydraulicId).Distinct().Count() != result.Count)
                throw new SafetyHardwareConfigurationException("PressureChannelMissingOrDuplicate");
            ValidatePhysicalChannels(result.Select(value => value.PhysicalChannel),
                "PressurePhysicalChannels");
            return result.OrderBy(value => value.HydraulicId).ToArray();
        }

        private static SafetyPowerSupply[] LoadPower(string path)
        {
            var root = LoadRoot(path, "PowerSupplyConfig");
            bool enabled;
            if (!bool.TryParse((string)root.Attribute("Enabled"), out enabled) || !enabled)
                throw new SafetyHardwareConfigurationException("PowerSupplyDisabled");
            var supplies = root.Elements("Supply").Select(value =>
            {
                int id;
                int port;
                if (!int.TryParse((string)value.Attribute("Id"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out id) || id <= 0 ||
                    !int.TryParse((string)value.Attribute("Port"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                    throw new SafetyHardwareConfigurationException("PowerSupplyIdentityInvalid");
                var host = ((string)value.Attribute("Host") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(host))
                    throw new SafetyHardwareConfigurationException("PowerSupplyHostMissing");
                return new SafetyPowerSupply
                {
                    Id = id,
                    DisplayName = ((string)value.Attribute("DisplayName") ?? "Power" + id).Trim(),
                    Host = host,
                    Port = port,
                    Terminator = DecodeTerminator((string)value.Attribute("Terminator"))
                };
            }).ToArray();
            if (supplies.Length == 0 || supplies.Select(value => value.Id).Distinct().Count() != supplies.Length)
                throw new SafetyHardwareConfigurationException("PowerSupplyMissingOrDuplicate");
            return supplies.OrderBy(value => value.Id).ToArray();
        }

        private static XElement LoadRoot(string path, string expectedName)
        {
            if (!File.Exists(path))
                throw new SafetyHardwareConfigurationException(expectedName + "Missing");
            try
            {
                var root = XDocument.Load(path, LoadOptions.None).Root;
                if (root == null || !string.Equals(root.Name.LocalName, expectedName,
                        StringComparison.Ordinal))
                    throw new SafetyHardwareConfigurationException(expectedName + "RootInvalid");
                return root;
            }
            catch (SafetyHardwareConfigurationException) { throw; }
            catch (Exception ex)
            {
                throw new SafetyHardwareConfigurationException(
                    expectedName + "ReadFailed:" + ex.GetBaseException().Message);
            }
        }

        private static bool IsEnabled(XElement value)
        {
            var raw = OptionalElement(value, "是否启用");
            bool enabled;
            int numeric;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric)
                ? numeric == 1
                : bool.TryParse(raw, out enabled) && enabled;
        }

        private static string RequiredElement(XElement parent, string name)
        {
            var value = OptionalElement(parent, name);
            if (string.IsNullOrWhiteSpace(value))
                throw new SafetyHardwareConfigurationException(name + "Missing");
            return value;
        }

        private static string OptionalElement(XElement parent, string name) =>
            ((string)parent?.Element(name) ?? string.Empty).Trim();

        private static double RequiredDouble(XElement element, string name)
        {
            double value;
            if (!double.TryParse(((string)element ?? string.Empty).Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value) || double.IsNaN(value) ||
                double.IsInfinity(value))
                throw new SafetyHardwareConfigurationException(name + "Invalid");
            return value;
        }

        private static string[] ValidatePhysicalChannels(IEnumerable<string> source, string name)
        {
            var values = (source ?? Array.Empty<string>())
                .Select(value => (value ?? string.Empty).Trim()).ToArray();
            if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace) ||
                values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length ||
                values.Any(value => string.IsNullOrWhiteSpace(DeviceName(value))))
                throw new SafetyHardwareConfigurationException(name + "Invalid");
            return values;
        }

        internal static string DeviceName(string physicalChannel)
        {
            var index = (physicalChannel ?? string.Empty).IndexOf('/');
            return index <= 0 ? string.Empty : physicalChannel.Substring(0, index);
        }

        private static bool TryParsePressureId(string value, out int id)
        {
            id = 0;
            const string prefix = "Pressure_";
            return !string.IsNullOrWhiteSpace(value) &&
                   value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(value.Substring(prefix.Length), NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out id) && id > 0;
        }

        private static string DecodeTerminator(string value)
        {
            var result = string.IsNullOrEmpty(value) ? "\r\n" : value;
            return result.Replace("\\r", "\r").Replace("\\n", "\n");
        }
    }

    public interface ISafetyDigitalOutput : IDisposable
    {
        void Write(bool[] values);
    }

    public interface ISafetyAnalogOutput : IDisposable
    {
        void WriteVoltage(double value);
    }

    public interface ISafetyPressureInput : IDisposable
    {
        double[] ReadSamples(int samplesPerChannel);
    }

    public interface ISafetyDaqBackend
    {
        ISafetyDigitalOutput OpenDigitalOutput(string taskName, string[] physicalLines);
        ISafetyAnalogOutput OpenAnalogOutput(string taskName, string physicalChannel);
        ISafetyPressureInput OpenPressureInput(
            string taskName,
            string physicalChannel,
            double sampleRateHz,
            int samplesPerChannel);
    }

    public sealed class NationalInstrumentsSafetyDaqBackend : ISafetyDaqBackend
    {
        private sealed class DigitalOutput : ISafetyDigitalOutput
        {
            private readonly NIDaqTask _task;
            private readonly DigitalMultiChannelWriter _writer;

            internal DigitalOutput(NIDaqTask task)
            {
                _task = task;
                _writer = new DigitalMultiChannelWriter(task.Stream);
            }

            public void Write(bool[] values)
            {
                _writer.WriteSingleSampleSingleLine(true, values);
            }

            public void Dispose()
            {
                _task.Dispose();
            }
        }

        private sealed class AnalogOutput : ISafetyAnalogOutput
        {
            private readonly NIDaqTask _task;
            private readonly AnalogSingleChannelWriter _writer;

            internal AnalogOutput(NIDaqTask task)
            {
                _task = task;
                _writer = new AnalogSingleChannelWriter(task.Stream);
            }

            public void WriteVoltage(double value)
            {
                _writer.WriteSingleSample(true, value);
            }

            public void Dispose()
            {
                _task.Dispose();
            }
        }

        private sealed class PressureInput : ISafetyPressureInput
        {
            private readonly NIDaqTask _task;
            private readonly AnalogSingleChannelReader _reader;

            internal PressureInput(NIDaqTask task)
            {
                _task = task;
                _reader = new AnalogSingleChannelReader(task.Stream);
            }

            public double[] ReadSamples(int samplesPerChannel)
            {
                return _reader.ReadMultiSample(samplesPerChannel);
            }

            public void Dispose()
            {
                _task.Dispose();
            }
        }

        public ISafetyDigitalOutput OpenDigitalOutput(
            string taskName,
            string[] physicalLines)
        {
            var task = new NIDaqTask(taskName);
            try
            {
                foreach (var line in physicalLines ?? Array.Empty<string>())
                    task.DOChannels.CreateChannel(
                        line,
                        string.Empty,
                        ChannelLineGrouping.OneChannelForEachLine);
                task.Control(TaskAction.Verify);
                return new DigitalOutput(task);
            }
            catch
            {
                task.Dispose();
                throw;
            }
        }

        public ISafetyAnalogOutput OpenAnalogOutput(
            string taskName,
            string physicalChannel)
        {
            var task = new NIDaqTask(taskName);
            try
            {
                task.AOChannels.CreateVoltageChannel(
                    physicalChannel,
                    string.Empty,
                    -10,
                    10,
                    AOVoltageUnits.Volts);
                return new AnalogOutput(task);
            }
            catch
            {
                task.Dispose();
                throw;
            }
        }

        public ISafetyPressureInput OpenPressureInput(
            string taskName,
            string physicalChannel,
            double sampleRateHz,
            int samplesPerChannel)
        {
            var task = new NIDaqTask(taskName);
            try
            {
                task.AIChannels.CreateVoltageChannel(
                    physicalChannel,
                    string.Empty,
                    AITerminalConfiguration.Rse,
                    -10,
                    10,
                    AIVoltageUnits.Volts);
                task.Timing.ConfigureSampleClock(
                    string.Empty,
                    sampleRateHz,
                    SampleClockActiveEdge.Rising,
                    SampleQuantityMode.ContinuousSamples,
                    Math.Max(samplesPerChannel * 4, 64));
                task.Start();
                return new PressureInput(task);
            }
            catch
            {
                task.Dispose();
                throw;
            }
        }
    }

    public sealed class SafetyDigitalOutputController : IDisposable
    {
        private readonly ISafetyDaqBackend _backend;
        private readonly List<ISafetyDigitalOutput> _outputs =
            new List<ISafetyDigitalOutput>();

        public SafetyDigitalOutputController()
            : this(new NationalInstrumentsSafetyDaqBackend())
        {
        }

        public SafetyDigitalOutputController(ISafetyDaqBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public bool ConfirmAllOff(IEnumerable<string> physicalLines)
        {
            try
            {
                foreach (var group in (physicalLines ?? Array.Empty<string>())
                             .GroupBy(SafetyHardwareConfiguration.DeviceName,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(group.Key))
                        throw new SafetyHardwareConfigurationException("DoDeviceInvalid");
                    var lines = group.ToArray();
                    var output = _backend.OpenDigitalOutput("SafetyDO_" + group.Key, lines);
                    _outputs.Add(output);
                    // This is deliberately the first hardware write after task creation.
                    output.Write(new bool[lines.Length]);
                }
                return _outputs.Count > 0;
            }
            catch (SafetyHardwareConfigurationException) { throw; }
            catch (Exception ex)
            {
                throw new SafetyHardwareUnavailableException("SafetyDoOffUnconfirmed", ex);
            }
        }

        public void Dispose()
        {
            foreach (var output in _outputs) try { output.Dispose(); } catch { }
            _outputs.Clear();
        }
    }

    public sealed class SafetyAnalogOutputController : IDisposable
    {
        private readonly ISafetyDaqBackend _backend;
        private readonly List<ISafetyAnalogOutput> _outputs =
            new List<ISafetyAnalogOutput>();

        public SafetyAnalogOutputController()
            : this(new NationalInstrumentsSafetyDaqBackend())
        {
        }

        public SafetyAnalogOutputController(ISafetyDaqBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public bool ConfirmLiteralZero(IEnumerable<string> physicalChannels)
        {
            try
            {
                foreach (var physical in physicalChannels ?? Array.Empty<string>())
                {
                    var output = _backend.OpenAnalogOutput(
                        "SafetyAO_" + SafetyHardwareConfiguration.DeviceName(physical),
                        physical);
                    _outputs.Add(output);
                    output.WriteVoltage(0.0);
                }
                return _outputs.Count > 0;
            }
            catch (Exception ex)
            {
                throw new SafetyHardwareUnavailableException("SafetyAoZeroUnconfirmed", ex);
            }
        }

        public void Dispose()
        {
            foreach (var output in _outputs) try { output.Dispose(); } catch { }
            _outputs.Clear();
        }
    }

    public readonly struct SafetyPressureSample
    {
        public SafetyPressureSample(double valueBar, long monotonicTicks)
        {
            ValueBar = valueBar;
            MonotonicTicks = monotonicTicks;
        }
        public double ValueBar { get; }
        public long MonotonicTicks { get; }
        public bool IsFinite => !double.IsNaN(ValueBar) && !double.IsInfinity(ValueBar);
        public double AgeMs => MonotonicTicks <= 0
            ? double.PositiveInfinity
            : (Stopwatch.GetTimestamp() - MonotonicTicks) * 1000.0 / Stopwatch.Frequency;
    }

    public sealed class SafetyPressureProbe : IDisposable
    {
        private sealed class Channel : IDisposable
        {
            internal SafetyPressureChannel Configuration;
            internal ISafetyPressureInput Input;
            public void Dispose() { try { Input?.Dispose(); } catch { } }
        }

        private readonly Dictionary<int, Channel> _channels = new Dictionary<int, Channel>();
        private readonly ISafetyDaqBackend _backend;
        private readonly int _samplesPerChannel;

        public SafetyPressureProbe(
            IEnumerable<SafetyPressureChannel> channels,
            double sampleRateHz,
            int samplesPerChannel)
            : this(channels, sampleRateHz, samplesPerChannel,
                new NationalInstrumentsSafetyDaqBackend())
        {
        }

        public SafetyPressureProbe(
            IEnumerable<SafetyPressureChannel> channels,
            double sampleRateHz,
            int samplesPerChannel,
            ISafetyDaqBackend backend)
        {
            if (sampleRateHz <= 0 || double.IsNaN(sampleRateHz) ||
                double.IsInfinity(sampleRateHz) || samplesPerChannel < 1)
                throw new SafetyHardwareConfigurationException("DaqRuntimeInvalid");
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _samplesPerChannel = samplesPerChannel;
            try
            {
                foreach (var configuration in channels ?? Array.Empty<SafetyPressureChannel>())
                {
                    var channel = new Channel
                    {
                        Configuration = configuration,
                        Input = _backend.OpenPressureInput(
                            "SafetyPressure_" + configuration.HydraulicId,
                            configuration.PhysicalChannel,
                            sampleRateHz,
                            samplesPerChannel)
                    };
                    _channels.Add(configuration.HydraulicId, channel);
                }
                if (_channels.Count == 0)
                    throw new SafetyHardwareConfigurationException("PressureChannelListEmpty");
            }
            catch (SafetyHardwareConfigurationException) { Dispose(); throw; }
            catch (Exception ex)
            {
                Dispose();
                throw new SafetyHardwareUnavailableException("SafetyPressureHardwareUnavailable", ex);
            }
        }

        public SafetyPressureSample Read(int hydraulicId)
        {
            Channel channel;
            if (!_channels.TryGetValue(hydraulicId, out channel))
                throw new SafetyHardwareConfigurationException("PressureChannelMissing");
            try
            {
                var raw = channel.Input.ReadSamples(_samplesPerChannel);
                if (raw == null || raw.Length == 0)
                    return new SafetyPressureSample(double.NaN, 0);
                Array.Sort(raw);
                var voltage = raw[raw.Length / 2];
                var cfg = channel.Configuration;
                return new SafetyPressureSample(
                    (voltage - cfg.ZeroDrift) * cfg.Scale + cfg.Offset,
                    Stopwatch.GetTimestamp());
            }
            catch (Exception ex)
            {
                throw new SafetyHardwareUnavailableException("SafetyPressureReadUnavailable", ex);
            }
        }

        public void Dispose()
        {
            foreach (var channel in _channels.Values) channel.Dispose();
            _channels.Clear();
        }
    }

    public sealed class SafetyPowerOutputController
    {
        public bool ConfirmAllOff(IEnumerable<SafetyPowerSupply> supplies, int timeoutMs)
        {
            var any = false;
            foreach (var supply in supplies ?? Array.Empty<SafetyPowerSupply>())
            {
                any = true;
                try
                {
                    var endpoint = new PswEndpoint
                    {
                        Id = supply.Id,
                        DisplayName = supply.DisplayName,
                        Host = supply.Host,
                        Port = supply.Port,
                        Terminator = supply.Terminator
                    };
                    using (var client = new PswTcpClient(
                               endpoint, NullPswLog.Instance,
                               Math.Min(3000, timeoutMs), Math.Min(2000, timeoutMs)))
                    using (var timeout = new CancellationTokenSource(timeoutMs))
                    {
                        client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
                        if (!client.SetOutputAsync(false, timeout.Token).GetAwaiter().GetResult())
                            return false;
                        var snapshot = client.ReadSnapshotAsync(timeout.Token).GetAwaiter().GetResult();
                        if (snapshot == null || !snapshot.IsConnected || snapshot.OutputEnabled)
                            return false;
                    }
                }
                catch (Exception ex)
                {
                    throw new SafetyHardwareUnavailableException("SafetyPowerOffUnconfirmed", ex);
                }
            }
            return any;
        }
    }
}
