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
                UpdatedUtcTicks = UpdatedUtcTicks
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
                DeadlineUtcTicks = value.DeadlineUtcTicks
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
