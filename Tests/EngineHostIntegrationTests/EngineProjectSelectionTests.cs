using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Threading;
using Config;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static partial class EngineProjectSelectionTests
    {
        internal static int RunAll()
        {
            Run("ProjectInitialBindingPreservesCountsAndRejectsImplicitSwitch", InitialBinding);
            Run("ProjectSwitchRequiresEveryClosedBoundary", ClosedBoundary);
            Run("ProjectSwitchActivationIsIdempotentAndNeverCopiesOldProgress", Activation);
            Run("ProjectSwitchDurableBoundariesSurviveStoreReopen", CrashBoundaries);
            Run("ProjectSwitchTargetMutationAbortAndLateActivationAreFenced", MutationAndAbort);
            Run("ProjectSelectionCorruptionNeverFallsBackToDefault", Corruption);
            Run("ProjectSwitchPlanRejectsForeignPathsEpochsAndReplayMutation", PlanValidation);
            Run("ProjectRunScopedCheckpointsKeepBothHistories", CheckpointSeparation);
            Run("EngineSelectedProjectIgnoresDefaultTemplateWithoutWritingIt", EngineConfigurationLoad);
            Run("ProjectMissingProgressAndOversizeAreNotZeroOrReady", InvalidData);
            Run("InitialProjectDiscoveryRejectsCrossProjectFileMetadata", InitialDiscoveryMismatch);
            Run("ProjectSourceExecutorUsesTypedCasAndKeepsCountsOnReplayAndAbort", SourceExecutor);
            Run("ProjectPrepareLostReceiptCanAbortOnlyItsExactPlan", CancelAndAbortLostReceipt);
            Run("ProjectDestinationArgumentsRejectPartialDuplicateAndForeignPlans", ActivationArguments);
            Run("ProjectDestinationActivationIsCancelledOrDurablyBoundWithoutProgressReset", BoundActivation);
            Run("ProjectDestinationIsolationPreservesCountsAndConservativelyMapsUnknownDomains", InheritedIsolation);
            Run("ProjectHandoffRequiresFreshExactSourceOrInitializedDestination", HandoffPolicy);
            Run("NewProjectInitializesOnlyNewHistoryAndPreservesHardwareIsolation", NewProjectHistory);
            Run("NewProjectExistingOrCompetingDirectoryIsNeverOverwritten", NewProjectCollision);
            Run("NewProjectPublicationAndSelectionSurviveDurableBoundaryCrashes", NewProjectCrashBoundaries);
            Run("NewProjectMetadataAndModeRemainBoundToTheOriginalCommand", NewProjectBinding);
            Run("ResetArchivesCompleteOldProjectAndRetainsConfigurationAndIsolation", ResetHistory);
            Run("ResetDestinationReplayNeverZeroesNewProgress", ResetReplay);
            Run("ResetDurableBoundariesFenceOldRunAndResumeOnlyExactActivation", ResetCrashBoundaries);
            Run("ResetRejectsChangedSourceConfigurationOrArchiveCollision", ResetConflicts);
            Run("ResetTypedPayloadCannotBecomeAnotherProjectOrCreation", ResetBinding);
            Run("ResetAbortedBeforePublicationKeepsOriginalHistory", ResetAbortBeforePublication);
            return 27;
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "MTTFTest.ProjectSelection." + Guid.NewGuid().ToString("N"));
            internal readonly RecoveryIdentity Source = new RecoveryIdentity { SessionId = RecoveryProtocolV7.NewId(),
                RunId = RecoveryProtocolV7.NewId(), RunEpoch = 4, IncidentId = RecoveryProtocolV7.NewId(),
                ResourceScope = "System", Generation = 2, Revision = 8 };
            internal readonly string StoreRoot;
            internal readonly string SourcePath;
            internal readonly string TargetPath;
            internal readonly string ConfigRoot;
            internal readonly EngineProjectSelectionStore Store;
            internal readonly ProjectSwitchPlan Plan;
            internal readonly RecoveryIdentity Destination;

            internal Fixture()
            {
                Directory.CreateDirectory(Root);
                StoreRoot = Path.Combine(Root, "selection");
                var repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
                ConfigRoot = Path.Combine(repo, "MTTfTest", "Config");
                SourcePath = CreateProject("SourceProject", 21000);
                TargetPath = CreateProject("TargetProject", 42000);
                Store = new EngineProjectSelectionStore(StoreRoot);
                Assert(Store.ReadActive(Source) == null && !Directory.Exists(StoreRoot), "Read created selection state");
                Store.BindInitial(Source, SourcePath);
                Plan = new ProjectSwitchPlan { SourceIdentity = Source.Clone(), OwnerId = RecoveryProtocolV7.NewId(),
                    OperatorCommandId = RecoveryProtocolV7.NewId(), SourceEngineInstanceId = RecoveryProtocolV7.NewId(),
                    NextRunId = RecoveryProtocolV7.NewId(), NextRunEpoch = 5, BaseSelectionRevision = 1,
                    SourceProjectFileSha256 = Hash(SourcePath), TargetConfigurationPath = TargetPath, TargetProjectFileSha256 = Hash(TargetPath) };
                Destination = Source.Clone(); Destination.RunId = Plan.NextRunId; Destination.RunEpoch = Plan.NextRunEpoch;
                Destination.IncidentId = RecoveryProtocolV7.NewId(); Destination.Generation++;
            }

            private string CreateProject(string name, int completed)
            {
                var path = Path.Combine(Root, name, "Config", "TestConfig.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(Path.Combine(ConfigRoot, "TestConfig.xml"), path);
                var config = ConfigLoader.LoadTest(path, null);
                config.StoreDir = Root; config.TestName = name;
                foreach (var record in config.EpbRecords.Snapshot())
                {
                    record.RunCount = completed + record.Id; record.MechanicalCycleCount = completed + 500 + record.Id;
                    record.RunTimeSpan = TimeSpan.FromHours(13); record.Enabled = record.Id != 6;
                }
                config.GetEpbRecord(6).PermanentAlarmLatched = true;
                config.GetEpbRecord(6).PermanentAlarmReason = "Synthetic isolated fixture";
                ConfigLoader.SaveTest(path, config);
                return path;
            }

            public void Dispose() { Directory.Delete(Root, true); }
        }

        private static EngineProjectSwitchBoundary Closed() => new EngineProjectSwitchBoundary
        { ExecutorQuiescent = true, NativeResourcesReleased = true, WritersClosed = true, CheckpointClosed = true };

        private static void InitialBinding()
        {
            using (var f = new Fixture())
            {
                var original = File.ReadAllBytes(f.SourcePath);
                Assert(f.Store.BindInitial(f.Source, f.SourcePath).Revision == 1, "Binding replay changed revision");
                Reject(() => f.Store.BindInitial(f.Destination, f.TargetPath), "ExplicitSwitch");
                Reject(() => f.Store.ReadActive(f.Destination), "ExplicitActivationRequired");
                Assert(original.SequenceEqual(File.ReadAllBytes(f.SourcePath)), "Initial binding wrote project data");
                Assert(ConfigLoader.LoadTest(f.SourcePath, null).GetEpbRecord(4).RunCount == 21004, "Initial count reset");
            }
        }

        private static void ClosedBoundary()
        {
            using (var f = new Fixture())
            {
                for (var missing = 0; missing < 4; missing++)
                {
                    var boundary = Closed();
                    if (missing == 0) boundary.ExecutorQuiescent = false;
                    if (missing == 1) boundary.NativeResourcesReleased = false;
                    if (missing == 2) boundary.WritersClosed = false;
                    if (missing == 3) boundary.CheckpointClosed = false;
                    Reject(() => f.Store.Prepare(f.Plan, boundary), "ClosedExecutorsWritersAndCheckpoint");
                }
                Assert(f.Store.ReadActive(f.Source).Revision == 1 && !Directory.GetFiles(f.StoreRoot, "prepared-*").Any(),
                    "Rejected boundary left a preparation or changed the active selection");
            }
        }

        private static void Activation()
        {
            using (var f = new Fixture())
            {
                var source = File.ReadAllBytes(f.SourcePath); var target = File.ReadAllBytes(f.TargetPath);
                var digest = f.Store.Prepare(f.Plan, Closed());
                Assert(f.Store.Prepare(f.Plan, Closed()) == digest && f.Store.ReadActive(f.Source).Revision == 2,
                    "Prepare replay changed the durable plan");
                var activated = f.Store.Activate(f.Destination, f.Plan.OperatorCommandId, digest);
                Assert(activated.Revision == 3 && activated.RunScopedCheckpoint && activated.ConfigurationPath == f.TargetPath,
                    "Activation selected the wrong project or checkpoint namespace");
                Assert(source.SequenceEqual(File.ReadAllBytes(f.SourcePath)) && target.SequenceEqual(File.ReadAllBytes(f.TargetPath)),
                    "Activation modified a project's history");
                var progressed = ConfigLoader.LoadTest(f.TargetPath, null); progressed.GetEpbRecord(4).RunCount++;
                ConfigLoader.SaveTest(f.TargetPath, progressed);
                Assert(new EngineProjectSelectionStore(f.StoreRoot).Activate(f.Destination, f.Plan.OperatorCommandId, digest).Revision == 3 &&
                    ConfigLoader.LoadTest(f.TargetPath, null).GetEpbRecord(4).RunCount == 42005, "Replay restored stale target progress");
                Reject(() => f.Store.ReadActive(f.Source), "RunMismatch");
                Reject(() => f.Store.Prepare(f.Plan, Closed()), "SourceRetired");
                Assert(!File.ReadAllText(Path.Combine(f.StoreRoot, "active-project.v1.json")).Contains("TargetProject"),
                    "Selection payload was not machine-protected");
            }
        }

        private static void CrashBoundaries()
        {
            foreach (var stage in new[] { "PreparedDurable", "PendingCommitted", "Activated" })
                using (var f = new Fixture())
                {
                    var digest = string.Empty;
                    if (stage != "Activated")
                        Reject(() => f.Store.Prepare(f.Plan, Closed(), p => { if (p == stage) throw new IOException("InjectedCrash"); }), "InjectedCrash");
                    var reopened = new EngineProjectSelectionStore(f.StoreRoot);
                    Assert(reopened.ReadActive(f.Source).ConfigurationPath == f.SourcePath, "Preparation activated target before commit");
                    digest = reopened.Prepare(f.Plan, Closed());
                    if (stage == "Activated")
                        Reject(() => reopened.Activate(f.Destination, f.Plan.OperatorCommandId, digest,
                            _ => { throw new IOException("InjectedCrash"); }), "InjectedCrash");
                    var final = new EngineProjectSelectionStore(f.StoreRoot).Activate(f.Destination, f.Plan.OperatorCommandId, digest);
                    Assert(final.Revision == 3 && ConfigLoader.LoadTest(f.TargetPath, null).GetEpbRecord(6).PermanentAlarmLatched,
                        "Crash replay lost revision or target isolation");
                }
        }

        private static void MutationAndAbort()
        {
            using (var f = new Fixture())
            {
                var digest = f.Store.Prepare(f.Plan, Closed());
                var changed = ConfigLoader.LoadTest(f.TargetPath, null); changed.Owner = "Changed after prepare";
                ConfigLoader.SaveTest(f.TargetPath, changed);
                Reject(() => f.Store.Activate(f.Destination, f.Plan.OperatorCommandId, digest), "TargetChanged");
                Assert(f.Store.ReadActive(f.Source).ConfigurationPath == f.SourcePath, "Rejected activation changed selection");
                Reject(() => f.Store.AbortPending(f.Source, RecoveryProtocolV7.NewId(), digest), "PendingBindingInvalid");
                f.Store.AbortPending(f.Source, f.Plan.OperatorCommandId, digest);
                Reject(() => f.Store.Activate(f.Destination, f.Plan.OperatorCommandId, digest), "PendingBindingInvalid");
                Reject(() => f.Store.Prepare(f.Plan, Closed()), "RevisionOrTargetConflict");
                Assert(f.Store.ReadActive(f.Source).Pending == null && f.Store.ReadActive(f.Source).Revision == 3,
                    "Abort left an ownerless pending project");
            }
        }

        private static void Corruption()
        {
            foreach (var remove in new[] { false, true })
                using (var f = new Fixture())
                {
                    var path = Path.Combine(f.StoreRoot, "active-project.v1.json");
                    if (remove) File.Delete(path); else File.WriteAllText(path, "{\"SchemaVersion\":1}");
                    Reject(() => f.Store.ReadActive(f.Source));
                    Reject(() => f.Store.BindInitial(f.Source, f.SourcePath));
                    Assert(ConfigLoader.LoadTest(f.SourcePath, null).GetEpbRecord(4).RunCount == 21004, "Corruption reset source");
                }
        }

        private static void PlanValidation()
        {
            using (var f = new Fixture())
            {
                Assert(f.Plan.IsStructurallyValid(), "Fixture plan invalid");
                foreach (var path in new[] { @"\\server\share\Config\TestConfig.xml", @"C:\x\..\p\Config\TestConfig.xml",
                    @"C:\p\Config\TestConfig.xml:stream", @"C:\p\TestConfig.xml", @"C:\p\Config\*.xml" })
                    Assert(!ProjectSwitchPlan.IsProjectConfigurationPath(path), "Noncanonical/remote path accepted");
                var bad = f.Plan.Clone(); bad.NextRunEpoch++;
                Assert(!bad.IsStructurallyValid(), "Skipped epoch accepted");
                bad = f.Plan.Clone(); bad.NextRunId = bad.SourceIdentity.RunId;
                Assert(!bad.IsStructurallyValid(), "Old run reused for new project");
                var digest = f.Store.Prepare(f.Plan, Closed());
                bad = f.Plan.Clone(); bad.OwnerId = RecoveryProtocolV7.NewId();
                Reject(() => f.Store.Prepare(bad, Closed()), "PendingConflict");
                var wrong = f.Destination.Clone(); wrong.SessionId = RecoveryProtocolV7.NewId();
                Reject(() => f.Store.Activate(wrong, f.Plan.OperatorCommandId, digest), "BindingInvalid");
            }
        }

        private static void CheckpointSeparation()
        {
            using (var f = new Fixture())
            {
                var root = Path.Combine(f.Root, "checkpoints");
                var source = ConfigLoader.LoadTest(f.SourcePath, null); var target = ConfigLoader.LoadTest(f.TargetPath, null);
                new EngineRunCheckpointStore(f.Source.SessionId, root).LoadOrCreate(f.Source, source,
                    EngineTestConfigurationStore.Capture(source).ComputeSha256());
                new EngineRunCheckpointStore(f.Source.SessionId, root, f.Destination.RunId).LoadOrCreate(f.Destination, target,
                    EngineTestConfigurationStore.Capture(target).ComputeSha256());
                var previous = new EngineRunCheckpointStore(f.Source.SessionId, root).Snapshot();
                var next = new EngineRunCheckpointStore(f.Source.SessionId, root, f.Destination.RunId).Snapshot();
                Assert(previous.FormalCyclesCompleted[3] == 21004 && next.FormalCyclesCompleted[3] == 42004 &&
                    previous.RunId != next.RunId && Directory.GetFiles(root, "*.v7.json").Length == 2,
                    "Run-scoped checkpoint overwrote or imported the old project's counts");
            }
        }

        private static void EngineConfigurationLoad()
        {
            using (var f = new Fixture())
            {
                var defaults = Path.Combine(f.Root, "defaults"); Directory.CreateDirectory(defaults);
                foreach (var name in new[] { "AOConfig.xml", "DOConfig.xml", "UIConfig.xml" })
                    File.Copy(Path.Combine(f.ConfigRoot, name), Path.Combine(defaults, name));
                var template = Path.Combine(defaults, "TestConfig.xml"); File.WriteAllText(template, "synthetic corrupt template");
                var loaded = ConfigLoader.LoadAllForEngine(defaults, f.TargetPath, null);
                Assert(loaded.Test.TestName == "TargetProject" && loaded.Test.GetEpbRecord(4).RunCount == 42004 &&
                    File.ReadAllText(template) == "synthetic corrupt template", "Engine read or modified the default instead of the selected project");
                File.Delete(template);
                loaded = ConfigLoader.LoadAllForEngine(defaults, f.TargetPath, null);
                Assert(!File.Exists(template) && loaded.Test.GetEpbRecord(4).MechanicalCycleCount == 42504,
                    "Missing template created/reset project counters");
            }
        }

        private static void InvalidData()
        {
            using (var f = new Fixture())
            {
                var doc = new XmlDocument(); doc.Load(f.TargetPath);
                var counter = doc.SelectSingleNode("/TestConfig/EpbRecords/Record[Id='4']/RunCount");
                counter.ParentNode.RemoveChild(counter); doc.Save(f.TargetPath);
                var plan = f.Plan.Clone(); plan.TargetProjectFileSha256 = Hash(f.TargetPath);
                Reject(() => f.Store.Prepare(plan, Closed()), "ProgressRecordsIncomplete");
                var restoredCounter = doc.CreateElement("RunCount"); restoredCounter.InnerText = "42004";
                doc.SelectSingleNode("/TestConfig/EpbRecords/Record[Id='4']").AppendChild(restoredCounter);
                doc.SelectSingleNode("/TestConfig/EpbRecords/Record[Id='5']/Id").InnerText = "04";
                doc.Save(f.TargetPath);
                plan.TargetProjectFileSha256 = Hash(f.TargetPath);
                Reject(() => f.Store.Prepare(plan, Closed()), "ProgressRecordsIncomplete");
                using (var stream = new FileStream(f.TargetPath, FileMode.Create, FileAccess.Write))
                    stream.SetLength(EngineProjectSelectionStore.MaximumProjectBytes + 1L);
                Reject(() => EngineProjectSelectionStore.ReadBytes(f.TargetPath, EngineProjectSelectionStore.MaximumProjectBytes), "Oversize");
                Assert(f.Store.ReadActive(f.Source).Revision == 1, "Invalid target modified active project");
            }
        }

        private static void InitialDiscoveryMismatch()
        {
            using (var f = new Fixture())
            {
                var configured = ConfigLoader.LoadTest(f.SourcePath, null);
                var changed = ConfigLoader.LoadTest(f.SourcePath, null);
                changed.TestName = "TargetProject";
                ConfigLoader.SaveTest(f.SourcePath, changed);
                Reject(() => EngineTestConfigurationStore.LoadProjectWithoutReset(configured, f.SourcePath), "ProjectFileIdentityMismatch");
                Assert(ConfigLoader.LoadTest(f.TargetPath, null).GetEpbRecord(4).RunCount == 42004,
                    "Cross-project metadata changed another project's records");
            }
        }

        private static string Hash(string path) => EngineProjectSelectionStore.Sha(File.ReadAllBytes(path));

        private static RecoveryCommand SourceCommand(Fixture f, EngineTestConfiguration configuration)
        {
            var plan = f.Plan.Clone(); plan.BaseConfigurationRevision = 7; plan.BaseConfigurationSha256 = configuration.ComputeSha256();
            var input = new ProjectSwitchRequest { EngineInstanceId = plan.SourceEngineInstanceId,
                BaseSelectionRevision = plan.BaseSelectionRevision, BaseConfigurationRevision = plan.BaseConfigurationRevision,
                BaseConfigurationSha256 = plan.BaseConfigurationSha256, SourceProjectFileSha256 = plan.SourceProjectFileSha256,
                TargetConfigurationPath = plan.TargetConfigurationPath, TargetProjectFileSha256 = plan.TargetProjectFileSha256 };
            var command = new RecoveryCommand { Identity = f.Source.Clone(), OwnerId = plan.OwnerId, ProjectSwitch = plan,
                Kind = RecoveryCommandKind.PrepareProjectSwitch, CommandId = RecoveryProtocolV7.NewId(), CommandSequence = 8,
                TargetResource = "System", DeadlineUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks,
                OperatorTransaction = new OperatorCommand { CommandId = plan.OperatorCommandId, SessionId = f.Source.SessionId,
                    RunId = f.Source.RunId, RunEpoch = f.Source.RunEpoch, BaseRevision = 2, Kind = OperatorCommandKind.SwitchProject,
                    IssuedUtcTicks = DateTime.UtcNow.Ticks, ProjectSwitch = input, PayloadSha256 = input.ComputeSha256() } };
            command.IdempotencyKey = command.ExpectedIdempotencyKey();
            Assert(command.IsStructurallyValid(), "source fixture command invalid");
            return command;
        }

        private static void SourceExecutor()
        {
            using (var f = new Fixture())
            {
                var config = EngineTestConfigurationStore.Capture(ConfigLoader.LoadTest(f.SourcePath, null));
                var command = SourceCommand(f, config);
                var executor = new EngineProjectSwitchExecutor(f.Store);
                Reject(() => executor.ExecuteSource(command, config, 8, Closed(), CancellationToken.None), "RevisionConflict");
                var open = Closed(); open.WritersClosed = false;
                Reject(() => executor.ExecuteSource(command, config, 7, open, CancellationToken.None), "BoundaryNotClosed");
                Assert(f.Store.ReadActive(f.Source).Revision == 1, "rejected command changed selection");
                var receipt = executor.ExecuteSource(command, config, 7, Closed(), CancellationToken.None);
                Assert(receipt.Matches(command) && !receipt.SelectionCommitted, "source executor claimed activation");
                var replay = executor.ExecuteSource(command, config, 7, Closed(), CancellationToken.None);
                Assert(receipt.PreparedDocumentSha256 == replay.PreparedDocumentSha256 && f.Store.ReadActive(f.Source).Pending != null &&
                    Hash(f.SourcePath) == f.Plan.SourceProjectFileSha256 && Hash(f.TargetPath) == f.Plan.TargetProjectFileSha256,
                    "source executor changed selection/data on replay");
                command.Kind = RecoveryCommandKind.AbortProjectSwitch; command.CommandSequence++;
                command.CommandId = RecoveryProtocolV7.NewId(); command.IdempotencyKey = command.ExpectedIdempotencyKey();
                executor.ExecuteSource(command, null, 0, Closed(), CancellationToken.None);
                executor.ExecuteSource(command, null, 0, Closed(), CancellationToken.None);
                Assert(f.Store.ReadActive(f.Source).Pending == null && Directory.GetFiles(f.StoreRoot, "prepared-*.v1.json").Length == 1,
                    "abort removed archived evidence or kept pending selection");
            }
        }

        private static void CancelAndAbortLostReceipt()
        {
            foreach (var boundary in new[] { "PreparedDurable", "PendingCommitted" })
            using (var f = new Fixture())
            using (var cancel = new CancellationTokenSource())
            {
                var config = EngineTestConfigurationStore.Capture(ConfigLoader.LoadTest(f.SourcePath, null));
                var command = SourceCommand(f, config);
                Reject(() => f.Store.Prepare(command.ProjectSwitch, Closed(), stage =>
                {
                    if (stage == boundary) { cancel.Cancel(); throw new OperationCanceledException(); }
                }, cancel.Token));
                var reopened = new EngineProjectSelectionStore(f.StoreRoot);
                var changed = command.ProjectSwitch.Clone(); changed.OwnerId = RecoveryProtocolV7.NewId();
                if (boundary == "PendingCommitted") Reject(() => reopened.AbortPrepared(changed, CancellationToken.None), "BindingInvalid");
                command.Kind = RecoveryCommandKind.AbortProjectSwitch; command.CommandId = RecoveryProtocolV7.NewId();
                command.IdempotencyKey = command.ExpectedIdempotencyKey();
                new EngineProjectSwitchExecutor(reopened).ExecuteSource(command, null, 0, Closed(), CancellationToken.None);
                Assert(reopened.ReadActive(f.Source).Pending == null && Hash(f.SourcePath) == f.Plan.SourceProjectFileSha256 &&
                    Hash(f.TargetPath) == f.Plan.TargetProjectFileSha256, "lost prepare receipt changed project counters");
            }
        }

        private static RecoveryCommand ActivationCommand(Fixture f)
        {
            var configuration = EngineTestConfigurationStore.Capture(ConfigLoader.LoadTest(f.SourcePath, null));
            var command = SourceCommand(f, configuration);
            command.ProjectSwitchPreparedSha256 = f.Store.Prepare(command.ProjectSwitch, Closed());
            command.Kind = RecoveryCommandKind.ActivateProjectSwitch;
            command.Identity.RunId = command.ProjectSwitch.NextRunId;
            command.Identity.RunEpoch = command.ProjectSwitch.NextRunEpoch;
            command.CommandId = RecoveryProtocolV7.NewId(); command.CommandSequence++;
            command.IdempotencyKey = command.ExpectedIdempotencyKey();
            Assert(command.IsStructurallyValid(), "destination fixture command invalid");
            return command;
        }

        private static RecoveryCommand NewProjectCommand(Fixture f)
        {
            var configuration = EngineTestConfigurationStore.Capture(ConfigLoader.LoadTest(f.SourcePath, null));
            var command = SourceCommand(f, configuration);
            var created = configuration.Clone(); created.TestName = "BrandNew"; created.Owner = "NewOperator";
            created.TestTarget = 150000; created.Channels.Single(row => row.Channel == 4).TargetTotalCount = 123456;
            var input = command.OperatorTransaction.ProjectSwitch;
            input.Creation = new ProjectCreationRequest { Configuration = created };
            input.TargetConfigurationPath = Path.Combine(f.Root, created.TestName, "Config", "TestConfig.xml");
            input.TargetProjectFileSha256 = string.Empty;
            command.OperatorTransaction.PayloadSha256 = input.ComputeSha256();
            command.ProjectSwitch.Creation = input.Creation.Clone();
            command.ProjectSwitch.TargetConfigurationPath = input.TargetConfigurationPath;
            command.ProjectSwitch.TargetProjectFileSha256 = string.Empty;
            command.ProjectSwitch.IsolatedResources = new[] { "Power:4" };
            command.IdempotencyKey = command.ExpectedIdempotencyKey();
            Assert(command.IsStructurallyValid(), "creation fixture command invalid");
            return command;
        }

        private static void NewProjectHistory()
        {
            using (var f = new Fixture())
            {
                var xml = new XmlDocument(); xml.Load(f.SourcePath);
                var stale = xml.CreateElement("V3ConfigurationTransaction"); stale.SetAttribute("RunId", f.Source.RunId);
                xml.DocumentElement.AppendChild(stale);
                var oldPermit = xml.CreateElement("UnrecognizedOldPermit"); oldPermit.InnerText = "must-not-migrate";
                xml.DocumentElement.AppendChild(oldPermit); xml.Save(f.SourcePath);
                var current = ConfigLoader.LoadTest(f.SourcePath, null);
                current.GetEpbRecord(7).OperatorFullRelearningRequired = true;
                current.GetEpbRecord(7).OperatorFullRelearningReason = "hardware-relearning-required";
                ConfigLoader.SaveTest(f.SourcePath, current);
                var command = NewProjectCommand(f);
                command.ProjectSwitch.SourceProjectFileSha256 = command.OperatorTransaction.ProjectSwitch.SourceProjectFileSha256 = Hash(f.SourcePath);
                command.OperatorTransaction.PayloadSha256 = command.OperatorTransaction.ProjectSwitch.ComputeSha256();
                command.IdempotencyKey = command.ExpectedIdempotencyKey();
                var source = File.ReadAllBytes(f.SourcePath); var other = File.ReadAllBytes(f.TargetPath);
                var digest = f.Store.Prepare(command.ProjectSwitch, Closed());
                var path = command.ProjectSwitch.TargetConfigurationPath;
                var created = ConfigLoader.LoadTest(path, null);
                Assert(created.TestName == "BrandNew" && created.Owner == "NewOperator" && created.TestTarget == 150000 &&
                    created.GetEpbRecord(4).TotalCount == 123456, "new metadata not applied");
                Assert(created.EpbRecords.Snapshot().All(row => row.RunCount == 0 && row.MechanicalCycleCount == 0 &&
                    row.RunTimeSpan == TimeSpan.Zero && row.StartTime == null && row.LatestStartTime == null), "new project imported old work");
                Assert(created.GetEpbRecord(6).PermanentAlarmLatched && created.GetEpbRecord(6).PermanentAlarmReason == "Synthetic isolated fixture" &&
                    created.GetEpbRecord(7).OperatorFullRelearningRequired && !created.GetEpbRecord(10).Enabled &&
                    created.GetEpbRecord(10).PermanentAlarmLatched, "new project cleared physical safety facts");
                Assert(!File.ReadAllText(path).Contains("V3ConfigurationTransaction") && !File.ReadAllText(path).Contains("UnrecognizedOldPermit"),
                    "new project migrated old authority");
                Assert(source.SequenceEqual(File.ReadAllBytes(f.SourcePath)) && other.SequenceEqual(File.ReadAllBytes(f.TargetPath)) &&
                    f.Store.ReadActive(f.Source).ConfigurationPath == f.SourcePath, "new project wrote source/other history or switched before activation");
                var destination = command.Identity.Clone(); destination.RunId = command.ProjectSwitch.NextRunId; destination.RunEpoch++;
                f.Store.Activate(destination, command.ProjectSwitch.OperatorCommandId, digest);
                created.GetEpbRecord(4).RunCount = 17; created.GetEpbRecord(4).MechanicalCycleCount = 21;
                ConfigLoader.SaveTest(path, created);
                f.Store.Activate(destination, command.ProjectSwitch.OperatorCommandId, digest);
                Assert(ConfigLoader.LoadTest(path, null).GetEpbRecord(4).RunCount == 17, "activation replay zeroed new progress");
            }
        }

        private static void NewProjectCollision()
        {
            foreach (var race in new[] { false, true })
            using (var f = new Fixture())
            {
                var command = NewProjectCommand(f); var root = Path.GetDirectoryName(Path.GetDirectoryName(command.ProjectSwitch.TargetConfigurationPath));
                var protectedFile = Path.Combine(root, "existing-history.txt");
                Action createExisting = () => { Directory.CreateDirectory(root); File.WriteAllText(protectedFile, "existing-work-21508"); };
                if (!race) createExisting();
                Reject(() => f.Store.Prepare(command.ProjectSwitch, Closed(), stage => { if (race && stage == "CreationStaged") createExisting(); }));
                Assert(File.ReadAllText(protectedFile) == "existing-work-21508" && !File.Exists(command.ProjectSwitch.TargetConfigurationPath) &&
                    f.Store.ReadActive(f.Source).Pending == null && ConfigLoader.LoadTest(f.SourcePath, null).GetEpbRecord(4).RunCount == 21004,
                    "new project replaced existing/competing directory");
            }
        }

        private static void NewProjectCrashBoundaries()
        {
            foreach (var boundary in new[] { "PreparedDurable", "CreationStaged", "CreationPublished", "PendingCommitted" })
            using (var f = new Fixture())
            {
                var command = NewProjectCommand(f); var plan = command.ProjectSwitch;
                Reject(() => f.Store.Prepare(plan, Closed(), stage => { if (stage == boundary) throw new IOException("InjectedCreationCrash"); }), "InjectedCreationCrash");
                var restarted = new EngineProjectSelectionStore(f.StoreRoot);
                Assert(restarted.ReadActive(f.Source).ConfigurationPath == f.SourcePath, "crashed creation activated new project");
                var digest = restarted.Prepare(plan, Closed());
                Assert(restarted.Prepare(plan, Closed()) == digest, "creation retry generated another preparation");
                Assert(ConfigLoader.LoadTest(plan.TargetConfigurationPath, null).GetEpbRecord(4).RunCount == 0 &&
                    Directory.GetDirectories(f.Root, ".v3-create-*").Length == 0, "complete publication left partial project");
                var destination = plan.SourceIdentity.Clone(); destination.RunId = plan.NextRunId; destination.RunEpoch = plan.NextRunEpoch;
                restarted.Activate(destination, plan.OperatorCommandId, digest);
                Assert(restarted.ReadActive(destination).ConfigurationPath == plan.TargetConfigurationPath, "creation activation was not durable");
            }
        }

        private static void NewProjectBinding()
        {
            using (var f = new Fixture())
            {
                var command = NewProjectCommand(f); var request = command.OperatorTransaction.ProjectSwitch;
                var clone = request.Clone(); clone.Creation.Configuration.Owner = "different";
                Assert(clone.ComputeSha256() != request.ComputeSha256() && request.Creation.Configuration.Owner == "NewOperator", "creation metadata not deep-cloned/bound");
                clone = request.Clone(); clone.TargetProjectFileSha256 = new string('a', 64);
                Assert(!clone.IsStructurallyValid(), "ambiguous existing/new mode accepted");
                foreach (var name in new[] { "..", "CON", "LPT1.txt", "trailing.", " trailing", ".v3-create-owned", "x\\child" })
                {
                    clone = request.Clone(); clone.Creation.Configuration.TestName = name;
                    clone.TargetConfigurationPath = Path.Combine(f.Root, name, "Config", "TestConfig.xml");
                    Assert(!clone.IsStructurallyValid(), "unsafe/aliased project name accepted");
                }
                var digest = f.Store.Prepare(command.ProjectSwitch, Closed());
                var changed = command.ProjectSwitch.Clone(); changed.Creation.Configuration.Owner = "different";
                Reject(() => f.Store.Prepare(changed, Closed()), "PendingConflict");
                var target = ConfigLoader.LoadTest(command.ProjectSwitch.TargetConfigurationPath, null); target.GetEpbRecord(4).RunCount = 1;
                ConfigLoader.SaveTest(command.ProjectSwitch.TargetConfigurationPath, target);
                var destination = command.Identity.Clone(); destination.RunId = command.ProjectSwitch.NextRunId; destination.RunEpoch++;
                Reject(() => f.Store.Activate(destination, command.ProjectSwitch.OperatorCommandId, digest), "TargetChanged");
                Assert(ConfigLoader.LoadTest(command.ProjectSwitch.TargetConfigurationPath, null).GetEpbRecord(4).RunCount == 1,
                    "rejected activation restored zeros");
            }
        }

        private static void ActivationArguments()
        {
            using (var f = new Fixture())
            {
                var command = ActivationCommand(f);
                var activation = EngineProjectActivation.FromCommand(command);
                var args = activation.ToArguments().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                Assert(EngineProjectActivation.ParseArguments(args).Matches(command), "activation arguments lost plan identity");
                Assert(EngineProjectActivation.ParseArguments(new[] { "--session", f.Source.SessionId }) == null, "normal restart requested project activation");
                Reject(() => EngineProjectActivation.ParseArguments(args.Take(4).ToArray()), "ArgumentsAmbiguous");
                Reject(() => EngineProjectActivation.ParseArguments(args.Concat(args.Take(2)).ToArray()), "ArgumentsAmbiguous");
                var bad = args.ToArray(); bad[1] = "--unsafe";
                Reject(() => EngineProjectActivation.ParseArguments(bad), "ArgumentsInvalid");
                activation.PlanSha256 = new string('f', 64);
                Assert(!activation.Matches(command), "foreign plan accepted");
            }
        }

        private static void BoundActivation()
        {
            using (var f = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                var command = ActivationCommand(f); var activation = EngineProjectActivation.FromCommand(command);
                cancellation.Cancel();
                Reject(() => f.Store.Activate(command.Identity, activation.OperatorCommandId, activation.PreparedDocumentSha256,
                    expectedPlanSha256: activation.PlanSha256, token: cancellation.Token));
                Assert(f.Store.ReadActive(f.Source).Pending != null, "cancelled activation retired source");
                Reject(() => f.Store.Activate(command.Identity, activation.OperatorCommandId, activation.PreparedDocumentSha256,
                    expectedPlanSha256: new string('f', 64)), "PendingBindingInvalid");
                var active = f.Store.Activate(command.Identity, activation.OperatorCommandId, activation.PreparedDocumentSha256,
                    expectedPlanSha256: activation.PlanSha256);
                Assert(active.Activation.Matches(command) && f.Store.ReadActivatedPlan(active).ComputeSha256() == activation.PlanSha256,
                    "activation metadata did not survive durable selection");
                var target = ConfigLoader.LoadTest(f.TargetPath, null); target.GetEpbRecord(4).RunCount += 3;
                ConfigLoader.SaveTest(f.TargetPath, target);
                var reopened = new EngineProjectSelectionStore(f.StoreRoot);
                var replay = reopened.Activate(command.Identity, activation.OperatorCommandId, activation.PreparedDocumentSha256,
                    expectedPlanSha256: activation.PlanSha256);
                Assert(replay.Revision == active.Revision && replay.Activation.Matches(command) &&
                    reopened.ReadActivatedPlan(replay).ComputeSha256() == activation.PlanSha256 &&
                    ConfigLoader.LoadTest(f.TargetPath, null).GetEpbRecord(4).RunCount == 42007, "activation replay overwrote newer progress");
                Reject(() => reopened.Activate(command.Identity, activation.OperatorCommandId, activation.PreparedDocumentSha256,
                    expectedPlanSha256: new string('e', 64)), "ReplayBindingInvalid");
            }
        }

        private static void InheritedIsolation()
        {
            Assert(EngineProjectIsolation.Resolve("Channel:4").SequenceEqual(new[] { 4 }), "channel scope mismatch");
            Assert(EngineProjectIsolation.Resolve("Power:2").SequenceEqual(new[] { 4, 5, 6 }), "power scope mismatch");
            Assert(EngineProjectIsolation.Resolve("Hydraulic:2").SequenceEqual(Enumerable.Range(7, 6)), "hydraulic scope mismatch");
            Assert(EngineProjectIsolation.Resolve("DAQ:Dev1").SequenceEqual(Enumerable.Range(1, 6)), "DAQ scope mismatch");
            foreach (var scope in new[] { "Unknown:future", "Power:9", "System", "", "DAQ:Dev3", "Power:Dev2", "Channel:+4", "Hydraulic:02" })
                Assert(EngineProjectIsolation.Resolve(scope).SequenceEqual(Enumerable.Range(1, 12)), "unknown scope bypassed isolation");
            using (var f = new Fixture())
            {
                var target = ConfigLoader.LoadTest(f.TargetPath, null);
                Assert(EngineProjectIsolation.Apply(target, new[] { "Power:2" }), "inherited scope not applied");
                Assert(!EngineProjectIsolation.Apply(target, new[] { "Power:2" }), "isolation replay was not idempotent");
                foreach (var row in target.EpbRecords.Snapshot())
                {
                    Assert(row.RunCount == 42000 + row.Id && row.MechanicalCycleCount == 42500 + row.Id &&
                        row.RunTimeSpan == TimeSpan.FromHours(13), "isolation reset historical progress");
                    Assert(row.PermanentAlarmLatched == (row.Id >= 4 && row.Id <= 6), "isolation affected healthy independent channel");
                }
                Assert(target.GetEpbRecord(6).PermanentAlarmReason == "Synthetic isolated fixture", "existing hard-fault evidence replaced");
                var checkpoint = new EngineRunCheckpointStore(f.Source.SessionId, Path.Combine(f.Root, "checkpoint"), f.Destination.RunId)
                    .LoadOrCreate(f.Destination, target, EngineTestConfigurationStore.Capture(target).ComputeSha256());
                Assert(checkpoint.IsolatedResources.OrderBy(x => x).SequenceEqual(new[] { "Channel:4", "Channel:5", "Channel:6" }) &&
                    checkpoint.FormalCyclesCompleted[3] == 42004, "initial destination checkpoint omitted isolation or counts");
            }
        }

        private static void HandoffPolicy()
        {
            using (var f = new Fixture())
            {
                var command = ActivationCommand(f);
                var source = new EngineStateSnapshot { SessionId = f.Source.SessionId, RunId = f.Source.RunId, RunEpoch = f.Source.RunEpoch,
                    EngineInstanceId = command.ProjectSwitch.SourceEngineInstanceId, Revision = 1, PulseSequence = 1,
                    CapturedUtcTicks = DateTime.UtcNow.Ticks, State = SystemTerminalState.StoppedByOperator,
                    HardwareRecompositionReady = true, RecoveryOwnerId = command.OwnerId, RecoveryIncidentId = command.Identity.IncidentId };
                Assert(ProjectEngineHandoffPolicy.IsSource(command, source), "closed exact source rejected");
                source.RecoveryOwnerId = RecoveryProtocolV7.NewId();
                Assert(!ProjectEngineHandoffPolicy.IsSource(command, source), "foreign owner can be killed");
                source.RecoveryOwnerId = command.OwnerId; source.OutputsEnergized = true;
                Assert(!ProjectEngineHandoffPolicy.IsSource(command, source), "energized source admitted");
                var destination = new EngineStateSnapshot { SessionId = command.Identity.SessionId, RunId = command.Identity.RunId,
                    RunEpoch = command.Identity.RunEpoch, EngineInstanceId = RecoveryProtocolV7.NewId(), Revision = 1, PulseSequence = 1,
                    CapturedUtcTicks = DateTime.UtcNow.Ticks, State = SystemTerminalState.SafeIdleAlarmed,
                    HardwareInitialized = true, ProjectActivation = EngineProjectActivation.FromCommand(command) };
                var receipt = ProjectEngineHandoffPolicy.DestinationReceipt(command, destination);
                Assert(receipt.Succeeded && receipt.ProjectSwitch.Matches(command) && receipt.ProjectSwitch.SelectionCommitted &&
                    !receipt.OutputsOff && !receipt.PressureSafe, "activation fabricated independent physical safety proof");
                destination.CapturedUtcTicks = DateTime.UtcNow.AddSeconds(-4).Ticks;
                Reject(() => ProjectEngineHandoffPolicy.DestinationReceipt(command, destination), "AdmissionInvalid");
                destination.CapturedUtcTicks = DateTime.UtcNow.Ticks; destination.ProjectActivation.PlanSha256 = new string('f', 64);
                Assert(!ProjectEngineHandoffPolicy.IsDestination(command, destination), "wrong activated plan admitted");
                destination.ProjectActivation = EngineProjectActivation.FromCommand(command); destination.HardwareInitialized = false;
                Assert(!ProjectEngineHandoffPolicy.IsDestination(command, destination), "activation accepted before composition completed");
                destination.HardwareInitialized = true; destination.EngineInstanceId = source.EngineInstanceId;
                Assert(!ProjectEngineHandoffPolicy.IsDestination(command, destination), "source process impersonated destination");
            }
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Reject(Action action, string reason = null)
        {
            try { action(); }
            catch (Exception ex) { if (reason == null || ex.Message.Contains(reason)) return; throw; }
            throw new Exception("Expected rejection: " + reason);
        }
        private static void Run(string name, Action action) { action(); Console.WriteLine("PASS " + name); }
    }
}
