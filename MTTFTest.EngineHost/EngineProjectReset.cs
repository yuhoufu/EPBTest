using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MTTFTest.EngineHost
{
    internal sealed partial class EngineProjectSelectionStore
    {
        private sealed class ResetConfigurationFile
        {
            public string Name { get; set; }
            public string ContentBase64 { get; set; }
            public string Sha256 { get; set; }
        }

        private static bool IsRetainedResetConfiguration(string name) =>
            new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "AlarmConfig.xml", "PowerSupplyConfig.xml",
                "UIConfig.xml", "UnattendedAlarmConfig.xml" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            name.StartsWith("CAN", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase);

        private static ResetConfigurationFile[] CaptureResetConfiguration(string configurationPath, CancellationToken token)
        {
            var directory = Path.GetDirectoryName(configurationPath);
            RequirePlainDirectory(directory);
            var result = new List<ResetConfigurationFile>(); var total = 0;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (!IsRetainedResetConfiguration(name)) continue;
                RejectResetReparse(file);
                var bytes = ReadBytes(file, MaximumProjectBytes);
                total = checked(total + bytes.Length);
                if (total > MaximumProjectBytes || result.Count >= 64) throw new InvalidDataException("ResetConfigurationLimitExceeded");
                result.Add(new ResetConfigurationFile { Name = name, ContentBase64 = Convert.ToBase64String(bytes), Sha256 = Sha(bytes) });
            }
            return result.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void ValidateResetPreparation(Preparation prepared)
        {
            var files = prepared.ResetConfigurationFiles;
            if (!SamePath(prepared.SourceConfigurationPath, prepared.Plan.TargetConfigurationPath) || files == null || files.Length > 64 ||
                files.Any(file => file == null || string.IsNullOrEmpty(file.Name) || file.Name != Path.GetFileName(file.Name) ||
                    file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !IsRetainedResetConfiguration(file.Name) ||
                    file.ContentBase64 == null || file.ContentBase64.Length > MaximumProjectBytes * 2 ||
                    Sha(Convert.FromBase64String(file.ContentBase64)) != file.Sha256) ||
                files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length ||
                files.Sum(file => (long)Convert.FromBase64String(file.ContentBase64).Length) > MaximumProjectBytes)
                throw new InvalidDataException("ResetPreparationConfigurationInvalid");
        }

        private string PublishResetProject(Preparation prepared, string digest, CancellationToken token, Action<string> crashPoint)
        {
            ValidateResetPreparation(prepared);
            var plan = prepared.Plan;
            var project = Path.GetDirectoryName(Path.GetDirectoryName(plan.TargetConfigurationPath));
            ValidateResetProjectRoot(project);
            var parent = Path.GetDirectoryName(project);
            var archive = Path.Combine(parent, ".v3-reset-archive-" + plan.OperatorCommandId);
            var stage = Path.Combine(parent, ".v3-reset-stage-" + plan.OperatorCommandId);
            var expected = new CreationMarker { PlanSha256 = plan.ComputeSha256(), PreparedDocumentSha256 = digest };
            const string archiveMarker = "V3ResetArchive.v1.json";
            const string activeMarker = "V3ResetActive.v1.json";
            token.ThrowIfCancellationRequested();
            RequirePlainDirectory(parent);

            if (Directory.Exists(archive))
            {
                RequireResetMarker(archive, archiveMarker, expected);
                VerifyResetArchive(archive, prepared, token);
                if (Directory.Exists(project))
                {
                    RequireResetMarker(project, activeMarker, expected);
                    VerifyResetCandidate(project, prepared, token);
                    return archive; // Published before selection-pointer commit.
                }
            }
            else
            {
                if (File.Exists(archive)) throw new IOException("ResetArchivePathOccupied");
                RequirePlainDirectory(project);
                if (Sha(ReadBytes(plan.TargetConfigurationPath, MaximumProjectBytes)) != plan.SourceProjectFileSha256)
                    throw new InvalidOperationException("ResetSourceChanged;ArchiveForbidden");
                VerifyResetArchive(project, prepared, token);
            }

            if (File.Exists(stage)) throw new IOException("ResetStagePathOccupied");
            Directory.CreateDirectory(stage); RequirePlainDirectory(stage);
            if (!File.Exists(Path.Combine(stage, activeMarker)) && Directory.EnumerateFileSystemEntries(stage).Any())
                throw new IOException("ResetStageNotOwned");
            EnsureResetMarker(stage, activeMarker, expected);
            var configDirectory = Path.Combine(stage, "Config");
            Directory.CreateDirectory(configDirectory); RequirePlainDirectory(configDirectory);
            if (Directory.EnumerateFileSystemEntries(stage).Any(path => !SamePath(path, configDirectory) &&
                    !SamePath(path, Path.Combine(stage, activeMarker))) || Directory.EnumerateDirectories(configDirectory).Any())
                throw new IOException("ResetStageUnexpectedContent");
            var allowed = prepared.ResetConfigurationFiles.Select(file => file.Name).Concat(new[] { "TestConfig.xml" }).ToArray();
            if (Directory.EnumerateFiles(configDirectory).Any(path => !allowed.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
                throw new IOException("ResetStageUnexpectedConfiguration");
            WriteResetCandidateFile(Path.Combine(configDirectory, "TestConfig.xml"), Convert.FromBase64String(prepared.TargetXmlBase64), token);
            foreach (var file in prepared.ResetConfigurationFiles)
                WriteResetCandidateFile(Path.Combine(configDirectory, file.Name), Convert.FromBase64String(file.ContentBase64), token);
            VerifyResetCandidate(stage, prepared, token);
            crashPoint?.Invoke("ResetStaged"); token.ThrowIfCancellationRequested();

            if (!Directory.Exists(archive))
            {
                VerifyResetArchive(project, prepared, token);
                EnsureResetMarker(project, archiveMarker, expected);
                crashPoint?.Invoke("ResetArchiveMarked"); token.ThrowIfCancellationRequested();
                // Same-volume subtree rename preserves all old files, including
                // unknown historical formats. No copy-then-delete or recursive deletion.
                Directory.Move(project, archive);
                crashPoint?.Invoke("ResetArchived"); token.ThrowIfCancellationRequested();
            }
            RequireResetMarker(archive, archiveMarker, expected);
            VerifyResetArchive(archive, prepared, token);
            Directory.Move(stage, project); // Never merge into a competing project.
            crashPoint?.Invoke("ResetPublished"); token.ThrowIfCancellationRequested();
            return archive;
        }

        private static void VerifyResetArchive(string root, Preparation prepared, CancellationToken token)
        {
            var directories = new Stack<string>(); directories.Push(root);
            while (directories.Count != 0)
            {
                token.ThrowIfCancellationRequested(); var directory = directories.Pop(); RequirePlainDirectory(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested(); RejectResetReparse(entry);
                    if (Directory.Exists(entry)) directories.Push(entry);
                }
            }
            var config = Path.Combine(root, "Config");
            if (Sha(ReadBytes(Path.Combine(config, "TestConfig.xml"), MaximumProjectBytes)) != prepared.Plan.SourceProjectFileSha256)
                throw new InvalidDataException("ResetArchiveConfigurationChanged");
            var actual = CaptureResetConfiguration(Path.Combine(config, "TestConfig.xml"), token);
            if (actual.Length != prepared.ResetConfigurationFiles.Length ||
                actual.Where((file, index) => file.Name != prepared.ResetConfigurationFiles[index].Name ||
                    file.Sha256 != prepared.ResetConfigurationFiles[index].Sha256).Any())
                throw new InvalidDataException("ResetRetainedConfigurationChanged");
        }

        private static void VerifyResetCandidate(string root, Preparation prepared, CancellationToken token)
        {
            RequirePlainDirectory(root); var directory = Path.Combine(root, "Config"); RequirePlainDirectory(directory);
            token.ThrowIfCancellationRequested();
            var allowed = prepared.ResetConfigurationFiles.Select(file => file.Name).Concat(new[] { "TestConfig.xml" }).ToArray();
            if (Directory.EnumerateFileSystemEntries(root).Any(path => !SamePath(path, directory) &&
                    !SamePath(path, Path.Combine(root, "V3ResetActive.v1.json"))) ||
                Directory.EnumerateDirectories(directory).Any() ||
                Directory.EnumerateFiles(directory).Any(path => !allowed.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("ResetCandidateUnexpectedContent;OldRuntimeImportForbidden");
            var path = Path.Combine(directory, "TestConfig.xml"); RejectResetReparse(path);
            if (Sha(ReadBytes(path, MaximumProjectBytes)) != Sha(Convert.FromBase64String(prepared.TargetXmlBase64)))
                throw new InvalidDataException("ResetPublishedCandidateChanged");
            foreach (var file in prepared.ResetConfigurationFiles)
            {
                token.ThrowIfCancellationRequested(); path = Path.Combine(directory, file.Name); RejectResetReparse(path);
                if (Sha(ReadBytes(path, MaximumProjectBytes)) != file.Sha256) throw new InvalidDataException("ResetCandidateConfigurationChanged");
            }
        }

        private void EnsureResetMarker(string directory, string name, CreationMarker expected)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) WriteResetCandidateFile(path, Encode("ResetDirectory", expected), CancellationToken.None);
            RequireResetMarker(directory, name, expected);
        }

        private void RequireResetMarker(string directory, string name, CreationMarker expected)
        {
            RequirePlainDirectory(directory); var path = Path.Combine(directory, name); RejectResetReparse(path);
            var actual = Read<CreationMarker>(path, "ResetDirectory", out _);
            if (actual.PlanSha256 != expected.PlanSha256 || actual.PreparedDocumentSha256 != expected.PreparedDocumentSha256)
                throw new InvalidDataException("ResetDirectoryOwnerConflict");
        }

        private static void WriteResetCandidateFile(string path, byte[] bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                RejectResetReparse(path);
                if (Sha(ReadBytes(path, MaximumEnvelopeBytes)) != Sha(bytes)) throw new IOException("ResetCandidateFileConflict");
                return;
            }
            var temporary = path + ".pending";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path);
        }

        private void ValidateResetProjectRoot(string project)
        {
            var root = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar);
            var protectedPaths = new[] { Path.GetPathRoot(root), _root, AppDomain.CurrentDomain.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
            foreach (var path in protectedPaths.Where(path => !string.IsNullOrEmpty(path)))
            {
                var protectedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(root, protectedPath, StringComparison.OrdinalIgnoreCase) ||
                    protectedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ResetProjectRootContainsProtectedState");
            }
        }

        private static void RejectResetReparse(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("ResetReparsePointForbidden");
        }
    }
}
