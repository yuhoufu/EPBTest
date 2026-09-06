using System.Text.Json;
using PowerSupplyDebugger.Models;

namespace PowerSupplyDebugger.Services;

public sealed class DeviceConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public DeviceConfigurationStore(string? configurationPath = null)
    {
        ConfigurationPath = configurationPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EPBTest",
            "PowerSupplyDebugger",
            "devices.json");
    }

    public string ConfigurationPath { get; }

    public async Task<IReadOnlyList<PowerSupplyEndpoint>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath))
        {
            var defaults = PowerSupplyEndpoint.CreateDefaults();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults.Select(static endpoint => endpoint.Clone()).ToArray();
        }

        await using var stream = File.OpenRead(ConfigurationPath);
        var endpoints = await JsonSerializer.DeserializeAsync<List<PowerSupplyEndpoint>>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (endpoints is not { Count: 4 })
        {
            throw new InvalidDataException("设备配置必须正好包含四台程控电源。");
        }

        foreach (var endpoint in endpoints)
        {
            endpoint.Validate();
        }

        if (endpoints.Select(static item => item.Id).Distinct().Count() != endpoints.Count)
        {
            throw new InvalidDataException("设备配置中的编号不能重复。");
        }

        return endpoints.OrderBy(static endpoint => endpoint.Id).ToArray();
    }

    public async Task SaveAsync(
        IEnumerable<PowerSupplyEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        var materialized = endpoints.OrderBy(static endpoint => endpoint.Id).ToArray();
        if (materialized.Length != 4)
        {
            throw new InvalidDataException("设备配置必须正好包含四台程控电源。");
        }

        foreach (var endpoint in materialized)
        {
            endpoint.Validate();
        }

        var directory = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new InvalidOperationException("配置路径无效。");
        Directory.CreateDirectory(directory);

        var temporaryPath = ConfigurationPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, materialized, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, ConfigurationPath, true);
    }
}
