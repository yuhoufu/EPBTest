using System;
using System.IO;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // Installed by an elevated installer. Mutable project state is never a
    // source of executable paths, hardware configuration or task names.
    public sealed class IndependentExecutorRegistration
    {
        public int SchemaVersion { get; set; } = 1;
        public string InstallationId { get; set; }
        public string InteractiveUserSid { get; set; }
        public string ProjectDirectory { get; set; }
        public string DatabasePath { get; set; }
        public long DatabaseCreationUtcTicks { get; set; }
        public string StateDirectory { get; set; }
        public string ExecutablePath { get; set; }
        public string ExecutableSha256 { get; set; }
        public string SafetyExecutablePath { get; set; }
        public string SafetyExecutableSha256 { get; set; }
        public string ConfigurationSha256 { get; set; }
        public string ConfigDirectory { get; set; }
        public IndependentSafetyConfigFile[] Files { get; set; }
        public SafetyRuntimeSnapshot Runtime { get; set; }
        public long StartupPositioningBudgetMs { get; set; } = 300000;
        public string LaunchTaskName => @"\MTTFTest\Independent-" + InstallationId;

        private static bool Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

        private static string LocalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) ||
                value.StartsWith(@"\\", StringComparison.Ordinal) || value.Length < 3 ||
                value[1] != ':' || value[2] != '\\' || value.Substring(2).Contains(":"))
                throw new InvalidDataException("IndependentRegistrationLocalPathRequired");
            var full = Path.GetFullPath(value).TrimEnd('\\');
            if (full.Length <= 2 || !string.Equals(full, value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentRegistrationCanonicalPathRequired");
            return full;
        }

        private static bool SamePath(string left, string right) =>
            string.Equals(LocalPath(left), LocalPath(right), StringComparison.OrdinalIgnoreCase);

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(InteractiveUserSid) ||
                !new System.Security.Principal.SecurityIdentifier(InteractiveUserSid).IsAccountSid())
                throw new InvalidDataException("IndependentRegistrationInteractiveUserInvalid");
            if (SchemaVersion != 1 || !Guid.TryParseExact(InstallationId, "N", out _) ||
                DatabaseCreationUtcTicks <= 0 || !Hash(ExecutableSha256) ||
                !Hash(SafetyExecutableSha256) || !Hash(ConfigurationSha256) || Runtime == null ||
                StartupPositioningBudgetMs <= 0 || StartupPositioningBudgetMs > 300000)
                throw new InvalidDataException("IndependentRegistrationInvalid");
            foreach (var path in new[] { ProjectDirectory, DatabasePath, StateDirectory,
                ExecutablePath, SafetyExecutablePath, ConfigDirectory }) LocalPath(path);
            if (!LocalPath(DatabasePath).StartsWith(LocalPath(ProjectDirectory) + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentRegistrationDatabaseOutsideProject");
            Runtime.Validate();
            var names = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" };
            if (Files == null || Files.Length != names.Length || Files.Any(f => f == null || !Hash(f.Sha256)) ||
                !Files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)
                    .SequenceEqual(names.OrderBy(n => n, StringComparer.Ordinal)))
                throw new InvalidDataException("IndependentRegistrationConfigManifestInvalid");
        }

        public void RequireBoundIntent(IndependentRunIntent intent)
        {
            Validate();
            if (intent == null) throw new InvalidDataException("IndependentRegistrationIntentMissing");
            intent.Validate();
            if (!SamePath(intent.ProjectDirectory, ProjectDirectory) || !SamePath(intent.DatabasePath, DatabasePath) ||
                intent.DatabaseCreationUtcTicks != DatabaseCreationUtcTicks || !SamePath(intent.ExecutablePath, ExecutablePath) ||
                !string.Equals(intent.ConfigurationSha256, ConfigurationSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentRegistrationIntentBindingMismatch");
        }

        public static IndependentExecutorRegistration LoadTrusted(string path)
        {
            IndependentProtectedFiles.RequireTrustedFile(path);
            var originalHash = SupervisorProtocol.ComputeSha256(path);
            var registration = BoundedJson.Read<IndependentExecutorRegistration>(path);
            if (registration == null) throw new InvalidDataException("IndependentRegistrationMissing");
            registration.Validate();
            if (!SamePath(Path.GetDirectoryName(Path.GetFullPath(path)), registration.StateDirectory))
                throw new InvalidDataException("IndependentRegistrationStateDirectoryMismatch");
            registration.VerifyFile(registration.ExecutablePath, registration.ExecutableSha256);
            registration.VerifyFile(registration.SafetyExecutablePath, registration.SafetyExecutableSha256);
            foreach (var file in registration.Files)
                registration.VerifyFile(Path.Combine(registration.ConfigDirectory, file.Name), file.Sha256);
            if (!string.Equals(originalHash, SupervisorProtocol.ComputeSha256(path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentRegistrationChangedDuringValidation");
            return registration;
        }

        public IndependentSafetyWorkerCommand CreateSafetyCommand(IndependentProjectState state,
            string executorIdentity, long now)
        {
            if (state == null) throw new InvalidDataException("IndependentRegistrationStateMissing");
            state.Validate();
            RequireBoundIntent(state.Intent);
            var transaction = state.Transaction;
            if (!state.SafetyCleanupPending || transaction == null ||
                string.IsNullOrWhiteSpace(executorIdentity) || transaction.ExecutorIdentity != executorIdentity)
                throw new InvalidOperationException("IndependentRegistrationSafetyOwnerMismatch");
            // Manual revocation must still allow an already-owned cleanup stage.
            // It never authorizes launch; the aggregate stage commit decides that.
            var command = new IndependentSafetyWorkerCommand
            {
                StageNonce = Guid.NewGuid().ToString("N"), Transaction = transaction,
                Runtime = Runtime, ConfigDirectory = ConfigDirectory,
                Files = Files.Select(file => new IndependentSafetyConfigFile
                    { Name = file.Name, Sha256 = file.Sha256 }).ToArray(),
                IssuedUtcTicks = now, DeadlineUtcTicks = transaction.PhaseDeadlineUtcTicks
            };
            command.Validate(now);
            return command;
        }

        private void VerifyFile(string path, string expected)
        {
            IndependentProtectedFiles.RequireTrustedFile(path);
            if (!string.Equals(SupervisorProtocol.ComputeSha256(path), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentRegistrationFileHashMismatch:" + Path.GetFileName(path));
        }
    }
}
