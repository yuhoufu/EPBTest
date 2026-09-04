using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed class EngineProjectSelection
    {
        public long Revision { get; set; }
        public RecoveryIdentity ActiveIdentity { get; set; }
        public string ConfigurationPath { get; set; }
        public bool RunScopedCheckpoint { get; set; }
        public string ActivatedByCommandId { get; set; }
        public EngineProjectActivation Activation { get; set; }
        public ProjectSwitchPlan Pending { get; set; }
        public string PendingDocumentSha256 { get; set; }
        public bool ResetPublicationStarted { get; set; }
        public string LastResetArchivePath { get; set; }

        internal bool Matches(RecoveryIdentity identity) => identity?.IsStructurallyValid() == true &&
            ActiveIdentity?.IsStructurallyValid() == true && ActiveIdentity.SessionId == identity.SessionId &&
            ActiveIdentity.RunId == identity.RunId && ActiveIdentity.RunEpoch == identity.RunEpoch;

        internal bool IsValid() => Revision > 0 && ActiveIdentity?.IsStructurallyValid() == true &&
            ProjectSwitchPlan.IsProjectConfigurationPath(ConfigurationPath) &&
            RunScopedCheckpoint == !string.IsNullOrEmpty(ActivatedByCommandId) &&
            (string.IsNullOrEmpty(ActivatedByCommandId) || RecoveryProtocolV7.IsGuid(ActivatedByCommandId)) &&
            (Activation == null || Activation.IsStructurallyValid() && Activation.OperatorCommandId == ActivatedByCommandId) &&
            (!ResetPublicationStarted || Pending?.Reset != null) &&
            (Pending == null ? string.IsNullOrEmpty(PendingDocumentSha256) : Pending.IsStructurallyValid() &&
                Matches(Pending.SourceIdentity) && ProjectSwitchPlan.IsSha256(PendingDocumentSha256));
    }

    internal sealed class EngineProjectSwitchBoundary
    {
        internal bool ExecutorQuiescent { get; set; }
        internal bool NativeResourcesReleased { get; set; }
        internal bool WritersClosed { get; set; }
        internal bool CheckpointClosed { get; set; }
        internal bool IsClosed => ExecutorQuiescent && NativeResourcesReleased && WritersClosed && CheckpointClosed;
    }

    // EngineHost is the only writer. Selection is not derived from a UI preference
    // or the mutable default template. Preparing never changes the active project.
    internal sealed partial class EngineProjectSelectionStore
    {
        internal const int MaximumProjectBytes = 1024 * 1024;
        private const int MaximumEnvelopeBytes = 4 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MTTFTest.ProjectSelection.V1");
        private static JavaScriptSerializer Json => new JavaScriptSerializer { MaxJsonLength = MaximumEnvelopeBytes };
        private readonly string _root;
        private readonly string _path;

        private sealed class Envelope
        {
            public int SchemaVersion { get; set; }
            public string Kind { get; set; }
            public string PayloadSha256 { get; set; }
            public string PayloadBase64 { get; set; }
        }

        private sealed class Preparation
        {
            public ProjectSwitchPlan Plan { get; set; }
            public string SourceConfigurationPath { get; set; }
            public string SourceXmlBase64 { get; set; }
            public string TargetXmlBase64 { get; set; }
            public ResetConfigurationFile[] ResetConfigurationFiles { get; set; }
        }

        internal EngineProjectSelectionStore(string isolatedRoot = null)
        {
            _root = Path.GetFullPath(isolatedRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTest", "RecoveryKernel", "project-selection"));
            _path = Path.Combine(_root, "active-project.v1.json");
        }

        internal EngineProjectSelection ReadActive(RecoveryIdentity identity)
        {
            if (identity?.IsStructurallyValid() != true) throw new ArgumentException("ProjectSelectionIdentityInvalid");
            var current = ReadSelection();
            if (current?.ResetPublicationStarted == true) throw new InvalidOperationException("ResetPublicationPending;OldRunLoadForbidden");
            if (current != null && !current.Matches(identity))
                throw new InvalidDataException("ProjectSelectionRunMismatch;ExplicitActivationRequired");
            if (current != null) ValidateProject(current.ConfigurationPath, ReadBytes(current.ConfigurationPath, MaximumProjectBytes));
            return current;
        }

        internal EngineProjectSelection BindInitial(RecoveryIdentity identity, string configurationPath)
        {
            if (identity?.IsStructurallyValid() != true || !ProjectSwitchPlan.IsProjectConfigurationPath(configurationPath))
                throw new ArgumentException("ProjectSelectionInitialBindingInvalid");
            using (AcquireWriter())
            {
                var current = ReadSelection();
                if (current != null)
                {
                    if (current.ResetPublicationStarted) throw new InvalidOperationException("ResetPublicationPending;OldRunBindForbidden");
                    if (!current.Matches(identity) || !SamePath(current.ConfigurationPath, configurationPath))
                        throw new InvalidOperationException("ProjectSelectionRequiresExplicitSwitch");
                    return current;
                }
                ValidateProject(configurationPath, ReadBytes(configurationPath, MaximumProjectBytes));
                current = new EngineProjectSelection { Revision = 1, ActiveIdentity = identity.Clone(),
                    ConfigurationPath = configurationPath, RunScopedCheckpoint = false };
                Write(_path, "Selection", current);
                return current;
            }
        }

        internal string Prepare(ProjectSwitchPlan plan, EngineProjectSwitchBoundary boundary, Action<string> crashPoint = null,
            CancellationToken token = default)
        {
            if (plan?.IsStructurallyValid() != true) throw new ArgumentException("ProjectSwitchPlanInvalid");
            if (boundary?.IsClosed != true) throw new InvalidOperationException("ProjectSwitchRequiresClosedExecutorsWritersAndCheckpoint");
            using (AcquireWriter(token))
            {
                token.ThrowIfCancellationRequested();
                var current = ReadSelection();
                if (current == null || !current.Matches(plan.SourceIdentity))
                    throw new InvalidOperationException("ProjectSwitchSourceRetired");
                if (current.ResetPublicationStarted) throw new InvalidOperationException("ResetPublicationPending;OldRunPrepareForbidden");
                if (current.Pending != null)
                {
                    if (current.Pending.ComputeSha256() != plan.ComputeSha256())
                        throw new InvalidOperationException("ProjectSwitchPendingConflict");
                    ReadPreparation(plan, current.PendingDocumentSha256);
                    return current.PendingDocumentSha256;
                }
                if (current.ResetPublicationStarted || current.Revision != plan.BaseSelectionRevision ||
                    (plan.Reset == null ? SamePath(current.ConfigurationPath, plan.TargetConfigurationPath) : !SamePath(current.ConfigurationPath, plan.TargetConfigurationPath)))
                    throw new InvalidOperationException("ProjectSwitchSelectionRevisionOrTargetConflict");
                var source = ReadBytes(current.ConfigurationPath, MaximumProjectBytes);
                if (Sha(source) != plan.SourceProjectFileSha256)
                    throw new InvalidOperationException("ProjectSwitchFileChanged");
                var sourceConfig = ValidateProject(current.ConfigurationPath, source);
                if (plan.Reset != null) ValidateResetProjectRoot(Path.GetDirectoryName(Path.GetDirectoryName(current.ConfigurationPath)));
                token.ThrowIfCancellationRequested();
                var preparedPath = PreparationPath(plan.OperatorCommandId);
                Preparation preparation;
                string digest;
                if (File.Exists(preparedPath) || File.Exists(preparedPath + ".bak"))
                {
                    preparation = Read<Preparation>(preparedPath, "Preparation", out digest);
                    ReadPreparation(plan, digest);
                }
                else
                {
                    var target = plan.Reset != null ? BuildFreshProjectBytes(sourceConfig, plan.Reset.Configuration, plan, token) :
                        plan.Creation == null ? ReadBytes(plan.TargetConfigurationPath, MaximumProjectBytes) :
                        BuildNewProjectBytes(sourceConfig, plan, token);
                    if (plan.Creation == null && plan.Reset == null && Sha(target) != plan.TargetProjectFileSha256)
                        throw new InvalidOperationException("ProjectSwitchFileChanged");
                    ValidateProject(plan.TargetConfigurationPath, target);
                    preparation = new Preparation { Plan = plan.Clone(), SourceConfigurationPath = current.ConfigurationPath,
                        SourceXmlBase64 = Convert.ToBase64String(source), TargetXmlBase64 = Convert.ToBase64String(target),
                        ResetConfigurationFiles = plan.Reset == null ? null : CaptureResetConfiguration(current.ConfigurationPath, token) };
                    digest = Sha(Utf8.GetBytes(Json.Serialize(preparation)));
                    Write(preparedPath, "Preparation", preparation);
                }
                crashPoint?.Invoke("PreparedDurable");
                token.ThrowIfCancellationRequested();
                if (plan.Creation != null) PublishNewProject(preparation, digest, token, crashPoint);
                else if (Sha(ReadBytes(plan.TargetConfigurationPath, MaximumProjectBytes)) != plan.TargetProjectFileSha256)
                    throw new InvalidOperationException("ProjectSwitchFileChanged");
                current.Revision = checked(current.Revision + 1);
                current.Pending = plan.Clone(); current.PendingDocumentSha256 = digest;
                Write(_path, "Selection", current);
                crashPoint?.Invoke("PendingCommitted");
                return digest;
            }
        }

        // Called only for an exactly capability-authorized destination launch;
        // no method here creates a process, an Owner, a permit or a formal cycle.
        internal EngineProjectSelection Activate(RecoveryIdentity destination, string commandId, string preparationSha256,
            Action<string> crashPoint = null, string expectedPlanSha256 = null, CancellationToken token = default)
        {
            if (destination?.IsStructurallyValid() != true || !RecoveryProtocolV7.IsGuid(commandId) ||
                !ProjectSwitchPlan.IsSha256(preparationSha256)) throw new ArgumentException("ProjectActivationIdentityInvalid");
            using (AcquireWriter(token))
            {
                token.ThrowIfCancellationRequested();
                var current = ReadSelection() ?? throw new InvalidOperationException("ProjectSelectionMissing");
                if (current.Matches(destination) && current.ActivatedByCommandId == commandId && current.Pending == null)
                {
                    var activated = Read<Preparation>(PreparationPath(commandId), "Preparation", out var digest);
                    if (digest != preparationSha256 || activated?.Plan?.MatchesDestination(destination) != true ||
                        expectedPlanSha256 != null && expectedPlanSha256 != activated.Plan.ComputeSha256() ||
                        !SamePath(current.ConfigurationPath, activated.Plan.TargetConfigurationPath))
                        throw new InvalidDataException("ProjectActivationReplayBindingInvalid");
                    return current; // Never overwrite the destination's newer progress with a frozen snapshot.
                }
                var plan = current.Pending;
                if (plan == null || plan.OperatorCommandId != commandId || !plan.MatchesDestination(destination) ||
                    current.PendingDocumentSha256 != preparationSha256 || expectedPlanSha256 != null && expectedPlanSha256 != plan.ComputeSha256())
                    throw new InvalidOperationException("ProjectActivationPendingBindingInvalid");
                var prepared = ReadPreparation(plan, preparationSha256);
                if (plan.Reset != null)
                {
                    if (!current.ResetPublicationStarted)
                    {
                        // Commit the old-run load fence before the first project
                        // mutation. Only this exact destination activation can finish it.
                        current.ResetPublicationStarted = true;
                        current.Revision = checked(current.Revision + 1);
                        token.ThrowIfCancellationRequested(); Write(_path, "Selection", current);
                        crashPoint?.Invoke("ResetCommitStarted");
                    }
                    current.LastResetArchivePath = PublishResetProject(prepared, preparationSha256, token, crashPoint);
                }
                var target = ReadBytes(plan.TargetConfigurationPath, MaximumProjectBytes);
                if (Sha(target) != Sha(Convert.FromBase64String(prepared.TargetXmlBase64))) throw new InvalidOperationException("ProjectActivationTargetChanged");
                ValidateProject(plan.TargetConfigurationPath, target);
                current.ActiveIdentity = destination.Clone(); current.ConfigurationPath = plan.TargetConfigurationPath;
                if (plan.Reset == null) current.LastResetArchivePath = null;
                current.RunScopedCheckpoint = true; current.ActivatedByCommandId = commandId;
                current.Activation = new EngineProjectActivation { OperatorCommandId = commandId,
                    PreparedDocumentSha256 = preparationSha256, PlanSha256 = plan.ComputeSha256() };
                current.Pending = null; current.PendingDocumentSha256 = null;
                current.ResetPublicationStarted = false;
                current.Revision = checked(current.Revision + 1);
                token.ThrowIfCancellationRequested();
                Write(_path, "Selection", current);
                crashPoint?.Invoke("Activated");
                return current;
            }
        }

        internal ProjectSwitchPlan ReadActivatedPlan(EngineProjectSelection selection)
        {
            if (selection == null || string.IsNullOrEmpty(selection.ActivatedByCommandId)) return null;
            var prepared = Read<Preparation>(PreparationPath(selection.ActivatedByCommandId), "Preparation", out var digest);
            if (prepared?.Plan?.MatchesDestination(selection.ActiveIdentity) != true ||
                prepared.Plan.OperatorCommandId != selection.ActivatedByCommandId || !SamePath(selection.ConfigurationPath, prepared.Plan.TargetConfigurationPath) ||
                selection.Activation != null && (selection.Activation.PlanSha256 != prepared.Plan.ComputeSha256() ||
                    selection.Activation.PreparedDocumentSha256 != digest)) throw new InvalidDataException("ActivatedProjectPlanInvalid");
            ReadPreparation(prepared.Plan, digest);
            return prepared.Plan.Clone();
        }

        private EngineProjectSelection ReadSelection()
        {
            if (!File.Exists(_path))
            {
                if (File.Exists(_path + ".bak") || Directory.Exists(_root) &&
                    Directory.EnumerateFiles(_root, "prepared-*.v1.json*", SearchOption.TopDirectoryOnly).Any())
                    throw new InvalidDataException("ProjectSelectionPrimaryMissing;DefaultFallbackForbidden");
                return null;
            }
            var selection = Read<EngineProjectSelection>(_path, "Selection", out _);
            if (selection?.IsValid() != true) throw new InvalidDataException("ProjectSelectionCorrupt");
            return selection;
        }

        internal void AbortPending(RecoveryIdentity source, string commandId, string preparationSha256)
        {
            if (source?.IsStructurallyValid() != true || !RecoveryProtocolV7.IsGuid(commandId) ||
                !ProjectSwitchPlan.IsSha256(preparationSha256)) throw new ArgumentException("ProjectAbortIdentityInvalid");
            using (AcquireWriter())
            {
                var current = ReadSelection();
                if (current?.Matches(source) != true) throw new InvalidOperationException("ProjectAbortSourceRetired");
                if (current.ResetPublicationStarted) throw new InvalidOperationException("ResetPublicationCannotAbortToOldRun");
                if (current.Pending == null) return;
                if (current.Pending.OperatorCommandId != commandId || current.PendingDocumentSha256 != preparationSha256)
                    throw new InvalidOperationException("ProjectAbortPendingBindingInvalid");
                current.Pending = null; current.PendingDocumentSha256 = null;
                current.Revision = checked(current.Revision + 1);
                Write(_path, "Selection", current);
            }
        }

        // A lost Prepare receipt may leave a pending record whose digest the
        // Supervisor never received. Abort by the exact signed plan, not a new
        // command ID, guessed digest or a broad deletion of all preparations.
        internal void AbortPrepared(ProjectSwitchPlan plan, CancellationToken token)
        {
            if (plan?.IsStructurallyValid() != true) throw new ArgumentException("ProjectAbortPlanInvalid");
            using (AcquireWriter(token))
            {
                token.ThrowIfCancellationRequested();
                var current = ReadSelection();
                if (current?.Matches(plan.SourceIdentity) != true) throw new InvalidOperationException("ProjectAbortSourceRetired");
                if (current.ResetPublicationStarted) throw new InvalidOperationException("ResetPublicationCannotAbortToOldRun");
                if (current.Pending == null) return;
                if (current.Pending.ComputeSha256() != plan.ComputeSha256()) throw new InvalidOperationException("ProjectAbortPendingBindingInvalid");
                current.Pending = null; current.PendingDocumentSha256 = null;
                current.Revision = checked(current.Revision + 1);
                token.ThrowIfCancellationRequested();
                Write(_path, "Selection", current);
            }
        }

        private Preparation ReadPreparation(ProjectSwitchPlan plan, string digest)
        {
            var value = Read<Preparation>(PreparationPath(plan.OperatorCommandId), "Preparation", out var actualDigest);
            if (actualDigest != digest || value?.Plan?.IsStructurallyValid() != true || value.Plan.ComputeSha256() != plan.ComputeSha256() ||
                !ProjectSwitchPlan.IsProjectConfigurationPath(value.SourceConfigurationPath) ||
                Sha(Convert.FromBase64String(value.SourceXmlBase64)) != plan.SourceProjectFileSha256 ||
                (plan.Creation == null && plan.Reset == null && Sha(Convert.FromBase64String(value.TargetXmlBase64)) != plan.TargetProjectFileSha256))
                throw new InvalidDataException("ProjectPreparationBindingInvalid");
            if (plan.Reset != null) ValidateResetPreparation(value);
            return value;
        }

        private string PreparationPath(string commandId) => Path.Combine(_root, "prepared-" + commandId + ".v1.json");

        private FileStream AcquireWriter(CancellationToken token = default)
        {
            Directory.CreateDirectory(_root);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(10); }
            }
        }

        private T Read<T>(string path, string kind, out string digest)
        {
            var envelope = Json.Deserialize<Envelope>(Utf8.GetString(ReadBytes(path, MaximumEnvelopeBytes)));
            if (envelope?.SchemaVersion != 1 || envelope.Kind != kind || !ProjectSwitchPlan.IsSha256(envelope.PayloadSha256))
                throw new InvalidDataException("ProjectSelectionEnvelopeInvalid");
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(envelope.PayloadBase64), Entropy, DataProtectionScope.LocalMachine);
            digest = Sha(clear);
            if (clear.Length > MaximumEnvelopeBytes || digest != envelope.PayloadSha256)
                throw new InvalidDataException("ProjectSelectionDigestInvalid");
            return Json.Deserialize<T>(Utf8.GetString(clear));
        }

        private void Write<T>(string path, string kind, T value)
        {
            DurableJsonFileStore.WriteAtomicWithBackup(path, Encode(kind, value));
        }

        private byte[] Encode<T>(string kind, T value)
        {
            var clear = Utf8.GetBytes(Json.Serialize(value));
            var envelope = new Envelope { SchemaVersion = 1, Kind = kind, PayloadSha256 = Sha(clear),
                PayloadBase64 = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine)) };
            var bytes = Utf8.GetBytes(Json.Serialize(envelope));
            if (bytes.Length > MaximumEnvelopeBytes) throw new InvalidDataException("ProjectSelectionEnvelopeOversize");
            return bytes;
        }

        private TestConfig ValidateProject(string path, byte[] bytes)
        {
            if (!ProjectSwitchPlan.IsProjectConfigurationPath(path)) throw new InvalidDataException("ProjectConfigurationPathInvalid");
            var doc = new XmlDocument { XmlResolver = null };
            using (var input = new MemoryStream(bytes, false))
            using (var reader = XmlReader.Create(input,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumProjectBytes }))
                doc.Load(reader);
            if (doc.DocumentElement?.Name != "TestConfig") throw new InvalidDataException("ProjectConfigurationRootInvalid");
            var records = doc.SelectNodes("/TestConfig/EpbRecords/Record").Cast<XmlNode>().ToArray();
            if (records.Length != 12 || records.Select(node => int.TryParse(node.SelectSingleNode("Id")?.InnerText,
                    out var parsedChannel) ? parsedChannel : -1).Distinct().Count() != 12 ||
                records.Any(node => !int.TryParse(node.SelectSingleNode("Id")?.InnerText, out var channel) || channel < 1 || channel > 12 ||
                    !int.TryParse(node.SelectSingleNode("RunCount")?.InnerText, out var completed) || completed < 0 ||
                    !int.TryParse(node.SelectSingleNode("TotalCount")?.InnerText, out var total) || total < 0))
                throw new InvalidDataException("ProjectProgressRecordsIncomplete");
            var temporary = Path.Combine(_root, "validate-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                using (var writer = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    writer.Write(bytes, 0, bytes.Length);
                var config = ConfigLoader.LoadTest(temporary, null);
                if (!SamePath(ConfigLoader.GetProjectTestConfigPath(config.StoreDir, config.TestName), path) ||
                    !EngineTestConfigurationStore.Capture(config).IsStructurallyValid())
                    throw new InvalidDataException("ProjectConfigurationIdentityInvalid");
                return config;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static bool SamePath(string left, string right) =>
            string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

        internal static byte[] ReadBytes(string path, int limit)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length <= 0 || stream.Length > limit) throw new InvalidDataException("ProjectFileEmptyOrOversize");
                var bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("ProjectFileTruncated");
                    offset += read;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("ProjectFileChangedDuringRead");
                return bytes;
            }
        }

        internal static string Sha(byte[] bytes)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
