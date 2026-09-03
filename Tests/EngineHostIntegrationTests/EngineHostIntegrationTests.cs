using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

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
            using (var engine = StartEngine(executable, sessionId, runId))
            {
                Assert(engine != null, "EngineHostStartFailed");
                try
                {
                    WaitUntilReady(engine);
                    passed += SnapshotCarriesBootstrapIdentity(sessionId, runId);
                    passed += PulseAndLatestOnlyTelemetryAdvance();
                    passed += RecoveryCommandIsIdempotent(sessionId, runId);
                    passed += RecoveryCommandStartSequenceRuns(sessionId, runId);
                    passed += StartCannotBypassKernelGate(sessionId, runId);
                    passed += DuplicateEngineHostIsRejected(executable, sessionId, runId);
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
                    passed += DurableReceiptSurvivesEngineReplacement();
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
            var first = Send(EngineHostRequestKind.ReadLatestTelemetry).Telemetry;
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
            _durableCommand = command;
            _durableReceipt = first;
            return Pass(nameof(RecoveryCommandIsIdempotent));
        }

        private static int DurableReceiptSurvivesEngineReplacement()
        {
            Assert(_durableCommand != null && _durableReceipt != null,
                "DurableReceiptFixtureMissing");
            var replay = Send(_durableCommand).RecoveryReceipt;
            Assert(replay != null, "DurableReplayReceiptMissing");
            Assert(replay.CommandId == _durableReceipt.CommandId &&
                   replay.CompletedUtcTicks == _durableReceipt.CompletedUtcTicks,
                "EngineReplacementReexecutedCompletedCommand");
            return Pass(nameof(DurableReceiptSurvivesEngineReplacement));
        }

        private static int RecoveryCommandStartSequenceRuns(
            string sessionId,
            string runId)
        {
            var owner = RecoveryProtocolV7.NewId();
            var incident = RecoveryProtocolV7.NewId();
            var preflight = Command(sessionId, runId, owner, incident,
                2, RecoveryCommandKind.RunPassivePreflight);
            var preflightReceipt = Send(preflight).RecoveryReceipt;
            Assert(preflightReceipt?.Succeeded == true,
                "SimulatedPreflightFailed");
            var qualification = Command(sessionId, runId, owner, incident,
                3, RecoveryCommandKind.RunQualificationCycle);
            var qualificationReceipt = Send(qualification).RecoveryReceipt;
            Assert(qualificationReceipt?.Succeeded == true &&
                   qualificationReceipt.QualificationCyclesCompleted == 2,
                "QualificationReceiptMissingTwoCycles");
            var resume = Command(sessionId, runId, owner, incident,
                4, RecoveryCommandKind.ResumeFormalRun);
            Assert(Send(resume).RecoveryReceipt?.Succeeded == true,
                "FormalResumeFailed");
            var snapshot = Send(EngineHostRequestKind.ReadLatestSnapshot).Snapshot;
            Assert(snapshot.State == SystemTerminalState.Running,
                "EngineSnapshotDidNotEnterRunning");
            return Pass(nameof(RecoveryCommandStartSequenceRuns));
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
            using (var duplicate = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--simulation --session " + sessionId +
                            " --run " + runId + " --epoch 1",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            }))
            {
                Assert(duplicate != null && duplicate.WaitForExit(5000),
                    "DuplicateEngineHostDidNotExit");
                Assert(duplicate.ExitCode == 3, "DuplicateEngineHostWasNotRejected");
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
