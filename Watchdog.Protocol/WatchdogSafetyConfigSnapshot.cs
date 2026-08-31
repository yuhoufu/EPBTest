using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class WatchdogSafetyConfigSnapshotEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    public sealed class WatchdogSafetyConfigSnapshotManifest
    {
        public int SchemaVersion { get; set; } = 2;
        public string HandoffId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public string MainExecutableSha256 { get; set; } = string.Empty;
        public string SafetyAgentExecutableSha256 { get; set; } = string.Empty;
        public string BuildIdentity { get; set; } = string.Empty;
        public long CreatedUtcTicks { get; set; }
        public WatchdogSafetyConfigSnapshotEntry[] Files { get; set; } =
            Array.Empty<WatchdogSafetyConfigSnapshotEntry>();
    }

    public sealed class WatchdogSafetyConfigSnapshotResult
    {
        public bool Succeeded { get; internal set; }
        public string SnapshotDirectory { get; internal set; } = string.Empty;
        public string ConfigDirectory { get; internal set; } = string.Empty;
        public string ManifestPath { get; internal set; } = string.Empty;
        public string ManifestSha256 { get; internal set; } = string.Empty;
        public WatchdogSafetyConfigSnapshotManifest Manifest { get; internal set; }
        public SafetyRuntimeSnapshot Runtime { get; internal set; }
        public string Error { get; internal set; } = string.Empty;
    }

    /// <summary>安全代理唯一允许使用的不可变运行参数。</summary>
    public sealed class SafetyRuntimeSnapshot
    {
        public int SchemaVersion { get; set; } = 2;
        public double SampleRateHz { get; set; }
        public int SamplesPerChannel { get; set; }
        public string[] PressureChannels { get; set; } = Array.Empty<string>();
        public double[] ReleaseSafePressureBar { get; set; } = Array.Empty<double>();
        public int PressureSampleMaxAgeMs { get; set; }
        public int ReleaseStableMs { get; set; }
        public int ReleaseTimeoutMs { get; set; }

        public void Validate()
        {
            if (SchemaVersion != 2 || double.IsNaN(SampleRateHz) ||
                double.IsInfinity(SampleRateHz) || SampleRateHz <= 0 ||
                SamplesPerChannel < 1)
                throw new InvalidDataException("SafetyAgentConfigInvalid:DaqRuntime");
            var periodMs = SamplesPerChannel * 1000.0 / SampleRateHz;
            if (periodMs < 0.1 || periodMs > 1000)
                throw new InvalidDataException("SafetyAgentConfigInvalid:BatchPeriod");
            if (PressureChannels == null || PressureChannels.Length == 0 ||
                PressureChannels.Any(string.IsNullOrWhiteSpace) ||
                PressureChannels.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                PressureChannels.Length ||
                ReleaseSafePressureBar == null ||
                ReleaseSafePressureBar.Length != PressureChannels.Length ||
                ReleaseSafePressureBar.Any(value => double.IsNaN(value) ||
                                                    double.IsInfinity(value) || value < 0) ||
                PressureSampleMaxAgeMs < 1 || ReleaseStableMs < 0 ||
                ReleaseTimeoutMs < Math.Max(1000, ReleaseStableMs))
                throw new InvalidDataException("SafetyAgentConfigInvalid:PressureRuntime");
        }
    }

    public static class WatchdogTakeoverPermitBindingPolicy
    {
        public static string HashNonce(string nonce) =>
            DurableJsonFileStore.ComputeSha256(
                new UTF8Encoding(false).GetBytes(nonce ?? string.Empty));

        public static bool Matches(
            DurableRelaunchAuthorityRecord record,
            string sessionId,
            long generation,
            string permitId,
            string permitNonceSha256)
        {
            return record != null &&
                   string.Equals(record.SessionId, sessionId, StringComparison.Ordinal) &&
                   record.State >= DurableRelaunchPermitState.Approved &&
                   record.State <= DurableRelaunchPermitState.Committed &&
                   record.Generation == generation && generation > 0 &&
                   string.Equals(record.PermitId, permitId, StringComparison.Ordinal) &&
                   RecoveryFailureReceipt.IsSha256(permitNonceSha256) &&
                   string.Equals(HashNonce(record.PermitNonce), permitNonceSha256,
                       StringComparison.Ordinal);
        }

        public static bool Matches(
            DurableRelaunchPermitRecord record,
            string sessionId,
            long generation,
            string permitId,
            string permitNonceSha256)
        {
            return record != null &&
                   string.Equals(record.SessionId, sessionId, StringComparison.Ordinal) &&
                   record.State >= DurableRelaunchPermitState.Approved &&
                   record.State <= DurableRelaunchPermitState.Committed &&
                   record.Generation == generation && generation > 0 &&
                   string.Equals(record.PermitId, permitId, StringComparison.Ordinal) &&
                   RecoveryFailureReceipt.IsSha256(permitNonceSha256) &&
                   string.Equals(HashNonce(record.PermitNonce), permitNonceSha256,
                       StringComparison.Ordinal);
        }
    }

    public static class WatchdogSafetyConfigSnapshotStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly JavaScriptSerializer Serializer =
            new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private static readonly string[] RequiredFiles =
        {
            "AIConfig.xml",
            "AOConfig.xml",
            "DOConfig.xml",
            "PowerSupplyConfig.xml",
            "TestConfig.xml",
            "safety-runtime.json"
        };

        public static WatchdogSafetyConfigSnapshotResult Create(
            string journalDirectory,
            string handoffId,
            string applicationConfigDirectory,
            string projectConfigDirectory,
            string buildIdentity,
            SafetyRuntimeSnapshot runtime,
            string sessionId,
            long sessionGeneration,
            long sessionLease,
            long permitGeneration,
            string permitId,
            string mainExecutableSha256,
            string safetyAgentExecutableSha256)
        {
            var result = new WatchdogSafetyConfigSnapshotResult();
            string temporaryRoot = null;
            try
            {
                Guid parsed;
                if (!Guid.TryParseExact(handoffId ?? string.Empty, "N", out parsed))
                    throw new InvalidOperationException("SafetyConfigSnapshotHandoffIdInvalid");
                var journalRoot = WatchdogJournalPaths.ValidateProjectDirectory(journalDirectory);
                if (string.IsNullOrWhiteSpace(applicationConfigDirectory))
                    throw new DirectoryNotFoundException(
                        "SafetyConfigSnapshotAppConfigMissing");
                var appConfig = Path.GetFullPath(applicationConfigDirectory ?? string.Empty);
                if (!Directory.Exists(appConfig))
                    throw new DirectoryNotFoundException("SafetyConfigSnapshotAppConfigMissing:" + appConfig);

                var finalRoot = Path.Combine(journalRoot, "safety-handoff-" + handoffId);
                var finalConfig = Path.Combine(finalRoot, "ConfigSnapshot");
                var finalManifest = Path.Combine(finalRoot, "config-snapshot-manifest.json");
                if (Directory.Exists(finalRoot))
                    return Validate(journalRoot, handoffId, finalConfig, finalManifest, null);

                temporaryRoot = finalRoot + ".tmp-" + Guid.NewGuid().ToString("N");
                var temporaryConfig = Path.Combine(temporaryRoot, "ConfigSnapshot");
                Directory.CreateDirectory(temporaryConfig);
                var sources = new Dictionary<string, Tuple<string, string>>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var source in Directory.GetFiles(appConfig, "*", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = RelativePath(appConfig, source);
                    if (string.Equals(
                            relative,
                            "safety-runtime.json",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    CopySnapshotFile(source, temporaryConfig, relative);
                    sources[relative] = Tuple.Create(source, "ApplicationConfig");
                }

                if (!string.IsNullOrWhiteSpace(projectConfigDirectory))
                {
                    var projectTest = Path.Combine(
                        Path.GetFullPath(projectConfigDirectory), "TestConfig.xml");
                    if (File.Exists(projectTest))
                    {
                        CopySnapshotFile(projectTest, temporaryConfig, "TestConfig.xml");
                        sources["TestConfig.xml"] =
                            Tuple.Create(projectTest, "ProjectTestConfig");
                    }
                }

                if (runtime == null)
                    throw new InvalidDataException("SafetyAgentConfigInvalid:RuntimeMissing");
                runtime.Validate();
                var runtimePath = Path.Combine(temporaryConfig, "safety-runtime.json");
                WriteDurable(runtimePath, Utf8.GetBytes(Serializer.Serialize(runtime)));
                sources["safety-runtime.json"] = Tuple.Create(
                    "GeneratedFromValidatedRuntime", "SafetyRuntime");

                var missing = RequiredFiles
                    .Where(name => !File.Exists(Path.Combine(temporaryConfig, name)))
                    .ToArray();
                if (missing.Length > 0)
                    throw new InvalidOperationException(
                        "SafetyConfigSnapshotIncomplete:" + string.Join(",", missing));

                var entries = Directory.GetFiles(temporaryConfig, "*", SearchOption.AllDirectories)
                    .Select(path =>
                    {
                        var relative = RelativePath(temporaryConfig, path);
                        var bytes = File.ReadAllBytes(path);
                        Tuple<string, string> source;
                        sources.TryGetValue(relative, out source);
                        return new WatchdogSafetyConfigSnapshotEntry
                        {
                            RelativePath = relative,
                            Role = source?.Item2 ?? "ApplicationConfig",
                            SourcePath = source?.Item1 ?? string.Empty,
                            Length = bytes.LongLength,
                            Sha256 = DurableJsonFileStore.ComputeSha256(bytes)
                        };
                    })
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var manifest = new WatchdogSafetyConfigSnapshotManifest
                {
                    HandoffId = handoffId,
                    SessionId = sessionId ?? string.Empty,
                    SessionGeneration = sessionGeneration,
                    SessionLease = sessionLease,
                    PermitGeneration = permitGeneration,
                    PermitId = permitId ?? string.Empty,
                    MainExecutableSha256 = mainExecutableSha256 ?? string.Empty,
                    SafetyAgentExecutableSha256 = safetyAgentExecutableSha256 ?? string.Empty,
                    BuildIdentity = buildIdentity ?? string.Empty,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks,
                    Files = entries
                };
                var manifestBytes = Utf8.GetBytes(Serializer.Serialize(manifest));
                var temporaryManifest = Path.Combine(
                    temporaryRoot, "config-snapshot-manifest.json");
                WriteDurable(temporaryManifest, manifestBytes);
                Directory.Move(temporaryRoot, finalRoot);
                temporaryRoot = null;
                return Validate(
                    journalRoot,
                    handoffId,
                    finalConfig,
                    finalManifest,
                    DurableJsonFileStore.ComputeSha256(manifestBytes));
            }
            catch (Exception ex)
            {
                result.Error = ex.GetBaseException().Message;
                return result;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(temporaryRoot) &&
                        Directory.Exists(temporaryRoot))
                        Directory.Delete(temporaryRoot, true);
                }
                catch { }
            }
        }

        public static WatchdogSafetyConfigSnapshotResult Validate(
            string journalDirectory,
            string handoffId,
            string configDirectory,
            string manifestPath,
            string expectedManifestSha256)
        {
            var result = new WatchdogSafetyConfigSnapshotResult();
            try
            {
                var journalRoot = WatchdogJournalPaths.ValidateProjectDirectory(journalDirectory);
                Guid parsed;
                if (!Guid.TryParseExact(handoffId ?? string.Empty, "N", out parsed))
                    throw new InvalidDataException("SafetyConfigSnapshotHandoffIdInvalid");
                var configRoot = RequireDescendant(journalRoot, configDirectory);
                var manifestFullPath = RequireDescendant(journalRoot, manifestPath);
                var expectedRoot = Path.Combine(
                    journalRoot,
                    "safety-handoff-" + handoffId);
                var expectedConfig = Path.Combine(expectedRoot, "ConfigSnapshot");
                var expectedManifest = Path.Combine(
                    expectedRoot,
                    "config-snapshot-manifest.json");
                if (!string.Equals(
                        configRoot,
                        Path.GetFullPath(expectedConfig),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        manifestFullPath,
                        Path.GetFullPath(expectedManifest),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "SafetyConfigSnapshotHandoffPathMismatch");
                if (!Directory.Exists(configRoot))
                    throw new DirectoryNotFoundException("SafetyConfigSnapshotDirectoryMissing");
                if (!File.Exists(manifestFullPath))
                    throw new FileNotFoundException("SafetyConfigSnapshotManifestMissing");

                var bytes = File.ReadAllBytes(manifestFullPath);
                var manifestSha = DurableJsonFileStore.ComputeSha256(bytes);
                if (!string.IsNullOrWhiteSpace(expectedManifestSha256) &&
                    !string.Equals(manifestSha, expectedManifestSha256,
                        StringComparison.Ordinal))
                    throw new InvalidDataException("SafetyConfigSnapshotManifestHashMismatch");
                var manifest = Serializer.Deserialize<WatchdogSafetyConfigSnapshotManifest>(
                    Utf8.GetString(bytes));
                if (manifest == null || manifest.SchemaVersion != 2 ||
                    !string.Equals(manifest.HandoffId, handoffId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(manifest.SessionId) ||
                    manifest.SessionGeneration <= 0 || manifest.SessionLease <= 0 ||
                    !RecoveryFailureReceipt.IsSha256(manifest.MainExecutableSha256) ||
                    !RecoveryFailureReceipt.IsSha256(manifest.SafetyAgentExecutableSha256) ||
                    manifest.Files == null || manifest.Files.Length == 0)
                    throw new InvalidDataException("SafetyConfigSnapshotManifestInvalid");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in manifest.Files)
                {
                    var relative = NormalizeRelative(entry?.RelativePath);
                    if (!seen.Add(relative))
                        throw new InvalidDataException("SafetyConfigSnapshotDuplicateEntry:" + relative);
                    var path = RequireDescendant(configRoot, Path.Combine(configRoot, relative));
                    if (!File.Exists(path))
                        throw new FileNotFoundException("SafetyConfigSnapshotFileMissing:" + relative);
                    var fileBytes = File.ReadAllBytes(path);
                    if (fileBytes.LongLength != entry.Length ||
                        !string.Equals(
                            DurableJsonFileStore.ComputeSha256(fileBytes),
                            entry.Sha256,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("SafetyConfigSnapshotFileHashMismatch:" + relative);
                }

                var actual = Directory.GetFiles(configRoot, "*", SearchOption.AllDirectories)
                    .Select(path => RelativePath(configRoot, path))
                    .ToArray();
                if (actual.Any(path => !seen.Contains(path)) || actual.Length != seen.Count)
                    throw new InvalidDataException("SafetyConfigSnapshotUnmanifestedFile");
                var missing = RequiredFiles.Where(name => !seen.Contains(name)).ToArray();
                if (missing.Length > 0)
                    throw new InvalidDataException(
                        "SafetyConfigSnapshotIncomplete:" + string.Join(",", missing));

                var runtimeBytes = File.ReadAllBytes(Path.Combine(configRoot, "safety-runtime.json"));
                var runtime = Serializer.Deserialize<SafetyRuntimeSnapshot>(Utf8.GetString(runtimeBytes));
                if (runtime == null)
                    throw new InvalidDataException("SafetyAgentConfigInvalid:RuntimeMissing");
                runtime.Validate();

                result.Succeeded = true;
                result.SnapshotDirectory = Directory.GetParent(configRoot)?.FullName ?? configRoot;
                result.ConfigDirectory = configRoot;
                result.ManifestPath = manifestFullPath;
                result.ManifestSha256 = manifestSha;
                result.Manifest = manifest;
                result.Runtime = runtime;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetBaseException().Message;
                return result;
            }
        }

        private static void CopySnapshotFile(
            string source,
            string destinationRoot,
            string relativePath)
        {
            var relative = NormalizeRelative(relativePath);
            var destination = RequireDescendant(
                destinationRoot,
                Path.Combine(destinationRoot, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }

        private static void WriteDurable(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(
                       path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static string RelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
            var pathUri = new Uri(Path.GetFullPath(path));
            return NormalizeRelative(Uri.UnescapeDataString(
                rootUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string NormalizeRelative(string relative)
        {
            var value = (relative ?? string.Empty)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
                value.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
                throw new InvalidDataException("SafetyConfigSnapshotRelativePathInvalid");
            return value;
        }

        private static string RequireDescendant(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SafetyConfigSnapshotPathEscapesJournal");
            return fullPath;
        }

        private static string AppendDirectorySeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
    }
}
