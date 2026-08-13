using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;

namespace Controller
{
    public sealed class HousekeepingBusyState
    {
        public bool DaqQueueNonEmpty { get; set; }
        public double DaqQueueAgeMs { get; set; }
        public bool AlarmBusy { get; set; }
        public bool StopBusy { get; set; }
        public bool RecoveryBusy { get; set; }
        public bool IsBusy => DaqQueueNonEmpty || DaqQueueAgeMs > 0 || AlarmBusy || StopBusy || RecoveryBusy;
    }

    public sealed class DataHousekeepingOptions
    {
        public int QueueCapacity { get; set; } = 128;
        public int MaxFilesPerBatch { get; set; } = 16;
        public long MaxBytesPerBatch { get; set; } = 32L * 1024L * 1024L;
        public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxRetryCount { get; set; } = 8;
        public bool DryRun { get; set; }
        /// <summary>Startup scan is enabled for production; tests/manual callers can opt out.</summary>
        public bool AutoScanOnStart { get; set; } = true;
        /// <summary>
        /// Retention is explicitly enabled only for Count mode.  Unlimited
        /// mode still constructs the service for lifecycle symmetry, but every
        /// scan/enqueue/process entry point must remain a no-op so a restart
        /// worker can never delete learning chains.
        /// </summary>
        public bool RetentionEnabled { get; set; } = true;
        public string RequiredDirectoryNameRegex { get; set; } = "^[0-9a-fA-F]{32}$";
        public Func<string, bool> IsCurrentPath { get; set; }
        /// <summary>由上层恢复协调器提供的受保护路径判定；Controller 不读取 checkpoint。</summary>
        public Func<string, bool> IsProtectedPath { get; set; }
        public IEnumerable<Guid> ProtectedRootIds { get; set; }
        public Func<HousekeepingBusyState> BusyStateProvider { get; set; }
        public Action<string> WarningSink { get; set; }
    }

    public sealed class DataHousekeepingStatistics
    {
        private long _queued;
        private long _processed;
        private long _staged;
        private long _deletedFiles;
        private long _deletedBytes;
        private long _skipped;
        private long _failed;
        private long _busyDeferred;

        public long Queued => Interlocked.Read(ref _queued);
        public long Processed => Interlocked.Read(ref _processed);
        public long Staged => Interlocked.Read(ref _staged);
        public long DeletedFiles => Interlocked.Read(ref _deletedFiles);
        public long DeletedBytes => Interlocked.Read(ref _deletedBytes);
        public long Skipped => Interlocked.Read(ref _skipped);
        public long Failed => Interlocked.Read(ref _failed);
        public long BusyDeferred => Interlocked.Read(ref _busyDeferred);
        internal void IncQueued() => Interlocked.Increment(ref _queued);
        internal void IncProcessed() => Interlocked.Increment(ref _processed);
        internal void IncStaged() => Interlocked.Increment(ref _staged);
        internal void AddDeleted(long bytes) { Interlocked.Increment(ref _deletedFiles); Interlocked.Add(ref _deletedBytes, bytes); }
        internal void IncSkipped() => Interlocked.Increment(ref _skipped);
        internal void IncFailed() => Interlocked.Increment(ref _failed);
        internal void IncBusy() => Interlocked.Increment(ref _busyDeferred);
        public override string ToString() =>
            $"Queued={Queued} Processed={Processed} Staged={Staged} DeletedFiles={DeletedFiles} " +
            $"DeletedBytes={DeletedBytes} Skipped={Skipped} Failed={Failed} BusyDeferred={BusyDeferred}";
    }

    /// <summary>
    /// Independent, low-priority retention worker.  It deliberately has no
    /// dependency on TaskSupervisor, EpbManager, SQLite or recovery locks.
    /// Failures are warnings and never escape the worker thread.
    /// </summary>
    public sealed class DataHousekeepingService : IDisposable
    {
        private const string StagingPrefix = ".retention-staging-";
        private const string SidecarSuffix = ".retention.json";
        private readonly string _managedRoot;
        private readonly DataHousekeepingOptions _options;
        private readonly Regex _nameRegex;
        private sealed class HousekeepingCommand
        {
            internal string Path;
            internal bool ScanRoot;
            internal int SuccessfulRetainCount;
            internal int FailedRetainCount;
            internal long NotBeforeUtcTicks;
        }

