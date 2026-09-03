using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Config;
using Controller;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed class EngineRunCheckpoint
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public long Revision { get; set; }
        public long ResourceGeneration { get; set; }
        public string ConfigurationSha256 { get; set; } = string.Empty;
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public int[] FormalCyclesCompleted { get; set; } = new int[12];
        public int[] RemainingFormalCycles { get; set; } = new int[12];
        public string[] IsolatedResources { get; set; } = Array.Empty<string>();
        public bool ActiveCycleInvalidated { get; set; }
        public int QualificationCyclesCompleted { get; set; }
        public int FormalCyclesSinceRecovery { get; set; }
        public long StableSinceUtcTicks { get; set; }
        public string State { get; set; } = "SafeIdle";
        public string LastReason { get; set; } = string.Empty;
        public long UpdatedUtcTicks { get; set; }

        public bool IsValidFor(RecoveryIdentity identity)
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   identity?.IsStructurallyValid() == true &&
                   string.Equals(SessionId, identity.SessionId, StringComparison.Ordinal) &&
                   string.Equals(RunId, identity.RunId, StringComparison.Ordinal) &&
                   RunEpoch == identity.RunEpoch && Revision > 0 &&
                   ResourceGeneration > 0 &&
                   IsSha256(ConfigurationSha256) &&
                   SelectedChannels != null &&
                   SelectedChannels.All(channel => channel >= 1 && channel <= 12) &&
                   SelectedChannels.Distinct().Count() == SelectedChannels.Length &&
                   FormalCyclesCompleted?.Length == 12 &&
                   RemainingFormalCycles?.Length == 12 &&
                   FormalCyclesCompleted.All(value => value >= 0) &&
                   RemainingFormalCycles.All(value => value >= 0) &&
                   QualificationCyclesCompleted >= 0 &&
                   FormalCyclesSinceRecovery >= 0 && UpdatedUtcTicks > 0;
        }

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Length == 64 &&
                   value.All(character =>
                       (character >= '0' && character <= '9') ||
                       (character >= 'a' && character <= 'f') ||
                       (character >= 'A' && character <= 'F'));
        }
    }

    /// <summary>
    /// EngineHost is the sole writer. The checkpoint deliberately migrates
    /// only work/count facts; V2 permits, nonces, owners and recovery stages
    /// are never copied into the V3 authority domain.
    /// </summary>
    internal sealed class EngineRunCheckpointStore
    {
        private sealed class Envelope
        {
            public int SchemaVersion { get; set; }
            public string Protection { get; set; } = string.Empty;
            public string PayloadBase64 { get; set; } = string.Empty;
        }

        private sealed class LegacyCheckpoint
        {
            public bool Armed { get; set; }
            public bool GracefulPaused { get; set; }
            public int[] SelectedChannels { get; set; } = Array.Empty<int>();
            public Dictionary<string, int> RemainingFormalCycles { get; set; } =
                new Dictionary<string, int>();
        }

        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly byte[] Entropy = Utf8.GetBytes(
            "MTTFTest.EngineRunCheckpoint.Schema7");
        private readonly object _gate = new object();
        private readonly string _path;

        internal EngineRunCheckpointStore(string sessionId)
        {
            if (!RecoveryProtocolV7.IsGuid(sessionId))
                throw new ArgumentException("EngineCheckpointSessionInvalid");
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTest", "RecoveryKernel", "engine-checkpoints");
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, "run-" + sessionId + ".v7.json");
        }

        internal EngineRunCheckpoint LoadOrCreate(
            RecoveryIdentity identity,
            TestConfig config,
            string configurationSha256)
        {
            lock (_gate)
            {
                var existing = Read();
                if (existing != null)
                {
                    if (!existing.IsValidFor(identity))
                        throw new InvalidDataException(
                            "EngineCheckpointIdentityMismatch");
                    if (!string.Equals(existing.ConfigurationSha256,
                            configurationSha256, StringComparison.Ordinal))
                        throw new InvalidDataException(
                            "EngineCheckpointConfigurationChanged");
                    if (string.Equals(existing.State, "Running",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.State, "QualifiedFormalStarted",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Revision++;
                        existing.ResourceGeneration = Math.Max(
                            existing.ResourceGeneration, identity.Generation);
                        existing.ActiveCycleInvalidated = true;
                        existing.QualificationCyclesCompleted = 0;
                        existing.FormalCyclesSinceRecovery = 0;
                        existing.StableSinceUtcTicks = 0;
                        existing.State = "SafeIdle";
                        existing.LastReason =
                            "PreviousEngineExitedActiveCycleInvalidated";
                        existing.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                        Write(existing);
                    }
                    return Clone(existing);
                }

                var selected = (config?.EnsureEpbRecords() ??
                                throw new InvalidDataException("EngineTestConfigMissing"))
                    .Snapshot()
                    .Where(record => record.Enabled && !record.PermanentAlarmLatched)
                    .Select(record => record.Id)
                    .Where(channel => channel >= 1 && channel <= 12)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                var remaining = new int[12];
                var completed = new int[12];
                foreach (var channel in Enumerable.Range(1, 12))
                {
                    var record = config.GetEpbRecord(channel);
                    completed[channel - 1] = (int)Math.Min(
                        int.MaxValue, Math.Max(0L, record.EffectiveMechanicalCycleCount));
                    remaining[channel - 1] = Math.Max(
                        0, record.GetRemainingMechanicalCycles(config.TestTarget));
                }
                ApplyLegacyWorkMigration(selected, remaining);
                selected = selected
                    .Where(channel => remaining[channel - 1] > 0)
                    .ToArray();
                var created = new EngineRunCheckpoint
                {
                    SessionId = identity.SessionId,
                    RunId = identity.RunId,
                    RunEpoch = identity.RunEpoch,
                    Revision = 1,
                    ResourceGeneration = Math.Max(1, identity.Generation),
                    ConfigurationSha256 = configurationSha256,
                    SelectedChannels = selected,
                    FormalCyclesCompleted = completed,
                    RemainingFormalCycles = remaining,
                    ActiveCycleInvalidated = true,
                    State = "Preflight",
                    LastReason = "V3CheckpointCommittedBeforeEnergization",
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                Write(created);
                return Clone(created);
            }
        }

        internal EngineRunCheckpoint Snapshot()
        {
            lock (_gate) return Clone(Read());
        }

        internal EngineRunCheckpoint Update(
            RecoveryIdentity identity,
            Func<EngineRunCheckpoint, bool> mutation,
            string reason)
        {
            lock (_gate)
            {
                var value = Read();
                if (value?.IsValidFor(identity) != true)
                    throw new InvalidDataException("EngineCheckpointUnavailable");
                if (mutation != null && !mutation(value)) return Clone(value);
                value.Revision++;
                value.ResourceGeneration = Math.Max(
                    value.ResourceGeneration, identity.Generation);
                value.LastReason = reason ?? string.Empty;
                value.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                Write(value);
                return Clone(value);
            }
        }

        internal EngineRunCheckpoint RefreshFormalProgress(
            RecoveryIdentity identity,
            TestConfig config)
        {
            return Update(identity, value =>
            {
                var changed = false;
                foreach (var channel in value.SelectedChannels ?? Array.Empty<int>())
                {
                    var record = config.GetEpbRecord(channel);
                    var completed = (int)Math.Min(
                        int.MaxValue, Math.Max(0L, record.EffectiveMechanicalCycleCount));
                    var remaining = Math.Max(
                        0, record.GetRemainingMechanicalCycles(config.TestTarget));
                    if (completed != value.FormalCyclesCompleted[channel - 1] ||
                        remaining != value.RemainingFormalCycles[channel - 1])
                    {
                        value.FormalCyclesSinceRecovery += Math.Max(
                            0, completed - value.FormalCyclesCompleted[channel - 1]);
                        value.FormalCyclesCompleted[channel - 1] = completed;
                        value.RemainingFormalCycles[channel - 1] = remaining;
                        changed = true;
                    }
                }
                return changed;
            }, "FormalProgressCommitted");
        }

        private void ApplyLegacyWorkMigration(int[] configured, int[] remaining)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MTTFTest", "unattended-run-checkpoint.json");
                if (!File.Exists(path)) return;
                var legacy = Json.Deserialize<LegacyCheckpoint>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (legacy == null || (!legacy.Armed && !legacy.GracefulPaused)) return;
                var allowed = new HashSet<int>(configured);
                foreach (var channel in legacy.SelectedChannels ?? Array.Empty<int>())
                {
                    if (!allowed.Contains(channel) || channel < 1 || channel > 12) continue;
                    int migrated;
                    if (legacy.RemainingFormalCycles != null &&
                        legacy.RemainingFormalCycles.TryGetValue(
                            channel.ToString(), out migrated) && migrated >= 0)
                        remaining[channel - 1] = Math.Min(
                            remaining[channel - 1], migrated);
                }
            }
            catch
            {
                // Legacy state is evidence only. A malformed V2 file can never
                // become V3 authority or prevent a fresh safe preflight.
            }
        }

        private EngineRunCheckpoint Read()
        {
            if (!File.Exists(_path)) return null;
            var envelope = Json.Deserialize<Envelope>(
                File.ReadAllText(_path, Encoding.UTF8));
            if (envelope == null ||
                envelope.SchemaVersion != RecoveryProtocolV7.SchemaVersion ||
                !string.Equals(envelope.Protection, "DPAPI-LocalMachine",
                    StringComparison.Ordinal))
                throw new InvalidDataException("EngineCheckpointEnvelopeInvalid");
            var clear = ProtectedData.Unprotect(
                Convert.FromBase64String(envelope.PayloadBase64),
                Entropy,
                DataProtectionScope.LocalMachine);
            return Json.Deserialize<EngineRunCheckpoint>(Utf8.GetString(clear));
        }

        private void Write(EngineRunCheckpoint value)
        {
            var envelope = new Envelope
            {
                SchemaVersion = RecoveryProtocolV7.SchemaVersion,
                Protection = "DPAPI-LocalMachine",
                PayloadBase64 = Convert.ToBase64String(ProtectedData.Protect(
                    Utf8.GetBytes(Json.Serialize(value)),
                    Entropy,
                    DataProtectionScope.LocalMachine))
            };
            DurableJsonFileStore.WriteAtomicWithBackup(
                _path, Utf8.GetBytes(Json.Serialize(envelope)));
        }

        private static EngineRunCheckpoint Clone(EngineRunCheckpoint value)
        {
            return value == null
                ? null
                : Json.Deserialize<EngineRunCheckpoint>(Json.Serialize(value));
        }
    }
}
