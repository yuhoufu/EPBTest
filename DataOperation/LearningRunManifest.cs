using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DataOperation
{
    /// <summary>Identity supplied by the recovery coordinator for one execution.</summary>
    [DataContract]
    public sealed class RunChainIdentity
    {
        public RunChainIdentity() { }

        public RunChainIdentity(Guid runId, Guid rootRunId = default, Guid parentRunId = default,
            int restartGeneration = 0, long runEpoch = 0)
        {
            RunId = runId;
            RootRunId = rootRunId;
            ParentRunId = parentRunId;
            RestartGeneration = Math.Max(0, restartGeneration);
            RunEpoch = runEpoch;
        }

        [DataMember(Order = 1)] public Guid RunId { get; set; }
        [DataMember(Order = 2)] public Guid RootRunId { get; set; }
        [DataMember(Order = 3)] public Guid ParentRunId { get; set; }
        [DataMember(Order = 4)] public int RestartGeneration { get; set; }
        [DataMember(Order = 5)] public long RunEpoch { get; set; }

        public Guid EffectiveRootRunId => RootRunId != Guid.Empty ? RootRunId : RunId;
        public Guid RunChainId => EffectiveRootRunId;

        public RunChainIdentity Normalize(Guid fallbackRunId)
        {
            var run = RunId == Guid.Empty ? fallbackRunId : RunId;
            var root = RootRunId == Guid.Empty ? run : RootRunId;
            return new RunChainIdentity(run, root, ParentRunId, RestartGeneration, RunEpoch);
        }
    }

    [DataContract]
    public sealed class LearningRunManifest
    {
        public const int CurrentSchema = 1;
        public const string FileName = "learning-run-manifest.json";

        [DataMember(Order = 1)] public int Schema { get; set; } = CurrentSchema;
        [DataMember(Order = 2)] public string AppVersion { get; set; } = string.Empty;
        [DataMember(Order = 3)] public string ConfigHash { get; set; } = string.Empty;
        [DataMember(Order = 4)] public string ChainRunId { get; set; } = string.Empty;
        [DataMember(Order = 5)] public string RootRunId { get; set; } = string.Empty;
        [DataMember(Order = 6)] public List<LearningRunExecution> Executions { get; set; } = new List<LearningRunExecution>();
        [DataMember(Order = 7)] public int[] PlannedChannels { get; set; } = Array.Empty<int>();
        [DataMember(Order = 8)] public int PlannedLearningCycles { get; set; }
        [DataMember(Order = 9)] public int PlannedQualificationCycles { get; set; }
        [DataMember(Order = 10)] public string FinalStatus { get; set; } = "Unknown";
        [DataMember(Order = 11)] public string Reason { get; set; } = string.Empty;
        [DataMember(Order = 12)] public DateTime StartedUtc { get; set; }
        [DataMember(Order = 13)] public DateTime CompletedUtc { get; set; }
        [DataMember(Order = 14)] public string ModelHash { get; set; } = string.Empty;
        [DataMember(Order = 15)] public string ModelCommitReceipt { get; set; } = string.Empty;
        [DataMember(Order = 16)] public string ManifestHash { get; set; } = string.Empty;
        [DataMember(Order = 17)] public long ManifestBytes { get; set; }
        [DataMember(Order = 18)] public List<string> ModelCommitReceipts { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class LearningRunExecution
    {
        [DataMember(Order = 1)] public string RunId { get; set; } = string.Empty;
        [DataMember(Order = 2)] public string RootRunId { get; set; } = string.Empty;
        [DataMember(Order = 3)] public string ParentRunId { get; set; } = string.Empty;
        [DataMember(Order = 4)] public long RunEpoch { get; set; }
        [DataMember(Order = 5)] public int RestartGeneration { get; set; }
        [DataMember(Order = 6)] public DateTime StartedUtc { get; set; }
        [DataMember(Order = 7)] public DateTime CompletedUtc { get; set; }
        [DataMember(Order = 8)] public string Status { get; set; } = "Unknown";
        [DataMember(Order = 9)] public string Reason { get; set; } = string.Empty;
        [DataMember(Order = 10)] public List<LearningArtifact> Artifacts { get; set; } = new List<LearningArtifact>();
        [DataMember(Order = 11)] public List<LearningAttemptState> Learning { get; set; } = new List<LearningAttemptState>();
        [DataMember(Order = 12)] public List<LearningAttemptState> Qualification { get; set; } = new List<LearningAttemptState>();
        [DataMember(Order = 13)] public string ModelHash { get; set; } = string.Empty;
        [DataMember(Order = 14)] public string ModelCommitReceipt { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class LearningAttemptState
    {
        [DataMember(Order = 1)] public string Channel { get; set; } = string.Empty;
        [DataMember(Order = 2)] public string LearningId { get; set; } = string.Empty;
        [DataMember(Order = 3)] public int Attempt { get; set; }
        [DataMember(Order = 4)] public string Status { get; set; } = "Unknown";
        [DataMember(Order = 5)] public int InternalCycle { get; set; }
        [DataMember(Order = 6)] public DateTime StartedUtc { get; set; }
        [DataMember(Order = 7)] public DateTime CompletedUtc { get; set; }
        [DataMember(Order = 8)] public string Reason { get; set; } = string.Empty;
        [DataMember(Order = 9)] public List<string> ArtifactPaths { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class LearningArtifact
    {
        [DataMember(Order = 1)] public string RelativePath { get; set; } = string.Empty;
        [DataMember(Order = 2)] public string Format { get; set; } = string.Empty;
        [DataMember(Order = 3)] public string Sha256 { get; set; } = string.Empty;
        [DataMember(Order = 4)] public long Bytes { get; set; }
        [DataMember(Order = 5)] public long SampleCount { get; set; }
        [DataMember(Order = 6)] public DateTime StartedUtc { get; set; }
        [DataMember(Order = 7)] public DateTime CompletedUtc { get; set; }
    }

    /// <summary>Durable per-logical-cycle receipt written beside the evidence.</summary>
    [DataContract]
    public sealed class LearningAttemptReceipt
    {
        [DataMember(Order = 1)] public string Phase { get; set; } = "Learning";
        [DataMember(Order = 2)] public int LogicalOrdinal { get; set; }
        [DataMember(Order = 3)] public int Attempt { get; set; }
        [DataMember(Order = 4)] public int InternalCycle { get; set; }
        [DataMember(Order = 5)] public string Status { get; set; } = "Unknown";
        [DataMember(Order = 6)] public long SampleCount { get; set; }
        [DataMember(Order = 7)] public DateTime StartedUtc { get; set; }
        [DataMember(Order = 8)] public DateTime CompletedUtc { get; set; }
        [DataMember(Order = 9)] public string Reason { get; set; } = string.Empty;
        [DataMember(Order = 10)] public List<LearningArtifact> Artifacts { get; set; } = new List<LearningArtifact>();
    }

    /// <summary>
    /// Atomic manifest publication and strict validation.  The implementation is
    /// intentionally independent of EpbManager/SQLite so it can be used by tests,
    /// export tools and an online retention worker without taking control locks.
    /// </summary>
    public static class LearningRunManifestStore
    {
        private static readonly object PublishGate = new object();
        public static Guid ResolveRunChainId(Guid rootRunId, Guid runId) =>
            rootRunId != Guid.Empty ? rootRunId : runId;

        public static string ManifestPath(string chainDirectory) =>
            Path.Combine(Path.GetFullPath(chainDirectory ?? throw new ArgumentNullException(nameof(chainDirectory))),
                LearningRunManifest.FileName);

        public const string AttemptReceiptFileName = "learning-attempt.json";

        public static string AttemptReceiptPath(string evidenceDirectory) =>
            Path.Combine(Path.GetFullPath(evidenceDirectory ?? throw new ArgumentNullException(nameof(evidenceDirectory))),
                AttemptReceiptFileName);

        public static void WriteAttemptReceiptAtomic(string evidenceDirectory, LearningAttemptReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var directory = Path.GetFullPath(evidenceDirectory ?? throw new ArgumentNullException(nameof(evidenceDirectory)));
            Directory.CreateDirectory(directory);
            var path = AttemptReceiptPath(directory);
            var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(LearningAttemptReceipt));
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                           16 * 1024, FileOptions.WriteThrough))
                {
                    serializer.WriteObject(stream, receipt);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        public static bool TryReadAttemptReceipt(string path, out LearningAttemptReceipt receipt)
        {
            receipt = null;
            try
            {
                if (!File.Exists(path)) return false;
                var serializer = new DataContractJsonSerializer(typeof(LearningAttemptReceipt));
                using (var stream = File.OpenRead(path))
                    receipt = serializer.ReadObject(stream) as LearningAttemptReceipt;
                return receipt != null && receipt.LogicalOrdinal > 0 && receipt.Attempt > 0 &&
                       !string.IsNullOrWhiteSpace(receipt.Phase) &&
                       !string.IsNullOrWhiteSpace(receipt.Status);
            }
            catch { receipt = null; return false; }
        }

        public static LearningRunManifest BuildFromDirectory(
            string chainDirectory,
            RunChainIdentity identity,
            string appVersion,
            string configHash,
            IEnumerable<int> plannedChannels,
            int plannedLearningCycles,
            int plannedQualificationCycles,
            string finalStatus,
            string reason,
            string modelHash = null,
            string modelCommitReceipt = null)
        {
            if (string.IsNullOrWhiteSpace(chainDirectory)) throw new ArgumentException("链目录不能为空。", nameof(chainDirectory));
            var full = Path.GetFullPath(chainDirectory);
            var normalized = (identity ?? new RunChainIdentity()).Normalize(Guid.Empty);
            var now = DateTime.UtcNow;
            var scanRoot = full;
            if (normalized.EffectiveRootRunId != normalized.RunId)
            {
                var executionRoot = Path.Combine(full, "Executions", normalized.RunId.ToString("N"));
                if (Directory.Exists(executionRoot)) scanRoot = executionRoot;
            }
            var manifest = new LearningRunManifest
            {
                AppVersion = appVersion ?? string.Empty,
                ConfigHash = configHash ?? string.Empty,
                ChainRunId = normalized.RunChainId.ToString("N"),
                RootRunId = normalized.EffectiveRootRunId.ToString("N"),
                PlannedChannels = (plannedChannels ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray(),
                PlannedLearningCycles = Math.Max(0, plannedLearningCycles),
                PlannedQualificationCycles = Math.Max(0, plannedQualificationCycles),
                FinalStatus = string.IsNullOrWhiteSpace(finalStatus) ? "Unknown" : finalStatus,
                Reason = reason ?? string.Empty,
                StartedUtc = Directory.GetCreationTimeUtc(full),
                CompletedUtc = now,
                ModelHash = modelHash ?? string.Empty,
                ModelCommitReceipt = modelCommitReceipt ?? string.Empty
            };
            if (manifest.StartedUtc == default || manifest.StartedUtc > now)
                manifest.StartedUtc = now;

            var execution = new LearningRunExecution
            {
                RunId = normalized.RunId.ToString("N"),
                RootRunId = normalized.EffectiveRootRunId.ToString("N"),
                ParentRunId = normalized.ParentRunId == Guid.Empty ? string.Empty : normalized.ParentRunId.ToString("N"),
                RunEpoch = normalized.RunEpoch,
                RestartGeneration = normalized.RestartGeneration,
                StartedUtc = manifest.StartedUtc,
                CompletedUtc = now,
                Status = manifest.FinalStatus,
                Reason = manifest.Reason,
                ModelHash = manifest.ModelHash,
                ModelCommitReceipt = manifest.ModelCommitReceipt
            };
            foreach (var path in Directory.Exists(scanRoot)
                         ? Directory.EnumerateFiles(scanRoot, "*", SearchOption.AllDirectories)
                         : Enumerable.Empty<string>())
            {
                var file = Path.GetFullPath(path);
                if (file.EndsWith(LearningRunManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                var info = new FileInfo(file);
                var rel = file.Substring(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                execution.Artifacts.Add(new LearningArtifact
                {
                    RelativePath = rel,
                    Format = info.Extension.TrimStart('.').ToUpperInvariant(),
                    Sha256 = ComputeSha256(file),
                    Bytes = info.Length,
                    SampleCount = GuessSampleCount(file),
                    StartedUtc = info.CreationTimeUtc,
                    CompletedUtc = info.LastWriteTimeUtc
                });
            }
            execution.Artifacts = execution.Artifacts.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToList();
            // Attempt metadata is authoritative.  Legacy directories without
            // this receipt intentionally produce no attempt states and are
            // therefore never eligible for automatic retention.
            foreach (var metadataPath in Directory.Exists(scanRoot)
                         ? Directory.EnumerateFiles(scanRoot, AttemptReceiptFileName, SearchOption.AllDirectories)
                         : Enumerable.Empty<string>())
            {
                if (!TryReadAttemptReceipt(metadataPath, out var receipt)) continue;
                var metadataDirectory = Path.GetDirectoryName(Path.GetFullPath(metadataPath)) ?? scanRoot;
                var metadataRelative = metadataDirectory.Substring(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var channel = metadataRelative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(x => x.StartsWith("EPB", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                var logical = (string.Equals(receipt.Phase, "Qualification", StringComparison.OrdinalIgnoreCase)
                                  ? "Qualification_" : "Learning_") + receipt.LogicalOrdinal.ToString("D4", CultureInfo.InvariantCulture);
                var artifactPaths = new List<string>();
                foreach (var receiptArtifact in receipt.Artifacts ?? new List<LearningArtifact>())
                {
                    var match = execution.Artifacts.FirstOrDefault(item =>
                        string.Equals(item.RelativePath, receiptArtifact.RelativePath,
                            StringComparison.OrdinalIgnoreCase) &&
                        item.Bytes == receiptArtifact.Bytes &&
                        string.Equals(item.Sha256, receiptArtifact.Sha256,
                            StringComparison.OrdinalIgnoreCase));
                    if (match != null) artifactPaths.Add(match.RelativePath);
                }
                artifactPaths = artifactPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.Ordinal).ToList();
                if (artifactPaths.Count != (receipt.Artifacts ?? new List<LearningArtifact>()).Count)
                    continue;
                var state = new LearningAttemptState
                {
                    Channel = channel,
                    LearningId = logical,
                    Attempt = receipt.Attempt,
                    Status = NormalizeAttemptStatus(receipt.Status),
                    InternalCycle = receipt.InternalCycle,
                    StartedUtc = receipt.StartedUtc == default ? manifest.StartedUtc : receipt.StartedUtc,
                    CompletedUtc = receipt.CompletedUtc == default ? manifest.CompletedUtc : receipt.CompletedUtc,
                    Reason = receipt.Reason ?? string.Empty,
                    ArtifactPaths = artifactPaths
                };
                if (string.Equals(receipt.Phase, "Qualification", StringComparison.OrdinalIgnoreCase))
                    execution.Qualification.Add(state);
                else
                    execution.Learning.Add(state);
            }
            manifest.Executions.Add(execution);
            return manifest;
        }

        private static string NormalizeAttemptStatus(string status)
        {
            if (string.Equals(status, "learning_completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "qualification_completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Successful", StringComparison.OrdinalIgnoreCase))
                return "Successful";
            if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                status.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Cancelled";
            if (string.Equals(status, "EvidenceCompleted", StringComparison.OrdinalIgnoreCase))
                return "EvidenceCompleted";
            return "Failed";
        }

        public static bool PublishAtomic(string chainDirectory, LearningRunManifest manifest, out string manifestPath)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var full = Path.GetFullPath(chainDirectory ?? throw new ArgumentNullException(nameof(chainDirectory)));
            Directory.CreateDirectory(full);
            manifestPath = Path.Combine(full, LearningRunManifest.FileName);
            lock (PublishGate)
            {
                var existing = File.Exists(manifestPath) ? ReadJson(manifestPath) : null;
                if (existing != null)
                {
                    existing.Schema = manifest.Schema;
                    existing.AppVersion = manifest.AppVersion;
                    existing.ConfigHash = manifest.ConfigHash;
                    existing.ChainRunId = manifest.ChainRunId;
                    existing.RootRunId = manifest.RootRunId;
                    existing.PlannedChannels = manifest.PlannedChannels;
                    existing.PlannedLearningCycles = manifest.PlannedLearningCycles;
                    existing.PlannedQualificationCycles = manifest.PlannedQualificationCycles;
                    existing.FinalStatus = manifest.FinalStatus;
                    existing.Reason = manifest.Reason;
                    existing.StartedUtc = existing.StartedUtc == default ? manifest.StartedUtc : existing.StartedUtc;
                    existing.CompletedUtc = manifest.CompletedUtc;
                    existing.ModelHash = manifest.ModelHash;
                    existing.ModelCommitReceipt = manifest.ModelCommitReceipt;
                    existing.ModelCommitReceipts = manifest.ModelCommitReceipts ?? new List<string>();
                    foreach (var execution in manifest.Executions ?? new List<LearningRunExecution>())
                    {
                        var old = existing.Executions.FirstOrDefault(item =>
                            string.Equals(item.RunId, execution.RunId, StringComparison.OrdinalIgnoreCase));
                        if (old != null) existing.Executions.Remove(old);
                        existing.Executions.Add(execution);
                    }
                    manifest = existing;
                }
                var temporary = Path.Combine(full, $".{LearningRunManifest.FileName}.{Guid.NewGuid():N}.tmp");
                try
                {
                    manifest.ManifestHash = string.Empty;
                    manifest.ManifestBytes = 0;
                    var unsigned = Serialize(manifest);
                    manifest.ManifestHash = ComputeSha256(new MemoryStream(unsigned));
                    long size = 0;
                    byte[] signed;
                    for (var i = 0; i < 8; i++)
                    {
                        manifest.ManifestBytes = size;
                        signed = Serialize(manifest);
                        var next = signed.LongLength;
                        if (next == size) break;
                        size = next;
                    }
                    manifest.ManifestBytes = size;
                    signed = Serialize(manifest);
                    for (var i = 0; signed.LongLength != manifest.ManifestBytes && i < 8; i++)
                    {
                        manifest.ManifestBytes = signed.LongLength;
                        signed = Serialize(manifest);
                    }
                    using (var output = new FileStream(
                               temporary,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.None,
                               64 * 1024,
                               FileOptions.WriteThrough))
                    {
                        output.Write(signed, 0, signed.Length);
                        output.Flush(true);
                    }
                    if (File.Exists(manifestPath)) File.Replace(temporary, manifestPath, null);
                    else File.Move(temporary, manifestPath);
                    return true;
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        public static bool TryReadValidated(string path, out LearningRunManifest manifest, out string reason)
        {
            manifest = null;
            reason = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    reason = "ManifestMissing";
                    return false;
                }
                manifest = ReadJson(path);
                if (manifest.Schema != LearningRunManifest.CurrentSchema)
                {
                    reason = "ManifestSchemaUnsupported";
                    manifest = null;
                    return false;
                }
                var actualBytes = new FileInfo(path).Length;
                if (manifest.ManifestBytes != actualBytes)
                {
                    reason = "ManifestSizeMismatch";
                    manifest = null;
                    return false;
                }
                var expectedHash = manifest.ManifestHash;
                manifest.ManifestHash = string.Empty;
                manifest.ManifestBytes = 0;
                var unsigned = Serialize(manifest);
                manifest.ManifestHash = expectedHash;
                manifest.ManifestBytes = actualBytes;
                if (!string.Equals(expectedHash, ComputeSha256(new MemoryStream(unsigned)), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "ManifestHashMismatch";
                    manifest = null;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(manifest.ChainRunId) ||
                    string.IsNullOrWhiteSpace(manifest.FinalStatus) ||
                    string.Equals(manifest.FinalStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "ManifestNonTerminal";
                    manifest = null;
                    return false;
                }
                if (string.Equals(manifest.FinalStatus, "Successful", StringComparison.OrdinalIgnoreCase))
                {
                    var plannedReceiptChannels = new HashSet<int>();
                    foreach (var receipt in manifest.ModelCommitReceipts ?? new List<string>())
                    {
                        var match = Regex.Match(receipt ?? string.Empty,
                            "^EPB(?<channel>[0-9]{2}):sha256:(?<hash>[0-9a-fA-F]{64})$",
                            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                        if (!match.Success || !int.TryParse(match.Groups["channel"].Value,
                                NumberStyles.None, CultureInfo.InvariantCulture, out var receiptChannel) ||
                            !plannedReceiptChannels.Add(receiptChannel))
                        {
                            reason = "ManifestSuccessEvidenceIncomplete";
                            manifest = null;
                            return false;
                        }
                    }
                    var plannedChannels = new HashSet<int>(manifest.PlannedChannels ?? Array.Empty<int>());
                    if (plannedReceiptChannels.Count != plannedChannels.Count ||
                        !plannedReceiptChannels.SetEquals(plannedChannels))
                    {
                        reason = "ManifestSuccessEvidenceIncomplete";
                        manifest = null;
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(manifest.ModelHash) ||
                        string.IsNullOrWhiteSpace(manifest.ModelCommitReceipt) ||
                        (manifest.PlannedChannels ?? Array.Empty<int>()).Length == 0 ||
                        plannedReceiptChannels.Count == 0)
                    {
                        reason = "ManifestSuccessEvidenceIncomplete";
                        manifest = null;
                        return false;
                    }
                    var executions = manifest.Executions ?? new List<LearningRunExecution>();
                    if (executions.Count == 0 || executions.Any(item =>
                        string.IsNullOrWhiteSpace(item.Status) ||
                        string.Equals(item.Status, "Unknown", StringComparison.OrdinalIgnoreCase)))
                    {
                        reason = "ManifestExecutionIncomplete";
                        manifest = null;
                        return false;
                    }
                }
                var root = Path.GetDirectoryName(Path.GetFullPath(path));
                foreach (var execution in manifest.Executions ?? new List<LearningRunExecution>())
                foreach (var artifact in execution.Artifacts ?? new List<LearningArtifact>())
                {
                    if (string.IsNullOrWhiteSpace(artifact.RelativePath) || Path.IsPathRooted(artifact.RelativePath))
                    {
                        reason = "ManifestPathInvalid";
                        manifest = null;
                        return false;
                    }
                    var candidate = Path.GetFullPath(Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsUnderRoot(root, candidate) || !File.Exists(candidate))
                    {
                        reason = "ManifestArtifactMissing";
                        manifest = null;
                        return false;
                    }
                    var info = new FileInfo(candidate);
                    if (info.Length != artifact.Bytes ||
                        !string.Equals(ComputeSha256(candidate), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "ManifestArtifactHashMismatch";
                        manifest = null;
                        return false;
                    }
                    if (artifact.Bytes <= 0 || string.IsNullOrWhiteSpace(artifact.Format))
                    {
                        reason = "ManifestArtifactMetadataInvalid";
                        manifest = null;
                        return false;
                    }
                }
                var manifestRoot = Path.GetDirectoryName(Path.GetFullPath(path));
                foreach (var receiptPath in Directory.EnumerateFiles(manifestRoot, AttemptReceiptFileName,
                             SearchOption.AllDirectories))
                {
                    if (!TryReadAttemptReceipt(receiptPath, out var receipt) ||
                        (receipt.Artifacts ?? new List<LearningArtifact>()).Count == 0)
                    {
                        reason = "ManifestAttemptReceiptInvalid";
                        manifest = null;
                        return false;
                    }
                }
                if (string.Equals(manifest.FinalStatus, "Successful", StringComparison.OrdinalIgnoreCase) &&
                    !HasPlannedEvidence(manifest))
                {
                    reason = "ManifestLearningEvidenceIncomplete";
                    manifest = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "ManifestReadError:" + ex.GetType().Name;
                manifest = null;
                return false;
            }
        }

        public static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path)) return ComputeSha256(stream);
        }

        public static string ComputeSha256(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool IsUnderRoot(string root, string candidate)
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   !File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint);
        }

        private static long GuessSampleCount(string file)
        {
            if (!string.Equals(Path.GetExtension(file), ".csv", StringComparison.OrdinalIgnoreCase)) return 0;
            try { return Math.Max(0, File.ReadLines(file).LongCount() - 1); } catch { return 0; }
        }

        public static bool HasPlannedEvidence(LearningRunManifest manifest)
        {
            if (manifest == null || (manifest.PlannedChannels ?? Array.Empty<int>()).Length == 0)
                return false;
            foreach (var channel in manifest.PlannedChannels.Distinct())
            {
                var name = "EPB" + channel.ToString("D2", CultureInfo.InvariantCulture);
                var learning = (manifest.Executions ?? new List<LearningRunExecution>())
                    .SelectMany(execution => execution.Learning ?? new List<LearningAttemptState>())
                    .Where(state => string.Equals(state.Channel, name, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(state.Status, "Successful", StringComparison.OrdinalIgnoreCase) &&
                                    state.ArtifactPaths != null && state.ArtifactPaths.Count > 0)
                    .Select(state => state.LearningId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var qualification = (manifest.Executions ?? new List<LearningRunExecution>())
                    .SelectMany(execution => execution.Qualification ?? new List<LearningAttemptState>())
                    .Where(state => string.Equals(state.Channel, name, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(state.Status, "Successful", StringComparison.OrdinalIgnoreCase) &&
                                    state.ArtifactPaths != null && state.ArtifactPaths.Count > 0)
                    .Select(state => state.LearningId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (learning.Length < Math.Max(0, manifest.PlannedLearningCycles) ||
                    qualification.Length < Math.Max(0, manifest.PlannedQualificationCycles))
                    return false;
                for (var ordinal = 1; ordinal <= Math.Max(0, manifest.PlannedLearningCycles); ordinal++)
                    if (!learning.Contains("Learning_" + ordinal.ToString("D4", CultureInfo.InvariantCulture),
                            StringComparer.OrdinalIgnoreCase)) return false;
                for (var ordinal = 1; ordinal <= Math.Max(0, manifest.PlannedQualificationCycles); ordinal++)
                    if (!qualification.Contains("Qualification_" + ordinal.ToString("D4", CultureInfo.InvariantCulture),
                            StringComparer.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static void WriteJson(string path, LearningRunManifest manifest)
        {
            var serializer = new DataContractJsonSerializer(typeof(LearningRunManifest));
            using (var stream = File.Create(path)) serializer.WriteObject(stream, manifest);
        }

        private static byte[] Serialize(LearningRunManifest manifest)
        {
            var serializer = new DataContractJsonSerializer(typeof(LearningRunManifest));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, manifest);
                return stream.ToArray();
            }
        }

        private static LearningRunManifest ReadJson(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(LearningRunManifest));
            using (var stream = File.OpenRead(path)) return (LearningRunManifest)serializer.ReadObject(stream);
        }
    }

    public static class LearningRetentionPlanner
    {
        public static IReadOnlyList<string> FindCandidates(
            string root,
            int successfulRetainCount,
            int failedRetainCount,
            Func<string, bool> isCurrent,
            Action<string> warningSink = null)
        {
            var result = new List<Tuple<string, LearningRunManifest>>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Array.Empty<string>();
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).StartsWith(".retention-staging-", StringComparison.OrdinalIgnoreCase)) continue;
                if (isCurrent?.Invoke(Path.GetFullPath(directory)) == true) continue;
                if (LearningRunManifestStore.TryReadValidated(LearningRunManifestStore.ManifestPath(directory), out var manifest, out _))
                    result.Add(Tuple.Create(directory, manifest));
            }
            var successful = result.Where(x => string.Equals(x.Item2.FinalStatus, "Successful", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Item2.CompletedUtc).Skip(Math.Max(0, successfulRetainCount)).Select(x => x.Item1);
            var failed = result.Where(x => string.Equals(x.Item2.FinalStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(x.Item2.FinalStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Item2.CompletedUtc).Skip(Math.Max(0, failedRetainCount)).Select(x => x.Item1);
            return successful.Concat(failed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
