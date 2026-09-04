using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class EngineAoConfigurationTests
    {
        internal static int RunAll()
        {
            Run("AoLegacyReadIsPassiveAndPreservesCalibrationAudit", Legacy);
            Run("AoAtomicSaveFitsPointsAndPreservesOtherCylinderAndHistory", Save);
            Run("AoArchivePrepareCommitCrashesReplayOriginalTransaction", Crashes);
            Run("AoRevisionPayloadArchiveAndReceiptConflictsAreRejected", Conflicts);
            Run("AoCancellationAndExternalWriterKeepActiveFile", Cancellation);
            Run("AoInvalidPointsAndMixedConfigurationPayloadsAreRejected", InvalidPoints);
            Run("AoCorruptDuplicateAndDtdConfigurationIsRejected", Corruption);
            Run("AoCalibrationMustRepresentCurrentProjectPressuresBeforeCommit", OperatingPressures);
            return 8;
        }
        private static void Run(string name, Action<Fixture> test)
        { using (var fixture = new Fixture()) test(fixture); Console.WriteLine("PASS " + name); }

        private static void Legacy(Fixture f)
        {
            var before = File.ReadAllBytes(f.Path); var value = f.Store.Read();
            Assert(value.Revision == 0 && value.Configuration.IsStructurallyValid());
            Assert(value.Configuration.Devices.All(d => d.Points.Length == 3 && d.Points.All(p => p.CommandPressure.HasValue)));
            var model = EngineAoConfigurationStore.ToHardwareConfiguration(value.Configuration);
            Assert(model.Devices["Cylinder1"].ScaleK == value.Configuration.Devices[0].ScaleK);
            Assert(before.SequenceEqual(File.ReadAllBytes(f.Path)) && Directory.GetFiles(f.Root).Length == 1);
            var clone = value.Configuration.Clone(); clone.Devices = clone.Devices.Reverse().ToArray();
            foreach (var device in clone.Devices) device.Points = device.Points.Reverse().ToArray();
            Assert(clone.ComputeSha256() == value.Sha256);
            clone.Devices[0].Points[0].CommandPressure = 10;
            Assert(clone.ComputeSha256() != value.Sha256);
        }

        private static void Save(Fixture f)
        {
            var history = System.IO.Path.Combine(f.Root, "nonzero-counts.xml");
            File.WriteAllText(history, "<Progress Formal='21508' Remaining='178492' Isolated='4' />");
            var historyBytes = File.ReadAllBytes(history); var before = File.ReadAllBytes(f.Path);
            var old = f.Store.Read(); var command = f.Command();
            var result = f.Store.Commit(command, CancellationToken.None);
            Assert(result.Revision == 1 && result.Sha256 != old.Sha256);
            var first = result.Configuration.Devices.Single(d => d.Name == "Cylinder1");
            Assert(Math.Abs(first.ScaleK - 20) < 1e-10 && Math.Abs(first.Offset - 5) < 1e-10);
            Assert(DeviceXml(f.Path, "Cylinder2") == DeviceXml(f.Archive(command), "Cylinder2"));
            Assert(before.SequenceEqual(File.ReadAllBytes(f.Archive(command))) && historyBytes.SequenceEqual(File.ReadAllBytes(history)));
            var after = File.ReadAllBytes(f.Path);
            var replay = new EngineAoConfigurationStore(f.Path).Commit(command.Clone(), CancellationToken.None);
            Assert(replay.Revision == 1 && after.SequenceEqual(File.ReadAllBytes(f.Path)));
            Assert(result.Configuration.MinVoltage == old.Configuration.MinVoltage && first.PhysicalChannel == "Dev1/ao0");
        }

        private static void Crashes(Fixture unused)
        {
            foreach (var boundary in new[] { "Archived", "Prepared", "Committed" })
            using (var f = new Fixture())
            {
                var before = File.ReadAllBytes(f.Path); var command = f.Command();
                Expect(() => f.Store.Commit(command, CancellationToken.None, crashPoint: point =>
                { if (point == boundary) throw new IOException("injected"); }), "injected");
                Assert(before.SequenceEqual(File.ReadAllBytes(f.Archive(command))));
                Assert(boundary == "Committed" || before.SequenceEqual(File.ReadAllBytes(f.Path)));
                var resumed = new EngineAoConfigurationStore(f.Path).Commit(command, CancellationToken.None);
                Assert(resumed.Revision == 1 && Directory.GetFiles(System.IO.Path.GetDirectoryName(f.Archive(command))).Length == 1);
            }
        }

        private static void Conflicts(Fixture f)
        {
            var command = f.Command(); var wrong = command.Clone(); wrong.TestConfiguration.BaseConfigurationRevision++;
            wrong.PayloadSha256 = wrong.TestConfiguration.ComputeSha256();
            Expect(() => f.Store.Commit(wrong, CancellationToken.None), "AoCalibrationRevisionConflict");
            f.Store.Commit(command, CancellationToken.None);
            wrong = command.Clone(); wrong.TestConfiguration.AoCalibration.Points[0].CommandPressure = 11;
            wrong.PayloadSha256 = wrong.TestConfiguration.ComputeSha256();
            Expect(() => f.Store.Commit(wrong, CancellationToken.None), "AoCalibrationReplayConflict");
            var next = f.Command(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f.Archive(next)));
            File.WriteAllText(f.Archive(next), "unrelated data");
            var before = File.ReadAllBytes(f.Path);
            Expect(() => f.Store.Commit(next, CancellationToken.None), "AoCalibrationArchiveConflict");
            Assert(before.SequenceEqual(File.ReadAllBytes(f.Path)));
            File.WriteAllText(f.Path, File.ReadAllText(f.Path).Replace("<Offset>5</Offset>", "<Offset>6</Offset>"));
            Expect(() => f.Store.Read(), "AoCalibrationReceiptCorrupt");
        }

        private static void Cancellation(Fixture f)
        {
            var before = File.ReadAllBytes(f.Path); var command = f.Command();
            using (var stop = new CancellationTokenSource())
            {
                try { f.Store.Commit(command, stop.Token, crashPoint: point => { if (point == "Prepared") stop.Cancel(); }); throw new Exception("CancellationIgnored"); }
                catch (OperationCanceledException) { }
            }
            Assert(before.SequenceEqual(File.ReadAllBytes(f.Path)));
            var external = File.ReadAllText(f.Path).Replace("<MaxPressure>200</MaxPressure>", "<MaxPressure>180</MaxPressure>");
            Expect(() => f.Store.Commit(command, CancellationToken.None, crashPoint: point =>
            { if (point == "Prepared") File.WriteAllText(f.Path, external); }), "AoCalibrationExternalWriterConflict");
            Assert(File.ReadAllText(f.Path) == external);
        }

        private static void InvalidPoints(Fixture f)
        {
            foreach (var mutate in new Action<AoCalibrationCommit>[]
            {
                c => c.Points[0].Voltage = double.NaN, c => c.Points[0].Pressure = double.PositiveInfinity,
                c => c.Points[0].Voltage = c.Points[1].Voltage, c => c.Points[1].Pressure = c.Points[0].Pressure,
                c => c.DeviceName = "Cylinder3", c => c.Points = new EngineAoCalibrationPoint[257], c => c.Points = null,
                c => c.Points[0].CommandPressure = -1
            }) { var command = f.Command(); mutate(command.TestConfiguration.AoCalibration); Assert(!command.TestConfiguration.IsStructurallyValid()); }
            var mixed = f.Command(); mixed.TestConfiguration.Configuration = new EngineTestConfiguration(); Assert(!mixed.TestConfiguration.IsStructurallyValid());
            mixed = f.Command(); mixed.TestConfiguration.DaqConfiguration = new EngineDaqConfiguration(); Assert(!mixed.TestConfiguration.IsStructurallyValid());
            var outside = f.Command(); outside.TestConfiguration.AoCalibration.Points[0].Voltage = -1;
            outside.PayloadSha256 = outside.TestConfiguration.ComputeSha256();
            Expect(() => f.Store.Commit(outside, CancellationToken.None), "AoCalibrationPointOutsideConfiguredRange");
            var tiny = f.Command(); tiny.TestConfiguration.AoCalibration.Points[1].Voltage = 1 + 1e-8;
            tiny.PayloadSha256 = tiny.TestConfiguration.ComputeSha256();
            ExpectPrefix(() => f.Store.Commit(tiny, CancellationToken.None), "AoCalibrationFitInvalid:");
        }

        private static void Corruption(Fixture f)
        {
            var original = File.ReadAllText(f.Path);
            foreach (var contents in new[]
            {
                original.Replace("<MinVoltage>0</MinVoltage>", "<MinVoltage>0</MinVoltage><MinVoltage>1</MinVoltage>"),
                original.Replace("<MaxPressure>200</MaxPressure>", "<MaxPressure>NaN</MaxPressure>"),
                original.Replace("<Name>Cylinder2</Name>", "<Name>Cylinder1</Name>"),
                original.Replace("</AOConfig>", "<V3AoCalibrationTransaction Revision='1'/></AOConfig>")
            }) { File.WriteAllText(f.Path, contents); ExpectPrefix(() => f.Store.Read(), "Ao"); }
            File.WriteAllText(f.Path, original.Replace("<AOConfig>", "<!DOCTYPE AOConfig [<!ENTITY x SYSTEM 'file:///unavailable'>]><AOConfig>"));
            try { f.Store.Read(); throw new Exception("DtdAccepted"); } catch (XmlException) { }
        }

        private static void OperatingPressures(Fixture f)
        {
            var command = f.Command(); var before = File.ReadAllBytes(f.Path);
            Expect(() => f.Store.Commit(command, CancellationToken.None, value => EngineAoConfigurationStore.ValidateOperatingPressures(value,
                new[] { new EngineHydraulicSetting { Id = 1, Enabled = true, PressureThresholdBar = 1 } })),
                "AoCalibrationCannotRepresentOperatingPressure:Cylinder1");
            Assert(before.SequenceEqual(File.ReadAllBytes(f.Path)) && !Directory.Exists(System.IO.Path.GetDirectoryName(f.Archive(command))));
            var committed = f.Store.Commit(command, CancellationToken.None, value => EngineAoConfigurationStore.ValidateOperatingPressures(value,
                new[] { new EngineHydraulicSetting { Id = 1, Enabled = true, PressureThresholdBar = 70 } }));
            EngineAoConfigurationStore.ValidateOperatingPressures(committed.Configuration,
                new[] { new EngineHydraulicSetting { Id = 1, Enabled = true, PressureThresholdBar = 0 } });
        }

        private static string DeviceXml(string path, string name)
        { var doc = new XmlDocument { XmlResolver = null }; doc.Load(path); return doc.SelectSingleNode("/AOConfig/Devices/Device[Name='" + name + "']").OuterXml; }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MTTFTest.AoTests." + Guid.NewGuid().ToString("N"));
            internal readonly string Path;
            internal readonly EngineAoConfigurationStore Store;
            internal Fixture()
            {
                Directory.CreateDirectory(Root); Path = System.IO.Path.Combine(Root, "AOConfig.xml");
                var repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
                File.Copy(System.IO.Path.Combine(repo, "MTTfTest", "Config", "AOConfig.xml"), Path); Store = new EngineAoConfigurationStore(Path);
            }
            internal OperatorCommand Command()
            {
                var current = Store.Read(); var settings = new TestConfigurationCommit { BaseConfigurationRevision = current.Revision,
                    BaseConfigurationSha256 = current.Sha256, AoCalibration = new AoCalibrationCommit { DeviceName = "Cylinder1", Points = new[]
                    { new EngineAoCalibrationPoint { Voltage = 1, Pressure = 25, CommandPressure = 20 },
                        new EngineAoCalibrationPoint { Voltage = 4, Pressure = 85, CommandPressure = 80 } } } };
                return new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(),
                    RunEpoch = 1, Kind = OperatorCommandKind.CommitConfiguration, IssuedUtcTicks = DateTime.UtcNow.Ticks,
                    TestConfiguration = settings, PayloadSha256 = settings.ComputeSha256() };
            }
            internal string Archive(OperatorCommand command) => System.IO.Path.Combine(Root, "V3AoCalibrationHistory", command.CommandId + ".before.xml");
            public void Dispose() { Directory.Delete(Root, true); }
        }
        private static void Assert(bool value) { if (!value) throw new Exception("AoCalibrationAssertionFailed"); }
        private static void Expect(Action action, string expected)
        { try { action(); } catch (Exception ex) when (ex.Message == expected) { return; } throw new Exception("Expected:" + expected); }
        private static void ExpectPrefix(Action action, string prefix)
        { try { action(); } catch (Exception ex) when (ex.Message.StartsWith(prefix, StringComparison.Ordinal)) { return; } throw new Exception("ExpectedPrefix:" + prefix); }
    }
}
