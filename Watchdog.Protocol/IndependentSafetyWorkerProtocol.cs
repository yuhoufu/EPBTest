using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class IndependentSafetyConfigFile
    {
        public string Name { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class IndependentSafetyWorkerCommand
    {
        public int SchemaVersion { get; set; } = 1;
        public string StageNonce { get; set; }
        public IndependentRecoveryTransaction Transaction { get; set; }
        public SafetyRuntimeSnapshot Runtime { get; set; }
        public string ConfigDirectory { get; set; }
        public IndependentSafetyConfigFile[] Files { get; set; }
        public long IssuedUtcTicks { get; set; }
        public long DeadlineUtcTicks { get; set; }

        public void Validate(long now)
        {
            if (SchemaVersion != 1 || !Guid.TryParseExact(StageNonce, "N", out _) || Transaction == null ||
                Runtime == null || !Path.IsPathRooted(ConfigDirectory ?? string.Empty) ||
                IssuedUtcTicks <= 0 || IssuedUtcTicks < Transaction.LastAttemptUtcTicks || now < IssuedUtcTicks || DeadlineUtcTicks <= now ||
                DeadlineUtcTicks - IssuedUtcTicks > TimeSpan.FromMinutes(5).Ticks ||
                DeadlineUtcTicks > Transaction.PhaseDeadlineUtcTicks)
                throw new InvalidDataException("IndependentSafetyCommandInvalid");
            Transaction.Validate(); Runtime.Validate();
            if (Transaction.Phase != IndependentRecoveryPhase.PowerOff && Transaction.Phase != IndependentRecoveryPhase.OutputsSafe)
                throw new InvalidDataException("IndependentSafetyCommandPhaseInvalid");
            var names = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" };
            if (Files == null || Files.Length != names.Length || Files.Any(f => f == null) ||
                !Files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(names.OrderBy(n => n, StringComparer.Ordinal)) ||
                Files.Any(f => f.Sha256?.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("IndependentSafetyConfigManifestInvalid");
        }
    }

    public sealed class IndependentSafetyWorkerReceipt
    {
        public int SchemaVersion { get; set; } = 1;
        public string StageNonce { get; set; }
        public string CommandSha256 { get; set; }
        public string RequestId { get; set; }
        public long Generation { get; set; }
        public IndependentRecoveryPhase Phase { get; set; }
        public int WorkerPid { get; set; }
        public long WorkerStartUtcTicks { get; set; }
        public long CompletedUtcTicks { get; set; }
        public bool Confirmed { get; set; }
        public string Detail { get; set; }

        public bool MatchesCurrent(IndependentSafetyWorkerCommand command, string commandSha256,
            int workerPid, long workerStartUtcTicks, long now)
        {
            if (command == null) return false;
            try { command.Validate(now); }
            catch (InvalidDataException) { return false; }
            return SchemaVersion == 1 && Confirmed && StageNonce == command.StageNonce &&
                commandSha256?.Length == 64 && string.Equals(CommandSha256, commandSha256, StringComparison.OrdinalIgnoreCase) &&
                RequestId == command.Transaction.RequestId && Generation == command.Transaction.Generation &&
                Phase == command.Transaction.Phase && workerPid > 0 && workerStartUtcTicks > 0 &&
                WorkerPid == workerPid && WorkerStartUtcTicks == workerStartUtcTicks &&
                CompletedUtcTicks >= Math.Max(command.IssuedUtcTicks, workerStartUtcTicks) &&
                CompletedUtcTicks < command.DeadlineUtcTicks && CompletedUtcTicks <= now;
        }
    }

    public static class IndependentProtectedFiles
    {
        private static bool Trusted(IdentityReference identity)
        {
            var sid = (SecurityIdentifier)identity.Translate(typeof(SecurityIdentifier));
            if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return true;
            var installer = (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier));
            return sid == installer;
        }

        public static void RequireTrustedFile(string path)
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path))
                throw new InvalidDataException("IndependentProtectedFileMissingOrRemote");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("IndependentProtectedFileReparsePoint");
            CheckAcl(File.GetAccessControl(path), true);
            RequireTrustedDirectory(Path.GetDirectoryName(path));
        }

        public static void RequireTrustedDirectory(string path)
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(path))
                throw new InvalidDataException("IndependentProtectedDirectoryMissingOrRemote");
            var directory = new DirectoryInfo(path);
            var immediate = true;
            while (directory != null)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("IndependentProtectedAncestorReparsePoint");
                CheckAcl(directory.GetAccessControl(), immediate);
                immediate = false;
                directory = directory.Parent;
            }
        }

        private static void CheckAcl(FileSystemSecurity acl, bool direct)
        {
            if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("IndependentProtectedOwnerUntrusted");
            var dangerous = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
                FileSystemRights.DeleteSubdirectoriesAndFiles;
            if (direct) dangerous |= FileSystemRights.Write | FileSystemRights.Delete;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                    (rule.FileSystemRights & dangerous) != 0 && !Trusted(rule.IdentityReference))
                    throw new UnauthorizedAccessException("IndependentProtectedWriteAccessUntrusted");
        }
    }
}
