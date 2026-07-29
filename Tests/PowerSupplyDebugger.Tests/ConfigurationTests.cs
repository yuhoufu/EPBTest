using PowerSupplyDebugger.Models;
using PowerSupplyDebugger.Services;

namespace PowerSupplyDebugger.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public async Task ConfigurationRoundTripsFourEndpoints()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PowerSupplyDebuggerTests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "devices.json");
        try
        {
            var store = new DeviceConfigurationStore(path);
            var defaults = PowerSupplyEndpoint.CreateDefaults();
            await store.SaveAsync(defaults);
            var loaded = await store.LoadAsync();

            Assert.Equal(4, loaded.Count);
            Assert.Equal("192.168.1.101", loaded[0].Host);
            Assert.Equal(2268, loaded[0].Port);
            Assert.Equal("\\r\\n", loaded[0].Terminator);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task ConfigurationRejectsAnythingOtherThanFourDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"devices-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DeviceConfigurationStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.SaveAsync(PowerSupplyEndpoint.CreateDefaults().Take(3)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
