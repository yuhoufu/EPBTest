using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;
using MTTFTest.Watchdog;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class EngineHostTests
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static RecoveryCommand _durableCommand;
        private static RecoveryCommandReceipt _durableReceipt;
        private static string _testInstance;
        private static string _testRoot;
        private static string _pipeName;
        private static OperatorCommand _panelCommand;
        private static OperatorExecutionReceipt _panelReceipt;
        private static RecoveryCommand _qualifiedCommand;

        internal static int RunAll()
        {
            _testInstance = RecoveryProtocolV7.NewId();
            _testRoot = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.EngineHostIntegration." + _testInstance);
            _pipeName = EngineHostProtocol.PipeName + ".test." + _testInstance;
            Directory.CreateDirectory(_testRoot);
            var configuration = new DirectoryInfo(
                AppDomain.CurrentDomain.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "MTTFTest.EngineHost", "bin", configuration,
                "MTTFTest.EngineHost.exe"));
            Assert(File.Exists(executable), "EngineHostExecutableMissing:" + executable);
            var sessionId = RecoveryProtocolV7.NewId();
            var runId = RecoveryProtocolV7.NewId();
            var passed = 0;
            var packagedConfig = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "MTTfTest", "bin", configuration, "MTTFTest.EngineHost.exe.config"));
            Assert(File.Exists(packagedConfig), "PackagedEngineHostRuntimeConfigurationMissing:" + packagedConfig);
            var configDocument = new System.Xml.XmlDocument { XmlResolver = null };
            configDocument.Load(packagedConfig);
            var values = new System.Collections.Specialized.NameValueCollection();
            foreach (System.Xml.XmlElement entry in configDocument.SelectNodes("/configuration/appSettings/add"))
                values.Add(entry.GetAttribute("key"), entry.GetAttribute("value"));
            var daqSettings = Config.DaqRuntimeSettings.Load(values);
            Assert(daqSettings.SampleRateHz > 0 && daqSettings.SamplesPerChannel > 0,
                "PackagedEngineHostRuntimeConfigurationInvalid");
            passed += Pass("PackagedEngineHostCarriesValidDaqRuntimeConfiguration");
            using (var engine = StartEngine(executable, sessionId, runId))
            {
                Assert(engine != null, "EngineHostStartFailed");
                try
                {
                    WaitUntilReady(engine);
                    passed += SnapshotCarriesBootstrapIdentity(sessionId, runId);
                    var productionSnapshot = MTTFTest.Watchdog.EngineHostPipeClient.Send(
                        new EngineHostRequest
                        {
                            RequestId = RecoveryProtocolV7.NewId(),
                            Kind = EngineHostRequestKind.ReadLatestSnapshot
                        }, 3000, _pipeName);
                    Assert(productionSnapshot.Accepted &&
                        productionSnapshot.Snapshot.SessionId == sessionId,
                        "ProductionSupervisorCannotReadEngineHost");
                    passed += Pass("ProductionSupervisorReadsEngineHostSnapshot");
                    var uiResponse = MTTFTest.Watchdog.EngineHostPipeClient.Send(new EngineHostRequest
                    {
                        RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ReadUiSnapshot
                    }, 3000, _pipeName + EngineHostProtocol.UiPipeSuffix);
                    Assert(uiResponse.Accepted && uiResponse.UiSnapshot?.IsStructurallyValid() == true &&
                        uiResponse.UiSnapshot.Engine.SessionId == sessionId &&
                        !uiResponse.UiSnapshot.Channels[0].Current.Valid,
                        "UiSnapshotMustCarryValidContractWithoutFabricatedMeasurements");
                    passed += Pass("ProductionEngineHostPublishesOriginalUiContract");
                    var logPage = MTTFTest.Watchdog.EngineHostPipeClient.Send(new EngineHostRequest
                    {
                        RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ReadUiLogs,
                        UiLogQuery = new EngineUiLogQuery { PageSize = 10 }
                    }, 3000, _pipeName + EngineHostProtocol.UiPipeSuffix);
                    Assert(logPage.Accepted && logPage.Snapshot?.SessionId == sessionId && logPage.UiLogPage?.IsStructurallyValid() == true &&
                        logPage.UiLogPage.Entries.Length <= 10, "UiLogPageMustBeBoundedAndIdentifyEngine");
                    passed += Pass("ProductionEngineHostPagesLogsWithoutControlAccess");
                    passed += StalledUiDoesNotBlockSupervisor(sessionId);
                    passed += AttachmentSnapshotBypassesStalledControl(engine, sessionId);
                    passed += MaintenanceLeaseUsesIndependentEndpoint(engine);
                    var denied = MTTFTest.Watchdog.EngineHostPipeClient.Send(new EngineHostRequest
                    {
                        RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ExecuteOperatorCommand,
                        OperatorCommand = new OperatorCommand
                        {
                            CommandId = RecoveryProtocolV7.NewId(), SessionId = sessionId, RunId = runId,
                            RunEpoch = 1, BaseRevision = 1, Kind = OperatorCommandKind.Start,
                            PayloadSha256 = SupervisorProtocol.ComputeTextSha256("OperatorStart"), IssuedUtcTicks = DateTime.UtcNow.Ticks
                        }
                    }, 5000, _pipeName + EngineHostProtocol.UiPipeSuffix);
                    Assert(!denied.Accepted && denied.FailureCode == "UiPipeReadOnly", "DisplayPipeAcceptedControl");
                    passed += Pass("UiPipeRejectsOperatorCommands");
                    passed += PanelCommandIsIndependent(sessionId, runId, uiResponse.UiSnapshot.AlarmPanel);
                    passed += PulseAndLatestOnlyTelemetryAdvance();
                    passed += RecoveryCommandIsIdempotent(sessionId, runId);
                    passed += RecoveryCommandStartSequenceRuns(sessionId, runId);
                    passed += StartCannotBypassKernelGate(sessionId, runId);
                    passed += ManualBatchKeepsHostIdentity(sessionId, runId);
                    passed += ManualChannelKeepsHealthyPeers(sessionId, runId);
                    passed += SafetyAndStateReadBypassStalledControl(sessionId, runId);
                    passed += SafetyAndStateEndpointsRejectOtherKinds(sessionId, runId);
                    passed += DuplicateEngineHostIsRejected(executable, sessionId, runId);
                    passed += QualificationInvalidatedByStop(sessionId, runId);
                    passed += PressureMaintenanceCommandRoundTrip(engine);
                }
                finally
                {
                    if (!engine.HasExited) engine.Kill();
                    engine.WaitForExit(5000);
                }
            }
            using (var replacement = StartEngine(executable, sessionId, runId))
            {
                Assert(replacement != null, "ReplacementEngineHostStartFailed");
                try
                {
                    WaitUntilReady(replacement);
                    passed += HardwareHandoffDoesNotSurviveEngineReplacement();
                    var staleResume = Command(sessionId, runId, _qualifiedCommand.OwnerId, _qualifiedCommand.Identity.IncidentId,
                        20, RecoveryCommandKind.ResumeFormalRun);
                    var staleResult = Send(staleResume);
                    Assert(staleResult.RecoveryReceipt?.Succeeded == false && staleResult.Snapshot?.State != SystemTerminalState.Running,
                        "ReplacementReusedOldQualification");
                    passed += Pass("EngineReplacementCannotReuseQualifiedContinuation");
                    var panelReplay = MTTFTest.Watchdog.EngineHostPipeClient.ExecutePanel(_panelCommand, 5000, _pipeName + EngineHostProtocol.PanelPipeSuffix);
                    Assert(panelReplay.CompletedUtcTicks == _panelReceipt.CompletedUtcTicks, "PanelCommandReexecutedAfterProcessReplacement");
                    passed += Pass("AlarmPanelReceiptSurvivesEngineReplacement");
                }
                finally
                {
                    if (!replacement.HasExited) replacement.Kill();
                    replacement.WaitForExit(5000);
                }
            }
            try { Directory.Delete(_testRoot, true); } catch { }
            return passed;
        }

        private static int PressureMaintenanceCommandRoundTrip(Process engine)
        {
            var before = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            var sequence = 100L;
            RecoveryCommand oldOutput = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var lease = PressureMaintenanceTransportTests.Lease(before.SessionId, before.RunId, before.EngineInstanceId);
                EngineHostPipeClient.SendMaintenanceLeaseAsync(lease,
                    (pid, started) => pid == engine.Id && started == engine.StartTime.ToUniversalTime().Ticks,
                    CancellationToken.None, _pipeName + EngineHostProtocol.MaintenanceLeasePipeSuffix).GetAwaiter().GetResult();
                var off = PressureMaintenanceExecutorTests.Operation(lease, RecoveryCommandKind.DisableOutputs, sequence++);
                var offResponse = Send(off);
                Assert(offResponse.RecoveryReceipt?.Succeeded == true && offResponse.RecoveryReceipt.HardwareHandoff?.ResourcesTransferable == true,
                    "maintenance initial joined handoff failed:" + offResponse.Detail);
                var prepare = PressureMaintenanceExecutorTests.Operation(lease, RecoveryCommandKind.PreparePressureMaintenance, sequence++);
                var prepared = Send(prepare);
                Assert(prepared.RecoveryReceipt?.Succeeded == true && prepared.RecoveryReceipt.PressureMaintenance?.OutputActive == false &&
                    prepared.Snapshot.RecoveryOwnerId == lease.OwnerId, "maintenance prepare failed:" + prepared.Detail + ";" + prepared.RecoveryReceipt?.Detail);
                var output = PressureMaintenanceExecutorTests.Operation(lease, RecoveryCommandKind.SetMaintenancePressure, sequence++);
                var result = Send(output); oldOutput = output;
                Assert(result.RecoveryReceipt?.Succeeded == true && result.RecoveryReceipt.PressureMaintenance?.Voltage == 1.5 &&
                    result.Snapshot.OutputsEnergized && result.RecoveryReceipt.FormalCyclesCompleted == 0 &&
                    result.RecoveryReceipt.QualificationCyclesCompleted == 0 && !result.RecoveryReceipt.InterruptedCycleCounted,
                    "maintenance output missing typed receipt or changed cycle accounting:" + result.RecoveryReceipt?.Detail);
                Assert(Send(output).RecoveryReceipt.CompletedUtcTicks == result.RecoveryReceipt.CompletedUtcTicks,
                    "maintenance repeated output was executed again");
                var stopOutput = Send(PressureMaintenanceExecutorTests.Operation(lease, RecoveryCommandKind.StopMaintenanceOutput, sequence++));
                Assert(stopOutput.RecoveryReceipt?.Succeeded == true && !stopOutput.Snapshot.OutputsEnergized &&
                    stopOutput.Snapshot.RecoveryOwnerId == lease.OwnerId, "stop output ended maintenance or kept output active");
                lease.Revision++; lease.Revoked = true;
                EngineHostPipeClient.SendMaintenanceLeaseAsync(lease,
                    (pid, started) => pid == engine.Id && started == engine.StartTime.ToUniversalTime().Ticks,
                    CancellationToken.None, _pipeName + EngineHostProtocol.MaintenanceLeasePipeSuffix).GetAwaiter().GetResult();
                var ended = Send(PressureMaintenanceExecutorTests.Operation(lease, RecoveryCommandKind.DisableOutputs, sequence++));
                Assert(ended.RecoveryReceipt?.Succeeded == true && ended.RecoveryReceipt.HardwareHandoff?.ResourcesTransferable == true &&
                    ended.Snapshot.State == SystemTerminalState.StoppedByOperator && string.IsNullOrEmpty(ended.Snapshot.RecoveryOwnerId) &&
                    !ended.Snapshot.OutputsEnergized && ended.Snapshot.HardwareRecompositionReady,
                    "maintenance end left stale owner or unreleased resources:" + ended.RecoveryReceipt?.Detail);
                Assert(!Send(oldOutput).Accepted, "completed output receipt revived after maintenance ended");
            }
            var after = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(after.EngineInstanceId == before.EngineInstanceId && after.FormalCyclesSinceRecovery == before.FormalCyclesSinceRecovery,
                "maintenance replaced engine or changed formal progress");
            return Pass("PressureMaintenanceActualHostCommandsRetainCountsAndEndOwner");
        }

        private static int MaintenanceLeaseUsesIndependentEndpoint(Process engine)
        {
            var before = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            var lease = PressureMaintenanceTransportTests.Lease(before.SessionId, before.RunId, before.EngineInstanceId);
            using (var control = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var display = new NamedPipeClientStream(".", _pipeName + EngineHostProtocol.UiPipeSuffix, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                control.Connect(3000); control.WriteByte(1);
                display.Connect(3000); display.WriteByte(1);
                var response = EngineHostPipeClient.SendMaintenanceLeaseAsync(lease,
                    (pid, started) => pid == engine.Id && started == engine.StartTime.ToUniversalTime().Ticks,
                    CancellationToken.None, _pipeName + EngineHostProtocol.MaintenanceLeasePipeSuffix).GetAwaiter().GetResult();
                Assert(response.ComputeSha256() == lease.ComputeSha256(), "lease blocked behind control/display");
                var invalidKind = EngineHostPipeClient.Send(new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(),
                    Kind = EngineHostRequestKind.ReadLatestSnapshot }, 2000, _pipeName + EngineHostProtocol.MaintenanceLeasePipeSuffix);
                Assert(!invalidKind.Accepted, "lease endpoint exposed other operations");
            }
            var request = new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.UpdateMaintenanceLease, MaintenanceLease = lease };
            Assert(!EngineHostPipeClient.Send(request, 3000, _pipeName).Accepted, "control endpoint accepted lease");
            Assert(!EngineHostPipeClient.Send(request, 3000, _pipeName + EngineHostProtocol.UiPipeSuffix).Accepted, "display endpoint accepted lease");
            var foreign = lease.Clone(); foreign.EngineInstanceId = RecoveryProtocolV7.NewId(); request.MaintenanceLease = foreign;
            Assert(!EngineHostPipeClient.Send(request, 3000, _pipeName + EngineHostProtocol.MaintenanceLeasePipeSuffix).Accepted, "replacement identity reused lease");
            lease.Revision++; lease.Revoked = true;
            EngineHostPipeClient.SendMaintenanceLeaseAsync(lease, (pid, started) => pid == engine.Id && started == engine.StartTime.ToUniversalTime().Ticks,
                CancellationToken.None, _pipeName + EngineHostProtocol.MaintenanceLeasePipeSuffix).GetAwaiter().GetResult();
            var revived = lease.Clone(); revived.Revision++; revived.Revoked = false; request.MaintenanceLease = revived;
            Assert(!EngineHostPipeClient.Send(request, 3000, _pipeName + EngineHostProtocol.MaintenanceLeasePipeSuffix).Accepted, "revocation revived by late renewal");
            var after = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(after.State == before.State && after.OutputsEnergized == before.OutputsEnergized && after.RecoveryOwnerId == before.RecoveryOwnerId &&
                after.FormalCyclesSinceRecovery == before.FormalCyclesSinceRecovery, "lease publication mutated operating state or formal history");
            return Pass("MaintenanceLeaseEndpointBypassesBlockedPipesWithoutActuating");
        }

        private static int StalledUiDoesNotBlockSupervisor(string sessionId)
        {
            using (var stalled = new NamedPipeClientStream(".", _pipeName + EngineHostProtocol.UiPipeSuffix,
                PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                stalled.Connect(3000);
                stalled.WriteByte(1); // Deliberately incomplete frame; do not send the rest.
                var elapsed = Stopwatch.StartNew();
                var snapshot = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
                Assert(snapshot.SessionId == sessionId && elapsed.ElapsedMilliseconds < 2000,
                    "PartialUiFrameBlockedSupervisorPipe");
            }
            return Pass(nameof(StalledUiDoesNotBlockSupervisor));
        }

        private static int AttachmentSnapshotBypassesStalledControl(Process engine, string sessionId)
        {
            using (var stalled = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                stalled.Connect(3000);
                stalled.WriteByte(1);
                var elapsed = Stopwatch.StartNew();
                var snapshot = EngineHostPipeClient.ReadBoundSnapshot(sessionId,
                    (id, started) => id == engine.Id && started == engine.StartTime.ToUniversalTime().Ticks,
                    out var serverId, out var serverStart, 3000,
                    _pipeName + EngineHostProtocol.SupervisorReadPipeSuffix);
                Assert(snapshot.SessionId == sessionId && serverId == engine.Id &&
                    serverStart == engine.StartTime.ToUniversalTime().Ticks && elapsed.ElapsedMilliseconds < 2000,
                    "Attachment snapshot must verify the actual engine without waiting for the command pipe");
            }
            return Pass(nameof(AttachmentSnapshotBypassesStalledControl));
        }

        private static int SafetyAndStateReadBypassStalledControl(string sessionId, string runId)
        {
            using (var stalled = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                stalled.Connect(3000); stalled.WriteByte(1);
                var elapsed = Stopwatch.StartNew();
                var before = MTTFTest.Watchdog.EngineHostPipeClient.Send(new EngineHostRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ReadLatestSnapshot
                }, 3000, _pipeName + EngineHostProtocol.SupervisorReadPipeSuffix).Snapshot;
                Assert(before?.State == SystemTerminalState.Running && elapsed.ElapsedMilliseconds < 2000,
                    "stalled command prevented independent state read");
                var stop = Command(sessionId, runId, RecoveryProtocolV7.NewId(), RecoveryProtocolV7.NewId(), 9, RecoveryCommandKind.StopByOperator);
                var response = MTTFTest.Watchdog.EngineHostPipeClient.Send(new EngineHostRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ExecuteRecoveryCommand, RecoveryCommand = stop
                }, 3000, _pipeName + EngineHostProtocol.SafetyPipeSuffix);
                Assert(response.Accepted && response.RecoveryReceipt?.Succeeded == true &&
                    response.Snapshot?.State == SystemTerminalState.StoppedByOperator,
                    "stalled command prevented priority stop");
                var after = MTTFTest.Watchdog.EngineHostPipeClient.Send(new EngineHostRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ReadLatestSnapshot
                }, 3000, _pipeName + EngineHostProtocol.SupervisorReadPipeSuffix).Snapshot;
                Assert(after.EngineInstanceId == before.EngineInstanceId && after.Revision > before.Revision,
                    "priority stop restarted EngineHost or failed to publish state");
            }
            return Pass(nameof(SafetyAndStateReadBypassStalledControl));
        }

        private static int SafetyAndStateEndpointsRejectOtherKinds(string sessionId, string runId)
        {
            var resume = Command(sessionId, runId, RecoveryProtocolV7.NewId(), RecoveryProtocolV7.NewId(), 10, RecoveryCommandKind.ResumeFormalRun);
            var request = new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ExecuteRecoveryCommand, RecoveryCommand = resume };
            var safety = MTTFTest.Watchdog.EngineHostPipeClient.Send(request, 3000, _pipeName + EngineHostProtocol.SafetyPipeSuffix);
            var readOnly = MTTFTest.Watchdog.EngineHostPipeClient.Send(request, 3000, _pipeName + EngineHostProtocol.SupervisorReadPipeSuffix);
            Assert(!safety.Accepted && !readOnly.Accepted, "dedicated endpoints admitted normal control");
            var snapshot = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(snapshot.State == SystemTerminalState.StoppedByOperator, "rejected resume changed state");
            return Pass(nameof(SafetyAndStateEndpointsRejectOtherKinds));
        }

        private static int PanelCommandIsIndependent(string sessionId, string runId, AlarmPanelStatus panel)
        {
            var before = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            var payload = new AlarmPanelCommand { PanelInstanceId = panel.PanelInstanceId, BaseRevision = panel.Revision, BuzzerEnabled = false };
            _panelCommand = new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = sessionId, RunId = runId,
                RunEpoch = 1, BaseRevision = before.Revision, Kind = OperatorCommandKind.SetBuzzerEnabled, AlarmPanel = payload,
                PayloadSha256 = payload.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks };
            var request = new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ExecutePanelCommand, OperatorCommand = _panelCommand };
            var denied = MTTFTest.Watchdog.EngineHostPipeClient.Send(request, 5000, _pipeName + EngineHostProtocol.UiPipeSuffix);
            Assert(!denied.Accepted, "UntrustedDisplayPipeAcceptedPanelCommand");
            denied = MTTFTest.Watchdog.EngineHostPipeClient.Send(request, 5000, _pipeName);
            Assert(!denied.Accepted, "PanelCommandEnteredRecoveryPipe");
            _panelReceipt = MTTFTest.Watchdog.EngineHostPipeClient.ExecutePanel(_panelCommand, 5000, _pipeName + EngineHostProtocol.PanelPipeSuffix);
            var replay = MTTFTest.Watchdog.EngineHostPipeClient.ExecutePanel(_panelCommand, 5000, _pipeName + EngineHostProtocol.PanelPipeSuffix);
            var after = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(_panelReceipt.Succeeded && replay.CompletedUtcTicks == _panelReceipt.CompletedUtcTicks &&
                before.Revision == after.Revision && before.State == after.State && before.RecoveryOwnerId == after.RecoveryOwnerId,
                "AlarmPanelChangedRecoveryStateOrExecutedTwice");
            using (var stalled = new NamedPipeClientStream(".", _pipeName + EngineHostProtocol.PanelPipeSuffix, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                stalled.Connect(3000); stalled.WriteByte(1);
                var clock = Stopwatch.StartNew(); var snapshot = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
                Assert(snapshot.SessionId == sessionId && clock.ElapsedMilliseconds < 2000, "PanelPipeBlockedRecoveryPipe");
            }
            return Pass("AlarmPanelCommandIsIdempotentAndIndependentOfRecoveryPipe");
        }

        private static int ManualChannelKeepsHealthyPeers(string sessionId, string runId)
        {
            var before = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            var pause = EngineManualBatchTests.ForChannel(EngineManualBatchTests.PauseCommand(), 4, true);
            pause.Identity.SessionId = sessionId; pause.Identity.RunId = runId; pause.Identity.Revision = pause.CommandSequence = 7;
            pause.OperatorTransaction.SessionId = sessionId; pause.OperatorTransaction.RunId = runId;
            pause.OperatorTransaction.ManualBatch.EngineInstanceId = before.EngineInstanceId;
            pause.OperatorTransaction.PayloadSha256 = pause.OperatorTransaction.ManualBatch.ComputeSha256(); pause.IdempotencyKey = pause.ExpectedIdempotencyKey();
            var held = Send(pause);
            Assert(held.RecoveryReceipt.Succeeded && held.Snapshot.State == SystemTerminalState.RunningDegraded &&
                held.Snapshot.ChannelResumeMask == 8 && held.Snapshot.ChannelPauseMask == (4095 & ~8), "single channel hold stopped healthy peers");
            var resume = EngineManualBatchTests.ForChannel(EngineManualBatchTests.ResumeCommand(pause), 4, false);
            var result = Send(resume);
            Assert(result.RecoveryReceipt.Succeeded && result.Snapshot.EngineInstanceId == before.EngineInstanceId &&
                result.Snapshot.ChannelPauseMask == 4095 && result.Snapshot.ChannelResumeMask == 0 &&
                result.Snapshot.State == SystemTerminalState.Running && result.Snapshot.RecoveryOwnerId == "", "channel continuation changed host or retained owner");
            return Pass(nameof(ManualChannelKeepsHealthyPeers));
        }

        private static int ManualBatchKeepsHostIdentity(string sessionId, string runId)
        {
            var before = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            var pause = EngineManualBatchTests.PauseCommand();
            pause.Identity.SessionId = sessionId; pause.Identity.RunId = runId; pause.Identity.Revision = 5; pause.CommandSequence = 5;
            pause.OperatorTransaction.SessionId = sessionId; pause.OperatorTransaction.RunId = runId;
            pause.OperatorTransaction.ManualBatch.EngineInstanceId = before.EngineInstanceId;
            pause.OperatorTransaction.PayloadSha256 = pause.OperatorTransaction.ManualBatch.ComputeSha256();
            pause.IdempotencyKey = pause.ExpectedIdempotencyKey();
            var paused = Send(pause);
            Assert(paused.RecoveryReceipt?.Succeeded == true && paused.Snapshot.State == SystemTerminalState.StoppedByOperator &&
                paused.Snapshot.RecoveryOwnerId == pause.OwnerId && !paused.RecoveryReceipt.ExecutionAuthorizationRevoked,
                "manual pause lost owner or fabricated hardware handoff");
            Assert(Send(pause).RecoveryReceipt.CompletedUtcTicks == paused.RecoveryReceipt.CompletedUtcTicks, "duplicate pause executed");
            var ui = MTTFTest.Watchdog.EngineHostPipeClient.Send(new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ReadUiSnapshot }, 3000, _pipeName + EngineHostProtocol.UiPipeSuffix);
            Assert(ui.UiSnapshot.BatchResumeAvailable && !ui.UiSnapshot.BatchPauseAvailable, "manual availability not published");
            var resume = EngineManualBatchTests.ResumeCommand(pause);
            var result = Send(resume);
            Assert(result.RecoveryReceipt?.Succeeded == true && result.Snapshot.State == SystemTerminalState.Running &&
                result.Snapshot.EngineInstanceId == before.EngineInstanceId && result.Snapshot.RunId == before.RunId &&
                result.Snapshot.RunEpoch == before.RunEpoch && result.Snapshot.RecoveryOwnerId == "", "resume replaced or reset running identity");
            return Pass(nameof(ManualBatchKeepsHostIdentity));
        }

        private static Process StartEngine(
            string executable,
            string sessionId,
            string runId)
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--simulation --session " + sessionId +
                            " --run " + runId + " --epoch 1",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
            start.EnvironmentVariables["MTTFTEST_ENGINEHOST_TEST_INSTANCE"] =
                _testInstance;
            start.EnvironmentVariables["MTTFTEST_ENGINEHOST_TEST_ROOT"] =
                _testRoot;
            return Process.Start(start);
        }

        private static int SnapshotCarriesBootstrapIdentity(string sessionId, string runId)
        {
            var response = Send(EngineHostRequestKind.ReadLatestSnapshot);
            Assert(response.Accepted, response.FailureCode + ":" + response.Detail);
            Assert(response.Snapshot != null, "SnapshotMissing");
            Assert(response.Snapshot.SchemaVersion == 7, "SnapshotSchemaNot7");
            Assert(response.Snapshot.SessionId == sessionId, "SessionIdentityLost");
            Assert(response.Snapshot.RunId == runId, "BootstrapRunIdentityLost");
            Assert(response.Snapshot.RunEpoch == 1, "RunEpochLost");
            Assert(response.Snapshot.State == SystemTerminalState.SafeIdleAlarmed,
                "EngineMustStartSafeIdle");
            Assert(response.Snapshot.HardwareInitialized, "SimulationSafeIdleNotProven");
            return Pass(nameof(SnapshotCarriesBootstrapIdentity));
        }

        private static int PulseAndLatestOnlyTelemetryAdvance()
        {
            EngineTelemetryFrame first = null;
            var readyDeadline = Stopwatch.StartNew();
            while (first == null && readyDeadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                first = Send(EngineHostRequestKind.ReadLatestTelemetry).Telemetry;
                if (first == null) Thread.Sleep(25);
            }
            Assert(first != null, "FirstTelemetryDeadlineExceeded");
            Thread.Sleep(650);
            var second = Send(EngineHostRequestKind.ReadLatestTelemetry).Telemetry;
            Assert(first != null && second != null, "TelemetryMissing");
            Assert(second.Sequence > first.Sequence, "LatestOnlyTelemetryDidNotAdvance");
            return Pass(nameof(PulseAndLatestOnlyTelemetryAdvance));
        }

        private static int RecoveryCommandIsIdempotent(string sessionId, string runId)
        {
            var identity = Identity(sessionId, runId, "System", 1);
            var command = new RecoveryCommand
            {
                Identity = identity,
                OwnerId = RecoveryProtocolV7.NewId(),
                CommandId = RecoveryProtocolV7.NewId(),
                CommandSequence = 1,
                Kind = RecoveryCommandKind.DisableOutputs,
                TargetResource = "System",
                DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(10).Ticks
            };
            command.IdempotencyKey = RecoveryProtocolV7.ComputeIdempotencyKey(
                command.Identity, command.Kind, command.CommandSequence);
            var first = Send(command).RecoveryReceipt;
            var second = Send(command).RecoveryReceipt;
            Assert(first != null && second != null, "RecoveryReceiptMissing");
            Assert(first.CommandId == second.CommandId, "DuplicateCommandExecutedTwice");
            Assert(first.CompletedUtcTicks == second.CompletedUtcTicks,
                "IdempotencyReceiptWasRecreated");
            var snapshot = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(first.HardwareHandoff?.Matches(command, snapshot.EngineInstanceId) == true &&
                first.HardwareHandoff.ResourcesTransferable && !snapshot.HardwareInitialized && snapshot.HardwareRecompositionReady,
                "ReleasedHandoffOrColdHostReadinessMissing");
            _durableCommand = command;
            _durableReceipt = first;
            return Pass(nameof(RecoveryCommandIsIdempotent));
        }

        private static int HardwareHandoffDoesNotSurviveEngineReplacement()
        {
            Assert(_durableCommand != null && _durableReceipt != null,
                "DurableReceiptFixtureMissing");
            var replay = Send(_durableCommand);
            Assert(!replay.Accepted && replay.RecoveryReceipt == null,
                "ReplacementReusedOldNativeReleaseEvidence");
            Assert(Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot.HardwareInitialized,
                "RejectedOldReceiptUnexpectedlyDisposedNewHardware");
            return Pass(nameof(HardwareHandoffDoesNotSurviveEngineReplacement));
        }

        private static int RecoveryCommandStartSequenceRuns(
            string sessionId,
            string runId)
        {
            var owner = RecoveryProtocolV7.NewId();
            var incident = RecoveryProtocolV7.NewId();
            var preflight = Command(sessionId, runId, owner, incident,
                2, RecoveryCommandKind.RunPassivePreflight);
            var preflightResponse = Send(preflight);
            var preflightReceipt = preflightResponse.RecoveryReceipt;
            Assert(preflightReceipt?.Succeeded == true,
                "SimulatedPreflightFailed");
            Assert(preflightResponse.Snapshot.HardwareInitialized && !preflightResponse.Snapshot.HardwareRecompositionReady,
                "PreflightDidNotRecomposeReleasedHardware");
            Assert(!Send(_durableCommand).Accepted, "SameEngineReusedReleaseReceiptAfterRecomposition");
            var qualification = Command(sessionId, runId, owner, incident,
                3, RecoveryCommandKind.RunQualificationCycle);
            var qualificationReceipt = Send(qualification).RecoveryReceipt;
            Assert(qualificationReceipt?.Succeeded == true &&
                   qualificationReceipt.QualificationCyclesCompleted == 2 && qualificationReceipt.StableSinceUtcTicks == 0,
                "QualificationReceiptMissingTwoCycles");
            var prepared = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(prepared.State != SystemTerminalState.Running && prepared.ChannelPauseMask == 0 &&
                prepared.RecoveryOwnerId == owner && prepared.RecoveryIncidentId == incident,
                "QualificationStartedFormalRunBeforeSupervisorAuthorization");
            var resume = Command(sessionId, runId, owner, incident,
                4, RecoveryCommandKind.ResumeFormalRun);
            Assert(Send(resume).RecoveryReceipt?.Succeeded == true,
                "FormalResumeFailed");
            var snapshot = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(snapshot.State == SystemTerminalState.Running,
                "EngineSnapshotDidNotEnterRunning");
            return Pass(nameof(RecoveryCommandStartSequenceRuns));
        }

        private static int QualificationInvalidatedByStop(string sessionId, string runId)
        {
            var owner = RecoveryProtocolV7.NewId(); var incident = RecoveryProtocolV7.NewId();
            _qualifiedCommand = Command(sessionId, runId, owner, incident, 11, RecoveryCommandKind.RunQualificationCycle);
            Assert(Send(_qualifiedCommand).RecoveryReceipt?.Succeeded == true, "SecondQualificationFailed");
            var stop = Command(sessionId, runId, RecoveryProtocolV7.NewId(), RecoveryProtocolV7.NewId(), 12, RecoveryCommandKind.StopByOperator);
            Assert(Send(stop).RecoveryReceipt?.Succeeded == true, "QualificationStopFailed");
            var stale = Command(sessionId, runId, owner, incident, 13, RecoveryCommandKind.ResumeFormalRun);
            var result = Send(stale);
            Assert(result.RecoveryReceipt?.Succeeded == false && result.Snapshot?.State == SystemTerminalState.StoppedByOperator &&
                result.Snapshot.ChannelPauseMask == 0, "StopAllowedOldQualificationToEnergize");
            return Pass(nameof(QualificationInvalidatedByStop));
        }

        private static int StartCannotBypassKernelGate(string sessionId, string runId)
        {
            var snapshot = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            var command = new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(),
                SessionId = sessionId,
                RunId = runId,
                RunEpoch = 1,
                BaseRevision = snapshot.Revision,
                PayloadSha256 = new string('A', 64),
                Kind = OperatorCommandKind.Start,
                IssuedUtcTicks = DateTime.UtcNow.Ticks
            };
            var response = Send(command);
            Assert(!response.Accepted, "OperatorStartBypassedRecoveryKernel");
            Assert(response.Detail.Contains("RecoveryKernelGate"),
                "UnexpectedStartRejection:" + response.Detail);
            return Pass(nameof(StartCannotBypassKernelGate));
        }

        private static int DuplicateEngineHostIsRejected(
            string executable,
            string sessionId,
            string runId)
        {
            using (var duplicate = StartEngine(executable, sessionId, runId))
            {
                try
                {
                    Assert(duplicate != null && duplicate.WaitForExit(5000), "DuplicateEngineHostDidNotExit");
                    Assert(duplicate.ExitCode == 3, "DuplicateEngineHostWasNotRejected");
                }
                finally
                {
                    if (duplicate != null && !duplicate.HasExited) { duplicate.Kill(); duplicate.WaitForExit(5000); }
                }
            }
            return Pass(nameof(DuplicateEngineHostIsRejected));
        }

        private static RecoveryIdentity Identity(
            string sessionId,
            string runId,
            string scope,
            long revision)
        {
            return new RecoveryIdentity
            {
                SessionId = sessionId,
                RunId = runId,
                RunEpoch = 1,
                IncidentId = RecoveryProtocolV7.NewId(),
                ResourceScope = scope,
                Generation = 1,
                Revision = revision
            };
        }

        private static RecoveryCommand Command(
            string sessionId,
            string runId,
            string owner,
            string incident,
            long sequence,
            RecoveryCommandKind kind)
        {
            var identity = Identity(sessionId, runId, "System", sequence);
            identity.IncidentId = incident;
            var command = new RecoveryCommand
            {
                Identity = identity,
                OwnerId = owner,
                CommandId = RecoveryProtocolV7.NewId(),
                CommandSequence = sequence,
                Kind = kind,
                TargetResource = "System",
                DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks
            };
            command.IdempotencyKey = RecoveryProtocolV7.ComputeIdempotencyKey(
                identity, kind, sequence);
            return command;
        }

        private static EngineHostResponse Send(EngineHostRequestKind kind)
        {
            return Send(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = kind
            });
        }

        private static EngineHostResponse Send(RecoveryCommand command)
        {
            return Send(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ExecuteRecoveryCommand,
                RecoveryCommand = command
            });
        }

        private static EngineHostResponse Send(OperatorCommand command)
        {
            return Send(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ExecuteOperatorCommand,
                OperatorCommand = command
            });
        }

        private static EngineHostResponse Send(EngineHostRequest request)
        {
            using (var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut,
                PipeOptions.None))
            {
                pipe.Connect(3000);
                using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                {
                    var payload = Encoding.UTF8.GetBytes(Json.Serialize(request));
                    writer.Write(payload.Length);
                    writer.Write(payload);
                    writer.Flush();
                    var length = reader.ReadInt32();
                    Assert(length > 0 && length <= EngineHostProtocol.MaximumRequestBytes,
                        "EngineResponseLengthInvalid");
                    return Json.Deserialize<EngineHostResponse>(
                        Encoding.UTF8.GetString(reader.ReadBytes(length)));
                }
            }
        }

        private static void WaitUntilReady(Process process)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            Exception last = null;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new InvalidOperationException(
                        "EngineHostExited:" + process.ExitCode);
                try
                {
                    var response = Send(EngineHostRequestKind.Ping);
                    if (response.Accepted && response.Snapshot?.HardwareInitialized == true)
                        return;
                }
                catch (Exception ex) { last = ex; }
                Thread.Sleep(100);
            }
            throw new TimeoutException("EngineHostNotReady", last);
        }

        private static int Pass(string name)
        {
            Console.WriteLine("PASS " + name);
            return 1;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
