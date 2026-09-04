using System;
using System.IO;
using System.Linq;
using System.Threading;
using Config;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static partial class EngineProjectSelectionTests
    {
        private static RecoveryCommand ResetCommand(Fixture f)
        {
            var configuration = EngineTestConfigurationStore.Capture(ConfigLoader.LoadTest(f.SourcePath, null));
            var command = SourceCommand(f, configuration);
            var input = command.OperatorTransaction.ProjectSwitch;
            var reset = configuration.Clone(); reset.TestPeriod = 19;
            input.TargetConfigurationPath = f.SourcePath; input.TargetProjectFileSha256 = input.SourceProjectFileSha256;
            input.Reset = new ProjectResetRequest { Configuration = reset };
            command.OperatorTransaction.PayloadSha256 = input.ComputeSha256();
            command.ProjectSwitch.Reset = input.Reset.Clone(); command.ProjectSwitch.TargetConfigurationPath = f.SourcePath;
            command.ProjectSwitch.TargetProjectFileSha256 = input.SourceProjectFileSha256;
            command.IdempotencyKey = command.ExpectedIdempotencyKey();
            Assert(command.IsStructurallyValid(), "reset fixture invalid");
            return command;
        }

        private static string ResetRoot(Fixture f) => Path.GetDirectoryName(Path.GetDirectoryName(f.SourcePath));
        private static string ResetArchive(Fixture f, ProjectSwitchPlan plan) => Path.Combine(f.Root, ".v3-reset-archive-" + plan.OperatorCommandId);
        private static RecoveryIdentity ResetDestination(ProjectSwitchPlan plan)
        { var result = plan.SourceIdentity.Clone(); result.RunId = plan.NextRunId; result.RunEpoch = plan.NextRunEpoch; return result; }

        private static readonly string[] ResetDataFiles = { "V3Persistence/index.db", "V3Persistence/1/ring.bin", "Raw/data.bin",
            "Latest/EPB4.bin", "log/run.log", "unknown-future-format/history.bin", "Config/EpbAdaptiveProfiles.xml" };

        private static void SeedReset(Fixture f)
        {
            foreach (var relative in ResetDataFiles)
            {
                var path = Path.Combine(ResetRoot(f), relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "historical-21508-" + relative);
            }
            File.WriteAllText(Path.Combine(ResetRoot(f), "Config", "AIConfig.xml"), "<Calibration slope='1.25' offset='0.05' />");
        }

        private static void ResetHistory()
        {
            using (var f = new Fixture())
            {
                SeedReset(f); var command = ResetCommand(f); var plan = command.ProjectSwitch;
                var original = File.ReadAllBytes(f.SourcePath); var target = File.ReadAllBytes(f.TargetPath);
                var digest = f.Store.Prepare(plan, Closed());
                Assert(File.ReadAllBytes(f.SourcePath).SequenceEqual(original) && !Directory.Exists(ResetArchive(f, plan)) &&
                    ResetDataFiles.All(relative => File.Exists(Path.Combine(ResetRoot(f), relative))), "preparation mutated live history");
                var destination = ResetDestination(plan);
                var selection = f.Store.Activate(destination, plan.OperatorCommandId, digest, expectedPlanSha256: plan.ComputeSha256());
                var archive = ResetArchive(f, plan);
                Assert(selection.LastResetArchivePath == archive && !selection.ResetPublicationStarted && selection.RunScopedCheckpoint &&
                    File.ReadAllBytes(Path.Combine(archive, "Config", "TestConfig.xml")).SequenceEqual(original), "old configuration not preserved");
                foreach (var relative in ResetDataFiles)
                    Assert(File.ReadAllText(Path.Combine(archive, relative)) == "historical-21508-" + relative &&
                        !File.Exists(Path.Combine(ResetRoot(f), relative)), "history deleted or reused as active: " + relative);
                var fresh = ConfigLoader.LoadTest(f.SourcePath, null);
                Assert(fresh.TestName == "SourceProject" && fresh.TestPeriod == 19 &&
                    fresh.EpbRecords.Snapshot().All(row => row.RunCount == 0 && row.MechanicalCycleCount == 0 && row.RunTimeSpan == TimeSpan.Zero),
                    "reset did not initialize a fresh run");
                Assert(fresh.GetEpbRecord(6).PermanentAlarmLatched && !fresh.GetEpbRecord(6).Enabled &&
                    fresh.GetEpbRecord(6).PermanentAlarmReason == "Synthetic isolated fixture", "reset cleared hardware isolation");
                Assert(File.ReadAllText(Path.Combine(ResetRoot(f), "Config", "AIConfig.xml")) == "<Calibration slope='1.25' offset='0.05' />" &&
                    File.ReadAllBytes(f.TargetPath).SequenceEqual(target), "reset changed retained calibration or another project");
                var checkpoint = new EngineRunCheckpointStore(destination.SessionId, Path.Combine(f.Root, "fresh-checkpoint"), destination.RunId)
                    .LoadOrCreate(destination, fresh, EngineTestConfigurationStore.Capture(fresh).ComputeSha256());
                Assert(checkpoint.FormalCyclesCompleted.All(count => count == 0) && checkpoint.IsolatedResources.Contains("Channel:6"), "new checkpoint imported old work/authority");
                Reject(() => f.Store.ReadActive(f.Source), "RunMismatch");
            }
        }

        private static void ResetReplay()
        {
            using (var f = new Fixture())
            {
                SeedReset(f); var plan = ResetCommand(f).ProjectSwitch; var destination = ResetDestination(plan);
                var digest = f.Store.Prepare(plan, Closed()); f.Store.Activate(destination, plan.OperatorCommandId, digest);
                var active = ConfigLoader.LoadTest(f.SourcePath, null); active.GetEpbRecord(4).RunCount = 9; active.GetEpbRecord(4).MechanicalCycleCount = 11;
                ConfigLoader.SaveTest(f.SourcePath, active);
                var live = Path.Combine(ResetRoot(f), "V3Persistence"); Directory.CreateDirectory(live); File.WriteAllText(Path.Combine(live, "new-work.bin"), "new work");
                var reopened = new EngineProjectSelectionStore(f.StoreRoot);
                reopened.Activate(destination, plan.OperatorCommandId, digest);
                Assert(ConfigLoader.LoadTest(f.SourcePath, null).GetEpbRecord(4).RunCount == 9 &&
                    File.ReadAllText(Path.Combine(live, "new-work.bin")) == "new work" &&
                    ConfigLoader.LoadTest(Path.Combine(ResetArchive(f, plan), "Config", "TestConfig.xml"), null).GetEpbRecord(4).RunCount == 21004,
                    "activation replay overwrote new or archived progress");
            }
        }

        private static void ResetCrashBoundaries()
        {
            foreach (var boundary in new[] { "ResetCommitStarted", "ResetStaged", "ResetArchiveMarked", "ResetArchived", "ResetPublished", "Activated" })
            using (var f = new Fixture())
            {
                SeedReset(f); var plan = ResetCommand(f).ProjectSwitch; var destination = ResetDestination(plan);
                var digest = f.Store.Prepare(plan, Closed());
                Reject(() => f.Store.Activate(destination, plan.OperatorCommandId, digest,
                    stage => { if (stage == boundary) throw new IOException("InjectedResetCrash"); }), "InjectedResetCrash");
                var reopened = new EngineProjectSelectionStore(f.StoreRoot);
                Reject(() => reopened.ReadActive(f.Source));
                if (boundary != "Activated")
                {
                    Reject(() => reopened.BindInitial(f.Source, f.SourcePath), "OldRunBindForbidden");
                    Reject(() => reopened.Prepare(plan, Closed()), "OldRunPrepareForbidden");
                    Reject(() => reopened.AbortPrepared(plan, CancellationToken.None), "CannotAbortToOldRun");
                }
                Reject(() => reopened.Activate(destination, plan.OperatorCommandId, digest, expectedPlanSha256: new string('f', 64)));
                var selected = reopened.Activate(destination, plan.OperatorCommandId, digest, expectedPlanSha256: plan.ComputeSha256());
                Assert(!selected.ResetPublicationStarted && selected.Pending == null && reopened.ReadActive(destination) != null &&
                    ConfigLoader.LoadTest(f.SourcePath, null).GetEpbRecord(4).RunCount == 0 &&
                    File.Exists(Path.Combine(ResetArchive(f, plan), "unknown-future-format", "history.bin")), "crashed reset lost history or selection");
            }
        }

        private static void ResetConflicts()
        {
            foreach (var scenario in new[] { "source", "calibration", "archive", "competing-active", "published-data" })
            using (var f = new Fixture())
            {
                SeedReset(f); var plan = ResetCommand(f).ProjectSwitch; var digest = f.Store.Prepare(plan, Closed());
                var archive = ResetArchive(f, plan);
                if (scenario == "source") File.AppendAllText(f.SourcePath, " ");
                if (scenario == "calibration") File.AppendAllText(Path.Combine(ResetRoot(f), "Config", "AIConfig.xml"), " ");
                if (scenario == "archive") { Directory.CreateDirectory(archive); File.WriteAllText(Path.Combine(archive, "protected.txt"), "existing"); }
                Reject(() => f.Store.Activate(ResetDestination(plan), plan.OperatorCommandId, digest, stage =>
                {
                    if (scenario == "competing-active" && stage == "ResetArchived")
                    { Directory.CreateDirectory(ResetRoot(f)); File.WriteAllText(Path.Combine(ResetRoot(f), "protected.txt"), "existing"); }
                    if (scenario == "published-data" && stage == "ResetPublished")
                    {
                        var restored = Path.Combine(ResetRoot(f), "V3Persistence"); Directory.CreateDirectory(restored);
                        File.WriteAllText(Path.Combine(restored, "index.db"), "unexpected restored history");
                        throw new IOException("InjectedPublishedDataBeforeSelectionCommit");
                    }
                }));
                var preserved = scenario == "competing-active" || scenario == "published-data" ? archive : ResetRoot(f);
                Assert(File.Exists(Path.Combine(preserved, "Raw", "data.bin")), "rejected reset deleted old data");
                if (scenario == "archive") Assert(File.ReadAllText(Path.Combine(archive, "protected.txt")) == "existing", "archive collision overwritten");
                if (scenario == "competing-active") Assert(File.ReadAllText(Path.Combine(ResetRoot(f), "protected.txt")) == "existing", "competing project overwritten");
                if (scenario == "published-data") Reject(() => f.Store.Activate(ResetDestination(plan), plan.OperatorCommandId, digest), "OldRuntimeImportForbidden");
                Reject(() => f.Store.ReadActive(f.Source), "OldRunLoadForbidden");
            }
        }

        private static void ResetBinding()
        {
            using (var f = new Fixture())
            {
                var command = ResetCommand(f); var request = command.OperatorTransaction.ProjectSwitch;
                var changed = request.Clone(); changed.Reset.Configuration.TestTarget++;
                Assert(changed.ComputeSha256() != request.ComputeSha256() && changed.Reset.Configuration.TestTarget != request.Reset.Configuration.TestTarget,
                    "reset payload not deeply bound");
                changed.Creation = new ProjectCreationRequest { Configuration = changed.Reset.Configuration.Clone() };
                Assert(!changed.IsStructurallyValid(), "reset and creation modes mixed");
                var plan = command.ProjectSwitch.Clone(); plan.TargetConfigurationPath = f.TargetPath; plan.Reset.Configuration.TestName = "TargetProject";
                Assert(plan.IsStructurallyValid(), "cross-project negative fixture invalid");
                Reject(() => f.Store.Prepare(plan, Closed()), "SelectionRevisionOrTargetConflict");
                Assert(ConfigLoader.LoadTest(f.SourcePath, null).GetEpbRecord(4).RunCount == 21004 &&
                    ConfigLoader.LoadTest(f.TargetPath, null).GetEpbRecord(4).RunCount == 42004, "invalid reset changed project work");
                var nestedStore = new EngineProjectSelectionStore(Path.Combine(ResetRoot(f), "nestedJournal"));
                nestedStore.BindInitial(f.Source, f.SourcePath);
                Reject(() => nestedStore.Prepare(command.ProjectSwitch, Closed()), "ResetProjectRootContainsProtectedState");
                Assert(nestedStore.ReadActive(f.Source).Pending == null, "unsafe root created pending reset work");
            }
        }

        private static void ResetAbortBeforePublication()
        {
            using (var f = new Fixture())
            {
                SeedReset(f); var plan = ResetCommand(f).ProjectSwitch;
                var original = File.ReadAllBytes(f.SourcePath); var digest = f.Store.Prepare(plan, Closed());
                f.Store.AbortPrepared(plan, CancellationToken.None);
                Assert(f.Store.ReadActive(f.Source).Pending == null && File.ReadAllBytes(f.SourcePath).SequenceEqual(original) &&
                    !Directory.Exists(ResetArchive(f, plan)), "aborted reset mutated original history");
                Reject(() => f.Store.Activate(ResetDestination(plan), plan.OperatorCommandId, digest), "PendingBindingInvalid");
            }
        }
    }
}
