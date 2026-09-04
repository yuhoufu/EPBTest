using System.Linq;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

internal static partial class Program
{
    private static EngineAoConfiguration AoSettings() => new EngineAoConfiguration
    {
        MinVoltage = 0, MaxVoltage = 10, MinPressure = 0, MaxPressure = 200,
        Devices = Enumerable.Range(1, 2).Select(id => new EngineAoDeviceConfiguration
        {
            Name = "Cylinder" + id, PhysicalChannel = "Dev" + id + "/ao0", ScaleK = 20 + id, Offset = id,
            Points = new[] { new EngineAoCalibrationPoint { Voltage = 1, Pressure = 21 + 2 * id, CommandPressure = 20 },
                new EngineAoCalibrationPoint { Voltage = 4, Pressure = 80 + 5 * id, CommandPressure = 80 } }
        }).ToArray()
    };

    private static void AoSnapshotMetadata()
    {
        var snapshot = ConfigurationClient().Value;
        snapshot.AoConfiguration = AoSettings(); snapshot.AoConfigurationRevision = 31;
        snapshot.AoConfigurationSha256 = snapshot.AoConfiguration.ComputeSha256();
        snapshot.Capabilities = snapshot.Capabilities.Concat(new[] { EngineUiContract.AoCalibration }).ToArray();
        Assert(snapshot.IsStructurallyValid());
        var json = new System.Web.Script.Serialization.JavaScriptSerializer();
        var copy = json.Deserialize<EngineUiSnapshot>(json.Serialize(snapshot));
        Assert(copy.IsStructurallyValid() && copy.AoConfigurationRevision == 31 && copy.ConfigurationRevision == snapshot.ConfigurationRevision);
        copy.AoConfiguration.Devices[0].Offset++; Assert(!copy.IsStructurallyValid());
        copy.AoConfiguration = null; Assert(!copy.IsStructurallyValid());
        Assert(snapshot.AoConfiguration.Devices[0].Offset == 1);
    }

    private static void AoClientCapabilityGate()
    {
        var fake = ConfigurationClient(); fake.Value.AoConfiguration = AoSettings(); fake.Value.AoConfigurationRevision = 31;
        fake.Value.AoConfigurationSha256 = fake.Value.AoConfiguration.ComputeSha256(); fake.RejectConfiguration = true;
        var counts = fake.Value.Channels.Select(c => c.FormalCycles).ToArray();
        var configuration = new TestConfigurationCommit { BaseConfigurationRevision = 31, BaseConfigurationSha256 = fake.Value.AoConfigurationSha256,
            AoCalibration = new AoCalibrationCommit { DeviceName = "Cylinder1", Points = fake.Value.AoConfiguration.Devices[0].Points.Select(p => p.Clone()).ToArray() } };
        using (var session = new V3MonitorSession(fake))
        {
            session.RefreshAsync().GetAwaiter().GetResult();
            Assert(!session.CanCalibrateAo); session.SubmitConfigurationAsync(configuration).GetAwaiter().GetResult(); Assert(fake.Commands == 0);
            fake.Value.Capabilities = fake.Value.Capabilities.Concat(new[] { EngineUiContract.AoCalibration }).ToArray();
            session.RefreshAsync().GetAwaiter().GetResult(); Assert(session.CanCalibrateAo);
            session.SubmitConfigurationAsync(configuration).GetAwaiter().GetResult();
            Assert(fake.Commands == 1 && fake.LastConfiguration.AoCalibration != null && fake.LastConfiguration.Configuration == null &&
                fake.LastConfiguration.DaqConfiguration == null && fake.LastConfiguration.BaseConfigurationRevision == 31);
            fake.Value.Engine.State = SystemTerminalState.Running; session.RefreshAsync().GetAwaiter().GetResult();
            Assert(!session.CanCalibrateAo); session.SubmitConfigurationAsync(configuration).GetAwaiter().GetResult(); Assert(fake.Commands == 1);
            Assert(fake.Value.Channels.Select(c => c.FormalCycles).SequenceEqual(counts) && fake.Value.AoConfigurationRevision == 31);
        }
    }
}
