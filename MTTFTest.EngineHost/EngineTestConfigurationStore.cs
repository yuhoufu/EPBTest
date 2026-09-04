using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using Config;
using Config.Models;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    // EngineHost is the sole writer. Metadata revision and last command receipt live in
    // the same atomic project XML replacement as the settings. Progress is not a UI payload.
    internal sealed class EngineTestConfigurationStore
    {
        private readonly string _path;
        private readonly object _gate = new object();
        internal EngineTestConfigurationStore(string projectPath) { _path = Path.GetFullPath(projectPath); }

        internal static TestConfig LoadProjectWithoutReset(TestConfig configured, string defaultPath)
        {
            if (configured == null || string.IsNullOrWhiteSpace(configured.StoreDir) || string.IsNullOrWhiteSpace(configured.TestName) ||
                configured.TestName == "." || configured.TestName == ".." || configured.TestName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("ProjectIdentityInvalid");
            var path = Path.GetFullPath(ConfigLoader.GetProjectTestConfigPath(configured.StoreDir, configured.TestName));
            if (!File.Exists(path))
            {
                // Login/bootstrap is not the explicit New Project command. Retain every
                // progress/isolation fact when importing the existing default checkpoint.
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var staged = path + ".import." + Guid.NewGuid().ToString("N");
                try
                {
                    CopyDurably(defaultPath, staged);
                    var imported = ConfigLoader.LoadTest(staged, null);
                    if (imported.TestName != configured.TestName ||
                        !string.Equals(Path.GetFullPath(imported.StoreDir), Path.GetFullPath(configured.StoreDir), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("ProjectImportIdentityMismatch");
                    File.Move(staged, path); // Never replace an existing project on a race.
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
            var loaded = ConfigLoader.LoadTest(path, null);
            if (loaded.TestName != configured.TestName || !string.Equals(Path.GetFullPath(loaded.StoreDir),
                    Path.GetFullPath(configured.StoreDir), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ProjectFileIdentityMismatch");
            return loaded;
        }

        internal static EngineTestConfiguration Capture(TestConfig config)
        {
            var json = new JavaScriptSerializer();
            var records = config.EpbRecords.Snapshot();
            return new EngineTestConfiguration
            {
                TestName = config.TestName, StoreDir = config.StoreDir, Owner = config.Owner, Description = config.Description,
                TestPeriod = config.TestPeriod, TestTarget = config.TestTarget, IsSameCycleForAllEpb = config.IsSameCycleForAllEpb,
                Channels = config.EpbCycleRunner.Channels.Values.OrderBy(r => r.Channel).Select(r =>
                {
                    var row = json.Deserialize<EngineRunnerConfiguration>(json.Serialize(r));
                    var record = records.Single(x => x.Id == r.Channel);
                    row.Selected = record.Enabled; row.TargetTotalCount = record.TotalCount;
                    return row;
                }).ToArray(),
                Hydraulics = config.Hydraulics.OrderBy(h => h.Id).Select(h => new EngineHydraulicSetting
                { Id = h.Id, Enabled = h.Enabled, PressureThresholdBar = h.PressureThresholdBar }).ToArray()
            };
        }

        internal long ReadRevision()
        {
            lock (_gate) return ReadRevision(ReadDocument());
        }

        internal bool CanRebindCheckpoint(RecoveryIdentity identity, string hash)
        {
            lock (_gate)
            {
                var doc = ReadDocument();
                var stamp = doc.DocumentElement?.SelectSingleNode("V3ConfigurationTransaction") as XmlElement;
                return stamp != null && ReadRevision(doc) > 0 && stamp.GetAttribute("SessionId") == identity.SessionId &&
                    stamp.GetAttribute("RunId") == identity.RunId &&
                    stamp.GetAttribute("RunEpoch") == identity.RunEpoch.ToString(CultureInfo.InvariantCulture) &&
                    stamp.GetAttribute("ConfigurationSha256") == hash;
            }
        }

        private static long ReadRevision(XmlDocument doc)
        {
            var stamp = doc.DocumentElement?.SelectSingleNode("V3ConfigurationTransaction") as XmlElement;
            if (stamp == null) return 0;
            if (!long.TryParse(stamp.GetAttribute("Revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
                throw new InvalidDataException("ConfigurationRevisionCorrupt");
            return revision;
        }

        internal TestConfig Commit(OperatorCommand command, Action<string> crashPoint = null)
        {
            if (command?.Kind != OperatorCommandKind.CommitConfiguration || !command.IsStructurallyValid() || command.TestConfiguration.Configuration == null)
                throw new InvalidDataException("ConfigurationCommandInvalid");
            lock (_gate)
                return ConfigLoader.WithTestFileLock(_path, () => CommitUnderFileLock(command, crashPoint));
        }

        private TestConfig CommitUnderFileLock(OperatorCommand command, Action<string> crashPoint)
        {
            {
                var document = ReadDocument();
                var previous = document.DocumentElement?.SelectSingleNode("V3ConfigurationTransaction") as XmlElement;
                if (previous?.GetAttribute("CommandId") == command.CommandId)
                {
                    if (previous.GetAttribute("Fingerprint") != OperatorCommandAdmission.GetFingerprint(command))
                        throw new InvalidDataException("ConfigurationCommandPayloadConflict");
                    return ConfigLoader.LoadTest(_path, null);
                }
                var current = ConfigLoader.LoadTest(_path, null);
                var settings = command.TestConfiguration;
                if (ReadRevision(document) != settings.BaseConfigurationRevision || Capture(current).ComputeSha256() != settings.BaseConfigurationSha256)
                    throw new InvalidOperationException("ConfigurationRevisionConflict");
                if (current.TestName != settings.Configuration.TestName ||
                    !string.Equals(Path.GetFullPath(current.StoreDir), Path.GetFullPath(settings.Configuration.StoreDir), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ProjectSwitchRequiresNewRunTransaction");
                Apply(current, settings.Configuration);

                var directory = Path.GetDirectoryName(_path);
                var archiveDirectory = Path.Combine(directory, "V3ConfigurationHistory");
                Directory.CreateDirectory(archiveDirectory);
                var archive = Path.Combine(archiveDirectory, command.CommandId + ".before.xml");
                if (!File.Exists(archive)) CopyDurably(_path, archive);
                crashPoint?.Invoke("Archived");
                var staged = Path.Combine(directory, ".v3-config-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(_path, staged, false);
                    ConfigLoader.SaveTest(staged, current);
                    var candidate = new XmlDocument { XmlResolver = null };
                    candidate.Load(staged);
                    var root = candidate.DocumentElement;
                    var stamp = root.SelectSingleNode("V3ConfigurationTransaction") as XmlElement;
                    if (stamp == null) { stamp = candidate.CreateElement("V3ConfigurationTransaction"); root.AppendChild(stamp); }
                    stamp.SetAttribute("Revision", checked(settings.BaseConfigurationRevision + 1).ToString(CultureInfo.InvariantCulture));
                    stamp.SetAttribute("CommandId", command.CommandId);
                    stamp.SetAttribute("Fingerprint", OperatorCommandAdmission.GetFingerprint(command));
                    stamp.SetAttribute("SessionId", command.SessionId); stamp.SetAttribute("RunId", command.RunId);
                    stamp.SetAttribute("RunEpoch", command.RunEpoch.ToString(CultureInfo.InvariantCulture));
                    stamp.SetAttribute("ConfigurationSha256", settings.Configuration.ComputeSha256());
                    using (var stream = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
                            candidate.Save(writer);
                        stream.Flush(true);
                    }
                    var verified = Capture(ConfigLoader.LoadTest(staged, null));
                    if (verified.ComputeSha256() != settings.Configuration.ComputeSha256())
                        throw new InvalidDataException("ConfigurationSerializationRoundTripMismatch");
                    crashPoint?.Invoke("Prepared");
                    File.Replace(staged, _path, null);
                    crashPoint?.Invoke("Committed");
                    return current;
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
        }

        internal void SaveProgress(long configurationRevision, int[] formal, long[] mechanical, long[] runTimeTicks,
            Action<string> crashPoint = null)
        {
            if (formal?.Length != 12 || mechanical?.Length != 12 || runTimeTicks?.Length != 12 ||
                Enumerable.Range(0, 12).Any(i => formal[i] < 0 || mechanical[i] < formal[i] || runTimeTicks[i] < 0))
                throw new InvalidDataException("ProgressProjectionInvalid");
            lock (_gate) ConfigLoader.WithTestFileLock(_path, () =>
            {
                var doc = ReadDocument();
                var current = ConfigLoader.LoadTest(_path, null);
                if (ReadRevision(doc) != configurationRevision)
                    throw new InvalidOperationException("ProgressConfigurationChanged");
                var changed = false;
                for (var ch = 1; ch <= 12; ch++)
                {
                    var nodes = doc.SelectNodes("/TestConfig/EpbRecords/Record[Id='" + ch + "']");
                    if (nodes.Count != 1) throw new InvalidDataException("ProgressRecordIdentityInvalid:" + ch);
                    var node = nodes[0];
                    var record = current.GetEpbRecord(ch);
                    void Set(string name, string value)
                    {
                        var target = node.SelectSingleNode(name);
                        if (target?.InnerText == value) return;
                        if (target == null) { target = doc.CreateElement(name); node.AppendChild(target); }
                        target.InnerText = value; changed = true;
                    }
                    Set("RunCount", Math.Max(record.RunCount, formal[ch - 1]).ToString(CultureInfo.InvariantCulture));
                    Set("MechanicalCycleCount", Math.Max(record.EffectiveMechanicalCycleCount, mechanical[ch - 1]).ToString(CultureInfo.InvariantCulture));
                    Set("RunTime", TimeSpan.FromTicks(Math.Max(record.RunTimeSpan.Ticks, runTimeTicks[ch - 1])).ToString("c", CultureInfo.InvariantCulture));
                }
                if (!changed) return false;
                var staged = _path + ".progress." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
                            doc.Save(writer);
                        stream.Flush(true);
                    }
                    crashPoint?.Invoke("ProgressPrepared");
                    File.Replace(staged, _path, null);
                    crashPoint?.Invoke("ProgressCommitted");
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
                return true;
            });
        }

        internal static void Apply(TestConfig current, EngineTestConfiguration value)
        {
            // Only the historically editable metadata changes. Retention, topology, safety
            // thresholds, learned evidence, isolation, progress and timing policy are preserved.
            current.Owner = value.Owner; current.Description = value.Description; current.TestPeriod = value.TestPeriod;
            current.TestTarget = value.TestTarget; current.IsSameCycleForAllEpb = value.IsSameCycleForAllEpb;
            var json = new JavaScriptSerializer();
            foreach (var row in value.Channels)
            {
                var record = current.GetEpbRecord(row.Channel);
                if (row.Selected && record.PermanentAlarmLatched)
                    throw new InvalidOperationException("ConfigurationCannotClearIsolation:" + row.Channel);
                if (row.Selected && (row.ForwardA <= 0 || row.FwdOnLimitMs <= 0 || row.OffCurrentClearTimeoutMs < 1000 ||
                    row.ForwardProgressConfirmMs < 20 || row.ForwardProgressDeadlineMs < row.ForwardProgressConfirmMs ||
                    row.ReverseProgressConfirmMs < 20 || row.ReverseProgressDeadlineMs < row.ReverseProgressConfirmMs ||
                    row.ForwardMinimumRiseSlopeAperMs <= 0 || row.ReverseMinimumDecaySlopeAperMs <= 0))
                    throw new InvalidOperationException("ConfigurationChannelSafetyLimitsInvalid:" + row.Channel);
                record.Enabled = row.Selected; record.TotalCount = row.TargetTotalCount;
                current.EpbCycleRunner.Channels[row.Channel] = json.Deserialize<EpbCycleRunnerConfig.Record>(json.Serialize(row));
            }
            foreach (var setting in value.Hydraulics)
            {
                if (setting.PressureThresholdBar > 200) throw new InvalidOperationException("ConfigurationPressureOutsideApprovedRange");
                var hydraulic = current.Hydraulics.Single(h => h.Id == setting.Id);
                hydraulic.Enabled = setting.Enabled; hydraulic.PressureThresholdBar = setting.PressureThresholdBar;
            }
        }

        private XmlDocument ReadDocument()
        {
            if (new FileInfo(_path).Length > 4 * 1024 * 1024) throw new InvalidDataException("TestConfigurationTooLarge");
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(_path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(reader);
            return doc;
        }

        private static void CopyDurably(string source, string destination)
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            { input.CopyTo(output); output.Flush(true); }
        }
    }
}
