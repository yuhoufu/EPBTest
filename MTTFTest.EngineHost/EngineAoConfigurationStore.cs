using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed class EngineAoConfigurationSnapshot
    {
        internal long Revision { get; set; }
        internal string Sha256 { get; set; }
        internal EngineAoConfiguration Configuration { get; set; }
    }

    // Used only after the kernel has closed maintenance, joined hardware execution
    // and obtained an independent safety proof. This store never initializes NI.
    internal sealed class EngineAoConfigurationStore
    {
        private const int MaximumBytes = 1024 * 1024;
        private const string StampName = "V3AoCalibrationTransaction";
        private readonly string _path;
        private readonly object _gate = new object();
        internal EngineAoConfigurationStore(string path) { _path = Path.GetFullPath(path); }
        internal EngineAoConfigurationSnapshot Read() { lock (_gate) return Capture(ReadDocument(out _)); }

        internal static AoConfig ToHardwareConfiguration(EngineAoConfiguration value)
        {
            if (value?.IsStructurallyValid() != true) throw new InvalidDataException("AoConfigurationMetadataInvalid");
            var result = new AoConfig { MinVoltage = value.MinVoltage, MaxVoltage = value.MaxVoltage,
                MinPressure = value.MinPressure, MaxPressure = value.MaxPressure };
            foreach (var row in value.Devices)
            {
                var device = new AoDevice { Name = row.Name, PhysicalChannel = row.PhysicalChannel, ScaleK = row.ScaleK, Offset = row.Offset };
                device.VoltageToPressure.AddRange(row.Points.Select(p => (p.Voltage, p.Pressure)));
                result.Devices.Add(device.Name, device);
            }
            return result;
        }

        internal EngineAoConfigurationSnapshot Commit(OperatorCommand command, CancellationToken token,
            Action<EngineAoConfiguration> validateCandidate = null, Action<string> crashPoint = null)
        {
            if (command?.Kind != OperatorCommandKind.CommitConfiguration || !command.IsStructurallyValid() || command.TestConfiguration.AoCalibration == null)
                throw new InvalidDataException("AoCalibrationCommandInvalid");
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                var document = ReadDocument(out var originalSha);
                var current = Capture(document);
                var request = command.TestConfiguration;
                var calibration = request.AoCalibration;
                var fingerprint = OperatorCommandAdmission.GetFingerprint(command);
                var stamp = document.DocumentElement.SelectSingleNode(StampName) as XmlElement;
                if (stamp?.GetAttribute("CommandId") == command.CommandId)
                {
                    if (stamp.GetAttribute("Fingerprint") != fingerprint || stamp.GetAttribute("ConfigurationSha256") != current.Sha256)
                        throw new InvalidDataException("AoCalibrationReplayConflict");
                    return current;
                }
                if (current.Revision != request.BaseConfigurationRevision || current.Sha256 != request.BaseConfigurationSha256)
                    throw new InvalidOperationException("AoCalibrationRevisionConflict");
                var points = calibration.Points.OrderBy(p => p.Voltage).ToArray();
                if (points.Any(p => p.Voltage < current.Configuration.MinVoltage || p.Voltage > current.Configuration.MaxVoltage ||
                    p.Pressure > current.Configuration.MaxPressure)) throw new InvalidDataException("AoCalibrationPointOutsideConfiguredRange");
                if (!CalibrationMath.TryFitPressureLine(points.Select(p => (p.Voltage, p.Pressure)),
                    out var scale, out var offset, out _, out var error)) throw new InvalidDataException("AoCalibrationFitInvalid:" + error);
                var device = document.DocumentElement.SelectNodes("Devices/Device").Cast<XmlElement>()
                    .Single(e => Text(e, "Name") == calibration.DeviceName);
                Set(device, "ScaleK", Format(scale)); Set(device, "Offset", Format(offset));
                var table = device.SelectSingleNode("VoltageToPressureTable") as XmlElement;
                if (table == null) { table = document.CreateElement("VoltageToPressureTable"); device.AppendChild(table); }
                table.RemoveAll();
                foreach (var point in points)
                {
                    var element = document.CreateElement("Point"); table.AppendChild(element);
                    Set(element, "Voltage", Format(point.Voltage)); Set(element, "Pressure", Format(point.Pressure));
                    if (point.CommandPressure.HasValue) Set(element, "CommandPressure", Format(point.CommandPressure.Value));
                }
                // The old receipt describes the old content; replace it in the same atomic file.
                if (stamp != null) document.DocumentElement.RemoveChild(stamp);
                var candidate = Capture(document);
                validateCandidate?.Invoke(candidate.Configuration.Clone());
                candidate.Revision = checked(current.Revision + 1);
                stamp = document.CreateElement(StampName); document.DocumentElement.AppendChild(stamp);
                stamp.SetAttribute("Revision", candidate.Revision.ToString(CultureInfo.InvariantCulture));
                stamp.SetAttribute("CommandId", command.CommandId); stamp.SetAttribute("Fingerprint", fingerprint);
                stamp.SetAttribute("SessionId", command.SessionId); stamp.SetAttribute("RunId", command.RunId);
                stamp.SetAttribute("RunEpoch", command.RunEpoch.ToString(CultureInfo.InvariantCulture));
                stamp.SetAttribute("ConfigurationSha256", candidate.Sha256);
                Capture(document); // Validate the durable receipt too, before any write.

                var history = Path.Combine(Path.GetDirectoryName(_path), "V3AoCalibrationHistory");
                Directory.CreateDirectory(history);
                var archive = Path.Combine(history, command.CommandId + ".before.xml");
                if (!File.Exists(archive))
                {
                    using (var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { input.CopyTo(output); output.Flush(true); }
                }
                if (SupervisorProtocol.ComputeSha256(archive) != originalSha) throw new InvalidDataException("AoCalibrationArchiveConflict");
                crashPoint?.Invoke("Archived");
                var staged = Path.Combine(Path.GetDirectoryName(_path), ".v3-ao-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
                            document.Save(writer);
                        output.Flush(true);
                    }
                    var readback = new EngineAoConfigurationStore(staged).Read();
                    if (readback.Revision != candidate.Revision || readback.Sha256 != candidate.Sha256) throw new InvalidDataException("AoCalibrationRoundTripMismatch");
                    crashPoint?.Invoke("Prepared");
                    token.ThrowIfCancellationRequested();
                    if (SupervisorProtocol.ComputeSha256(_path) != originalSha) throw new InvalidOperationException("AoCalibrationExternalWriterConflict");
                    File.Replace(staged, _path, null);
                    crashPoint?.Invoke("Committed");
                    return candidate;
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
        }

        internal static void ValidateOperatingPressures(EngineAoConfiguration configuration, IEnumerable<EngineHydraulicSetting> hydraulics)
        {
            if (configuration?.IsStructurallyValid() != true || hydraulics == null) throw new InvalidDataException("AoOperatingConfigurationMissing");
            foreach (var hydraulic in hydraulics.Where(h => h.Enabled))
            {
                var device = configuration.Devices.Single(d => d.Name == "Cylinder" + hydraulic.Id);
                var pressure = hydraulic.PressureThresholdBar;
                // A zero pressure request is a physical OFF, not inverse calibration.
                var voltage = pressure == 0 ? 0 : (pressure - device.Offset) / device.ScaleK;
                if (pressure < 0 || pressure > configuration.MaxPressure || !EngineUiContract.IsFinite(voltage) ||
                    voltage < configuration.MinVoltage || voltage > configuration.MaxVoltage)
                    throw new InvalidOperationException("AoCalibrationCannotRepresentOperatingPressure:" + device.Name);
            }
        }

        private XmlDocument ReadDocument(out string sha)
        {
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumBytes) throw new InvalidDataException("AoConfigurationLengthInvalid");
                using (var digest = System.Security.Cryptography.SHA256.Create())
                    sha = BitConverter.ToString(digest.ComputeHash(stream)).Replace("-", string.Empty).ToUpperInvariant();
                stream.Position = 0;
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes }))
                { var document = new XmlDocument { XmlResolver = null }; document.Load(reader); return document; }
            }
        }

        private static EngineAoConfigurationSnapshot Capture(XmlDocument document)
        {
            var root = document.DocumentElement;
            if (root?.Name != "AOConfig" || root.SelectNodes("Devices").Count != 1) throw new InvalidDataException("AoConfigurationRootInvalid");
            var value = new EngineAoConfiguration
            {
                MinVoltage = Real(root, "MinVoltage"), MaxVoltage = Real(root, "MaxVoltage"),
                MinPressure = Real(root, "MinPressure"), MaxPressure = Real(root, "MaxPressure"),
                Devices = root.SelectNodes("Devices/Device").Cast<XmlElement>().Select(e =>
                {
                    if (e.SelectNodes("VoltageToPressureTable").Count > 1) throw new InvalidDataException("AoCalibrationTableDuplicated");
                    return new EngineAoDeviceConfiguration { Name = Text(e, "Name"), PhysicalChannel = Text(e, "PhysicalChannel"),
                        ScaleK = Real(e, "ScaleK"), Offset = Real(e, "Offset"),
                        Points = e.SelectNodes("VoltageToPressureTable/Point").Cast<XmlElement>().Select(p => new EngineAoCalibrationPoint
                        { Voltage = Real(p, "Voltage"), Pressure = Real(p, "Pressure"),
                            CommandPressure = p.SelectNodes("CommandPressure").Count == 0 ? (double?)null : Real(p, "CommandPressure") }).ToArray() };
                }).ToArray()
            };
            if (!value.IsStructurallyValid()) throw new InvalidDataException("AoConfigurationMetadataInvalid");
            var sha = value.ComputeSha256(); long revision = 0;
            var stamps = root.SelectNodes(StampName);
            if (stamps.Count > 1) throw new InvalidDataException("AoCalibrationReceiptDuplicated");
            if (stamps.Count == 1)
            {
                var stamp = (XmlElement)stamps[0];
                if (!long.TryParse(stamp.GetAttribute("Revision"), NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision <= 0 ||
                    !RecoveryProtocolV7.IsGuid(stamp.GetAttribute("CommandId")) || !RecoveryProtocolV7.IsGuid(stamp.GetAttribute("SessionId")) ||
                    !RecoveryProtocolV7.IsGuid(stamp.GetAttribute("RunId")) || !ProjectSwitchPlan.IsSha256(stamp.GetAttribute("Fingerprint")) ||
                    !long.TryParse(stamp.GetAttribute("RunEpoch"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch <= 0 ||
                    stamp.GetAttribute("ConfigurationSha256") != sha) throw new InvalidDataException("AoCalibrationReceiptCorrupt");
            }
            return new EngineAoConfigurationSnapshot { Revision = revision, Sha256 = sha, Configuration = value };
        }
        private static string Text(XmlElement element, string name)
        {
            var nodes = element.SelectNodes(name);
            if (nodes.Count != 1) throw new InvalidDataException("AoFieldMissingOrDuplicated:" + name);
            return nodes[0].InnerText;
        }
        private static double Real(XmlElement element, string name) => double.Parse(Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture);
        private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static void Set(XmlElement element, string name, string value)
        {
            var child = element.SelectSingleNode(name);
            if (child == null) { child = element.OwnerDocument.CreateElement(name); element.AppendChild(child); }
            child.InnerText = value;
        }
    }
}
