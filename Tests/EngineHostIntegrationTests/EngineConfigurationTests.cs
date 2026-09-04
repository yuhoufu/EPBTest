using System;
using System.IO;
using System.Linq;
using Config;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class EngineConfigurationTests
    {
        internal static int RunAll()
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.ConfigTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
                var path = Path.Combine(root, "TestConfig.xml");
                File.Copy(Path.Combine(repo, "MTTfTest", "Config", "TestConfig.xml"), path);
                var config = ConfigLoader.LoadTest(path, null);
                config.StoreDir = root; config.TestName = "SyntheticProject";
                foreach (var record in config.EpbRecords.Snapshot())
                {
                    record.Enabled = record.Id != 6; record.RunCount = 21000 + record.Id;
                    record.MechanicalCycleCount = 21500 + record.Id; record.RunTimeSpan = TimeSpan.FromHours(13);
                }
                config.GetEpbRecord(6).PermanentAlarmLatched = true;
                config.GetEpbRecord(6).PermanentAlarmReason = "synthetic hard fault";
                ConfigLoader.SaveTest(path, config);
                var imported = EngineTestConfigurationStore.LoadProjectWithoutReset(config, path);
                Assert(imported.GetEpbRecord(4).MechanicalCycleCount == 21504 && imported.GetEpbRecord(6).PermanentAlarmLatched,
                    "BootstrapImportResetProgressOrIsolation");
                imported.GetEpbRecord(4).MechanicalCycleCount = 30000;
                ConfigLoader.SaveTest(ConfigLoader.GetProjectTestConfigPath(imported.StoreDir, imported.TestName), imported);
                Assert(EngineTestConfigurationStore.LoadProjectWithoutReset(config, path).GetEpbRecord(4).MechanicalCycleCount == 30000,
                    "BootstrapReplacedExistingProjectWithDefault");
                Console.WriteLine("PASS BootstrapProjectImportNeverResetsExistingCounts");
                var store = new EngineTestConfigurationStore(path);
                var before = EngineTestConfigurationStore.Capture(config);
                Assert(before.IsStructurallyValid(), "FixtureSettingsInvalid");
                var hash = before.ComputeSha256();
                config.GetEpbRecord(1).RunCount++; config.GetEpbRecord(1).MechanicalCycleCount++;
                Assert(hash == EngineTestConfigurationStore.Capture(config).ComputeSha256(), "CountsChangedConfigurationHash");
                Console.WriteLine("PASS ConfigurationHashIgnoresRuntimeProgress");

                var command = Command(before, hash);
                var originalBytes = File.ReadAllBytes(path);
                ExpectFailure(() => store.Commit(command, boundary => { if (boundary == "Prepared") throw new IOException("crash"); }), "crash");
                Assert(originalBytes.SequenceEqual(File.ReadAllBytes(path)) && store.ReadRevision() == 0, "PreparedTransactionChangedActiveConfiguration");
                Console.WriteLine("PASS ConfigurationCrashBeforeCommitPreservesOldFile");

                Assert(command.IsStructurallyValid(), "ConfigurationCommandHashChangedAfterPrepare");
                var reordered = command.TestConfiguration.Configuration.Clone();
                reordered.Channels = reordered.Channels.Reverse().ToArray();
                reordered.Hydraulics = reordered.Hydraulics.Reverse().ToArray();
                typeof(EngineRunnerConfiguration).GetProperty("Selected").GetValue(reordered.Channels[0]);
                Assert(reordered.ComputeSha256() == command.TestConfiguration.Configuration.ComputeSha256(),
                    "ConfigurationHashDependsOnSerializationOrder");

                ExpectFailure(() => store.Commit(command, boundary => { if (boundary == "Committed") throw new IOException("crash"); }), "crash");
                var committed = new EngineTestConfigurationStore(path).Commit(command);
                Assert(store.ReadRevision() == 1 && committed.TestPeriod == 19 && committed.Owner == "R26 test", "CommittedTransactionReplayMismatch");
                Assert(committed.GetEpbRecord(4).MechanicalCycleCount == 21504 && committed.GetEpbRecord(4).RunCount == 21004 &&
                    committed.GetEpbRecord(4).RunTimeSpan == TimeSpan.FromHours(13), "ConfigurationResetHistoricalProgress");
                Assert(committed.GetEpbRecord(6).PermanentAlarmLatched && !committed.GetEpbRecord(6).Enabled, "ConfigurationClearedHardIsolation");
                Assert(committed.Hydraulics[0].ReleaseTimeoutMs == config.Hydraulics[0].ReleaseTimeoutMs &&
                    committed.OverrunPolicy == config.OverrunPolicy, "ConfigurationChangedUneditableSafetySettings");
                Assert(Directory.GetFiles(Path.Combine(root, "V3ConfigurationHistory")).Length == 1, "ReplayCreatedExtraArchive");
                Console.WriteLine("PASS ConfigurationAtomicReceiptSurvivesCrashWithoutReset");

                var stale = command.Clone(); stale.CommandId = RecoveryProtocolV7.NewId();
                ExpectFailure(() => store.Commit(stale), "ConfigurationRevisionConflict");
                var illegal = Command(EngineTestConfigurationStore.Capture(committed), EngineTestConfigurationStore.Capture(committed).ComputeSha256());
                illegal.TestConfiguration.BaseConfigurationRevision = 1;
                illegal.TestConfiguration.Configuration.Channels.Single(c => c.Channel == 6).Selected = true;
                illegal.PayloadSha256 = illegal.TestConfiguration.ComputeSha256();
                var committedBytes = File.ReadAllBytes(path);
                ExpectFailure(() => store.Commit(illegal), "ConfigurationCannotClearIsolation:6");
                Assert(committedBytes.SequenceEqual(File.ReadAllBytes(path)), "RejectedConfigurationMutatedFile");
                Console.WriteLine("PASS ConfigurationConflictsAndIsolationRemainFailClosed");

                committed.GetEpbRecord(4).RunCount++; ConfigLoader.SaveTest(path, committed);
                Assert(store.ReadRevision() == 1 && store.Commit(command).GetEpbRecord(4).RunCount == 21005,
                    "NormalProgressSaveLostTransactionReceipt");
                Console.WriteLine("PASS RuntimeProgressSavePreservesConfigurationRevisionAndReceipt");
                var identity = new RecoveryIdentity
                {
                    SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(), RunEpoch = 1,
                    IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 1, Revision = 1
                };
                var checkpoints = new EngineRunCheckpointStore(identity.SessionId, Path.Combine(root, "checkpoints"));
                checkpoints.LoadOrCreate(identity, committed, command.TestConfiguration.Configuration.ComputeSha256());
                checkpoints.Update(identity, value => { value.State = "StoppedByOperator";
                    value.IsolatedResources = new[] { "Channel:6" }; return true; }, "synthetic stopped");
                committed.TestTarget = 400000;
                committed.GetEpbRecord(4).TotalCount = 400000;
                checkpoints.RebindStoppedConfiguration(identity, committed, EngineTestConfigurationStore.Capture(committed).ComputeSha256());
                var restored = new EngineRunCheckpointStore(identity.SessionId, Path.Combine(root, "checkpoints")).Snapshot();
                Assert(restored.ProgressSchemaVersion == 1 && restored.FormalCyclesCompleted[3] == 21005 &&
                    restored.MechanicalCyclesCompleted[3] == 21504 && restored.RemainingFormalCycles[3] == 400000 - 21504 &&
                    !restored.SelectedChannels.Contains(6) && restored.IsolatedResources.Contains("Channel:6") &&
                    restored.ActiveCycleInvalidated && restored.QualificationCyclesCompleted == 0,
                    "CheckpointConfigurationRebindResetOrReenabledProgress");
                checkpoints.Update(identity, value => { value.State = "Running"; return true; }, "synthetic running");
                ExpectFailure(() => checkpoints.RebindStoppedConfiguration(identity, committed, hash), "ConfigurationCheckpointNotSafelyStopped");
                Console.WriteLine("PASS ConfigurationCheckpointRebindPreservesProgressAndIsolation");
                checkpoints.Update(identity, value => { value.QualificationCyclesCompleted = 2; value.FormalCyclesSinceRecovery = 9;
                    value.StableSinceUtcTicks = DateTime.UtcNow.AddHours(-1).Ticks; return true; }, "synthetic stable window");
                var previous = checkpoints.Snapshot();
                checkpoints.RecordManualBatchState(identity, true);
                var paused = new EngineRunCheckpointStore(identity.SessionId, Path.Combine(root, "checkpoints")).Snapshot();
                Assert(paused.State == "PausedByOperator" && paused.StableSinceUtcTicks == 0 && paused.FormalCyclesSinceRecovery == 0 &&
                    paused.QualificationCyclesCompleted == 2 && paused.FormalCyclesCompleted.SequenceEqual(previous.FormalCyclesCompleted) &&
                    paused.MechanicalCyclesCompleted.SequenceEqual(previous.MechanicalCyclesCompleted) && paused.RunTimeTicks.SequenceEqual(previous.RunTimeTicks) &&
                    paused.RemainingFormalCycles.SequenceEqual(previous.RemainingFormalCycles) && paused.IsolatedResources.SequenceEqual(previous.IsolatedResources),
                    "ManualPauseResetHistoryOrKeptOldStabilityWindow");
                var resumedAt = DateTime.UtcNow.Ticks;
                var resumed = checkpoints.RecordManualBatchState(identity, false);
                Assert(resumed.State == "Running" && resumed.StableSinceUtcTicks >= resumedAt && resumed.FormalCyclesSinceRecovery == 0 &&
                    resumed.FormalCyclesCompleted.SequenceEqual(previous.FormalCyclesCompleted), "PausedTimeQualifiedForRecoveryBudgetReset");
                Console.WriteLine("PASS ManualPausePreservesHistoryAndRestartsContinuousStabilityWindow");
                ExpectFailure(() => checkpoints.RecordFormalRunAuthorized(identity), "FormalRunCheckpointNotQualified");
                var qualified = checkpoints.RecordQualificationPrepared(identity);
                var reopened = new EngineRunCheckpointStore(identity.SessionId, Path.Combine(root, "checkpoints")).Snapshot();
                Assert(qualified.State == "QualifiedAwaitingSupervisor" && reopened.State == qualified.State &&
                    reopened.QualificationCyclesCompleted == 2 && reopened.StableSinceUtcTicks == 0 && reopened.FormalCyclesSinceRecovery == 0 &&
                    reopened.FormalCyclesCompleted.SequenceEqual(previous.FormalCyclesCompleted) &&
                    reopened.MechanicalCyclesCompleted.SequenceEqual(previous.MechanicalCyclesCompleted) &&
                    reopened.RemainingFormalCycles.SequenceEqual(previous.RemainingFormalCycles) &&
                    reopened.RunTimeTicks.SequenceEqual(previous.RunTimeTicks) && reopened.IsolatedResources.SequenceEqual(previous.IsolatedResources),
                    "QualificationStartedFormalStabilityOrResetHistory");
                var authorizedAt = DateTime.UtcNow.Ticks;
                var formal = checkpoints.RecordFormalRunAuthorized(identity);
                Assert(formal.State == "Running" && formal.StableSinceUtcTicks >= authorizedAt &&
                    formal.FormalCyclesCompleted.SequenceEqual(previous.FormalCyclesCompleted), "FormalAuthorizationCountedWaitingTime");
                ExpectFailure(() => checkpoints.RecordFormalRunAuthorized(identity), "FormalRunCheckpointNotQualified");
                Console.WriteLine("PASS QualificationCheckpointDoesNotStartFormalRunOrCountWaitingTime");
                checkpoints.Update(identity, value =>
                {
                    value.State = "QualifiedAwaitingSupervisor";
                    value.QualificationCyclesCompleted = 2;
                    value.IsolatedResources = new[] { "Channel:6" };
                    value.SelectedChannels = value.SelectedChannels.Where(channel => channel != 6).ToArray();
                    return true;
                }, "synthetic qualification retry");
                var retryHash = EngineTestConfigurationStore.Capture(committed).ComputeSha256();
                var reintegrated = checkpoints.RecordQualificationRetryReintegrated(identity, "Channel:6", 6, retryHash);
                Assert(reintegrated.State == "QualifiedAwaitingSupervisor" && reintegrated.SelectedChannels.Contains(6) &&
                    !reintegrated.IsolatedResources.Contains("Channel:6") && reintegrated.ConfigurationSha256 == retryHash &&
                    reintegrated.FormalCyclesCompleted.SequenceEqual(previous.FormalCyclesCompleted) &&
                    reintegrated.MechanicalCyclesCompleted.SequenceEqual(previous.MechanicalCyclesCompleted) &&
                    reintegrated.RunTimeTicks.SequenceEqual(previous.RunTimeTicks),
                    "qualification retry reintegration reset work or released before qualification");
                checkpoints.RecordFormalRunAuthorized(identity);
                ExpectFailure(() => checkpoints.RecordQualificationRetryReintegrated(identity, "Channel:6", 6, retryHash),
                    "QualificationRetryCheckpointNotQualifiedOrIsolated");
                Console.WriteLine("PASS QualificationRetryReintegrationPreservesHistoryAndRequiresQualifiedIsolation");
                return 10;
            }
            finally { Directory.Delete(root, true); }
        }

        private static OperatorCommand Command(EngineTestConfiguration configuration, string hash)
        {
            var settings = new TestConfigurationCommit { BaseConfigurationRevision = 0, BaseConfigurationSha256 = hash, Configuration = configuration.Clone() };
            settings.Configuration.TestPeriod = 19; settings.Configuration.Owner = "R26 test";
            var command = new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(), SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(),
                RunEpoch = 1, Kind = OperatorCommandKind.CommitConfiguration, IssuedUtcTicks = DateTime.UtcNow.Ticks,
                TestConfiguration = settings, PayloadSha256 = settings.ComputeSha256()
            };
            return command;
        }

        private static void ExpectFailure(Action action, string message)
        {
            try { action(); }
            catch (Exception ex) when (ex.Message == message) { return; }
            throw new Exception("ExpectedFailure:" + message);
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