        [DataContract]
        private sealed class RetentionSidecar
        {
            [DataMember(Name = "schema")] public int Schema { get; set; }
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "staging")] public string Staging { get; set; }
            [DataMember(Name = "chainRunId")] public string ChainRunId { get; set; }
            [DataMember(Name = "manifestHash")] public string ManifestHash { get; set; }
        }

        private readonly BlockingCollection<HousekeepingCommand> _queue;
        private readonly ConcurrentDictionary<string, byte> _protectedRoots =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _scheduledRetries =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Thread _worker;
        private readonly object _batchGate = new object();
        private readonly DataHousekeepingStatistics _statistics = new DataHousekeepingStatistics();
        private long _lastBatchTicks;
        private int _disposed;

        public DataHousekeepingService(string managedRoot, DataHousekeepingOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(managedRoot)) throw new ArgumentException("受管根目录不能为空。", nameof(managedRoot));
            _managedRoot = NormalizeRoot(managedRoot);
            _options = options ?? new DataHousekeepingOptions();
            _options.QueueCapacity = Math.Max(1, _options.QueueCapacity);
            _options.MaxFilesPerBatch = Math.Min(16, Math.Max(1, _options.MaxFilesPerBatch));
            _options.MaxBytesPerBatch = Math.Min(32L * 1024L * 1024L, Math.Max(1, _options.MaxBytesPerBatch));
            _options.MaxRetryCount = Math.Max(0, _options.MaxRetryCount);
            _nameRegex = new Regex(_options.RequiredDirectoryNameRegex ?? "^[0-9a-fA-F]{32}$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);
            _queue = new BlockingCollection<HousekeepingCommand>(
                new ConcurrentQueue<HousekeepingCommand>(), _options.QueueCapacity);
            foreach (var root in _options.ProtectedRootIds ?? Enumerable.Empty<Guid>()) ProtectRoot(root);
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "EPB.DataHousekeeping"
            };
            _worker.Start();
            // Construction must remain O(1): recovery/staging enumeration and all
            // manifest/hash work are performed by the BelowNormal worker.
            if (_options.RetentionEnabled && _options.AutoScanOnStart) EnqueueScanRoot();
        }

        public string ManagedRoot => _managedRoot;
        public bool DryRun => _options.DryRun;
        public int QueueCount => _queue.Count;
        public DataHousekeepingStatistics Statistics => _statistics;

        /// <summary>Protect a logical learning root until the recovery coordinator explicitly releases it.</summary>
        public void ProtectRoot(Guid rootRunId)
        {
            if (rootRunId == Guid.Empty) return;
            _protectedRoots[rootRunId.ToString("N")] = 0;
        }

        public void UnprotectRoot(Guid rootRunId)
        {
            if (rootRunId == Guid.Empty) return;
            _protectedRoots.TryRemove(rootRunId.ToString("N"), out _);
        }

        public void SetProtectedRoots(IEnumerable<Guid> rootRunIds)
        {
            _protectedRoots.Clear();
            foreach (var root in rootRunIds ?? Enumerable.Empty<Guid>()) ProtectRoot(root);
        }

        private bool IsProtectedPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path ?? string.Empty);
                if (_options.IsProtectedPath?.Invoke(full) == true) return true;
                var name = Path.GetFileName(full);
                return Guid.TryParseExact(name, "N", out var root) &&
                       _protectedRoots.ContainsKey(root.ToString("N"));
            }
            catch { return true; }
        }

        public bool Enqueue(string directory)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_options.RetentionEnabled ||
                string.IsNullOrWhiteSpace(directory)) return false;
            var full = Path.GetFullPath(directory);
            try
            {
                if (!_queue.TryAdd(new HousekeepingCommand { Path = full }))
                {
                    Warn($"Housekeeping队列已满，跳过候选：{full}");
                    _statistics.IncSkipped();
                    return false;
                }
                _statistics.IncQueued();
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        public int EnqueueNewManifestChains(int successfulRetainCount = 3, int failedRetainCount = 3)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_options.RetentionEnabled) return 0;
            try
            {
                if (!_queue.TryAdd(new HousekeepingCommand
                {
                    ScanRoot = true,
                    SuccessfulRetainCount = Math.Max(0, successfulRetainCount),
                    FailedRetainCount = Math.Max(0, failedRetainCount)
                }))
                    return 0;
                _statistics.IncQueued();
                return 1;
            }
            catch (InvalidOperationException) { return 0; }
        }

        /// <summary>Deterministic synchronous hook for tests and dry-run audits.</summary>
        public bool ProcessOne(string directory)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_options.RetentionEnabled) return false;
            try { return ProcessCandidate(Path.GetFullPath(directory)); }
            catch (Exception ex) { _statistics.IncFailed(); Warn($"Housekeeping候选失败：{ex.Message}"); return false; }
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var command in _queue.GetConsumingEnumerable(_stop.Token))
                {
                    if (_stop.IsCancellationRequested) break;
                    try
                    {
                        if (command == null) continue;
                        if (command.NotBeforeUtcTicks > 0)
                        {
                            var remaining = command.NotBeforeUtcTicks - DateTime.UtcNow.Ticks;
                            if (remaining > 0)
                            {
                                var waitMs = (int)Math.Min(60000,
                                    Math.Max(1, remaining / TimeSpan.TicksPerMillisecond));
                                if (_stop.Token.WaitHandle.WaitOne(waitMs)) break;
                            }
                        }
                        if (command.ScanRoot)
                            ProcessScanRoot(command.SuccessfulRetainCount, command.FailedRetainCount);
                        else if (!string.IsNullOrWhiteSpace(command.Path))
                        {
                            _scheduledRetries.TryRemove(command.Path, out _);
                            ProcessCandidate(command.Path);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _statistics.IncFailed(); Warn($"Housekeeping worker异常：{ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _statistics.IncFailed(); Warn($"Housekeeping worker退出：{ex.Message}"); }
        }

        private void ProcessScanRoot(int successfulRetainCount, int failedRetainCount)
        {
            if (!_options.RetentionEnabled) return;
            // This method is called only by the BelowNormal worker.  It owns all
            // directory enumeration, manifest validation and artifact hashing.
            ResumeOwnStaging();
            var candidates = LearningRetentionPlanner.FindCandidates(
                _managedRoot,
                successfulRetainCount <= 0 ? 3 : successfulRetainCount,
                failedRetainCount <= 0 ? 3 : failedRetainCount,
                _options.IsCurrentPath,
                _options.WarningSink,
                IsProtectedPath);
            foreach (var candidate in candidates) Enqueue(candidate);
        }

        private bool ProcessCandidate(string path)
        {
            if (!_options.RetentionEnabled) return false;
            _statistics.IncProcessed();
            if (IsOwnedStaging(path))
            {
                if (IsStagingProtectedOrCurrent(path, out var stagingReason))
                {
                    _statistics.IncSkipped();
                    Warn($"Housekeeping跳过受保护/当前staging：{path}; Reason={stagingReason}");
                    return false;
                }
                if (IsBusy()) { _statistics.IncBusy(); ScheduleBusyRetry(path); return false; }
                try { return DeleteStagingInBatches(path); }
                catch (Exception ex) { _statistics.IncFailed(); Warn($"Housekeeping续扫staging失败：{path}; {ex.Message}"); return false; }
            }
            if (!IsEligibleDirectory(path, out var manifest, out var reason))
            {
                _statistics.IncSkipped();
                if (!string.Equals(reason, "ManifestMissing", StringComparison.Ordinal)) Warn($"Housekeeping跳过：{path}; Reason={reason}");
                return false;
            }
            if (IsBusy())
            {
                _statistics.IncBusy();
                // Do not spin in the worker while the experiment is active; a
                // future scan/Enqueue call will retry the candidate.
                Warn($"Housekeeping因DAQ/报警/停止/恢复忙碌延期：{path}");
                ScheduleBusyRetry(path);
                return false;
            }
            if (_options.DryRun)
            {
                _statistics.IncStaged();
                return true;
            }

            var parent = Directory.GetParent(path)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || !IsUnderRoot(parent))
            {
                _statistics.IncSkipped();
                Warn($"Housekeeping候选父目录越界：{path}");
                return false;
            }
            var staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
            var pendingSidecar = path + ".retention.pending.json";
            try
            {
                WriteSidecar(pendingSidecar, staging, path, manifest);
                Directory.Move(path, staging); // same volume atomic rename
                File.Move(pendingSidecar, staging + SidecarSuffix);
                _statistics.IncStaged();
                return DeleteStagingInBatches(staging);
            }
            catch (IOException ex) { _statistics.IncFailed(); Warn($"Housekeeping改名/占用失败，已跳过：{path}; {ex.Message}"); return false; }
            catch (UnauthorizedAccessException ex) { _statistics.IncFailed(); Warn($"Housekeeping权限失败，已跳过：{path}; {ex.Message}"); return false; }
            catch (Exception ex) { _statistics.IncFailed(); Warn($"Housekeeping失败，保留原目录：{path}; {ex.Message}"); return false; }
        }

        private bool IsEligibleDirectory(string path, out LearningRunManifest manifest, out string reason)
        {
            manifest = null;
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { reason = "Missing"; return false; }
            var full = Path.GetFullPath(path);
            if (!IsUnderRoot(full) || Path.GetFileName(full).StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase)) { reason = "OutsideOrStaging"; return false; }
            if (!_nameRegex.IsMatch(Path.GetFileName(full))) { reason = "NonStandardName"; return false; }
            try
            {
                if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) { reason = "ReparsePoint"; return false; }
                if (Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories)
                    .Any(directory => File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)))
                { reason = "ReparsePoint"; return false; }
                if (IsProtectedPath(full)) { reason = "Protected"; return false; }
                if (_options.IsCurrentPath != null && _options.IsCurrentPath(full)) { reason = "Current"; return false; }
                if (Directory.EnumerateFiles(full, "*.tmp", SearchOption.AllDirectories).Any() ||
                    Directory.EnumerateFiles(full, "*.partial", SearchOption.AllDirectories).Any()) { reason = "TempFile"; return false; }
                var pathToManifest = LearningRunManifestStore.ManifestPath(full);
                if (!LearningRunManifestStore.TryReadValidated(pathToManifest, out manifest, out reason)) return false;
                var declared = new HashSet<string>(
                    manifest.Executions.SelectMany(execution => execution.Artifacts ?? new List<LearningArtifact>())
                        .Select(artifact => artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var name = Path.GetFileName(file);
                    if (string.Equals(name, LearningRunManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, LearningRunManifestStore.AttemptReceiptFileName, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!declared.Contains(relative)) { reason = "ManifestUnknownFile"; return false; }
                }
                if (!IsTerminalStatus(manifest.FinalStatus)) { reason = "ManifestNonTerminal"; return false; }
                return true;
            }
            catch (Exception ex) { reason = "QualificationError:" + ex.GetType().Name; return false; }
        }

        private bool DeleteStagingInBatches(string staging)
        {
            if (IsStagingProtectedOrCurrent(staging, out var initialReason))
            {
                Warn($"Housekeeping删除前staging身份已受保护/当前：{staging}; Reason={initialReason}");
                return false;
            }
            var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var index = 0;
            while (index < files.Length)
            {
                if (!_options.RetentionEnabled) return false;
                // Re-read sidecar/source identity before every deletion batch.
                // A recovery coordinator may reactivate the same RootRunId while
                // this worker is between batches; in that case preserve staging.
                if (IsStagingProtectedOrCurrent(staging, out var batchReason))
                {
                    Warn($"Housekeeping删除批次前staging身份已受保护/当前：{staging}; Reason={batchReason}");
                    return false;
                }
                if (!WaitForBatchInterval()) return false;
                if (IsStagingProtectedOrCurrent(staging, out batchReason))
                {
                    Warn($"Housekeeping删除批次等待后staging身份已受保护/当前：{staging}; Reason={batchReason}");
                    return false;
                }
                var count = 0;
                long bytes = 0;
                while (index < files.Length && count < _options.MaxFilesPerBatch)
                {
                    if (_stop.IsCancellationRequested) return false;
                    var info = new FileInfo(files[index]);
                    if (info.Length > _options.MaxBytesPerBatch)
                    {
                        Warn($"Housekeeping单文件超过批次上限，保留待人工处置：{files[index]}");
                        return false;
                    }
                    if (count > 0 && bytes + info.Length > _options.MaxBytesPerBatch) break;
                    try
                    {
                        File.Delete(files[index]);
                        _statistics.AddDeleted(info.Length);
                        bytes += info.Length;
                        count++;
                        index++;
                    }
                    catch (Exception ex) { Warn($"Housekeeping删除失败，保留staging：{files[index]}; {ex.Message}"); return false; }
                }
                if (count == 0) return false;
            }
            if (!_options.RetentionEnabled) return false;
            if (IsStagingProtectedOrCurrent(staging, out var finalReason))
            {
                Warn($"Housekeeping清理staging前身份已受保护/当前：{staging}; Reason={finalReason}");
                return false;
            }
            try
            {
                var sidecar = staging + SidecarSuffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
                foreach (var dir in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                    Directory.Delete(dir, false);
                Directory.Delete(staging, false);
                return true;
            }
            catch (Exception ex) { Warn($"Housekeeping staging目录未能清空，等待下次续扫：{staging}; {ex.Message}"); return false; }
        }

        private void ResumeOwnStaging()
        {
            if (!_options.RetentionEnabled) return;
            try
            {
                foreach (var pending in Directory.EnumerateFiles(_managedRoot, "*.retention.pending.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var text = File.ReadAllText(pending);
                        var stagingMatch = Regex.Match(text, "\\\"staging\\\":\\\"(?<staging>[^\\\"]+)\\\"",
                            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                        var source = pending.Substring(0, pending.Length - ".retention.pending.json".Length);
                        if (stagingMatch.Success && !Directory.Exists(source))
                        {
                            var staging = stagingMatch.Groups["staging"].Value;
                            if (Directory.Exists(staging) && !File.Exists(staging + SidecarSuffix))
                                File.Move(pending, staging + SidecarSuffix);
                        }
                    }
                    catch { }
                }
                foreach (var staging in Directory.EnumerateDirectories(_managedRoot, StagingPrefix + "*", SearchOption.AllDirectories))
                    if (File.Exists(staging + SidecarSuffix) &&
                        !IsStagingProtectedOrCurrent(staging, out var reason))
                        Enqueue(staging);
                    else if (File.Exists(staging + SidecarSuffix))
                        _statistics.IncSkipped();
            }
            catch (Exception ex) { Warn($"Housekeeping续扫staging失败：{ex.Message}"); }
        }

        private bool IsOwnedStaging(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!(Directory.Exists(full) && IsUnderRoot(full) &&
                       Path.GetFileName(full).StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) &&
                       File.Exists(full + SidecarSuffix))) return false;
                if (!TryReadSidecar(full, out var sidecar) || sidecar.Schema != 1 ||
                    string.IsNullOrWhiteSpace(sidecar.Source) ||
                    string.IsNullOrWhiteSpace(sidecar.Staging) ||
                    string.IsNullOrWhiteSpace(sidecar.ChainRunId) ||
                    string.IsNullOrWhiteSpace(sidecar.ManifestHash))
                    return false;
                if (!string.Equals(Path.GetFullPath(sidecar.Staging), full, StringComparison.OrdinalIgnoreCase))
                    return false;
                var manifestPath = Path.Combine(full, LearningRunManifest.FileName);
                return File.Exists(manifestPath) &&
                       string.Equals(sidecar.ManifestHash,
                           ReadManifestHash(manifestPath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private bool IsStagingProtectedOrCurrent(string staging, out string reason)
        {
            reason = string.Empty;
            try
            {
                if (!TryReadSidecar(staging, out var sidecar))
                {
                    reason = "SidecarInvalid";
                    return true;
                }

                var fullStaging = Path.GetFullPath(staging);
                var source = Path.GetFullPath(sidecar.Source ?? string.Empty);
                if (!IsUnderRoot(source) || !Guid.TryParseExact(sidecar.ChainRunId ?? string.Empty, "N", out var chainRunId) ||
                    !string.Equals(Path.GetFileName(source), chainRunId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "IdentityInvalid";
                    return true;
                }

                // The source path is the original chain location.  The
                // canonical RootRunId path remains meaningful even after the
                // source was moved into staging, so recovery can re-protect a
                // restarted chain before this worker's next batch.
                var canonicalRoot = Path.Combine(_managedRoot, chainRunId.ToString("N"));
                var identities = new[] { source, canonicalRoot }
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var identity in identities)
                {
                    if (IsProtectedPath(identity))
                    {
                        reason = "Protected";
                        return true;
                    }
                    if (_options.IsCurrentPath?.Invoke(identity) == true)
                    {
                        reason = "Current";
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                reason = "IdentityCheckError";
                return true;
            }
        }

        private static bool TryReadSidecar(string staging, out RetentionSidecar sidecar)
        {
            sidecar = null;
            try
            {
                var path = Path.GetFullPath(staging) + SidecarSuffix;
                if (!File.Exists(path)) return false;
                var serializer = new DataContractJsonSerializer(typeof(RetentionSidecar));
                using (var stream = File.OpenRead(path))
                    sidecar = serializer.ReadObject(stream) as RetentionSidecar;
                return sidecar != null;
            }
            catch
            {
                sidecar = null;
                return false;
            }
        }

        private static string ReadManifestHash(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                var match = Regex.Match(text, "\\\"ManifestHash\\\"\\s*:\\s*\\\"(?<hash>[0-9a-fA-F]{64})\\\"",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                return match.Success ? match.Groups["hash"].Value : string.Empty;
            }
            catch { return string.Empty; }
        }

        private bool WaitForBatchInterval()
        {
            lock (_batchGate)
            {
                var minTicks = (long)Math.Max(0, _options.BatchInterval.TotalSeconds * Stopwatch.Frequency);
                var now = Stopwatch.GetTimestamp();
                var remaining = minTicks - (now - _lastBatchTicks);
                if (_lastBatchTicks != 0 && remaining > 0)
                {
                    var ms = (int)Math.Min(60000, Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency));
                    if (ms > 0 && _stop.Token.WaitHandle.WaitOne(ms)) return false;
                }
                _lastBatchTicks = Stopwatch.GetTimestamp();
                return !_stop.IsCancellationRequested;
            }
        }

        private bool IsBusy()
        {
            try { return _options.BusyStateProvider?.Invoke()?.IsBusy == true; }
            catch (Exception ex) { Warn($"Housekeeping忙碌状态读取失败，保守延期：{ex.Message}"); return true; }
        }

        private void WriteSidecar(string pendingPath, string staging, string source, LearningRunManifest manifest)
        {
            var text = "{\"schema\":1,\"source\":\"" + Escape(source) + "\",\"staging\":\"" + Escape(staging) + "\",\"stagedUtc\":\"" +
                DateTime.UtcNow.ToString("O") + "\",\"chainRunId\":\"" + Escape(manifest.ChainRunId) +
                "\",\"manifestHash\":\"" + Escape(manifest.ManifestHash) + "\"}";
            using (var stream = new FileStream(pendingPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static string Escape(string text) => (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private bool IsUnderRoot(string path)
        {
            var full = Path.GetFullPath(path);
            var root = _managedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root + Path.DirectorySeparatorChar;
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRoot(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        private void Warn(string message)
        {
            try { (_options.WarningSink ?? Console.Error.WriteLine)?.Invoke(message); } catch { }
        }

        private void ScheduleBusyRetry(string path)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_options.RetentionEnabled) return;
            if (!_scheduledRetries.TryAdd(path, 0)) return;
            try
            {
                if (!_queue.TryAdd(new HousekeepingCommand
                {
                    Path = path,
                    NotBeforeUtcTicks = DateTime.UtcNow.Add(
                        _options.BatchInterval < TimeSpan.FromSeconds(5)
                            ? TimeSpan.FromSeconds(5)
                            : _options.BatchInterval).Ticks
                }))
                    _scheduledRetries.TryRemove(path, out _);
            }
            catch
            {
                _scheduledRetries.TryRemove(path, out _);
            }
        }

        private void EnqueueScanRoot()
        {
            if (!_options.RetentionEnabled || Volatile.Read(ref _disposed) != 0) return;
            try
            {
                if (_queue.TryAdd(new HousekeepingCommand
                {
                    ScanRoot = true,
                    SuccessfulRetainCount = 3,
                    FailedRetainCount = 3
                }))
                    _statistics.IncQueued();
            }
            catch { }
        }

        private static bool IsTerminalStatus(string status) =>
            string.Equals(status, "Successful", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _queue.CompleteAdding(); } catch { }
            try { _stop.Cancel(); } catch { }
            var joined = false;
            try { joined = _worker.Join(TimeSpan.FromSeconds(2)); } catch { }
            // If a filesystem operation outlives the bounded wait, leave the
            // queue/token alive for that background worker.  Disposing either
            // object while it is still running can turn a safe cancellation
            // into an unhandled ObjectDisposedException.
            if (joined)
            {
                _queue.Dispose();
                _stop.Dispose();
            }
        }
    }

    public static class LearningRetentionPlanner
    {
        public static IReadOnlyList<string> FindCandidates(
            string root,
            int successfulRetainCount,
            int failedRetainCount,
            Func<string, bool> isCurrent,
            Action<string> warningSink = null,
            Func<string, bool> isProtected = null)
        {
            var result = new List<Tuple<string, LearningRunManifest>>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return Array.Empty<string>();
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).StartsWith(".retention-staging-", StringComparison.OrdinalIgnoreCase)) continue;
                if (isProtected?.Invoke(Path.GetFullPath(directory)) == true) continue;
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
