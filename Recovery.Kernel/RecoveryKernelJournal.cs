using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    public sealed class RecoveryBudgetState
    {
        public string ResourceScope { get; set; } = string.Empty;
        public int LocalRebuildAttempts { get; set; }
        public int CurrentEngineReplacementAttempts { get; set; }
        public int LastKnownGoodAttempts { get; set; }
        public int ActiveQualificationAttempts { get; set; }
        public long LastFailureUtcTicks { get; set; }
        public long StableSinceUtcTicks { get; set; }

        public RecoveryBudgetState Clone()
        {
            return (RecoveryBudgetState)MemberwiseClone();
        }
    }

    public sealed class RecoveryKernelJournalDocument
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string ArchitectureGeneration { get; set; } =
            RecoveryProtocolV7.ArchitectureGeneration;
        public long Revision { get; set; }
        public SystemDesiredState DesiredState { get; set; }
        public RecoveryIntent[] ActiveIntents { get; set; } = Array.Empty<RecoveryIntent>();
        public RecoveryBudgetState[] Budgets { get; set; } = Array.Empty<RecoveryBudgetState>();
        public RecoveryCommand PendingCommand { get; set; }
        public string[] CompletedIdempotencyKeys { get; set; } = Array.Empty<string>();
        public string[] IsolatedResources { get; set; } = Array.Empty<string>();
        public long UpdatedUtcTicks { get; set; }
        public OperatorCommandAdmission[] OperatorAdmissions { get; set; } = Array.Empty<OperatorCommandAdmission>();
        public long OperatorReplayFloorUtcTicks { get; set; }

        public RecoveryKernelJournalDocument Clone()
        {
            return new RecoveryKernelJournalDocument
            {
                SchemaVersion = SchemaVersion,
                ArchitectureGeneration = ArchitectureGeneration,
                Revision = Revision,
                DesiredState = DesiredState == null
                    ? null
                    : new SystemDesiredState
                    {
                        SchemaVersion = DesiredState.SchemaVersion,
                        SessionId = DesiredState.SessionId,
                        RunId = DesiredState.RunId,
                        RunEpoch = DesiredState.RunEpoch,
                        Revision = DesiredState.Revision,
                        State = DesiredState.State,
                        Reason = DesiredState.Reason,
                        UpdatedUtcTicks = DesiredState.UpdatedUtcTicks
                    },
                ActiveIntents = (ActiveIntents ?? Array.Empty<RecoveryIntent>())
                    .Select(CloneIntent).ToArray(),
                Budgets = (Budgets ?? Array.Empty<RecoveryBudgetState>())
                    .Select(item => item.Clone()).ToArray(),
                PendingCommand = CloneCommand(PendingCommand),
                CompletedIdempotencyKeys =
                    (CompletedIdempotencyKeys ?? Array.Empty<string>()).ToArray(),
                IsolatedResources = (IsolatedResources ?? Array.Empty<string>()).ToArray(),
                UpdatedUtcTicks = UpdatedUtcTicks,
                OperatorAdmissions = (OperatorAdmissions ?? Array.Empty<OperatorCommandAdmission>()).Select(value => value.Clone()).ToArray(),
                OperatorReplayFloorUtcTicks = OperatorReplayFloorUtcTicks
            };
        }

        internal static RecoveryIntent CloneIntent(RecoveryIntent value)
        {
            if (value == null) return null;
            return new RecoveryIntent
            {
                SchemaVersion = value.SchemaVersion,
                Identity = value.Identity?.Clone(),
                OwnerId = value.OwnerId,
                Stage = value.Stage,
                DesiredTerminalState = value.DesiredTerminalState,
                HardwareConfirmed = value.HardwareConfirmed,
                AutomaticReplayForbidden = value.AutomaticReplayForbidden,
                LocalRebuildAttempts = value.LocalRebuildAttempts,
                CurrentEngineReplacementAttempts = value.CurrentEngineReplacementAttempts,
                LastKnownGoodAttempts = value.LastKnownGoodAttempts,
                ActiveQualificationAttempts = value.ActiveQualificationAttempts,
                IsOperatorStart = value.IsOperatorStart,
                RequiresEngineReplacement = value.RequiresEngineReplacement,
                CommandAfterSafetyProof = value.CommandAfterSafetyProof,
                ManualBatchEngineInstanceId = value.ManualBatchEngineInstanceId,
                ManualPausedChannelsMask = value.ManualPausedChannelsMask,
                ManualRunningChannelsMask = value.ManualRunningChannelsMask,
                OperatorTransaction = value.OperatorTransaction?.Clone(),
                ProjectSwitch = value.ProjectSwitch?.Clone(),
                ProjectSwitchPreparedSha256 = value.ProjectSwitchPreparedSha256,
                ProjectSwitchFailure = value.ProjectSwitchFailure,
                ProjectSwitchStopRequested = value.ProjectSwitchStopRequested,
                PressureMaintenance = value.PressureMaintenance?.Clone(),
                CreatedUtcTicks = value.CreatedUtcTicks,
                UpdatedUtcTicks = value.UpdatedUtcTicks
            };
        }

        internal static RecoveryCommand CloneCommand(RecoveryCommand value)
        {
            if (value == null) return null;
            return new RecoveryCommand
            {
                SchemaVersion = value.SchemaVersion,
                Identity = value.Identity?.Clone(),
                OwnerId = value.OwnerId,
                CommandId = value.CommandId,
                CommandSequence = value.CommandSequence,
                IdempotencyKey = value.IdempotencyKey,
                Kind = value.Kind,
                TargetResource = value.TargetResource,
                OperatorTransaction = value.OperatorTransaction?.Clone(),
                ProjectSwitch = value.ProjectSwitch?.Clone(),
                ProjectSwitchPreparedSha256 = value.ProjectSwitchPreparedSha256,
                DeadlineUtcTicks = value.DeadlineUtcTicks,
                PressureMaintenance = value.PressureMaintenance?.Clone()
            };
        }
    }

    public sealed class RecoveryJournalCommitResult
    {
        public bool Committed { get; set; }
        public bool Conflict { get; set; }
        public string Reason { get; set; } = string.Empty;
        public RecoveryKernelJournalDocument Document { get; set; }
    }

    public interface IRecoveryKernelJournal
    {
        RecoveryKernelJournalDocument Load();
        RecoveryJournalCommitResult CompareExchange(
            long expectedRevision,
            RecoveryKernelJournalDocument candidate);
    }

    public sealed class FileRecoveryKernelJournal : IRecoveryKernelJournal
    {
        private sealed class Envelope
        {
            public int SchemaVersion { get; set; }
            public bool Protected { get; set; }
            public string PayloadBase64 { get; set; }
            public string PayloadSha256 { get; set; }
        }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "MTTFTest.RecoveryKernel.Schema7");
        private readonly string _path;
        private readonly bool _protect;
        private readonly string _mutexName;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue
        };

        public FileRecoveryKernelJournal(string directory = null, bool protect = true)
        {
            var root = directory;
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "MTTFTest",
                    "RecoveryKernel");
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, "recovery-journal.v7.json");
            _protect = protect;
            _mutexName = "Global\\MTTFTest.RecoveryKernel." + ShortHash(root);
        }

        public RecoveryKernelJournalDocument Load()
        {
            using (var mutex = new Mutex(false, _mutexName))
            {
                Enter(mutex);
                try { return LoadUnlocked(); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        public RecoveryJournalCommitResult CompareExchange(
            long expectedRevision,
            RecoveryKernelJournalDocument candidate)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));
            using (var mutex = new Mutex(false, _mutexName))
            {
                Enter(mutex);
                try
                {
                    var current = LoadUnlocked();
                    if (current.Revision != expectedRevision)
                        return new RecoveryJournalCommitResult
                        {
                            Conflict = true,
                            Reason = "JournalRevisionConflict",
                            Document = current
                        };
                    var normalized = candidate.Clone();
                    normalized.SchemaVersion = RecoveryProtocolV7.SchemaVersion;
                    normalized.ArchitectureGeneration = RecoveryProtocolV7.ArchitectureGeneration;
                    normalized.Revision = expectedRevision + 1;
                    normalized.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    Validate(normalized);
                    WriteUnlocked(normalized);
                    return new RecoveryJournalCommitResult
                    {
                        Committed = true,
                        Reason = "JournalCommitted",
                        Document = normalized.Clone()
                    };
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private RecoveryKernelJournalDocument LoadUnlocked()
        {
            if (!File.Exists(_path))
                return new RecoveryKernelJournalDocument
                {
                    Revision = 0,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
            var envelope = _json.Deserialize<Envelope>(
                File.ReadAllText(_path, Encoding.UTF8));
            if (envelope == null || envelope.SchemaVersion != RecoveryProtocolV7.SchemaVersion)
                throw new InvalidDataException("RecoveryJournalEnvelopeSchemaMismatch");
            var bytes = Convert.FromBase64String(envelope.PayloadBase64 ?? string.Empty);
            var clear = envelope.Protected
                ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine)
                : bytes;
            if (!string.Equals(Hash(clear), envelope.PayloadSha256, StringComparison.Ordinal))
                throw new InvalidDataException("RecoveryJournalChecksumMismatch");
            var document = _json.Deserialize<RecoveryKernelJournalDocument>(
                Encoding.UTF8.GetString(clear));
            Validate(document);
            return document.Clone();
        }

        private void WriteUnlocked(RecoveryKernelJournalDocument document)
        {
            var clear = Encoding.UTF8.GetBytes(_json.Serialize(document));
            var stored = _protect
                ? ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine)
                : clear;
            var envelope = new Envelope
            {
                SchemaVersion = RecoveryProtocolV7.SchemaVersion,
                Protected = _protect,
                PayloadBase64 = Convert.ToBase64String(stored),
                PayloadSha256 = Hash(clear)
            };
            var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            var backup = _path + ".bak";
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(_json.Serialize(envelope));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, backup, true);
                else File.Move(temporary, _path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        private static void Validate(RecoveryKernelJournalDocument document)
        {
            if (document == null ||
                document.SchemaVersion != RecoveryProtocolV7.SchemaVersion ||
                !string.Equals(
                    document.ArchitectureGeneration,
                    RecoveryProtocolV7.ArchitectureGeneration,
                    StringComparison.Ordinal) ||
                document.Revision < 0 || document.UpdatedUtcTicks <= 0)
                throw new InvalidDataException("RecoveryJournalInvalid");
            var intents = document.ActiveIntents ?? Array.Empty<RecoveryIntent>();
            if (intents.Any(item => item?.IsStructurallyValid() != true))
                throw new InvalidDataException("RecoveryJournalIntentInvalid");
            if (intents.GroupBy(item => item.Identity.IncidentId, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
                throw new InvalidDataException("RecoveryJournalDuplicateIncident");
            if (intents.GroupBy(item => item.Identity.ResourceScope, StringComparer.Ordinal)
                .Any(group => group.Select(item => item.OwnerId).Distinct().Count() != 1))
                throw new InvalidDataException("RecoveryJournalMultipleOwners");
            if (document.PendingCommand?.IsStructurallyValid() == false)
                throw new InvalidDataException("RecoveryJournalPendingCommandInvalid");
            if (document.DesiredState?.IsStructurallyValid() == false)
                throw new InvalidDataException("RecoveryJournalDesiredStateInvalid");
            var project = intents.FirstOrDefault(value => value.ProjectSwitch != null);
            if (project != null)
            {
                var command = document.PendingCommand;
                var desired = document.DesiredState;
                if (intents.Length != 1 || command?.ProjectSwitch == null || command.OwnerId != project.OwnerId ||
                    command.ProjectSwitch.ComputeSha256() != project.ProjectSwitch.ComputeSha256() ||
                    command.ProjectSwitchPreparedSha256 != project.ProjectSwitchPreparedSha256 ||
                    command.Identity.SessionId != project.Identity.SessionId || command.Identity.RunId != project.Identity.RunId ||
                    command.Identity.RunEpoch != project.Identity.RunEpoch || command.Identity.IncidentId != project.Identity.IncidentId ||
                    command.Identity.Generation != project.Identity.Generation || desired == null ||
                    desired.SessionId != project.Identity.SessionId || desired.RunId != project.Identity.RunId || desired.RunEpoch != project.Identity.RunEpoch ||
                    !(document.OperatorAdmissions ?? Array.Empty<OperatorCommandAdmission>()).Any(a => a.ProjectTransaction != null &&
                        !a.ExecutionCompleted && a.OwnerId == project.OwnerId && a.IncidentId == project.Identity.IncidentId && a.Matches(project.OperatorTransaction)))
                    throw new InvalidDataException("RecoveryJournalProjectTransactionBindingInvalid");
            }
            else if (document.PendingCommand?.ProjectSwitch != null)
                throw new InvalidDataException("RecoveryJournalProjectOwnerMissing");
            var admissions = document.OperatorAdmissions ?? Array.Empty<OperatorCommandAdmission>();
            var maintenance = intents.FirstOrDefault(i => i.PressureMaintenance != null);
            if (maintenance != null)
            {
                var pending = document.PendingCommand;
                if (intents.Length != 1 ||
                    (maintenance.Stage == RecoveryStage.PressureMaintenanceReady ? pending != null || maintenance.OperatorTransaction != null :
                        pending?.PressureMaintenance == null || pending.OwnerId != maintenance.OwnerId ||
                        !pending.PressureMaintenance.SameSession(maintenance.PressureMaintenance) ||
                        pending.PressureMaintenance.Revision > maintenance.PressureMaintenance.Revision ||
                        pending.PressureMaintenance.ExpiresUtcTicks > maintenance.PressureMaintenance.ExpiresUtcTicks))
                    throw new InvalidDataException("RecoveryJournalMaintenanceOwnerBindingInvalid");
            }
            else if (document.PendingCommand?.PressureMaintenance != null)
                throw new InvalidDataException("RecoveryJournalMaintenanceOwnerMissing");
            foreach (var operation in admissions.Where(a => a?.MaintenanceTransaction != null && !a.ExecutionCompleted))
                if (maintenance == null || operation.OwnerId != maintenance.OwnerId || operation.IncidentId != maintenance.Identity.IncidentId ||
                    (!operation.Matches(maintenance.OperatorTransaction) &&
                     !(maintenance.PressureMaintenance.Revoked && operation.MaintenanceTransaction.Kind == OperatorCommandKind.EndPressureMaintenance &&
                       operation.SessionId == maintenance.Identity.SessionId && operation.RunId == maintenance.Identity.RunId &&
                       operation.RunEpoch == maintenance.Identity.RunEpoch && operation.MaintenanceTransaction.PressureMaintenance?.Binds(
                           maintenance.PressureMaintenance, OperatorCommandKind.EndPressureMaintenance) == true)))
                    throw new InvalidDataException("RecoveryJournalMaintenanceOperationOrphaned");
            foreach (var configuration in admissions.Where(value => value?.ConfigurationTransaction != null && !value.ExecutionCompleted))
                if (!intents.Any(intent => intent.OwnerId == configuration.OwnerId && intent.Identity.IncidentId == configuration.IncidentId &&
                    configuration.Matches(intent.OperatorTransaction)))
                    throw new InvalidDataException("RecoveryJournalConfigurationOwnerMissing");
            if (admissions.Length > 1024 || document.OperatorReplayFloorUtcTicks < 0 ||
                admissions.Any(value => value?.IsStructurallyValid() != true) ||
                admissions.Count(value => value.PanelTransaction != null && !value.ExecutionCompleted) > 16 ||
                admissions.Select(value => value.CommandId).Distinct().Count() != admissions.Length)
                throw new InvalidDataException("RecoveryJournalOperatorAdmissionsInvalid");
        }

        private static void Enter(Mutex mutex)
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("RecoveryJournalMutexTimeout");
            }
            catch (AbandonedMutexException) { }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes ?? Array.Empty<byte>()))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
        }

        private static string ShortHash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)), 0, 12)
                    .Replace("-", string.Empty);
        }
    }
}
