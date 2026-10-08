using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    // Only the manual Start boundary calls Change. Recovery never enrolls new
    // configuration. Maintenance is persisted before stopping the executor or
    // replacing files, and remains in force after every incomplete operation.
    internal static class IndependentProjectBinding
    {
        internal sealed class ChangeRecord
        {
            public string Id { get; set; }
            public string InstallationId { get; set; }
            public bool Committed { get; set; }
            public bool HasSetup { get; set; }
        }

        internal sealed class DatabaseIdentity
        {
            public string ProjectDirectory { get; set; }
            public string DatabasePath { get; set; }
            public long CreationUtcTicks { get; set; }
            public string FileIdentity { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        private static string ReadFileIdentity(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!GetFileInformationByHandle(file.SafeFileHandle, out var information))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "IndependentProjectDatabaseIdentityUnavailable");
                return information.Volume.ToString("X8") + ":" + information.IndexHigh.ToString("X8") + information.IndexLow.ToString("X8");
            }
        }

        internal static string PendingPath(string registrationPath)
            => Path.Combine(Path.GetDirectoryName(registrationPath), "project-binding-pending.json");

        private static string StatePath(string registrationPath)
            => Path.Combine(Path.GetDirectoryName(registrationPath), "independent-project-state.json");

        private static string SetupPath(IndependentExecutorRegistration registration)
            => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(registration.ExecutablePath)), "install-setup.json");

        private static bool SamePath(string left, string right)
            => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

        internal static bool NeedsChange(string registrationPath, IndependentExecutorRegistration registration, GlobalConfig config)
        {
            if (File.Exists(PendingPath(registrationPath))) return true;
            var project = ConfigLoader.GetProjectRootDir(config.Test.StoreDir, config.Test.TestName);
            RequireDatabaseIdentity(registration, project, Path.Combine(project, "index.db"));
            var hash = UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config);
            if (hash == "unavailable") throw new InvalidOperationException("IndependentProjectConfigurationUnavailable：项目配置缺失或无法读取。");
            return !SamePath(project, registration.ProjectDirectory) ||
                !SupervisorProtocol.Sha256Equals(hash, registration.ConfigurationSha256) ||
                !File.Exists(IdentityPath(registration, project));
        }

        internal static IndependentExecutorRegistration Change(string registrationPath, GlobalConfig config)
        {
            RecoverPending(registrationPath);
            var current = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            RequireInteractiveUser(current);
            if (!NeedsChange(registrationPath, current, config)) return current;
            var state = new IndependentProjectStateStore(current.StateDirectory).Read();
            RequireStopped(state);
            if (state.Maintenance)
                throw new InvalidOperationException("IndependentProjectBindingExternalMaintenance：安装正处于维护状态，不能自动解除。");

            var project = ConfigLoader.GetProjectRootDir(config.Test.StoreDir, config.Test.TestName);
            var id = Guid.NewGuid().ToString("N");
            var draft = Path.Combine(current.StateDirectory, "binding-draft-" + id + ".json");
            var prepared = Path.Combine(current.StateDirectory, "binding-prepared-" + id + ".json");
            // Export and sealing only prepare protected files; no service, intent,
            // hardware output or installed binding changes until CommitPrepared.
            IndependentRegistrationExport.Export(project, current.StateDirectory,
                current.InteractiveUserSid, current.InstallationId, draft);
            var exported = BoundedJson.Read<IndependentExecutorRegistration>(draft);
            PreserveInstallationSettings(current, exported);
            BoundedJson.Write(draft, exported);
            var candidate = IndependentExecutorRegistration.SealDraft(draft, prepared);
            RequireSameInstallation(current, candidate);
            if (!SupervisorProtocol.Sha256Equals(candidate.ConfigurationSha256,
                UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config)))
                throw new InvalidOperationException("IndependentProjectBindingInputsChanged");
            CommitPrepared(registrationPath, prepared, IndependentExecutorRegistration.LoadTrusted,
                running => SetServiceRunning(current, running));
            return IndependentExecutorRegistration.LoadTrusted(registrationPath);
        }

        internal static void RecoverPending(string registrationPath)
        {
            if (!File.Exists(PendingPath(registrationPath))) return;
            IndependentProtectedFiles.RequireTrustedFile(PendingPath(registrationPath));
            var current = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            RequireInteractiveUser(current);
            RecoverPendingCore(registrationPath, IndependentExecutorRegistration.LoadTrusted,
                running => SetServiceRunning(current, running));
        }

        // The delegates let the file transaction run against a simulated service
        // in regression tests. Production always passes LoadTrusted and SCM above.
        internal static void CommitPrepared(string registrationPath, string preparedPath,
            Func<string, IndependentExecutorRegistration> readRegistration, Action<bool> service,
            Action<string> checkpoint = null)
        {
            if (File.Exists(PendingPath(registrationPath)))
                throw new InvalidOperationException("IndependentProjectBindingRecoveryRequired");
            var current = readRegistration(registrationPath);
            var candidate = readRegistration(preparedPath);
            var originalHash = SupervisorProtocol.ComputeSha256(registrationPath);
            var preparedHash = SupervisorProtocol.ComputeSha256(preparedPath);
            RequireSameInstallation(current, candidate);
            RequireDatabaseIdentity(current, candidate.ProjectDirectory, candidate.DatabasePath);
            if (File.GetCreationTimeUtc(candidate.DatabasePath).Ticks != candidate.DatabaseCreationUtcTicks)
                throw new InvalidOperationException("IndependentProjectDatabaseIdentityChanged");
            var store = new IndependentProjectStateStore(current.StateDirectory);
            var state = store.Read();
            RequireStopped(state);
            if (state.Intent != null) current.RequireBoundIntent(state.Intent);
            if (state.Maintenance) throw new InvalidOperationException("IndependentProjectBindingExternalMaintenance");
            var record = new ChangeRecord
            {
                Id = Guid.NewGuid().ToString("N"), InstallationId = current.InstallationId,
                HasSetup = File.Exists(SetupPath(current))
            };
            var archive = Path.Combine(current.StateDirectory, "binding-" + record.Id);
            Directory.CreateDirectory(archive);
            BoundedJson.Write(Path.Combine(archive, "old-registration.json"), current);
            BoundedJson.Write(Path.Combine(archive, "new-registration.json"), candidate);
            if (record.HasSetup)
            {
                var setup = BoundedJson.Read<MtEmbTest.IndependentInstallSetup.SetupState>(SetupPath(current));
                if (setup.SchemaVersion != 1 || setup.Stage != "Ready" || setup.InteractiveUserSid != current.InteractiveUserSid)
                    throw new InvalidDataException("IndependentProjectBindingSetupInvalid");
                BoundedJson.Write(Path.Combine(archive, "old-setup.json"), setup);
                setup.ProjectDirectory = candidate.ProjectDirectory;
                BoundedJson.Write(Path.Combine(archive, "new-setup.json"), setup);
            }
            store.Update(state.Revision, value =>
            {
                RequireStopped(value);
                if (value.Maintenance) throw new InvalidOperationException("IndependentProjectBindingExternalMaintenance");
                if (originalHash != SupervisorProtocol.ComputeSha256(registrationPath))
                    throw new InvalidOperationException("IndependentProjectBindingRegistrationChanged");
                value.Maintenance = true;
                if (value.Ticket != null) value.Ticket.Revoked = true;
                BoundedJson.Write(Path.Combine(archive, "old-state.json"), value);
                // Durable journal precedes the maintenance write. A crash at
                // either side is recoverable to this stopped, unarmed snapshot.
                BoundedJson.Write(PendingPath(registrationPath), record);
                return true;
            });
            checkpoint?.Invoke("Maintenance");
            service(false);
            checkpoint?.Invoke("ServiceStopped");
            using (var lease = new IndependentExecutorLease(current.StateDirectory, current.InstallationId))
            {
                state = store.Read();
                RequireStopped(state);
                if (!state.Maintenance) throw new InvalidOperationException("IndependentProjectBindingMaintenanceLost");
                candidate = readRegistration(preparedPath);
                if (originalHash != SupervisorProtocol.ComputeSha256(registrationPath) ||
                    preparedHash != SupervisorProtocol.ComputeSha256(preparedPath))
                    throw new InvalidOperationException("IndependentProjectBindingInputsChanged");
                RequireSameInstallation(current, candidate);
                RequireDatabaseIdentity(current, candidate.ProjectDirectory, candidate.DatabasePath);
                RememberDatabase(current);
                RememberDatabase(candidate);
                var next = SamePath(current.ProjectDirectory, candidate.ProjectDirectory)
                    ? BoundedJson.Read<IndependentProjectState>(Path.Combine(archive, "old-state.json"))
                    : new IndependentProjectState();
                next.Revision = checked(state.Revision + 1);
                next.Maintenance = true;
                next.Transaction = null;
                next.Ticket = null;
                next.CooperativeStopReceipt = null;
                next.SessionProcesses = Array.Empty<IndependentSessionProcess>();
                if (next.Intent != null)
                {
                    // Keep same-project permanent exclusions and epoch history;
                    // only the next explicit ArmManualRun may authorize motion.
                    next.Intent.ConfigurationSha256 = candidate.ConfigurationSha256;
                    next.Intent.Armed = false;
                    next.Intent.ManualStopped = true;
                    next.Intent.Revision = checked(next.Intent.Revision + 1);
                }
                next.Validate();
                BoundedJson.Write(Path.Combine(archive, "new-state.json"), next);
                checkpoint?.Invoke("Prepared");
                InstallSnapshots(registrationPath, candidate, record, archive, "new", checkpoint);
                readRegistration(registrationPath).RequireStoppedBinding(store.Read());
                record.Committed = true;
                BoundedJson.Write(PendingPath(registrationPath), record);
                checkpoint?.Invoke("Committed");
            }
            Finish(registrationPath, service, checkpoint);
        }

        internal static void RecoverPendingCore(string registrationPath,
            Func<string, IndependentExecutorRegistration> readRegistration, Action<bool> service,
            Action<string> checkpoint = null)
        {
            var marker = PendingPath(registrationPath);
            if (!File.Exists(marker)) return;
            var record = BoundedJson.Read<ChangeRecord>(marker);
            var current = readRegistration(registrationPath);
            if (!Guid.TryParseExact(record.Id, "N", out _) || record.InstallationId != current.InstallationId)
                throw new InvalidDataException("IndependentProjectBindingJournalInvalid");
            var archive = Path.Combine(current.StateDirectory, "binding-" + record.Id);
            var selected = record.Committed ? "new" : "old";
            var restored = BoundedJson.Read<IndependentExecutorRegistration>(Path.Combine(archive, selected + "-registration.json"));
            RequireSameInstallation(current, restored);
            var store = new IndependentProjectStateStore(current.StateDirectory);
            var state = store.Read();
            RequireStopped(state);
            store.SetInstallationMaintenance(state.Revision, true);
            service(false);
            using (var lease = new IndependentExecutorLease(current.StateDirectory, current.InstallationId))
            {
                RequireStopped(store.Read());
                RequireDatabaseIdentity(restored, restored.ProjectDirectory, restored.DatabasePath);
                InstallSnapshots(registrationPath, restored, record, archive, selected, checkpoint);
                readRegistration(registrationPath).RequireStoppedBinding(store.Read());
            }
            Finish(registrationPath, service, checkpoint);
        }

        private static void Finish(string registrationPath, Action<bool> service, Action<string> checkpoint)
        {
            // The service must reacquire its lease and construct a fresh runtime
            // from the replaced registration while launch is still fenced off.
            var store = new IndependentProjectStateStore(Path.GetDirectoryName(registrationPath));
            try
            {
                service(true);
                checkpoint?.Invoke("ServiceStarted");
                var state = store.Read();
                RequireStopped(state);
                store.SetInstallationMaintenance(state.Revision, false);
                checkpoint?.Invoke("Enabled");
                File.Delete(PendingPath(registrationPath));
            }
            catch
            {
                var state = store.Read();
                if (!state.Maintenance) store.SetInstallationMaintenance(state.Revision, true);
                throw;
            }
        }

        private static void InstallSnapshots(string registrationPath, IndependentExecutorRegistration registration,
            ChangeRecord record, string archive, string prefix, Action<string> checkpoint)
        {
            var state = BoundedJson.Read<IndependentProjectState>(Path.Combine(archive, prefix + "-state.json"));
            RequireStopped(state);
            state.Maintenance = true;
            state.Revision = checked(new IndependentProjectStateStore(registration.StateDirectory).Read().Revision + 1);
            BoundedJson.Write(Path.Combine(archive, "install-state.json"), state);
            AtomicCopy(Path.Combine(archive, prefix + "-registration.json"), registrationPath);
            checkpoint?.Invoke("RegistrationReplaced");
            AtomicCopy(Path.Combine(archive, "install-state.json"), StatePath(registrationPath));
            checkpoint?.Invoke("StateReplaced");
            if (record.HasSetup) AtomicCopy(Path.Combine(archive, prefix + "-setup.json"), SetupPath(registration));
            checkpoint?.Invoke("SetupReplaced");
        }

        private static void AtomicCopy(string source, string destination)
        {
            var temporary = destination + ".binding-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    if (input.Length > BoundedJson.MaximumBytes) throw new InvalidDataException("IndependentProjectBindingSnapshotTooLarge");
                    input.CopyTo(output, 4096);
                    output.Flush(true);
                }
                File.Replace(temporary, destination, destination + ".binding-backup");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static void RequireStopped(IndependentProjectState state)
        {
            if (state == null) throw new InvalidOperationException("IndependentProjectBindingStateMissing");
            state.Validate();
            if (state.SafetyCleanupPending || state.Transaction?.IsTerminal == false ||
                state.Intent != null && state.Intent.Armed && !state.Intent.ManualStopped)
                throw new InvalidOperationException("IndependentProjectBindingRequiresStoppedRun：请先完成停止和安全收尾。");
            foreach (var child in state.SessionProcesses)
            {
                try
                {
                    using (var process = Process.GetProcessById(child.Process.Pid))
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == child.Process.StartUtcTicks)
                            throw new InvalidOperationException("IndependentProjectBindingSessionStillRunning");
                }
                catch (ArgumentException) { }
            }
        }

        private static void RequireStoppedBinding(this IndependentExecutorRegistration registration, IndependentProjectState state)
        {
            RequireStopped(state);
            if (!state.Maintenance) throw new InvalidOperationException("IndependentProjectBindingMaintenanceLost");
            if (state.Intent != null) registration.RequireBoundIntent(state.Intent);
        }

        private static void RequireSameInstallation(IndependentExecutorRegistration current, IndependentExecutorRegistration candidate)
        {
            current.Validate(); candidate.Validate();
            if (current.InstallationId != candidate.InstallationId || current.InteractiveUserSid != candidate.InteractiveUserSid ||
                !SamePath(current.StateDirectory, candidate.StateDirectory) || !SamePath(current.ExecutablePath, candidate.ExecutablePath) ||
                !SamePath(current.SafetyExecutablePath, candidate.SafetyExecutablePath) ||
                !SupervisorProtocol.Sha256Equals(current.ExecutableSha256, candidate.ExecutableSha256) ||
                !SupervisorProtocol.Sha256Equals(current.SafetyExecutableSha256, candidate.SafetyExecutableSha256) ||
                current.StartupPositioningBudgetMs != candidate.StartupPositioningBudgetMs)
                throw new InvalidDataException("IndependentProjectBindingInstallationChanged");
        }

        internal static void PreserveInstallationSettings(IndependentExecutorRegistration current, IndependentExecutorRegistration draft)
        {
            // Export defaults are for a new installation, not a project switch.
            draft.StartupPositioningBudgetMs = current.StartupPositioningBudgetMs;
            RequireSameInstallation(current, draft);
        }

        private static string IdentityPath(IndependentExecutorRegistration registration, string project)
            => Path.Combine(registration.StateDirectory, "ProjectBindings",
                SupervisorProtocol.ComputeTextSha256(Path.GetFullPath(project).TrimEnd('\\').ToUpperInvariant()) + ".json");

        internal static void RequireDatabaseIdentity(IndependentExecutorRegistration current, string project, string database)
        {
            if (!File.Exists(database)) throw new FileNotFoundException("IndependentProjectDatabaseMissing：不会重建历史数据库。", database);
            var created = File.GetCreationTimeUtc(database).Ticks;
            if (SamePath(current.ProjectDirectory, project) &&
                (!SamePath(current.DatabasePath, database) || created != current.DatabaseCreationUtcTicks))
                throw new InvalidOperationException("IndependentProjectDatabaseIdentityChanged：数据库身份已变化，不能自动重新授权。");
            var path = IdentityPath(current, project);
            if (!File.Exists(path)) return;
            var known = BoundedJson.Read<DatabaseIdentity>(path);
            if (!SamePath(known.ProjectDirectory, project) || !SamePath(known.DatabasePath, database) || known.CreationUtcTicks != created ||
                string.IsNullOrWhiteSpace(known.FileIdentity) || known.FileIdentity != ReadFileIdentity(database))
                throw new InvalidOperationException("IndependentProjectDatabaseIdentityChanged：曾绑定项目的数据库已变化。");
        }

        private static void RememberDatabase(IndependentExecutorRegistration registration)
        {
            RequireDatabaseIdentity(registration, registration.ProjectDirectory, registration.DatabasePath);
            var path = IdentityPath(registration, registration.ProjectDirectory);
            if (!File.Exists(path)) BoundedJson.Write(path, new DatabaseIdentity
            {
                ProjectDirectory = registration.ProjectDirectory, DatabasePath = registration.DatabasePath,
                CreationUtcTicks = registration.DatabaseCreationUtcTicks, FileIdentity = ReadFileIdentity(registration.DatabasePath)
            });
        }

        private static void RequireInteractiveUser(IndependentExecutorRegistration registration)
        {
            using (var user = WindowsIdentity.GetCurrent())
                if (user.User.Value != registration.InteractiveUserSid)
                    throw new UnauthorizedAccessException("IndependentProjectBindingInteractiveUserMismatch");
            using (var process = Process.GetCurrentProcess())
                if (!SamePath(process.MainModule.FileName, registration.ExecutablePath) ||
                    !SupervisorProtocol.Sha256Equals(SupervisorProtocol.ComputeSha256(process.MainModule.FileName), registration.ExecutableSha256))
                    throw new UnauthorizedAccessException("IndependentProjectBindingControllerIdentityMismatch");
        }

        private static void SetServiceRunning(IndependentExecutorRegistration registration, bool running)
        {
            using (var service = new ServiceController("MTTFTestIndependent-" + registration.InstallationId))
            {
                service.Refresh();
                var desired = running ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped;
                if (service.Status == desired) return;
                if (running)
                {
                    if (service.Status == ServiceControllerStatus.StopPending)
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    if (service.Status != ServiceControllerStatus.StartPending) service.Start();
                }
                else
                {
                    if (service.Status == ServiceControllerStatus.StartPending)
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                }
                service.WaitForStatus(desired, TimeSpan.FromSeconds(15));
                service.Refresh();
                if (service.Status != desired) throw new InvalidOperationException("IndependentProjectBindingServiceTransitionUnconfirmed");
            }
        }
    }
}
