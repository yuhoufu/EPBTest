using System;
using System.Configuration;
using System.IO;
using System.Linq;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    // Installer preparation only. This never arms a run, installs a binding,
    // opens hardware, or treats an exported draft as a trusted registration.
    internal static class IndependentRegistrationExport
    {
        internal static bool TryRun(string[] args)
        {
            if (args == null || !args.Contains("--export-independent-registration-draft")) return false;
            try
            {
                if (args.Length != 6 || args[0] != "--export-independent-registration-draft")
                    throw new ArgumentException("Expected project, state directory, interactive SID, installation ID, output path");
                Export(args[1], args[2], args[3], args[4], args[5]);
                Environment.ExitCode = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("IndependentRegistrationExportFailed:" + error.Message);
                Environment.ExitCode = 1;
            }
            return true;
        }

        internal static void Export(string project, string stateDirectory, string interactiveSid, string installationId, string output)
        {
            project = Path.GetFullPath(project).TrimEnd('\\');
            output = Path.GetFullPath(output);
            if (File.Exists(output)) throw new IOException("IndependentRegistrationDraftAlreadyExists");
            var projectConfig = Path.Combine(project, "Config", "TestConfig.xml");
            var database = Path.Combine(project, "index.db");
            if (!File.Exists(projectConfig) || !File.Exists(database))
                throw new FileNotFoundException("ExistingProjectConfigAndDatabaseRequired");
            var projectHash = SupervisorProtocol.ComputeSha256(projectConfig);
            var config = new GlobalConfig { Test = ConfigLoader.LoadTest(projectConfig, null) };
            config.Test.StoreDir = Path.GetDirectoryName(project);
            config.Test.TestName = Path.GetFileName(project);
            var hash = UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config);
            var hydraulics = config.Test.Hydraulics.Where(h => h.Enabled).OrderBy(h => h.Id).ToArray();
            if (hydraulics.Length == 0) throw new InvalidDataException("IndependentHydraulicConfigurationMissing");
            var daq = DaqRuntimeSettings.Load(ConfigurationManager.AppSettings);
            var registration = new IndependentExecutorRegistration
            {
                InstallationId = installationId, InteractiveUserSid = interactiveSid,
                ProjectDirectory = project, StateDirectory = Path.GetFullPath(stateDirectory).TrimEnd('\\'),
                DatabasePath = database, DatabaseCreationUtcTicks = File.GetCreationTimeUtc(database).Ticks,
                ExecutablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MTTFTest.exe"),
                SafetyExecutablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MTTFTest.SafetyAgent.exe"),
                ConfigurationSha256 = hash, ConfigDirectory = RuntimeConfigPaths.Directory,
                Runtime = new SafetyRuntimeSnapshot
                {
                    SampleRateHz = daq.SampleRateHz, SamplesPerChannel = daq.SamplesPerChannel,
                    PressureChannels = hydraulics.Select(h => "Pressure_" + h.Id).ToArray(),
                    ReleaseSafePressureBar = hydraulics.Select(h => h.ReleaseSafePressureBar).ToArray(),
                    PressureSampleMaxAgeMs = hydraulics.Min(h => h.PressureSampleMaxAgeMs),
                    ReleaseStableMs = hydraulics.Max(h => h.ReleaseStableMs),
                    ReleaseTimeoutMs = hydraulics.Max(h => h.ReleaseTimeoutMs)
                }
            };
            registration.ExecutableSha256 = SupervisorProtocol.ComputeSha256(registration.ExecutablePath);
            registration.SafetyExecutableSha256 = SupervisorProtocol.ComputeSha256(registration.SafetyExecutablePath);
            registration.Files = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" }
                .Select(name => new IndependentSafetyConfigFile
                {
                    Name = name, Sha256 = SupervisorProtocol.ComputeSha256(Path.Combine(registration.ConfigDirectory, name))
                }).ToArray();
            registration.Validate();
            if (projectHash != SupervisorProtocol.ComputeSha256(projectConfig) ||
                hash != UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config) ||
                registration.DatabaseCreationUtcTicks != File.GetCreationTimeUtc(database).Ticks)
                throw new IOException("IndependentRegistrationInputsChanged");
            var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                BoundedJson.Write(temporary, registration);
                File.Move(temporary, output); // no overwrite, including a concurrent export
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
