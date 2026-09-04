using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Runs the independent SafetyAgent from the LocalSystem Supervisor and
    /// converts its hardware read-back into the schema-7 proof consumed by
    /// Recovery Kernel.  EngineHost facts are used only for the data/process
    /// boundary; they can never substitute for hardware read-back.
    /// </summary>
    internal sealed class V3SafetyProofExecutor
    {
        private readonly Action<string, string> _audit;
        private readonly string _stateDirectory;

        internal V3SafetyProofExecutor(Action<string, string> audit)
        {
            _audit = audit ?? ((eventType, detail) => { });
            _stateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTest", "RecoveryKernel", "safety-authority");
            Directory.CreateDirectory(_stateDirectory);
        }

        internal SafetyProof Execute(
            RecoveryCommand command,
            RecoveryCommandReceipt engineReceipt,
            TimeSpan timeout)
        {
            if (command?.IsStructurallyValid() != true)
                throw new ArgumentException("SafetyProofCommandInvalid", nameof(command));
            var captured = DateTime.UtcNow.Ticks;
            var proof = new SafetyProof
            {
                Identity = command.Identity.Clone(),
                ProofId = RecoveryProtocolV7.NewId(),
                SafetyAgentInstanceId = RecoveryProtocolV7.NewId(),
                CapturedUtcTicks = captured,
                ValidUntilUtcTicks = captured + TimeSpan.FromSeconds(30).Ticks
            };
            try
            {
                var boundary = V3SafetyHandoffBoundary.Capture(command, engineReceipt,
                    engineReceipt?.HardwareHandoff == null && V3EngineProcessReplacer.ProveNoLiveEngineHost());
                proof.DataBoundaryClosed = boundary.DataBoundaryClosed;
                proof.OldProcessIsolated = boundary.OldExecutionIsolated;
                var baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                var configDirectory = Path.Combine(baseDirectory, "Config");
                var mainExecutable = Path.Combine(baseDirectory, "MTTFTest.EngineHost.exe");
                var safetyExecutable = Path.Combine(baseDirectory, "MTTFTest.SafetyAgent.exe");
                if (!File.Exists(mainExecutable) || !File.Exists(safetyExecutable))
                    throw new FileNotFoundException("V3SafetyExecutableMissing");

                var handoffId = RecoveryProtocolV7.NewId();
                var incidentDirectory = Path.Combine(
                    _stateDirectory,
                    "incident-" + command.Identity.IncidentId,
                    "command-" + command.CommandId);
                Directory.CreateDirectory(incidentDirectory);
                var mainSha = SupervisorProtocol.ComputeSha256(mainExecutable);
                var safetySha = SupervisorProtocol.ComputeSha256(safetyExecutable);
                var runtime = ReadSafetyRuntime(configDirectory,
                    mainExecutable + ".config");
                var snapshot = WatchdogSafetyConfigSnapshotStore.Create(
                    incidentDirectory,
                    handoffId,
                    configDirectory,
                    configDirectory,
                    "EPB-RecoveryKernel-V3/3.0.0.0",
                    runtime,
                    command.Identity.SessionId,
                    Math.Max(1, command.Identity.Generation),
                    Math.Max(1, command.CommandSequence),
                    0,
                    string.Empty,
                    mainSha,
                    safetySha);
                if (snapshot?.Succeeded != true)
                    throw new InvalidDataException(
                        "SafetyConfigSnapshotFailed:" + snapshot?.Error);

                var receipt = new WatchdogSafetyHandoffReceipt
                {
                    SchemaVersion = SupervisorProtocol.SchemaVersion,
                    SessionId = command.Identity.SessionId,
                    SessionGeneration = Math.Max(1, command.Identity.Generation),
                    SessionLease = Math.Max(1, command.CommandSequence),
                    HandoffId = handoffId,
                    Nonce = RecoveryProtocolV7.NewId(),
                    StopSafetyTransactionId = command.Identity.IncidentId,
                    RunId = command.Identity.RunId,
                    RunEpoch = command.Identity.RunEpoch,
                    Revision = 1,
                    State = WatchdogSafetyHandoffState.Accepted,
                    Stage = WatchdogSafetyStage.None,
                    PersistenceDrained = boundary.DataBoundaryClosed,
                    LogicalQuiescent = boundary.LogicalQuiescent,
                    HardwareResourcesReleased = boundary.HardwareResourcesReleased,
                    ExecutionAuthorizationRevoked = boundary.ExecutionAuthorizationRevoked,
                    CallbacksIsolated = boundary.CallbacksIsolated,
                    DataAuditState = boundary.DataBoundaryClosed
                        ? WatchdogDataAuditState.Drained
                        : WatchdogDataAuditState.DataIncomplete,
                    ProjectDirectory = incidentDirectory,
                    MainExecutablePath = mainExecutable,
                    MainExecutableSha256 = mainSha,
                    SafetyAgentExecutablePath = safetyExecutable,
                    SafetyAgentExecutableSha256 = safetySha,
                    ConfigSnapshotPath = snapshot.ConfigDirectory,
                    ConfigSnapshotManifestPath = snapshot.ManifestPath,
                    ConfigSnapshotManifestSha256 = snapshot.ManifestSha256,
                    ConfigSnapshotSchemaVersion = 2,
                    RelaunchDisposition = WatchdogRelaunchDisposition.Forbidden,
                    Detail = "RecoveryKernelIndependentSafetyProofRequested",
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                var authority = SupervisorSafetyAuthorityStore.CreateOrRead(
                    _stateDirectory, incidentDirectory, receipt);
                var authorityPath = SupervisorSafetyAuthorityStore.GetPath(
                    _stateDirectory, authority.AuthorityId);
                var arguments = string.Join(" ", new[]
                {
                    "--session-id", Quote(receipt.SessionId),
                    "--handoff-id", Quote(receipt.HandoffId),
                    "--handoff-nonce", Quote(receipt.Nonce),
                    "--journal-directory", Quote(incidentDirectory),
                    "--authority-id", Quote(authority.AuthorityId),
                    "--authority-receipt", Quote(authorityPath),
                    "--authority-revision", authority.InitialReceiptRevision.ToString(CultureInfo.InvariantCulture),
                    "--authority-sha256", Quote(authority.InitialReceiptCanonicalSha256)
                });
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = safetyExecutable,
                    Arguments = arguments,
                    WorkingDirectory = baseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null)
                        throw new InvalidOperationException("SafetyAgentStartReturnedNull");
                    proof.SafetyAgentInstanceId = handoffId;
                    var deadline = DateTime.UtcNow + timeout;
                    SupervisorSafetyAuthorityRecord current = null;
                    string failure = string.Empty;
                    while (DateTime.UtcNow <= deadline)
                    {
                        if (SupervisorSafetyAuthorityStore.TryRead(
                                _stateDirectory, authority.AuthorityId,
                                out current, out failure) &&
                            current.Receipt.IsTerminal)
                            break;
                        Thread.Sleep(50);
                    }
                    if (current?.Receipt?.IsTerminal != true)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        throw new TimeoutException(
                            "SafetyAgentProofTimeout:" + failure);
                    }
                    // The terminal record can precede finally/DAQ disposal. Do
                    // not let a new EngineHost acquire the agent's live handles.
                    var remaining = (int)Math.Max(0, Math.Min(int.MaxValue, (deadline - DateTime.UtcNow).TotalMilliseconds));
                    if (!process.WaitForExit(remaining) || process.ExitCode != 0)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        throw new InvalidOperationException("SafetyAgentDidNotExitCleanlyAfterProof");
                    }
                    proof.OutputsOff = current.Receipt.MotorsOff &&
                                       current.Receipt.PowerOff;
                    proof.PressureSafe = current.Receipt.PressureSafe;
                    proof.SafetyChainHealthy = current.Receipt.IsSafetyCompleted;
                    proof.CapturedUtcTicks = DateTime.UtcNow.Ticks;
                    proof.ValidUntilUtcTicks =
                        proof.CapturedUtcTicks + TimeSpan.FromSeconds(30).Ticks;
                    _audit("IndependentSafetyProofCompleted",
                        "Incident=" + command.Identity.IncidentId +
                        ";Handoff=" + handoffId +
                        ";Complete=" + proof.IsComplete +
                        ";State=" + current.Receipt.State +
                        ";Failure=" + current.Receipt.FailureCode);
                }
            }
            catch (Exception ex)
            {
                proof.SafetyChainHealthy = false;
                proof.CapturedUtcTicks = DateTime.UtcNow.Ticks;
                proof.ValidUntilUtcTicks =
                    proof.CapturedUtcTicks + TimeSpan.FromSeconds(30).Ticks;
                _audit("IndependentSafetyProofFailed", ex.GetBaseException().Message);
            }
            return proof;
        }

        private static SafetyRuntimeSnapshot ReadSafetyRuntime(
            string configDirectory,
            string applicationConfigPath)
        {
            var document = XDocument.Load(
                Path.Combine(configDirectory, "TestConfig.xml"),
                LoadOptions.None);
            var hydraulics = document.Root?.Element("Hydraulics")?
                .Elements("Hydraulic")
                .Where(value => ReadBool(value, "Enabled", false))
                .OrderBy(value => ReadInt(value, "Id", 0))
                .ToArray() ?? Array.Empty<XElement>();
            if (hydraulics.Length == 0)
                throw new InvalidDataException("SafetyHydraulicConfigMissing");
            var settings = ReadAppSettings(applicationConfigPath);
            var runtime = new SafetyRuntimeSnapshot
            {
                SampleRateHz = ReadDouble(settings, "DaqFrequency", 2000),
                SamplesPerChannel = ReadInt(settings, "SamplesPerChannel", 20),
                PressureChannels = hydraulics.Select(value =>
                    "Pressure_" + ReadInt(value, "Id", 0)).ToArray(),
                ReleaseSafePressureBar = hydraulics.Select(value =>
                    ReadDouble(value, "ReleaseSafePressureBar", 0)).ToArray(),
                PressureSampleMaxAgeMs = hydraulics.Min(value =>
                    Math.Max(1, ReadInt(value, "PressureSampleMaxAgeMs", 100))),
                ReleaseStableMs = hydraulics.Max(value =>
                    Math.Max(0, ReadInt(value, "ReleaseStableMs", 100))),
                ReleaseTimeoutMs = hydraulics.Max(value =>
                    Math.Max(1000, ReadInt(value, "ReleaseTimeoutMs", 5000)))
            };
            runtime.Validate();
            return runtime;
        }

        private static Dictionary<string, string> ReadAppSettings(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            return XDocument.Load(path).Root?.Element("appSettings")?
                .Elements("add")
                .Where(value => !string.IsNullOrWhiteSpace((string)value.Attribute("key")))
                .GroupBy(value => (string)value.Attribute("key"),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => (string)group.Last().Attribute("value") ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static bool ReadBool(XElement parent, string name, bool fallback)
        {
            return bool.TryParse(((string)parent?.Element(name) ?? string.Empty).Trim(),
                out var value) ? value : fallback;
        }

        private static int ReadInt(XElement parent, string name, int fallback)
        {
            return int.TryParse(((string)parent?.Element(name) ?? string.Empty).Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;
        }

        private static double ReadDouble(
            XElement parent,
            string name,
            double fallback)
        {
            return double.TryParse(((string)parent?.Element(name) ?? string.Empty).Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value : fallback;
        }

        private static int ReadInt(
            IDictionary<string, string> values,
            string name,
            int fallback)
        {
            return values.TryGetValue(name, out var raw) &&
                   int.TryParse(raw, NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out var value)
                ? value : fallback;
        }

        private static double ReadDouble(
            IDictionary<string, string> values,
            string name,
            double fallback)
        {
            return values.TryGetValue(name, out var raw) &&
                   double.TryParse(raw, NumberStyles.Float,
                       CultureInfo.InvariantCulture, out var value)
                ? value : fallback;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
