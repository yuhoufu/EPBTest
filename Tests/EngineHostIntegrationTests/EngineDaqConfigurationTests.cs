using System;
using System.IO;
using System.Linq;
using System.Threading;
using Config;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class EngineDaqConfigurationTests
    {
        internal static int RunAll()
        {
            Run("DaqAtomicSavePreservesHistoricalFilesAndReadOnlyFields", Save);
            Run("DaqReceiptReplayAcrossPersistenceBoundaries", Crashes);
            Run("DaqRevisionPayloadAndImmutableRouteConflicts", Conflicts);
            Run("DaqCancellationAndExternalWriterPreserveActiveFile", CancellationAndExternalEdit);
            Run("DaqMetadataRejectsDuplicateNonFiniteAndAmbiguousMode", InvalidMetadata);
            return 5;
        }

        private static void Run(string name, Action<Fixture> test)
        { using (var fixture = new Fixture()) test(fixture); Console.WriteLine("PASS " + name); }

        private static void Save(Fixture f)
        {
            var test = Path.Combine(f.Root, "TestConfig.xml");
            File.Copy(Path.Combine(f.Repo, "MTTfTest", "Config", "TestConfig.xml"), test);
            var metadata = ConfigLoader.LoadTest(test, null);
            metadata.GetEpbRecord(4).RunCount = 21004; metadata.GetEpbRecord(4).MechanicalCycleCount = 21504;
            metadata.GetEpbRecord(6).PermanentAlarmLatched = true; metadata.GetEpbRecord(6).PermanentAlarmReason = "retained";
            ConfigLoader.SaveTest(test, metadata);
            var oldTest = File.ReadAllBytes(test); var oldAi = File.ReadAllBytes(f.Path);
            var command = f.Command();
            var changed = f.Store.Commit(command, CancellationToken.None);
            Assert(changed.BaseConfigurationRevision == 1 && changed.BaseConfigurationSha256 == command.TestConfiguration.DaqConfiguration.ComputeSha256());
            var hardware = f.Store.Load();
            var row = hardware.Records.Single(c => c.序号 == 4);
            Assert(row.变换斜率 == 11.5 && row.变换截距 == -0.123 && row.物理通道 == "Dev1/ai3");
            Assert(oldTest.SequenceEqual(File.ReadAllBytes(test)));
            Assert(oldAi.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root, "V3DaqConfigurationHistory", command.CommandId + ".before.xml"))));
            Assert(new EngineDaqConfigurationStore(f.Path).Commit(command, CancellationToken.None).BaseConfigurationRevision == 1);
            Assert(Directory.GetFiles(Path.Combine(f.Root, "V3DaqConfigurationHistory")).Length == 1);
        }

        private static void Crashes(Fixture f)
        {
            foreach (var boundary in new[] { "Archived", "Prepared", "Committed" })
            {
                var before = File.ReadAllBytes(f.Path); var command = f.Command(); var baseRevision = f.Store.Read().BaseConfigurationRevision;
                Expect(() => f.Store.Commit(command, CancellationToken.None, point => { if (point == boundary) throw new IOException("crash:" + point); }), "crash:" + boundary);
                if (boundary != "Committed") Assert(before.SequenceEqual(File.ReadAllBytes(f.Path)));
                var reopened = new EngineDaqConfigurationStore(f.Path);
                Assert(reopened.Commit(command, CancellationToken.None).BaseConfigurationRevision == baseRevision + 1);
                var after = File.ReadAllBytes(f.Path);
                reopened.Commit(command, CancellationToken.None); Assert(after.SequenceEqual(File.ReadAllBytes(f.Path)));
            }
        }

        private static void Conflicts(Fixture f)
        {
            var command = f.Command(); f.Store.Commit(command, CancellationToken.None);
            var before = File.ReadAllBytes(f.Path);
            var stale = command.Clone(); stale.CommandId = RecoveryProtocolV7.NewId();
            Expect(() => f.Store.Commit(stale, CancellationToken.None), "DaqConfigurationRevisionConflict");
            var tampered = command.Clone(); tampered.TestConfiguration.DaqConfiguration.Channels[0].Slope++;
            tampered.PayloadSha256 = tampered.TestConfiguration.ComputeSha256();
            Expect(() => f.Store.Commit(tampered, CancellationToken.None), "DaqCommandPayloadOrCommittedFileConflict");
            foreach (var field in new[] { "Sequence", "PhysicalChannel", "ZeroOffset" })
            {
                var immutable = f.Command(); var row = immutable.TestConfiguration.DaqConfiguration.Channels[0];
                if (field == "Sequence") row.Sequence = 100;
                if (field == "PhysicalChannel") row.PhysicalChannel = "Dev3/ai0";
                if (field == "ZeroOffset") row.ZeroOffset++;
                immutable.PayloadSha256 = immutable.TestConfiguration.ComputeSha256();
                Expect(() => f.Store.Commit(immutable, CancellationToken.None), "DaqReadOnlyChannelIdentityChanged");
            }
            Assert(before.SequenceEqual(File.ReadAllBytes(f.Path)));
        }

        private static void CancellationAndExternalEdit(Fixture f)
        {
            var command = f.Command(); var before = File.ReadAllBytes(f.Path);
            using (var cancel = new CancellationTokenSource())
            {
                try { f.Store.Commit(command, cancel.Token, point => { if (point == "Prepared") cancel.Cancel(); }); throw new Exception("CancellationIgnored"); }
                catch (OperationCanceledException) { }
            }
            Assert(before.SequenceEqual(File.ReadAllBytes(f.Path)));
            var external = new System.Xml.XmlDocument { XmlResolver = null }; external.Load(f.Path);
            external.DocumentElement.AppendChild(external.CreateElement("FutureDeviceMetadata")).InnerText = "do not overwrite";
            Expect(() => f.Store.Commit(command, CancellationToken.None, point => { if (point == "Prepared") external.Save(f.Path); }), "DaqConfigurationExternalWriterConflict");
            Assert(File.ReadAllText(f.Path).Contains("do not overwrite") && f.Store.Read().BaseConfigurationRevision == 0);
        }

        private static void InvalidMetadata(Fixture f)
        {
            var current = f.Store.Read(); var hash = current.DaqConfiguration.ComputeSha256();
            var reordered = current.DaqConfiguration.Clone(); reordered.Channels = reordered.Channels.Reverse().ToArray();
            Assert(reordered.ComputeSha256() == hash);
            foreach (var mode in new[] { "duplicate", "nan", "zero", "type" })
            {
                var invalid = f.Command(); var rows = invalid.TestConfiguration.DaqConfiguration.Channels;
                if (mode == "duplicate") rows[1].ParameterName = rows[0].ParameterName;
                if (mode == "nan") rows[0].Slope = double.NaN;
                if (mode == "zero") rows[0].Slope = 0;
                if (mode == "type") rows[0].ParameterType = "unsupported";
                Assert(!invalid.IsStructurallyValid());
            }
            var ambiguous = f.Command(); ambiguous.TestConfiguration.Configuration = new EngineTestConfiguration();
            Assert(!ambiguous.IsStructurallyValid());
            var serialized = File.ReadAllText(f.Path).Replace("<AIConfigDetail>", "<!DOCTYPE AIConfigDetail [<!ENTITY x SYSTEM 'file:///not-readable'>]><AIConfigDetail>");
            File.WriteAllText(f.Path, serialized);
            try { f.Store.Read(); throw new Exception("DtdAccepted"); } catch (System.Xml.XmlException) { }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MTTFTest.DaqConfigTests." + Guid.NewGuid().ToString("N"));
            internal readonly string Repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
            internal readonly string Path;
            internal readonly EngineDaqConfigurationStore Store;
            internal Fixture()
            {
                Directory.CreateDirectory(Root); Path = System.IO.Path.Combine(Root, "AIConfig.xml");
                File.Copy(System.IO.Path.Combine(Repo, "MTTfTest", "Config", "AIConfig.xml"), Path);
                Store = new EngineDaqConfigurationStore(Path);
            }
            internal OperatorCommand Command()
            {
                var settings = Store.Read();
                var row = settings.DaqConfiguration.Channels.Single(c => c.Sequence == 4); row.Slope = 11.5; row.Intercept = -0.123;
                return new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(),
                    RunEpoch = 1, IssuedUtcTicks = DateTime.UtcNow.Ticks, Kind = OperatorCommandKind.CommitConfiguration,
                    TestConfiguration = settings, PayloadSha256 = settings.ComputeSha256() };
            }
            public void Dispose() { Directory.Delete(Root, true); }
        }
        private static void Assert(bool value) { if (!value) throw new Exception("DaqConfigurationAssertionFailed"); }
        private static void Expect(Action action, string expected)
        {
            try { action(); } catch (Exception ex) when (ex.Message == expected) { return; }
            throw new Exception("Expected:" + expected);
        }
    }
}
