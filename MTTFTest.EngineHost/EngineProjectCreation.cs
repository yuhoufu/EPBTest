using System;
using System.IO;
using System.Linq;
using System.Threading;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed partial class EngineProjectSelectionStore
    {
        private const string CreationMarkerName = "V3NewProject.v1.json";
        private sealed class CreationMarker
        {
            public string PlanSha256 { get; set; }
            public string PreparedDocumentSha256 { get; set; }
        }

        private byte[] BuildNewProjectBytes(TestConfig candidate, ProjectSwitchPlan plan, CancellationToken token)
        {
            var creation = plan.Creation;
            if (creation?.IsStructurallyValid(plan.TargetConfigurationPath) != true)
                throw new InvalidOperationException("NewProjectRequestInvalid");
            var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(plan.TargetConfigurationPath));
            if (Directory.Exists(projectRoot) || File.Exists(projectRoot)) throw new IOException("NewProjectAlreadyExists;OverwriteForbidden");
            if (!Directory.Exists(creation.Configuration.StoreDir)) throw new DirectoryNotFoundException("NewProjectStoreRootMissing");
            return BuildFreshProjectBytes(candidate, creation.Configuration, plan, token);
        }

        private byte[] BuildFreshProjectBytes(TestConfig candidate, EngineTestConfiguration configuration,
            ProjectSwitchPlan plan, CancellationToken token)
        {
            // candidate was deserialized from the frozen source bytes, not the
            // live configuration. Only the new project's history is initialized.
            EngineTestConfigurationStore.Apply(candidate, configuration);
            candidate.StoreDir = configuration.StoreDir;
            candidate.TestName = configuration.TestName;
            foreach (var record in candidate.EpbRecords.Snapshot())
            {
                record.StartTime = null; record.LatestStartTime = null;
                record.RunCount = 0; record.MechanicalCycleCount = 0; record.RunTimeSpan = TimeSpan.Zero;
                record.ConsecutivePeriodOverrunCount = 0; record.LastPeriodOverrunUtc = null;
                record.Status = record.PermanentAlarmLatched ? EpbTestStatus.Alarm : EpbTestStatus.NotStarted;
                // Do not call ResetKeepTotalCount: it clears permanent alarms
                // and the hardware's operator-relearning safety requirement.
            }
            EngineProjectIsolation.Apply(candidate, plan.IsolatedResources);
            var scratch = Path.Combine(_root, "new-project-" + RecoveryProtocolV7.NewId() + ".xml");
            try
            {
                token.ThrowIfCancellationRequested();
                // A fresh root excludes old transaction stamps and unknown
                // authority fields; SaveTest writes only supported configuration.
                using (var stream = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // SaveTest updates an existing Basic element; without it,
                    // LoadTest silently substitutes the template name and path.
                    var root = Utf8.GetBytes("<TestConfig><Basic /></TestConfig>"); stream.Write(root, 0, root.Length);
                }
                ConfigLoader.SaveTest(scratch, candidate);
                var bytes = ReadBytes(scratch, MaximumProjectBytes);
                var validated = ValidateProject(plan.TargetConfigurationPath, bytes);
                if (EngineTestConfigurationStore.Capture(validated).ComputeSha256() != EngineTestConfigurationStore.Capture(candidate).ComputeSha256() ||
                    validated.EpbRecords.Snapshot().Any(row => row.RunCount != 0 || row.MechanicalCycleCount != 0 || row.RunTimeSpan != TimeSpan.Zero))
                    throw new InvalidDataException("NewProjectCandidateRoundTripInvalid");
                token.ThrowIfCancellationRequested();
                return bytes;
            }
            finally
            {
                if (File.Exists(scratch)) File.Delete(scratch);
                if (File.Exists(scratch + ".tmp")) File.Delete(scratch + ".tmp");
            }
        }

        private void PublishNewProject(Preparation prepared, string digest, CancellationToken token, Action<string> crashPoint)
        {
            var plan = prepared.Plan;
            var targetRoot = Path.GetDirectoryName(Path.GetDirectoryName(plan.TargetConfigurationPath));
            var parent = Path.GetDirectoryName(targetRoot);
            var stage = Path.Combine(parent, ".v3-create-" + plan.OperatorCommandId);
            var expected = new CreationMarker { PlanSha256 = plan.ComputeSha256(), PreparedDocumentSha256 = digest };
            var targetBytes = Convert.FromBase64String(prepared.TargetXmlBase64);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(targetRoot))
            {
                RequireCreationMarker(targetRoot, expected);
                RequirePlainDirectory(Path.GetDirectoryName(plan.TargetConfigurationPath));
                if (Sha(ReadBytes(plan.TargetConfigurationPath, MaximumProjectBytes)) != Sha(targetBytes))
                    throw new InvalidOperationException("NewProjectPublishedCandidateChanged");
                return; // Rename committed before its source preparation receipt.
            }
            if (File.Exists(targetRoot)) throw new IOException("NewProjectAlreadyExists;OverwriteForbidden");
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("NewProjectStoreRootMissing");
            Directory.CreateDirectory(stage);
            RequirePlainDirectory(stage);
            var markerPath = Path.Combine(stage, CreationMarkerName);
            if (File.Exists(markerPath)) RequireCreationMarker(stage, expected);
            else
            {
                // A crash immediately after creation may leave an empty owned
                // staging directory. Any pre-existing content is not ours to replace.
                if (Directory.EnumerateFileSystemEntries(stage).Any()) throw new IOException("NewProjectStagingNotOwned");
                // Immutable ownership marker: no rolling backup, and no long
                // journal temporary filename inside the movable project directory.
                var temporaryMarker = Path.Combine(stage, "creation.pending");
                var markerBytes = Encode("NewProject", expected);
                using (var stream = new FileStream(temporaryMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(markerBytes, 0, markerBytes.Length); stream.Flush(true); }
                File.Move(temporaryMarker, markerPath);
            }
            var configDirectory = Path.Combine(stage, "Config");
            Directory.CreateDirectory(configDirectory); RequirePlainDirectory(configDirectory);
            if (Directory.EnumerateDirectories(stage).Any(path => !SamePath(path, configDirectory)) ||
                Directory.EnumerateDirectories(configDirectory).Any() ||
                Directory.EnumerateFiles(stage).Any(path => !SamePath(path, markerPath)) ||
                Directory.EnumerateFiles(configDirectory).Any(path => !string.Equals(Path.GetFileName(path), "TestConfig.xml", StringComparison.Ordinal)))
                throw new IOException("NewProjectStagingUnexpectedContent");
            var stagedConfig = Path.Combine(configDirectory, "TestConfig.xml");
            if (File.Exists(stagedConfig))
            {
                if (Sha(ReadBytes(stagedConfig, MaximumProjectBytes)) != Sha(targetBytes)) throw new IOException("NewProjectStagedCandidateChanged");
            }
            else
            {
                var temporary = stagedConfig + ".partial." + RecoveryProtocolV7.NewId();
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { stream.Write(targetBytes, 0, targetBytes.Length); stream.Flush(true); }
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, stagedConfig);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            crashPoint?.Invoke("CreationStaged");
            token.ThrowIfCancellationRequested();
            // Same-parent directory rename publishes a complete project at once.
            // Move must fail, never merge, if a competing project appeared.
            Directory.Move(stage, targetRoot);
            crashPoint?.Invoke("CreationPublished");
            token.ThrowIfCancellationRequested();
        }

        private void RequireCreationMarker(string directory, CreationMarker expected)
        {
            RequirePlainDirectory(directory);
            var path = Path.Combine(directory, CreationMarkerName);
            if (!File.Exists(path)) throw new IOException("NewProjectAlreadyExists;UnownedProjectCannotBeOverwritten");
            var marker = Read<CreationMarker>(path, "NewProject", out _);
            if (marker?.PlanSha256 != expected.PlanSha256 || marker.PreparedDocumentSha256 != expected.PreparedDocumentSha256)
                throw new InvalidDataException("NewProjectCreationOwnerConflict");
        }

        private static void RequirePlainDirectory(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("NewProjectReparsePointForbidden");
        }
    }
}
