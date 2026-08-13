using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    public sealed class IncidentSessionRetentionOptions
    {
        public int RetainCompleteSessionsPerDevice { get; set; } = 10;
        public bool DryRun { get; set; }
        public int MaxFilesPerBatch { get; set; } = 16;
        public long MaxBytesPerBatch { get; set; } = 32L * 1024L * 1024L;
        public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Optional cancellation-aware wait hook used by deterministic tests.
        /// The return value follows WaitHandle.WaitOne semantics: true means
        /// the wait was interrupted/cancelled. Production leaves this null and
        /// waits on the cancellation token's handle.
        /// </summary>
        internal Func<TimeSpan, CancellationToken, bool> BatchWaiter { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Action<string> WarningSink { get; set; }

        internal IncidentSessionRetentionOptions Normalize()
        {
            return new IncidentSessionRetentionOptions
            {
                RetainCompleteSessionsPerDevice = Math.Max(1, Math.Min(10000, RetainCompleteSessionsPerDevice)),
                DryRun = DryRun,
                MaxFilesPerBatch = Math.Max(1, Math.Min(16, MaxFilesPerBatch)),
                MaxBytesPerBatch = Math.Max(1, Math.Min(32L * 1024L * 1024L, MaxBytesPerBatch)),
                BatchInterval = BatchInterval < TimeSpan.Zero ? TimeSpan.Zero : BatchInterval,
                BatchWaiter = BatchWaiter,
                CancellationToken = CancellationToken,
                WarningSink = WarningSink
            };
        }
    }

    public sealed class IncidentSessionRetentionResult
    {
        public IReadOnlyList<string> Candidates { get; internal set; } = Array.Empty<string>();
        public int DeletedDirectories { get; internal set; }
        public long DeletedBytes { get; internal set; }
        public int SkippedDirectories { get; internal set; }
        public int FailedDirectories { get; internal set; }
        public bool DryRun { get; internal set; }
    }

    /// <summary>
    /// Conservative retention planner for modern terminal IncidentSnapshots.
    /// Legacy/unknown/active roots are never candidates. A shared root is only
    /// deleted when every device in that root is terminal and quiet and every
    /// device is outside its own most-recent-N retention set.
    /// </summary>
    public static class IncidentSessionRetention
    {
        private const string StagingPrefix = ".incident-retention-staging-";
        private const string SidecarSuffix = ".incident-retention.json";
        private const string PendingSuffix = ".incident-retention.pending.json";

        [DataContract]
        private sealed class RetentionSidecar
        {
            [DataMember(Order = 1)] public int Schema { get; set; } = 1;
            [DataMember(Order = 2)] public string Source { get; set; } = string.Empty;
            [DataMember(Order = 3)] public string Staging { get; set; } = string.Empty;
            [DataMember(Order = 4)] public DateTime StagedUtc { get; set; }
        }

        public static IReadOnlyList<string> FindCandidates(
            string incidentRoot,
            int retainCompleteSessionsPerDevice = 10,
            Action<string> warningSink = null)
        {
            var options = new IncidentSessionRetentionOptions
            {
                RetainCompleteSessionsPerDevice = retainCompleteSessionsPerDevice,
                WarningSink = warningSink
            }.Normalize();
            return FindCandidates(incidentRoot, options, out _);
        }

        public static IReadOnlyList<string> FindCandidates(
            string incidentRoot,
            IncidentSessionRetentionOptions options,
            out int skipped)
        {
            skipped = 0;
            options = (options ?? new IncidentSessionRetentionOptions()).Normalize();
            if (string.IsNullOrWhiteSpace(incidentRoot) || !Directory.Exists(incidentRoot))
                return Array.Empty<string>();
            var root = NormalizeRoot(incidentRoot);
            var manifests = new List<Tuple<string, IncidentSessionManifest>>();
            foreach (var directory in SafeDirectories(root))
            {
                if (Path.GetFileName(directory).StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsUnderRoot(root, directory)) { skipped++; continue; }
                if (IsReparse(directory)) { skipped++; Warn(options, $"IncidentRetention reparse跳过：{directory}"); continue; }
                if (File.Exists(directory + PendingSuffix))
                {
                    skipped++;
                    Warn(options, $"IncidentRetention pending sidecar 未收口，跳过：{directory}");
                    continue;
                }
                if (!IncidentSessionManifestStore.TryReadValidated(directory, out var manifest, out _))
                {
                    skipped++;
                    continue;
                }
                if (!ValidateManifestReceipts(directory, manifest, options))
                {
                    skipped++;
                    continue;
                }
                if (!HasOnlyManifestDeclaredEntries(directory, manifest, options))
                {
                    skipped++;
                    continue;
                }
                manifests.Add(Tuple.Create(directory, manifest));
            }

            var rankByPath = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var allDevices = manifests.SelectMany(item => item.Item2.Devices ?? new List<IncidentDeviceManifestState>())
                .Select(item => IncidentSessionPolicy.NormalizeDevice(item.Device))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var device in allDevices)
            {
                var ordered = manifests
                    .Where(item => (item.Item2.Devices ?? new List<IncidentDeviceManifestState>())
                        .Any(state => string.Equals(
                            IncidentSessionPolicy.NormalizeDevice(state.Device), device,
                            StringComparison.OrdinalIgnoreCase) && state.TerminalSeen && state.Quiet))
                    .OrderByDescending(item => item.Item2.CompletedUtc)
                    .ThenByDescending(item => item.Item1, StringComparer.OrdinalIgnoreCase)
                    .Select((item, index) => new { item.Item1, Rank = index })
                    .ToArray();
                foreach (var item in ordered)
                {
                    if (!rankByPath.TryGetValue(item.Item1, out var byDevice))
                        rankByPath[item.Item1] = byDevice = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    byDevice[device] = item.Rank;
                }
            }

            var result = manifests
                .Where(item =>
                {
                    var states = item.Item2.Devices ?? new List<IncidentDeviceManifestState>();
                    if (states.Count == 0 || states.Any(state =>
                            !state.TriggerSeen || !state.TerminalSeen || !state.Quiet ||
                            !string.Equals(state.Status, "Terminal", StringComparison.OrdinalIgnoreCase)))
                        return false;
                    // A root shared by two devices may only be removed when both
                    // devices have aged beyond the retention count.
                    return states.All(state =>
                    {
                        if (!rankByPath.TryGetValue(item.Item1, out var byDevice) ||
                            !byDevice.TryGetValue(IncidentSessionPolicy.NormalizeDevice(state.Device), out var rank)) return false;
                        return rank >= options.RetainCompleteSessionsPerDevice;
                    });
                })
                .Select(item => item.Item1)
                .ToArray();
            return result;
        }

        public static IncidentSessionRetentionResult Enforce(
            string incidentRoot,
            IncidentSessionRetentionOptions options = null)
        {
            options = (options ?? new IncidentSessionRetentionOptions()).Normalize();
            var candidates = FindCandidates(incidentRoot, options, out var skipped);
            var result = new IncidentSessionRetentionResult
            {
                Candidates = candidates,
                SkippedDirectories = skipped,
                DryRun = options.DryRun
            };
            if (options.DryRun) return result;

            var root = NormalizeRoot(incidentRoot);
            foreach (var path in candidates)
            {
                if (!IsUnderRoot(root, path))
                {
                    result.SkippedDirectories++;
                    Warn(options, $"IncidentRetention越界跳过：{path}");
                    continue;
                }
                var bytes = IncidentSessionManifestStore.GetDirectoryBytes(path);
                var staging = Path.Combine(
                    Path.GetDirectoryName(path) ?? root,
                    StagingPrefix + Guid.NewGuid().ToString("N"));
                var pending = PendingSidecarPath(path);
                try
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    if (IsReparse(path))
                    {
                        result.SkippedDirectories++;
                        Warn(options, $"IncidentRetention reparse跳过：{path}");
                        continue;
                    }
                    // Persist the intended move before touching the source. A
                    // crash before Directory.Move leaves a durable pending
                    // record which the next worker invocation can complete.
                    WriteSidecar(pending, path, staging, flush: true);
                    Directory.Move(path, staging);
                    PromotePendingSidecar(pending, staging);
                    DeleteStagingInBatches(staging, options);
                    TryDeleteFile(RetentionSidecarPath(staging));
                    result.DeletedDirectories++;
                    result.DeletedBytes += bytes;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    result.FailedDirectories++;
                    Warn(options, $"IncidentRetention删除失败（不影响主进程）：{path}; {ex}");
                    // If rename succeeded but deletion failed, leave staging for
                    // a subsequent low-load invocation; never move it back while
                    // the producer might still hold a file handle.
                }
            }
            return result;
        }

        internal static void DeleteStagingInBatches(
            string staging,
            IncidentSessionRetentionOptions options)
        {
            if (IsReparse(staging)) throw new IOException("staging是reparse点");
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories))
                if (IsReparse(entry)) throw new IOException("staging包含reparse点");
            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var index = 0;
            while (index < files.Length)
            {
                var count = 0;
                long bytes = 0;
                while (index < files.Length && count < options.MaxFilesPerBatch)
                {
                    ThrowIfCancelled(options);
                    if (!File.Exists(files[index]))
                    {
                        index++;
                        continue;
                    }
                    FileInfo info;
                    try { info = new FileInfo(files[index]); }
                    catch (FileNotFoundException) { index++; continue; }
                    catch (DirectoryNotFoundException) { index++; continue; }
                    if (!info.Exists) { index++; continue; }
                    long length;
                    try { length = info.Length; }
                    catch (FileNotFoundException) { index++; continue; }
                    catch (DirectoryNotFoundException) { index++; continue; }
                    if (length > options.MaxBytesPerBatch)
                    {
                        Warn(options,
                            $"IncidentRetention单文件超过批次预算，保留并退避：{files[index]} ({length} bytes)");
                        // Do not delete the oversized file. Throwing keeps the
                        // containing staging tree and its sidecar in place so a
                        // later low-load pass can retry after configuration or
                        // disk conditions change.
                        throw new IOException("单文件超过IncidentRetention批次字节预算");
                    }
                    if (count > 0 && bytes + length > options.MaxBytesPerBatch) break;
                    File.Delete(files[index]);
                    bytes += length;
                    count++;
                    index++;
                }
                if (count == 0) break;
                if (index < files.Length && options.BatchInterval > TimeSpan.Zero)
                    if (WaitBatchInterval(options))
                        throw new OperationCanceledException(options.CancellationToken);
            }
            ThrowIfCancelled(options);
            foreach (var directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
                         .OrderByDescending(item => item.Length))
                Directory.Delete(directory, false);
            Directory.Delete(staging, false);
        }

        private static IEnumerable<string> SafeDirectories(string root)
        {
            try { return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static string NormalizeRoot(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        private static bool IsUnderRoot(string root, string path)
        {
            var fullRoot = NormalizeRoot(root);
            var full = Path.GetFullPath(path);
            return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReparse(string path)
        {
            try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
            catch { return true; }
        }

        private static string PendingSidecarPath(string source) => source + PendingSuffix;
        private static string RetentionSidecarPath(string staging) => staging + SidecarSuffix;

        private static void ThrowIfCancelled(IncidentSessionRetentionOptions options)
        {
            if (options.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(options.CancellationToken);
        }

        private static bool WaitBatchInterval(IncidentSessionRetentionOptions options)
        {
            ThrowIfCancelled(options);
            if (options.BatchInterval <= TimeSpan.Zero) return false;
            if (options.BatchWaiter != null)
            {
                var interrupted = options.BatchWaiter(options.BatchInterval, options.CancellationToken);
                if (interrupted || options.CancellationToken.IsCancellationRequested)
                    return true;
                return false;
            }
            return options.CancellationToken.WaitHandle.WaitOne(options.BatchInterval);
        }

        private static void WriteSidecar(string path, string source, string staging, bool flush)
        {
            var temporary = path + ".tmp";
            var sidecar = new RetentionSidecar
            {
                Source = source ?? string.Empty,
                Staging = staging ?? string.Empty,
                StagedUtc = DateTime.UtcNow
            };
            var serializer = new DataContractJsonSerializer(typeof(RetentionSidecar));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    serializer.WriteObject(stream, sidecar);
                    if (flush) stream.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static void PromotePendingSidecar(string pending, string staging)
        {
            var target = RetentionSidecarPath(staging);
            if (File.Exists(target))
            {
                TryDeleteFile(pending);
                return;
            }
            File.Move(pending, target);
        }

        private static bool TryReadSidecar(string path, out RetentionSidecar sidecar)
        {
            sidecar = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || IsReparse(path)) return false;
                var serializer = new DataContractJsonSerializer(typeof(RetentionSidecar));
                using (var stream = File.OpenRead(path))
                    sidecar = serializer.ReadObject(stream) as RetentionSidecar;
                return sidecar != null && sidecar.Schema == 1 &&
                       !string.IsNullOrWhiteSpace(sidecar.Source) &&
                       !string.IsNullOrWhiteSpace(sidecar.Staging);
            }
            catch
            {
                sidecar = null;
                return false;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static bool HasOnlyManifestDeclaredEntries(
            string directory,
            IncidentSessionManifest manifest,
            IncidentSessionRetentionOptions options)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
                {
                    ThrowIfCancelled(options);
                    if (IsReparse(entry))
                    {
                        Warn(options, $"IncidentRetention reparse条目跳过：{entry}");
                        return false;
                    }
                    var name = Path.GetFileName(entry);
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(PendingSuffix, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        Warn(options, $"IncidentRetention未知临时/sidecar条目跳过：{entry}");
                        return false;
                    }
                    var relative = MakeRelative(directory, entry);
                    // A phase receipt declares its whole phase directory.  Heavy
                    // evidence and the per-phase build identity live below that
                    // directory, so they are known entries even though the
                    // receipt only names incident.json and phase.receipt.json.
                    var declared = manifest.Phases.Any(phase =>
                        phase != null &&
                        (string.Equals(phase.PhaseDirectory, relative, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(phase.JsonPath, relative, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(phase.ReceiptPath, relative, StringComparison.OrdinalIgnoreCase) ||
                         IsRelativeWithin(relative, phase.PhaseDirectory))) ||
                        string.Equals(relative, IncidentSessionManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(relative, "build-identity.json", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(relative, "manifest.jsonl", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(relative), "build-identity.json", StringComparison.OrdinalIgnoreCase);
                    if (!declared && File.Exists(entry))
                    {
                        Warn(options, $"IncidentRetention未知文件跳过：{entry}");
                        return false;
                    }
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private static bool ValidateManifestReceipts(
            string directory,
            IncidentSessionManifest manifest,
            IncidentSessionRetentionOptions options)
        {
            try
            {
                foreach (var phase in manifest?.Phases ?? new List<IncidentPhaseReceipt>())
                {
                    ThrowIfCancelled(options);
                    if (phase == null || !IsSafeRelativePath(phase.PhaseDirectory) ||
                        !IsSafeRelativePath(phase.JsonPath) || !IsSafeRelativePath(phase.ReceiptPath))
                    {
                        Warn(options, $"IncidentRetention phase路径非法，跳过：{directory}");
                        return false;
                    }
                    var phaseDirectory = ResolveRelative(directory, phase.PhaseDirectory);
                    var jsonPath = ResolveRelative(directory, phase.JsonPath);
                    var receiptPath = ResolveRelative(directory, phase.ReceiptPath);
                    if (phaseDirectory == null || jsonPath == null || receiptPath == null ||
                        !Directory.Exists(phaseDirectory) || !File.Exists(jsonPath) ||
                        !File.Exists(receiptPath) || IsReparse(phaseDirectory) ||
                        IsReparse(jsonPath) || IsReparse(receiptPath)) return false;
                    IncidentPhaseReceipt stored;
                    try
                    {
                        var serializer = new DataContractJsonSerializer(typeof(IncidentPhaseReceipt));
                        using (var stream = File.OpenRead(receiptPath))
                            stored = serializer.ReadObject(stream) as IncidentPhaseReceipt;
                    }
                    catch { return false; }
                    if (stored == null || !stored.JsonCommitted ||
                        !string.Equals(NormalizeRelative(stored.PhaseDirectory), NormalizeRelative(phase.PhaseDirectory), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(NormalizeRelative(stored.JsonPath), NormalizeRelative(phase.JsonPath), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(NormalizeRelative(stored.ReceiptPath), NormalizeRelative(phase.ReceiptPath), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(stored.PhaseKey, phase.PhaseKey, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(stored.Device, phase.Device, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(stored.Sha256, IncidentSessionManifestStore.ComputeSha256(jsonPath), StringComparison.OrdinalIgnoreCase) ||
                        stored.Bytes != phase.Bytes ||
                        IncidentSessionManifestStore.GetDirectoryBytes(phaseDirectory) < Math.Max(0, stored.Bytes))
                        return false;
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private static string ResolveRelative(string root, string relative)
        {
            try
            {
                if (!IsSafeRelativePath(relative)) return null;
                var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                return IsUnderRoot(root, full) ? full : null;
            }
            catch { return null; }
        }

        private static bool IsSafeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(":") ||
                normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")) return false;
            return true;
        }

        private static string NormalizeRelative(string path) =>
            (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

        /// <summary>
        /// Completes a move/sidecar transaction left by a process crash.  Only
        /// sidecars written by this implementation and rooted below the given
        /// incident root are acted upon; an unmarked staging directory is left
        /// untouched for operator inspection.
        /// </summary>
        internal static void ResumeStaging(
            string incidentRoot,
            CancellationToken cancellationToken,
            Action<string> warningSink,
            Func<TimeSpan, CancellationToken, bool> batchWaiter = null)
        {
            if (string.IsNullOrWhiteSpace(incidentRoot) || !Directory.Exists(incidentRoot)) return;
            var root = NormalizeRoot(incidentRoot);
            var options = new IncidentSessionRetentionOptions
            {
                // Resume uses the same safe cadence as normal enforcement;
                // callers/tests may inject a cancellable waiter without
                // weakening the production five-second throttle.
                BatchInterval = TimeSpan.FromSeconds(5),
                BatchWaiter = batchWaiter,
                CancellationToken = cancellationToken,
                WarningSink = warningSink
            }.Normalize();

            // A pending sidecar is written before Directory.Move.  If the
            // source still exists, the move did not happen and the pending
            // marker can be removed so the next Enforce pass can re-plan it.
            foreach (var pending in SafeFiles(root, "*" + PendingSuffix))
            {
                ThrowIfCancelled(options);
                if (!TryReadSidecar(pending, out var sidecar))
                {
                    Warn(options, $"IncidentRetention未知pending sidecar跳过：{pending}");
                    continue;
                }
                var expectedSource = pending.Substring(0, pending.Length - PendingSuffix.Length);
                if (!IsValidSidecar(root, sidecar, expectedSource, sidecar.Staging))
                {
                    Warn(options, $"IncidentRetention非法pending sidecar跳过：{pending}");
                    continue;
                }

                var source = Path.GetFullPath(expectedSource);
                var staging = Path.GetFullPath(sidecar.Staging);
                var target = RetentionSidecarPath(staging);
                var sourceExists = Directory.Exists(source);
                var stagingExists = Directory.Exists(staging);
                if (sourceExists && !stagingExists)
                {
                    if (IsReparse(source))
                    {
                        Warn(options, $"IncidentRetention pending源为reparse，保留：{source}");
                        continue;
                    }
                    TryDeleteFile(pending);
                    continue;
                }
                if (!sourceExists && stagingExists)
                {
                    if (IsReparse(staging))
                    {
                        Warn(options, $"IncidentRetention pending staging为reparse，保留：{staging}");
                        continue;
                    }
                    try
                    {
                        PromotePendingSidecar(pending, staging);
                        DeleteStagingInBatches(staging, options);
                        TryDeleteFile(target);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Warn(options, $"IncidentRetention pending续扫失败（保留staging）：{staging}; {ex.Message}");
                    }
                    continue;
                }
                // Both paths existing is a move race/collision.  Never guess
                // which tree is authoritative and never delete either one.
                if (sourceExists && stagingExists)
                    Warn(options, $"IncidentRetention pending源与staging同时存在，保留：{pending}");
                else
                    Warn(options, $"IncidentRetention pending路径不存在，保留：{pending}");
            }

            foreach (var staging in SafeDirectories(root))
            {
                ThrowIfCancelled(options);
                if (!Path.GetFileName(staging).StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !IsUnderRoot(root, staging) || IsReparse(staging)) continue;
                var sidecarPath = RetentionSidecarPath(staging);
                if (!TryReadSidecar(sidecarPath, out var sidecar) ||
                    !IsValidSidecar(root, sidecar, sidecar.Source, staging))
                {
                    // Unknown staging is intentionally not removed.
                    continue;
                }
                try
                {
                    DeleteStagingInBatches(staging, options);
                    TryDeleteFile(sidecarPath);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Warn(options, $"IncidentRetention staging续扫失败（保留staging）：{staging}; {ex.Message}");
                }
            }
        }

        private static IEnumerable<string> SafeFiles(string root, string pattern)
        {
            try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        private static bool IsValidSidecar(
            string root,
            RetentionSidecar sidecar,
            string expectedSource,
            string expectedStaging)
        {
            try
            {
                if (sidecar == null || sidecar.Schema != 1 ||
                    string.IsNullOrWhiteSpace(sidecar.Source) ||
                    string.IsNullOrWhiteSpace(sidecar.Staging) ||
                    !Path.IsPathRooted(sidecar.Source) || !Path.IsPathRooted(sidecar.Staging)) return false;
                var source = Path.GetFullPath(sidecar.Source);
                var staging = Path.GetFullPath(sidecar.Staging);
                if (!IsUnderRoot(root, source) || !IsUnderRoot(root, staging) ||
                    !string.Equals(source, Path.GetFullPath(expectedSource), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(staging, Path.GetFullPath(expectedStaging), StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(staging).StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                var sourceParent = Path.GetDirectoryName(source);
                var stagingParent = Path.GetDirectoryName(staging);
                return string.Equals(sourceParent, stagingParent, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        private static string MakeRelative(string root, string path)
        {
            var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(basePath.Length).Replace(Path.DirectorySeparatorChar, '/')
                : full;
        }

        private static bool IsRelativeWithin(string relative, string directory)
        {
            if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(directory)) return false;
            var normalizedRelative = relative.Replace('\\', '/').TrimStart('/');
            var normalizedDirectory = directory.Replace('\\', '/').Trim('/');
            return normalizedRelative.StartsWith(normalizedDirectory + "/",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void Warn(IncidentSessionRetentionOptions options, string message)
        {
            try { options.WarningSink?.Invoke(message); } catch { }
        }
    }

    /// <summary>
    /// Independent low-priority incident retention worker. Export code only
    /// enqueues the root; all scanning/rename/delete happens here and failures
    /// are reduced to warnings. A busy DAQ/recovery/stop window is deferred.
    /// </summary>
    public sealed class IncidentHousekeepingService : IDisposable
    {
        private readonly BlockingCollection<string> _queue =
            new BlockingCollection<string>(new ConcurrentQueue<string>(), 32);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Func<bool> _busy;
        private readonly int _retain;
        private readonly Action<string> _warning;
        private readonly Task _worker;
        private int _disposed;

        public IncidentHousekeepingService(
            int retainCompleteSessionsPerDevice,
            Func<bool> busyProvider,
            Action<string> warningSink)
        {
            _retain = Math.Max(1, retainCompleteSessionsPerDevice);
            _busy = busyProvider;
            _warning = warningSink;
            _worker = Task.Factory.StartNew(WorkerLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public bool Enqueue(string incidentRoot)
        {
            if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(incidentRoot)) return false;
            try { return _queue.TryAdd(Path.GetFullPath(incidentRoot)); }
            catch { return false; }
        }

        private void WorkerLoop()
        {
            try
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                foreach (var root in _queue.GetConsumingEnumerable(_stop.Token))
                {
                    if (_stop.IsCancellationRequested) break;
                    try
                    {
                        if (_busy?.Invoke() == true)
                        {
                            Warn($"IncidentRetention忙碌延期：{root}");
                            if (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                                _queue.TryAdd(root);
                            continue;
                        }
                        IncidentSessionRetention.ResumeStaging(root, _stop.Token, _warning);
                        IncidentSessionRetention.Enforce(root, new IncidentSessionRetentionOptions
                        {
                            RetainCompleteSessionsPerDevice = _retain,
                            CancellationToken = _stop.Token,
                            WarningSink = _warning
                        });
                    }
                    catch (Exception ex) { Warn($"IncidentRetention worker失败（跳过，不影响主进程）：{ex.Message}"); }
                    // Keep deletion I/O off the hot path and below the same
                    // five-second batch cadence as the existing housekeeping.
                    try { if (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) { } }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Warn($"IncidentRetention worker退出：{ex.Message}"); }
        }

        private void Warn(string message)
        {
            try { (_warning ?? Console.Error.WriteLine)?.Invoke(message); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _queue.CompleteAdding(); } catch { }
            try { _stop.Cancel(); } catch { }
            var stopped = false;
            try { stopped = _worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
            // A cancellation timeout is deliberately treated as a resource
            // leak rather than disposing objects the still-running worker may
            // touch.  The worker observes the token and performs no new work;
            // the process can reclaim these handles at shutdown.
            if (stopped)
            {
                try { _queue.Dispose(); } catch { }
                try { _stop.Dispose(); } catch { }
            }
        }
    }
}
