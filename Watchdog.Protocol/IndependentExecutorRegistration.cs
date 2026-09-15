using System;
using System.IO;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class IndependentInstallationBinding
    {
        public int SchemaVersion { get; set; } = 1;
        public string InstallationId { get; set; }
        public string RegistrationPath { get; set; }

        public static string PathFor(string executablePath) => Path.GetFullPath(executablePath) + ".independent.json";

        public void Validate(IndependentExecutorRegistration registration, string executablePath)
        {
            if (SchemaVersion != 1 || registration == null || InstallationId != registration.InstallationId ||
                !Path.IsPathRooted(RegistrationPath ?? string.Empty) ||
                !string.Equals(Path.GetFullPath(RegistrationPath), RegistrationPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(RegistrationPath), registration.StateDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(executablePath), registration.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentInstallationBindingMismatch");
            registration.Validate();
        }

        public static IndependentInstallationBinding Resolve(string executablePath)
        {
            var path = PathFor(executablePath);
            if (!File.Exists(path)) return null;
            IndependentProtectedFiles.RequireTrustedFile(path);
            var originalHash = SupervisorProtocol.ComputeSha256(path);
            var binding = BoundedJson.Read<IndependentInstallationBinding>(path);
            if (binding == null || string.IsNullOrWhiteSpace(binding.RegistrationPath))
                throw new InvalidDataException("IndependentInstallationBindingMissing");
            var registration = IndependentExecutorRegistration.LoadTrusted(binding.RegistrationPath);
            binding.Validate(registration, executablePath);
            if (originalHash != SupervisorProtocol.ComputeSha256(path))
                throw new InvalidDataException("IndependentInstallationBindingChanged");
            return binding;
        }

        public static void RequireLegacyLaunchAllowed(string executablePath)
        {
            if (Resolve(executablePath) != null)
                throw new InvalidOperationException("IndependentExecutorOwnsProcessRelaunch");
        }

        public static IndependentInstallationBinding ResolveForStartup(string executablePath)
            => WithBindingLock(executablePath, () => Resolve(executablePath));

        public static void RequireCurrentSessionHost(string mainExecutable, string projectDirectory, int parentPid, long parentStartTicks)
        {
            var binding = Resolve(mainExecutable);
            if (binding == null) return;
            var registration = IndependentExecutorRegistration.LoadTrusted(binding.RegistrationPath);
            var state = new IndependentProjectStateStore(registration.StateDirectory).Read();
            if (state?.Intent != null) registration.RequireBoundIntent(state.Intent);
            RequireSessionHostState(state, registration.ProjectDirectory, projectDirectory, parentPid, parentStartTicks);
            using (var parent = System.Diagnostics.Process.GetProcessById(parentPid))
                if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != parentStartTicks ||
                    !string.Equals(Path.GetFullPath(parent.MainModule.FileName), Path.GetFullPath(mainExecutable), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentSessionParentIdentityMismatch");
        }

        public static string ProjectDirectoryFromSessionJournal(string journalDirectory)
        {
            if (!Path.IsPathRooted(journalDirectory ?? string.Empty))
                throw new InvalidDataException("IndependentSessionJournalPathInvalid");
            var path = Path.GetFullPath(journalDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(path), "WatchdogSessions", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentSessionJournalPathInvalid");
            return Path.GetDirectoryName(path);
        }

        public static void RequireCurrentSessionHostFromJournal(string mainExecutable, string journalDirectory,
            int parentPid, long parentStartTicks)
        {
            if (Resolve(mainExecutable) == null) return;
            RequireCurrentSessionHost(mainExecutable, ProjectDirectoryFromSessionJournal(journalDirectory),
                parentPid, parentStartTicks);
        }

        public static void RequireSessionHostState(IndependentProjectState state, string registeredProject,
            string requestedProject, int parentPid, long parentStartTicks)
        {
            if (state == null || parentPid <= 0 || parentStartTicks <= 0 ||
                !string.Equals(Path.GetFullPath(registeredProject), Path.GetFullPath(requestedProject), StringComparison.OrdinalIgnoreCase) ||
                state.Maintenance || state.SafetyCleanupPending ||
                state.Transaction != null && !state.Transaction.IsTerminal &&
                    state.Transaction.Phase != IndependentRecoveryPhase.Verifying &&
                    state.Transaction.Phase != IndependentRecoveryPhase.LaunchPending)
                throw new InvalidOperationException("IndependentSessionLaunchNotAdmitted");
            // During replacement bootstrap the consumed ticket identifies the
            // new parent before it commits its new run. Old snapshots cannot.
            var parent = state.Ticket != null && !state.Ticket.Revoked && state.Ticket.Consumer != null
                ? state.Ticket.Consumer : state.Controller;
            if (state.Intent?.Armed == true && (parent == null || parent.Pid != parentPid || parent.StartUtcTicks != parentStartTicks))
                throw new InvalidOperationException("IndependentSessionParentSuperseded");
        }

        public static void Install(string registrationPath)
        {
            var registration = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            WithBindingLock(registration.ExecutablePath, () =>
            {
                RequireControllerAbsent(registration.ExecutablePath);
                InstallCore(registrationPath, registration);
                return true;
            });
        }

        public static System.Diagnostics.Process StartLegacyProcess(System.Diagnostics.ProcessStartInfo startInfo)
        {
            if (startInfo == null || !Path.IsPathRooted(startInfo.FileName ?? string.Empty))
                throw new ArgumentException("IndependentLegacyExecutableMustBeAbsolute");
            return WithBindingLock(startInfo.FileName, () =>
            {
                RequireLegacyLaunchAllowed(startInfo.FileName);
                return System.Diagnostics.Process.Start(startInfo);
            });
        }

        public static void RequireControllerAbsent(string executablePath)
        {
            var expected = Path.GetFullPath(executablePath);
            var processes = System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected));
            try
            {
                if (processes.Length > 128) throw new InvalidOperationException("IndependentControllerInspectionLimit");
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        if (string.Equals(Path.GetFullPath(process.MainModule.FileName), expected, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("IndependentBindingControllerStillRunning");
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        if (!process.HasExited) throw;
                    }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private static T WithBindingLock<T>(string executablePath, Func<T> action)
        {
            using (var mutex = new System.Threading.Mutex(false, "Global\\MTTF-IndependentBinding-" +
                SupervisorProtocol.ComputeTextSha256(Path.GetFullPath(executablePath).ToUpperInvariant())))
            {
                var held = false;
                try
                {
                    try { held = mutex.WaitOne(5000); }
                    catch (System.Threading.AbandonedMutexException) { held = true; }
                    if (!held) throw new TimeoutException("IndependentInstallationBindingBusy");
                    return action();
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private static void InstallCore(string registrationPath, IndependentExecutorRegistration registration)
        {
            var state = new IndependentProjectStateStore(registration.StateDirectory).Read();
            if (state == null || !state.Maintenance || state.SafetyCleanupPending ||
                state.Transaction != null && !state.Transaction.IsTerminal ||
                state.Intent != null && state.Intent.Armed && !state.Intent.ManualStopped)
                throw new InvalidOperationException("IndependentBindingRequiresInstallationMaintenance");
            var existing = Resolve(registration.ExecutablePath);
            if (existing != null)
            {
                if (existing.InstallationId != registration.InstallationId ||
                    !string.Equals(existing.RegistrationPath, registrationPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentInstallationAlreadyBound");
                return;
            }
            var binding = new IndependentInstallationBinding
            { InstallationId = registration.InstallationId, RegistrationPath = Path.GetFullPath(registrationPath) };
            binding.Validate(registration, registration.ExecutablePath);
            BoundedJson.Write(PathFor(registration.ExecutablePath), binding);
            Resolve(registration.ExecutablePath);
        }
    }

    // Installed by an elevated installer. Mutable project state is never a
    // source of executable paths, hardware configuration or task names.
    public sealed class IndependentExecutorRegistration
    {
        public const string CurrentConfigurationHashProfile = "IndependentEligibilityV1";
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
        public string ConfigurationHashProfile { get; set; } = CurrentConfigurationHashProfile;
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
                !Hash(SafetyExecutableSha256) || !Hash(ConfigurationSha256) || ConfigurationHashProfile != CurrentConfigurationHashProfile || Runtime == null ||
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

        public void RequireSessionProcess(IndependentSessionProcess child)
        {
            Validate();
            if (child == null) throw new ArgumentNullException(nameof(child));
            child.Validate();
            var expected = Path.Combine(Path.GetDirectoryName(SafetyExecutablePath),
                child.Role == "Watchdog" ? "MTTFTest.Watchdog.exe" : "MTTFTest.SafetyAgent.exe");
            if (!SamePath(child.Process.ExecutablePath, expected))
                throw new InvalidOperationException("IndependentSessionExecutableMismatch");
        }

        public IndependentSessionProcess SelectSessionCleanupTarget(IndependentProjectState state, string executorIdentity)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            state.Validate();
            RequireBoundIntent(state.Intent);
            if (!state.SafetyCleanupPending || state.Transaction == null ||
                state.Transaction.Phase != IndependentRecoveryPhase.PowerOff ||
                string.IsNullOrWhiteSpace(executorIdentity) || state.Transaction.ExecutorIdentity != executorIdentity)
                throw new InvalidOperationException("IndependentSessionCleanupAuthorityMismatch");
            // Every entry was admitted for this installation under the same durable
            // lock. An older parent identity must not hide a surviving helper.
            // Validate the entire bounded registry before retiring any process.
            foreach (var child in state.SessionProcesses) RequireSessionProcess(child);
            return state.SessionProcesses.FirstOrDefault();
        }

        public static IndependentExecutorRegistration LoadTrusted(string path)
        {
            return LoadTrustedRegistration(path, true);
        }

        // Maintenance may need to replace a damaged control executable. This
        // reads protected installation identity, never grants launch authority.
        // Runtime and safety callers must continue using LoadTrusted.
        public static IndependentExecutorRegistration LoadTrustedForMaintenance(string path)
        {
            return LoadTrustedRegistration(path, false);
        }

        private static IndependentExecutorRegistration LoadTrustedRegistration(string path, bool verifyExecutables)
        {
            IndependentProtectedFiles.RequireTrustedFile(path);
            var originalHash = SupervisorProtocol.ComputeSha256(path);
            var registration = BoundedJson.Read<IndependentExecutorRegistration>(path);
            if (registration == null) throw new InvalidDataException("IndependentRegistrationMissing");
            registration.Validate();
            if (!SamePath(Path.GetDirectoryName(Path.GetFullPath(path)), registration.StateDirectory))
                throw new InvalidDataException("IndependentRegistrationStateDirectoryMismatch");
            if (verifyExecutables)
            {
                registration.VerifyFile(registration.ExecutablePath, registration.ExecutableSha256);
                registration.VerifyFile(registration.SafetyExecutablePath, registration.SafetyExecutableSha256);
            }
            foreach (var file in registration.Files)
                registration.VerifyFile(Path.Combine(registration.ConfigDirectory, file.Name), file.Sha256);
            if (!string.Equals(originalHash, SupervisorProtocol.ComputeSha256(path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentRegistrationChangedDuringValidation");
            return registration;
        }

        public static IndependentExecutorRegistration SealDraft(string draftPath, string registrationPath)
        {
            // This prepares files only. Binding installation and enabling the
            // service remain separate maintenance-controlled operations.
            IndependentProtectedFiles.RequireTrustedFile(draftPath);
            var registration = BoundedJson.Read<IndependentExecutorRegistration>(draftPath);
            if (registration == null) throw new InvalidDataException("IndependentRegistrationDraftMissing");
            registration.Validate();
            registrationPath = Path.GetFullPath(registrationPath);
            if (!SamePath(Path.GetDirectoryName(registrationPath), registration.StateDirectory))
                throw new InvalidDataException("IndependentRegistrationStateDirectoryMismatch");
            IndependentProtectedFiles.RequireTrustedDirectory(registration.StateDirectory);
            if (File.Exists(registrationPath)) throw new IOException("IndependentRegistrationAlreadyExists");
            var snapshot = Path.Combine(registration.StateDirectory, "safety-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapshot);
            IndependentProtectedFiles.RequireTrustedDirectory(snapshot);
            foreach (var file in registration.Files)
            {
                var source = Path.Combine(registration.ConfigDirectory, file.Name);
                var target = Path.Combine(snapshot, file.Name);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("IndependentConfigFileTooLarge");
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.CopyTo(output, 16384); output.Flush(true); }
                }
                registration.VerifyFile(target, file.Sha256);
            }
            registration.ConfigDirectory = snapshot;
            var temporary = registrationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                BoundedJson.Write(temporary, registration);
                LoadTrusted(temporary);
                File.Move(temporary, registrationPath);
                return LoadTrusted(registrationPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            // Failed snapshots deliberately remain as bounded installation
            // evidence. They never install a binding or authorize a run.
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
