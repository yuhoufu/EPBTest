using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace Controller
{
    [DataContract]
    public sealed class IncidentPhaseReceipt
    {
        [DataMember(Order = 1)] public string PhaseKey { get; set; } = string.Empty;
        [DataMember(Order = 2)] public string Kind { get; set; } = "summary";
        [DataMember(Order = 3)] public string Device { get; set; } = string.Empty;
        [DataMember(Order = 4)] public string SessionKey { get; set; } = string.Empty;
        [DataMember(Order = 5)] public string PhaseDirectory { get; set; } = string.Empty;
        [DataMember(Order = 6)] public string JsonPath { get; set; } = string.Empty;
        [DataMember(Order = 7)] public string ReceiptPath { get; set; } = string.Empty;
        [DataMember(Order = 8)] public bool JsonCommitted { get; set; }
        [DataMember(Order = 9)] public bool HeavyEvidenceIncluded { get; set; }
        [DataMember(Order = 10)] public bool HeavyEvidenceSuppressed { get; set; }
        [DataMember(Order = 11)] public long Bytes { get; set; }
        [DataMember(Order = 12)] public DateTime CapturedUtc { get; set; }
        [DataMember(Order = 13)] public string Sha256 { get; set; } = string.Empty;
    }

    [DataContract]
    public sealed class IncidentDeviceManifestState
    {
        [DataMember(Order = 1)] public string Device { get; set; } = string.Empty;
        [DataMember(Order = 2)] public bool TriggerSeen { get; set; }
        [DataMember(Order = 3)] public bool TerminalSeen { get; set; }
        [DataMember(Order = 4)] public bool Quiet { get; set; }
        [DataMember(Order = 5)] public string Status { get; set; } = "Unknown";
    }

    /// <summary>
    /// Immutable-once-published terminal session manifest. A manifest is only
    /// eligible for retention when every observed device is terminal and quiet.
    /// </summary>
    [DataContract]
    public sealed class IncidentSessionManifest
    {
        public const int CurrentSchema = 1;
        public const string CurrentFormat = "IncidentSessionV2";
        public const string FileName = "session-manifest.json";

        [DataMember(Order = 1)] public int Schema { get; set; } = CurrentSchema;
        [DataMember(Order = 2)] public string Format { get; set; } = CurrentFormat;
        [DataMember(Order = 3)] public string RunId { get; set; } = string.Empty;
        [DataMember(Order = 4)] public string CorrelationId { get; set; } = string.Empty;
        [DataMember(Order = 5)] public string SessionKey { get; set; } = string.Empty;
        [DataMember(Order = 6)] public DateTime StartedUtc { get; set; }
        [DataMember(Order = 7)] public DateTime CompletedUtc { get; set; }
        [DataMember(Order = 8)] public bool Terminal { get; set; }
        [DataMember(Order = 9)] public bool AllDevicesTerminal { get; set; }
        [DataMember(Order = 10)] public bool Quiet { get; set; }
        [DataMember(Order = 11)] public long Bytes { get; set; }
        [DataMember(Order = 12)] public string CommitReceipt { get; set; } = string.Empty;
        [DataMember(Order = 13)] public List<IncidentDeviceManifestState> Devices { get; set; } = new();
        [DataMember(Order = 14)] public List<IncidentPhaseReceipt> Phases { get; set; } = new();
        [DataMember(Order = 15)] public List<string> ExpectedDevices { get; set; } = new();

        public bool IsRetentionEligible =>
            Schema == CurrentSchema &&
            string.Equals(Format, CurrentFormat, StringComparison.Ordinal) &&
            Terminal && AllDevicesTerminal && Quiet &&
            Devices != null && Devices.Count > 0 &&
            (ExpectedDevices == null || ExpectedDevices.Count == 0 ||
             ExpectedDevices.All(expected => Devices.Any(actual =>
                 string.Equals(IncidentSessionPolicy.NormalizeDevice(actual.Device),
                     IncidentSessionPolicy.NormalizeDevice(expected), StringComparison.OrdinalIgnoreCase) &&
                 actual.TriggerSeen && actual.TerminalSeen && actual.Quiet &&
                 string.Equals(actual.Status, "Terminal", StringComparison.OrdinalIgnoreCase)))) &&
            Devices.All(item => item != null && item.TriggerSeen && item.TerminalSeen && item.Quiet &&
                                string.Equals(item.Status, "Terminal", StringComparison.OrdinalIgnoreCase)) &&
            Phases != null && Phases.Count > 0 &&
            Phases.All(item => item != null && item.JsonCommitted &&
                               !string.IsNullOrWhiteSpace(item.ReceiptPath));
    }

    /// <summary>Phase-level atomic writes and terminal manifest publication.</summary>
    public static class IncidentSessionManifestStore
    {
        private sealed class Accumulator
        {
            internal readonly object Gate = new object();
            internal IncidentSessionManifest Manifest = new IncidentSessionManifest();
        }

        private static readonly ConcurrentDictionary<string, Accumulator> Accumulators =
            new ConcurrentDictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);

        public static string ManifestPath(string root) =>
            Path.Combine(Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root))),
                IncidentSessionManifest.FileName);

        public static IncidentPhaseReceipt WritePhase(
            string phaseDirectory,
            string phaseKey,
            string kind,
            string device,
            string sessionKey,
            string incidentJson,
            Action<string> writeHeavyEvidence,
            bool heavyEvidenceSuppressed,
            out Exception heavyError)
        {
            if (string.IsNullOrWhiteSpace(phaseDirectory))
                throw new ArgumentException("事故 phase 目录不能为空。", nameof(phaseDirectory));
            Directory.CreateDirectory(phaseDirectory);
            var normalizedJson = string.IsNullOrWhiteSpace(incidentJson) ? "{}\n" : incidentJson;
            var jsonPath = Path.Combine(phaseDirectory, "incident.json");
            AtomicWriteTextIfMissing(jsonPath, normalizedJson);

            heavyError = null;
            var receiptPath = Path.Combine(phaseDirectory, "phase.receipt.json");
            // Retries after a terminal-manifest failure must not duplicate heavy
            // diagnostics or cycle copies. A committed receipt is the idempotency
            // boundary for this phase.
            if (File.Exists(receiptPath))
            {
                try
                {
                    var existing = ReadJson<IncidentPhaseReceipt>(receiptPath);
                    if (existing != null && existing.JsonCommitted) return existing;
                }
                catch { /* rebuild below */ }
            }
            var before = GetDirectoryBytes(phaseDirectory);
            if (writeHeavyEvidence != null && !heavyEvidenceSuppressed)
            {
                try { writeHeavyEvidence(phaseDirectory); }
                catch (Exception ex) { heavyError = ex; }
            }
            var after = GetDirectoryBytes(phaseDirectory);
            var receipt = new IncidentPhaseReceipt
            {
                PhaseKey = phaseKey ?? string.Empty,
                Kind = string.IsNullOrWhiteSpace(kind) ? "summary" : kind,
                Device = IncidentSessionPolicy.NormalizeDevice(device),
                SessionKey = sessionKey ?? string.Empty,
                PhaseDirectory = Path.GetFileName(phaseDirectory),
                JsonPath = Path.Combine(Path.GetFileName(phaseDirectory), "incident.json")
                    .Replace(Path.DirectorySeparatorChar, '/'),
                JsonCommitted = File.Exists(jsonPath),
                HeavyEvidenceIncluded = writeHeavyEvidence != null && !heavyEvidenceSuppressed,
                HeavyEvidenceSuppressed = heavyEvidenceSuppressed || heavyError != null,
                Bytes = Math.Max(0, after),
                CapturedUtc = DateTime.UtcNow,
                Sha256 = ComputeSha256(jsonPath)
            };
            if (heavyError != null) receipt.HeavyEvidenceIncluded = false;
            AtomicWriteJson(receiptPath, receipt);
            receipt.ReceiptPath = Path.Combine(receipt.PhaseDirectory, "phase.receipt.json")
                .Replace(Path.DirectorySeparatorChar, '/');
            // ReceiptPath is part of its own payload. Rewrite once so consumers
            // can validate it without guessing the filename.
            AtomicWriteJson(receiptPath, receipt);
            return receipt;
        }

        public static IncidentPhaseReceipt BuildSummaryReceipt(
            string phaseKey,
            string kind,
            string device,
            string sessionKey,
            string incidentJson,
            bool heavyEvidenceSuppressed)
        {
            var payload = incidentJson ?? "{}";
            return new IncidentPhaseReceipt
            {
                PhaseKey = phaseKey ?? string.Empty,
                Kind = string.IsNullOrWhiteSpace(kind) ? "summary" : kind,
                Device = IncidentSessionPolicy.NormalizeDevice(device),
                SessionKey = sessionKey ?? string.Empty,
                PhaseDirectory = string.Empty,
                JsonPath = string.Empty,
                ReceiptPath = string.Empty,
                JsonCommitted = payload.Trim().Length > 0,
                HeavyEvidenceSuppressed = heavyEvidenceSuppressed,
                Bytes = Encoding.UTF8.GetByteCount(payload),
                CapturedUtc = DateTime.UtcNow,
                Sha256 = ComputeTextSha256(payload)
            };
        }

        public static void RewriteReceipt(string phaseDirectory, IncidentPhaseReceipt receipt)
        {
            if (string.IsNullOrWhiteSpace(phaseDirectory) || receipt == null) return;
            AtomicWriteJson(Path.Combine(phaseDirectory, "phase.receipt.json"), receipt);
        }

        /// <summary>
        /// Records a phase after its JSON and receipt have committed. This is
        /// process-local state only; only PublishTerminalAtomic creates the
        /// retention-visible file.
        /// </summary>
        public static void RecordPhase(
            string root,
            Guid runId,
            Guid correlationId,
            DateTime startedUtc,
            IncidentPhaseReceipt receipt,
            bool isTrigger,
            bool isTerminal)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var full = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
            var accumulator = Accumulators.GetOrAdd(full, _ => new Accumulator());
            lock (accumulator.Gate)
            {
                var manifest = accumulator.Manifest;
                if (manifest.RunId.Length == 0) manifest.RunId = runId.ToString("N");
                if (manifest.CorrelationId.Length == 0) manifest.CorrelationId = correlationId.ToString("N");
                if (manifest.StartedUtc == default || startedUtc < manifest.StartedUtc)
                    manifest.StartedUtc = startedUtc == default ? DateTime.UtcNow : startedUtc.ToUniversalTime();
                manifest.SessionKey = receipt.SessionKey ?? manifest.SessionKey;
                var device = IncidentSessionPolicy.NormalizeDevice(receipt.Device);
                var deviceState = manifest.Devices.FirstOrDefault(item =>
                    string.Equals(item.Device, device, StringComparison.OrdinalIgnoreCase));
                if (deviceState == null)
                {
                    deviceState = new IncidentDeviceManifestState { Device = device };
                    manifest.Devices.Add(deviceState);
                }
                deviceState.TriggerSeen |= isTrigger;
                deviceState.TerminalSeen |= isTerminal;
                deviceState.Quiet = deviceState.TerminalSeen;
                deviceState.Status = deviceState.TerminalSeen ? "Terminal" : "Active";
                if (!manifest.Phases.Any(item =>
                        string.Equals(item.Device, receipt.Device, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.PhaseKey, receipt.PhaseKey, StringComparison.OrdinalIgnoreCase)))
                {
                    manifest.Phases.Add(receipt);
                    manifest.Bytes = Math.Max(0, manifest.Bytes + receipt.Bytes);
                }
            }
        }

        public static void RegisterExpectedDevices(
            string root,
            IEnumerable<string> devices)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            var full = Path.GetFullPath(root);
            var accumulator = Accumulators.GetOrAdd(full, CreateAccumulatorFromDisk);
            lock (accumulator.Gate)
            {
                if (accumulator.Manifest.ExpectedDevices == null)
                    accumulator.Manifest.ExpectedDevices = new List<string>();
                foreach (var device in devices ?? Enumerable.Empty<string>())
                {
                    var normalized = IncidentSessionPolicy.NormalizeDevice(device);
                    if (!accumulator.Manifest.ExpectedDevices.Any(item =>
                            string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
                        accumulator.Manifest.ExpectedDevices.Add(normalized);
                }
            }
        }

        /// <summary>Atomically publishes a terminal session manifest.</summary>
        public static bool PublishTerminalAtomic(
            string root,
            bool quiet = true,
            DateTime completedUtc = default)
        {
            var full = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
            var accumulator = Accumulators.GetOrAdd(full, CreateAccumulatorFromDisk);
            lock (accumulator.Gate)
            {
                var manifest = accumulator.Manifest;
                if (manifest.Devices == null || manifest.Devices.Count == 0) return false;
                manifest.Terminal = manifest.Devices.All(item => item.TerminalSeen);
                var expectedComplete = (manifest.ExpectedDevices == null || manifest.ExpectedDevices.Count == 0) ||
                    manifest.ExpectedDevices.All(expected => manifest.Devices.Any(actual =>
                        string.Equals(IncidentSessionPolicy.NormalizeDevice(actual.Device),
                            IncidentSessionPolicy.NormalizeDevice(expected), StringComparison.OrdinalIgnoreCase) &&
                        actual.TriggerSeen && actual.TerminalSeen && actual.Quiet &&
                        string.Equals(actual.Status, "Terminal", StringComparison.OrdinalIgnoreCase)));
                manifest.AllDevicesTerminal = manifest.Terminal && expectedComplete;
                manifest.Quiet = quiet && manifest.AllDevicesTerminal && manifest.Devices.All(item => item.Quiet);
                manifest.CompletedUtc = completedUtc == default
                    ? DateTime.UtcNow
                    : completedUtc.ToUniversalTime();
                if (!manifest.Terminal || !manifest.AllDevicesTerminal || !manifest.Quiet) return false;
                Directory.CreateDirectory(full);
                var path = ManifestPath(full);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                manifest.CommitReceipt = Guid.NewGuid().ToString("N");
                try
                {
                    WriteJson(temporary, manifest);
                    AtomicReplace(temporary, path);
                    return true;
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        public static bool TryReadValidated(
            string root,
            out IncidentSessionManifest manifest,
            out string reason)
        {
            manifest = null;
            reason = string.Empty;
            try
            {
                var path = File.Exists(root ?? string.Empty)
                    ? root
                    : ManifestPath(root);
                if (!File.Exists(path)) { reason = "ManifestMissing"; return false; }
                manifest = ReadJson(path);
                if (manifest == null || !manifest.IsRetentionEligible)
                {
                    reason = "ManifestIncompleteOrLegacy";
                    manifest = null;
                    return false;
                }
                var rootDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
                foreach (var phase in manifest.Phases)
                {
                    if (phase == null || string.IsNullOrWhiteSpace(phase.PhaseDirectory) ||
                        string.IsNullOrWhiteSpace(phase.JsonPath) ||
                        string.IsNullOrWhiteSpace(phase.ReceiptPath))
                    {
                        reason = "PhaseReceiptMissing";
                        manifest = null;
                        return false;
                    }
                    var phaseDirectory = ResolveUnderRoot(rootDirectory, phase.PhaseDirectory);
                    var jsonPath = ResolveUnderRoot(rootDirectory, phase.JsonPath);
                    var receiptPath = ResolveUnderRoot(rootDirectory, phase.ReceiptPath);
                    if (phaseDirectory == null || jsonPath == null || receiptPath == null ||
                        !Directory.Exists(phaseDirectory) || !File.Exists(jsonPath) ||
                        !File.Exists(receiptPath) || IsReparse(phaseDirectory) ||
                        IsReparse(jsonPath) || IsReparse(receiptPath))
                    {
                        reason = "PhasePathInvalid";
                        manifest = null;
                        return false;
                    }
                    if (Directory.EnumerateFiles(phaseDirectory, "*.tmp", SearchOption.AllDirectories).Any())
                    {
                        reason = "TemporaryFilePresent";
                        manifest = null;
                        return false;
                    }
                    IncidentPhaseReceipt stored;
                    try { stored = ReadJson<IncidentPhaseReceipt>(receiptPath); }
                    catch
                    {
                        reason = "PhaseReceiptUnreadable";
                        manifest = null;
                        return false;
                    }
                    if (stored == null || !stored.JsonCommitted ||
                        !string.Equals(stored.PhaseKey, phase.PhaseKey, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(stored.Device, phase.Device, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(stored.Sha256, ComputeSha256(jsonPath), StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "PhaseReceiptMismatch";
                        manifest = null;
                        return false;
                    }
                    var phaseBytes = GetDirectoryBytes(phaseDirectory);
                    if (phaseBytes < Math.Max(0, phase.Bytes))
                    {
                        reason = "PhaseBytesMismatch";
                        manifest = null;
                        return false;
                    }
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

        public static long GetDirectoryBytes(string directory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
                return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(path =>
                    {
                        try { return new FileInfo(path).Length; } catch { return 0L; }
                    })
                    .Sum();
            }
            catch { return 0; }
        }

        public static string ComputeSha256(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty)
                        .ToLowerInvariant();
            }
            catch { return string.Empty; }
        }

        public static string ComputeTextSha256(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void AtomicWriteTextIfMissing(string path, string text)
        {
            if (File.Exists(path)) return;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                           temp,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.Read,
                           4096,
                           FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(text ?? string.Empty);
                    writer.Flush();
                    stream.Flush(true);
                }
                AtomicReplace(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void AtomicWriteJson<T>(string path, T value)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteJson(temp, value);
                AtomicReplace(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void WriteJson<T>(string path, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new FileStream(
                       path,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.Read,
                       4096,
                       FileOptions.SequentialScan))
            {
                serializer.WriteObject(stream, value);
                stream.Flush(true);
            }
        }

        private static T ReadJson<T>(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = File.OpenRead(path)) return (T)serializer.ReadObject(stream);
        }

        private static IncidentSessionManifest ReadJson(string path) => ReadJson<IncidentSessionManifest>(path);

        private static void AtomicReplace(string temporary, string target)
        {
            if (File.Exists(target))
            {
                try { File.Replace(temporary, target, null); return; }
                catch (PlatformNotSupportedException) { }
                catch (IOException) { }
            }
            File.Move(temporary, target);
        }

        private static Accumulator CreateAccumulatorFromDisk(string full)
        {
            var accumulator = new Accumulator();
            var manifestPath = ManifestPath(full);
            try
            {
                if (File.Exists(manifestPath))
                    accumulator.Manifest = ReadJson<IncidentSessionManifest>(manifestPath) ?? accumulator.Manifest;
            }
            catch { }
            try
            {
                foreach (var receiptPath in Directory.EnumerateFiles(
                             full, "phase.receipt.json", SearchOption.AllDirectories))
                {
                    if (IsReparse(receiptPath)) continue;
                    IncidentPhaseReceipt receipt;
                    try { receipt = ReadJson<IncidentPhaseReceipt>(receiptPath); }
                    catch { continue; }
                    if (receipt == null || !receipt.JsonCommitted) continue;
                    var phaseDirectory = Path.GetDirectoryName(receiptPath);
                    var relative = MakeRelative(full, phaseDirectory);
                    receipt.PhaseDirectory = relative;
                    receipt.JsonPath = MakeRelative(full, Path.Combine(phaseDirectory, "incident.json"));
                    receipt.ReceiptPath = MakeRelative(full, receiptPath);
                    var manifest = accumulator.Manifest;
                    if (manifest.StartedUtc == default || receipt.CapturedUtc < manifest.StartedUtc)
                        manifest.StartedUtc = receipt.CapturedUtc;
                    manifest.SessionKey = receipt.SessionKey ?? manifest.SessionKey;
                    var device = IncidentSessionPolicy.NormalizeDevice(receipt.Device);
                    var deviceState = manifest.Devices.FirstOrDefault(item =>
                        string.Equals(item.Device, device, StringComparison.OrdinalIgnoreCase));
                    if (deviceState == null)
                    {
                        deviceState = new IncidentDeviceManifestState { Device = device };
                        manifest.Devices.Add(deviceState);
                    }
                    deviceState.TriggerSeen |= string.Equals(receipt.Kind, "trigger", StringComparison.OrdinalIgnoreCase);
                    deviceState.TerminalSeen |= string.Equals(receipt.Kind, "terminal", StringComparison.OrdinalIgnoreCase);
                    deviceState.Quiet = deviceState.TerminalSeen;
                    deviceState.Status = deviceState.TerminalSeen ? "Terminal" : "Active";
                    if (!manifest.Phases.Any(item =>
                            string.Equals(item.PhaseDirectory, receipt.PhaseDirectory, StringComparison.OrdinalIgnoreCase)))
                        manifest.Phases.Add(receipt);
                }
                accumulator.Manifest.Bytes = accumulator.Manifest.Phases.Sum(item => Math.Max(0, item.Bytes));
            }
            catch { }
            return accumulator;
        }

        private static string ResolveUnderRoot(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return IsUnderRoot(root, full) ? full : null;
        }

        private static bool IsUnderRoot(string root, string candidate)
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReparse(string path)
        {
            try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
            catch { return true; }
        }

        private static string MakeRelative(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                           Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(rootFull.Length).Replace(Path.DirectorySeparatorChar, '/')
                : string.Empty;
        }
    }
}
