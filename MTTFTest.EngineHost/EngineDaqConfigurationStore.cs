using System;
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
    // Single EngineHost writer; metadata and its idempotent receipt commit together.
    internal sealed class EngineDaqConfigurationStore
    {
        private const int MaximumBytes = 1024 * 1024;
        private readonly string _path;
        private readonly object _gate = new object();
        internal EngineDaqConfigurationStore(string path) { _path = Path.GetFullPath(path); }

        internal TestConfigurationCommit Read()
        {
            lock (_gate) return Capture(ReadDocument());
        }

        internal AiConfigDetail Load() => ToHardwareConfiguration(Read().DaqConfiguration);

        internal static AiConfigDetail ToHardwareConfiguration(EngineDaqConfiguration value) => new AiConfigDetail
        {
            Records = value.Channels.OrderBy(c => c.Sequence).Select(c => new AiConfigDetailRecord
            {
                序号 = c.Sequence, 物理通道 = c.PhysicalChannel, 参数名 = c.ParameterName, 单位 = c.Unit,
                变换斜率 = c.Slope, 变换截距 = c.Intercept, 参数类型 = c.ParameterType, 是否启用 = c.Enabled ? 1 : 0, 零位漂移 = c.ZeroOffset
            }).ToList()
        };

        internal TestConfigurationCommit Commit(OperatorCommand command, CancellationToken token, Action<string> crashPoint = null)
        {
            if (command?.Kind != OperatorCommandKind.CommitConfiguration || !command.IsStructurallyValid() ||
                command.TestConfiguration.DaqConfiguration == null)
                throw new InvalidDataException("DaqConfigurationCommandInvalid");
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                var document = ReadDocument(out var originalSha);
                var current = Capture(document);
                var previous = document.DocumentElement.SelectSingleNode("V3DaqConfigurationTransaction") as XmlElement;
                var settings = command.TestConfiguration;
                var fingerprint = OperatorCommandAdmission.GetFingerprint(command);
                if (previous?.GetAttribute("CommandId") == command.CommandId)
                {
                    if (previous.GetAttribute("Fingerprint") != fingerprint ||
                        current.BaseConfigurationSha256 != settings.DaqConfiguration.ComputeSha256())
                        throw new InvalidDataException("DaqCommandPayloadOrCommittedFileConflict");
                    return current;
                }
                if (current.BaseConfigurationRevision != settings.BaseConfigurationRevision ||
                    current.BaseConfigurationSha256 != settings.BaseConfigurationSha256)
                    throw new InvalidOperationException("DaqConfigurationRevisionConflict");
                // Fields which were read-only in the original grid are enforced here too.
                if (current.DaqConfiguration.Channels.Length != settings.DaqConfiguration.Channels.Length ||
                    current.DaqConfiguration.Channels.Any(old => !settings.DaqConfiguration.Channels.Any(next =>
                        next.Sequence == old.Sequence && next.PhysicalChannel == old.PhysicalChannel && next.ZeroOffset == old.ZeroOffset)))
                    throw new InvalidOperationException("DaqReadOnlyChannelIdentityChanged");

                foreach (var row in settings.DaqConfiguration.Channels)
                {
                    var element = document.DocumentElement.SelectNodes("Records").Cast<XmlElement>()
                        .Single(e => Number(e, "序号") == row.Sequence);
                    Set(element, "参数名", row.ParameterName); Set(element, "单位", row.Unit);
                    Set(element, "变换斜率", row.Slope.ToString("R", CultureInfo.InvariantCulture));
                    Set(element, "变换截距", row.Intercept.ToString("R", CultureInfo.InvariantCulture));
                    Set(element, "参数类型", row.ParameterType); Set(element, "是否启用", row.Enabled ? "1" : "0");
                }
                var stamp = previous ?? document.CreateElement("V3DaqConfigurationTransaction");
                if (previous == null) document.DocumentElement.AppendChild(stamp);
                stamp.SetAttribute("Revision", checked(current.BaseConfigurationRevision + 1).ToString(CultureInfo.InvariantCulture));
                stamp.SetAttribute("CommandId", command.CommandId); stamp.SetAttribute("Fingerprint", fingerprint);
                stamp.SetAttribute("SessionId", command.SessionId); stamp.SetAttribute("RunId", command.RunId);
                stamp.SetAttribute("RunEpoch", command.RunEpoch.ToString(CultureInfo.InvariantCulture));
                stamp.SetAttribute("ConfigurationSha256", settings.DaqConfiguration.ComputeSha256());
                var candidate = Capture(document);
                if (candidate.BaseConfigurationSha256 != settings.DaqConfiguration.ComputeSha256())
                    throw new InvalidDataException("DaqConfigurationRoundTripMismatch");

                var archiveDirectory = Path.Combine(Path.GetDirectoryName(_path), "V3DaqConfigurationHistory");
                Directory.CreateDirectory(archiveDirectory);
                var archive = Path.Combine(archiveDirectory, command.CommandId + ".before.xml");
                if (!File.Exists(archive))
                {
                    using (var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { input.CopyTo(output); output.Flush(true); }
                }
                if (SupervisorProtocol.ComputeSha256(archive) != originalSha)
                    throw new InvalidDataException("DaqConfigurationArchiveConflict");
                crashPoint?.Invoke("Archived");
                var staged = Path.Combine(Path.GetDirectoryName(_path), ".v3-daq-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
                            document.Save(writer);
                        output.Flush(true);
                    }
                    if (new EngineDaqConfigurationStore(staged).Read().BaseConfigurationSha256 != candidate.BaseConfigurationSha256)
                        throw new InvalidDataException("DaqConfigurationPersistedRoundTripMismatch");
                    crashPoint?.Invoke("Prepared");
                    token.ThrowIfCancellationRequested();
                    if (SupervisorProtocol.ComputeSha256(_path) != originalSha)
                        throw new InvalidOperationException("DaqConfigurationExternalWriterConflict");
                    File.Replace(staged, _path, null);
                    crashPoint?.Invoke("Committed");
                    return candidate;
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
        }

        private XmlDocument ReadDocument() => ReadDocument(out _);

        private XmlDocument ReadDocument(out string sha)
        {
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumBytes) throw new InvalidDataException("DaqConfigurationLengthInvalid");
                using (var digest = System.Security.Cryptography.SHA256.Create())
                    sha = BitConverter.ToString(digest.ComputeHash(stream)).Replace("-", string.Empty).ToUpperInvariant();
                stream.Position = 0;
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes }))
                {
                    var document = new XmlDocument { XmlResolver = null }; document.Load(reader); return document;
                }
            }
        }

        private static TestConfigurationCommit Capture(XmlDocument document)
        {
            if (document.DocumentElement?.Name != "AIConfigDetail") throw new InvalidDataException("DaqConfigurationRootInvalid");
            var stamps = document.DocumentElement.SelectNodes("V3DaqConfigurationTransaction");
            if (stamps.Count > 1) throw new InvalidDataException("DaqConfigurationReceiptDuplicated");
            long revision = 0;
            if (stamps.Count == 1 && (!long.TryParse(((XmlElement)stamps[0]).GetAttribute("Revision"), NumberStyles.None,
                CultureInfo.InvariantCulture, out revision) || revision <= 0)) throw new InvalidDataException("DaqConfigurationRevisionCorrupt");
            var value = new EngineDaqConfiguration
            {
                Channels = document.DocumentElement.SelectNodes("Records").Cast<XmlElement>().Select(e =>
                {
                    var enabled = Text(e, "是否启用");
                    if (enabled != "0" && enabled != "1") throw new InvalidDataException("DaqEnabledInvalid");
                    return new EngineDaqChannelConfiguration
                    {
                        Sequence = Number(e, "序号"), PhysicalChannel = Text(e, "物理通道"), ParameterName = Text(e, "参数名"),
                        Unit = Text(e, "单位"), Slope = Real(e, "变换斜率"), Intercept = Real(e, "变换截距"),
                        ParameterType = Text(e, "参数类型"), Enabled = enabled == "1", ZeroOffset = Real(e, "零位漂移")
                    };
                }).ToArray()
            };
            if (!value.IsStructurallyValid()) throw new InvalidDataException("DaqConfigurationMetadataInvalid");
            return new TestConfigurationCommit { BaseConfigurationRevision = revision,
                BaseConfigurationSha256 = value.ComputeSha256(), DaqConfiguration = value };
        }

        private static string Text(XmlElement element, string name)
        {
            var nodes = element.SelectNodes(name);
            if (nodes.Count != 1) throw new InvalidDataException("DaqFieldMissingOrDuplicated:" + name);
            return nodes[0].InnerText;
        }
        private static int Number(XmlElement element, string name) => int.Parse(Text(element, name), NumberStyles.None, CultureInfo.InvariantCulture);
        private static double Real(XmlElement element, string name) => double.Parse(Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture);
        private static void Set(XmlElement element, string name, string value) => element.SelectSingleNode(name).InnerText = value;
    }
}
