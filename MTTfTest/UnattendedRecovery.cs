using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using Config;
using Controller;

namespace MTEmbTest
{
    internal sealed class RecoveryStartupIntent
    {
        public string Nonce { get; set; }
        public int ParentPid { get; set; }
        public long ParentStartUtcTicks { get; set; }
    }

    internal sealed class UnattendedRunCheckpoint
    {
        public int SchemaVersion { get; set; } = 1;
        public bool Armed { get; set; }
        public bool RestartPending { get; set; }
        public bool GracefulPaused { get; set; }
        public string PausedUtc { get; set; }
        public string AdaptiveProfilesSha256 { get; set; }
        public string ProgramSafetySha256 { get; set; }
        public bool MotorOffConfirmed { get; set; }
        public bool PressureSafeConfirmed { get; set; }
        public bool PersistenceDrained { get; set; }
        public int RecentSnapshotCycles { get; set; }
        public string StoreDir { get; set; }
        public string TestName { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public Dictionary<string, int> RemainingFormalCycles { get; set; } =
            new Dictionary<string, int>();
        public int LearnCycles { get; set; }
        public string ConfigurationSha256 { get; set; }
        public string ExecutableSha256 { get; set; }
        public string BuildVersion { get; set; }
        public string RunId { get; set; }
        public string ActiveFaultCorrelationId { get; set; }
        public string RecoveryNonce { get; set; }
        public string LastReason { get; set; }
        public string UpdatedUtc { get; set; }
        public List<string> RestartHistoryUtc { get; set; } = new List<string>();
        public bool InProcessRecoveryPending { get; set; }
        public string InProcessRecoveryFingerprint { get; set; }
        public List<string> InProcessRecoveryHistory { get; set; } = new List<string>();
        public string LastInProcessRecoveryResult { get; set; }
    }

    internal static class UnattendedRunCheckpointStore
    {
        private const int CurrentSchemaVersion = 4;
        private static readonly object Sync = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static readonly string CheckpointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MTTFTest",
            "unattended-run-checkpoint.json");

        internal static void Arm(GlobalConfig config, IEnumerable<int> channels, Guid runId)
        {
            if (config?.Test == null) return;
            var selected = (channels ?? Enumerable.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0) return;

            lock (Sync)
            {
                var checkpoint = LoadUnsafe() ?? new UnattendedRunCheckpoint();
                checkpoint.SchemaVersion = CurrentSchemaVersion;
                checkpoint.Armed = true;
                checkpoint.RestartPending = false;
                checkpoint.InProcessRecoveryPending = false;
                checkpoint.GracefulPaused = false;
                checkpoint.StoreDir = config.Test.StoreDir ?? string.Empty;
                checkpoint.TestName = config.Test.TestName ?? string.Empty;
                checkpoint.SelectedChannels = selected;
                checkpoint.LearnCycles = Math.Max(5, config.Test.LearnCycles);
                checkpoint.ConfigurationSha256 = ComputeConfigurationHash(config);
                checkpoint.ExecutableSha256 = ComputeFileHash(GetExecutablePath());
                checkpoint.BuildVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                checkpoint.RunId = runId == Guid.Empty ? string.Empty : runId.ToString("N");
                checkpoint.ActiveFaultCorrelationId = string.Empty;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.LastReason = "FormalRunArmed";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                checkpoint.RemainingFormalCycles = selected.ToDictionary(
                    channel => channel.ToString(CultureInfo.InvariantCulture),
                    channel =>
                    {
                        var record = config.Test.GetEpbRecord(channel);
                        return Math.Max(0, record.TotalCount - record.RunCount);
                    });
                SaveUnsafe(checkpoint);
            }
        }

        internal static void Disarm(string reason)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe() ?? new UnattendedRunCheckpoint();
                checkpoint.Armed = false;
                checkpoint.RestartPending = false;
                checkpoint.InProcessRecoveryPending = false;
                checkpoint.GracefulPaused = false;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.LastReason = string.IsNullOrWhiteSpace(reason) ? "AuthorizationRevoked" : reason;
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static bool DisarmIfRunMatches(string runId, string reason)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null) return true;
                if (!EpbManager.ShouldApplyRunAuthorizationRevocation(checkpoint.RunId, runId))
                    return false;
                DisarmUnsafe(
                    checkpoint,
                    string.IsNullOrWhiteSpace(reason) ? "AuthorizationRevoked" : reason);
                return true;
            }
        }

        internal static void RecordFormalCycleCommitted(int channel)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null || !checkpoint.Armed || checkpoint.RestartPending ||
                    checkpoint.SelectedChannels == null ||
                    !checkpoint.SelectedChannels.Contains(channel))
                    return;
                checkpoint.RemainingFormalCycles ??= new Dictionary<string, int>();
                var key = channel.ToString(CultureInfo.InvariantCulture);
                if (checkpoint.RemainingFormalCycles.TryGetValue(key, out var remaining))
                    checkpoint.RemainingFormalCycles[key] = Math.Max(0, remaining - 1);
                checkpoint.LastReason = "FormalCycleCommitted";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static bool TryRegisterRestart(
            string correlationId,
            string expectedRunId,
            out RecoveryStartupIntent intent,
            out string error)
        {
            intent = null;
            error = string.Empty;
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null || !checkpoint.Armed)
                {
                    error = "无人值守续测未授权。";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(expectedRunId) &&
                    !EpbManager.AreSameNonEmptyRunIds(checkpoint.RunId, expectedRunId))
                {
                    error = "RunIdMismatch";
                    return false;
                }
                if (checkpoint.SchemaVersion < CurrentSchemaVersion ||
                    !EpbManager.AreSameNonEmptyRunIds(checkpoint.RunId, checkpoint.RunId))
                {
                    error = "检查点缺少V4运行身份，拒绝自动续测。";
                    return false;
                }

                var now = DateTime.UtcNow;
                if (!TryParseUtc(checkpoint.UpdatedUtc, out var lastCheckpointUtc) ||
                    now - lastCheckpointUtc > TimeSpan.FromMinutes(5))
                {
                    DisarmUnsafe(checkpoint, "CheckpointExpiredBeforeRestart");
                    error = "检查点已超过5分钟，拒绝自动续测。";
                    return false;
                }
                if (string.Equals(checkpoint.ConfigurationSha256, "unavailable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(checkpoint.ExecutableSha256, "unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    DisarmUnsafe(checkpoint, "CheckpointHashUnavailable");
                    error = "配置或程序哈希不可用，拒绝自动续测。";
                    return false;
                }
                checkpoint.RestartHistoryUtc = (checkpoint.RestartHistoryUtc ?? new List<string>())
                    .Where(value => TryParseUtc(value, out var timestamp) &&
                                    now - timestamp <= TimeSpan.FromMinutes(10))
                    .ToList();
                if (checkpoint.RestartHistoryUtc.Count >= 3)
                {
                    checkpoint.Armed = false;
                    checkpoint.RestartPending = false;
                    checkpoint.LastReason = "RestartBudgetExhausted";
                    checkpoint.UpdatedUtc = now.ToString("O", CultureInfo.InvariantCulture);
                    SaveUnsafe(checkpoint);
                    error = "10分钟内已执行3次自重启，重启预算耗尽。";
                    return false;
                }

                var nonce = Guid.NewGuid().ToString("N");
                checkpoint.RestartHistoryUtc.Add(now.ToString("O", CultureInfo.InvariantCulture));
                checkpoint.RestartPending = true;
                checkpoint.RecoveryNonce = nonce;
                checkpoint.ActiveFaultCorrelationId = correlationId ?? string.Empty;
                checkpoint.LastReason = "SystemFaultRestartPending";
                checkpoint.UpdatedUtc = now.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);

                using (var process = Process.GetCurrentProcess())
                    intent = new RecoveryStartupIntent
                    {
                        Nonce = nonce,
                        ParentPid = process.Id,
                        ParentStartUtcTicks = process.StartTime.ToUniversalTime().Ticks
                    };
                return true;
            }
        }

        internal static bool TryRegisterInProcessRecovery(
            GlobalConfig config,
            string fingerprint,
            string expectedRunId,
            out UnattendedRunCheckpoint checkpoint,
            out string error)
        {
            checkpoint = null;
            error = string.Empty;
            lock (Sync)
            {
                var current = LoadUnsafe();
                if (current == null || !current.Armed)
                {
                    error = "无人值守续测未授权。";
                    return false;
                }
                if (!EpbManager.AreSameNonEmptyRunIds(current.RunId, expectedRunId))
                {
                    error = "RunIdMismatch";
                    return false;
                }
                if (current.SchemaVersion < CurrentSchemaVersion)
                {
                    error = "检查点版本低于V4，拒绝同进程自动续测。";
                    return false;
                }
                if (current.InProcessRecoveryPending)
                {
                    error = "已有同进程恢复正在执行。";
                    return false;
                }
                if (config?.Test == null ||
                    !string.Equals(current.StoreDir, config.Test.StoreDir, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.TestName, config.Test.TestName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.ConfigurationSha256, ComputeConfigurationHash(config),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.ExecutableSha256, ComputeFileHash(GetExecutablePath()),
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "项目、配置或程序身份已变化，拒绝同进程自动续测。";
                    return false;
                }

                var now = DateTime.UtcNow;
                var normalized = string.IsNullOrWhiteSpace(fingerprint)
                    ? "UnknownSystemFault"
                    : fingerprint.Trim();
                current.InProcessRecoveryHistory = (current.InProcessRecoveryHistory ?? new List<string>())
                    .Where(entry => TryParseRecoveryHistory(entry, out var timestamp, out _) &&
                                    now - timestamp <= TimeSpan.FromMinutes(10))
                    .ToList();
                if (current.InProcessRecoveryHistory.Any(entry =>
                        TryParseRecoveryHistory(entry, out _, out var savedFingerprint) &&
                        string.Equals(savedFingerprint, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    error = "相同故障指纹10分钟内已执行过一次同进程恢复。";
                    SaveUnsafe(current);
                    return false;
                }

                // Schema 只能单调前进。V4 包含 RunId，恢复登记不得把它降回旧格式。
                current.SchemaVersion = CurrentSchemaVersion;
                current.InProcessRecoveryPending = true;
                current.InProcessRecoveryFingerprint = normalized;
                current.InProcessRecoveryHistory.Add(
                    now.ToString("O", CultureInfo.InvariantCulture) + "|" + normalized);
                current.LastReason = "InProcessRecoveryPending";
                current.UpdatedUtc = now.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(current);
                checkpoint = current;
                return true;
            }
        }

        internal static void CompleteInProcessRecovery(bool success, string reason)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null) return;
                checkpoint.InProcessRecoveryPending = false;
                checkpoint.LastInProcessRecoveryResult =
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "|" +
                    (success ? "Success|" : "Failed|") + (reason ?? "Unknown");
                checkpoint.LastReason = success
                    ? "InProcessRecoveryCompleted"
                    : "InProcessRecoveryFailed: " + (reason ?? "Unknown");
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        private static bool TryParseRecoveryHistory(
            string value,
            out DateTime timestampUtc,
            out string fingerprint)
        {
            timestampUtc = DateTime.MinValue;
            fingerprint = string.Empty;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var separator = value.IndexOf('|');
            if (separator <= 0 || separator >= value.Length - 1) return false;
            fingerprint = value.Substring(separator + 1);
            return TryParseUtc(value.Substring(0, separator), out timestampUtc);
        }

        internal static bool TryConsume(
            RecoveryStartupIntent intent,
            GlobalConfig config,
            out UnattendedRunCheckpoint checkpoint,
            out string error)
        {
            checkpoint = null;
            error = string.Empty;
            if (intent == null || string.IsNullOrWhiteSpace(intent.Nonce))
            {
                error = "恢复参数缺失。";
                return false;
            }

            lock (Sync)
            {
                var current = LoadUnsafe();
                if (current == null || !current.Armed || !current.RestartPending ||
                    !string.Equals(current.RecoveryNonce, intent.Nonce, StringComparison.Ordinal))
                {
                    error = "检查点未授权、已被消费或恢复令牌不匹配。";
                    return false;
                }
                if (!TryParseUtc(current.UpdatedUtc, out var updatedUtc) ||
                    DateTime.UtcNow - updatedUtc > TimeSpan.FromMinutes(5))
                {
                    DisarmUnsafe(current, "CheckpointExpired");
                    error = "检查点已超过5分钟。";
                    return false;
                }
                if (config?.Test == null ||
                    !string.Equals(current.StoreDir, config.Test.StoreDir, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.TestName, config.Test.TestName, StringComparison.OrdinalIgnoreCase))
                {
                    DisarmUnsafe(current, "ProjectIdentityChanged");
                    error = "当前加载项目与检查点不一致。";
                    return false;
                }
                if (!string.Equals(
                        current.ConfigurationSha256,
                        ComputeConfigurationHash(config),
                        StringComparison.OrdinalIgnoreCase))
                {
                    DisarmUnsafe(current, "ConfigurationChanged");
                    error = "项目或设备配置已变更。";
                    return false;
                }
                if (!string.Equals(
                        current.ExecutableSha256,
                        ComputeFileHash(GetExecutablePath()),
                        StringComparison.OrdinalIgnoreCase))
                {
                    DisarmUnsafe(current, "ExecutableChanged");
                    error = "程序构建哈希与检查点不一致。";
                    return false;
                }
                var currentBuildVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                if (!string.Equals(current.BuildVersion, currentBuildVersion, StringComparison.OrdinalIgnoreCase))
                {
                    DisarmUnsafe(current, "BuildVersionChanged");
                    error = "程序构建版本与检查点不一致。";
                    return false;
                }

                current.RestartPending = false;
                current.RecoveryNonce = string.Empty;
                current.LastReason = "RecoveryInstanceValidated";
                current.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(current);
                checkpoint = current;
                return true;
            }
        }

        internal static void CancelPendingRestart(string reason)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null) return;
                checkpoint.Armed = false;
                checkpoint.RestartPending = false;
                checkpoint.InProcessRecoveryPending = false;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.LastReason = reason ?? "RestartCancelled";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static UnattendedRunCheckpoint Load()
        {
            lock (Sync) return LoadUnsafe();
        }

        internal static void SaveGracefulPause(
            GlobalConfig config,
            IEnumerable<int> channels,
            DateTime pausedUtc,
            Guid runId)
        {
            if (config?.Test == null)
                throw new ArgumentNullException(nameof(config));
            if (runId == Guid.Empty)
                throw new InvalidOperationException("正常暂停缺少有效RunId，拒绝生成可自动恢复检查点。");
            var selected = (channels ?? Enumerable.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException("正常暂停检查点没有有效通道。");

            lock (Sync)
            {
                var checkpoint = LoadUnsafe() ?? new UnattendedRunCheckpoint();
                checkpoint.SchemaVersion = CurrentSchemaVersion;
                checkpoint.Armed = true;
                checkpoint.RestartPending = false;
                checkpoint.InProcessRecoveryPending = false;
                checkpoint.GracefulPaused = true;
                checkpoint.StoreDir = config.Test.StoreDir ?? string.Empty;
                checkpoint.TestName = config.Test.TestName ?? string.Empty;
                checkpoint.SelectedChannels = selected;
                checkpoint.LearnCycles = Math.Max(5, config.Test.LearnCycles);
                checkpoint.ConfigurationSha256 = ComputeConfigurationHash(config);
                checkpoint.ExecutableSha256 = ComputeFileHash(GetExecutablePath());
                checkpoint.BuildVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                checkpoint.RunId = runId.ToString("N");
                checkpoint.AdaptiveProfilesSha256 = ComputeFileHash(Path.Combine(
                    ConfigLoader.GetProjectConfigDir(config.Test.StoreDir, config.Test.TestName),
                    "EpbAdaptiveProfiles.xml"));
                checkpoint.ProgramSafetySha256 = ComputeFileHash(
                    AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
                checkpoint.PausedUtc = pausedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                checkpoint.LastReason = "GracefulPaused";
                checkpoint.MotorOffConfirmed = true;
                checkpoint.PressureSafeConfirmed = true;
                checkpoint.PersistenceDrained = true;
                checkpoint.RecentSnapshotCycles = 10;
                checkpoint.RemainingFormalCycles = selected.ToDictionary(
                    channel => channel.ToString(CultureInfo.InvariantCulture),
                    channel =>
                    {
                        var record = config.Test.GetEpbRecord(channel);
                        return Math.Max(0, record.TotalCount - record.RunCount);
                    });
                SaveUnsafe(checkpoint);
            }
        }

        internal static bool TryLoadGracefulPause(
            GlobalConfig config,
            out UnattendedRunCheckpoint checkpoint,
            out string error)
        {
            checkpoint = null;
            error = string.Empty;
            lock (Sync)
            {
                var current = LoadUnsafe();
                if (current == null || current.SchemaVersion < CurrentSchemaVersion ||
                    !current.Armed || !current.GracefulPaused)
                {
                    error = "没有正常暂停检查点。";
                    return false;
                }
                if (!EpbManager.AreSameNonEmptyRunIds(current.RunId, current.RunId))
                {
                    error = "正常暂停检查点缺少有效RunId，必须完整学习。";
                    return false;
                }
                if (!current.MotorOffConfirmed || !current.PressureSafeConfirmed ||
                    !current.PersistenceDrained || current.RecentSnapshotCycles < 10)
                {
                    error = "正常暂停检查点缺少安全停机或最近10圈落盘确认。";
                    return false;
                }
                var pauseAge = TryParseUtc(current.PausedUtc, out var pausedUtc)
                    ? DateTime.UtcNow - pausedUtc
                    : TimeSpan.MinValue;
                if (pauseAge < TimeSpan.Zero || pauseAge > TimeSpan.FromDays(7))
                {
                    error = "正常暂停检查点已超过7天，必须完整学习。";
                    return false;
                }
                if (config?.Test == null ||
                    !string.Equals(current.StoreDir, config.Test.StoreDir, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.TestName, config.Test.TestName, StringComparison.OrdinalIgnoreCase))
                {
                    error = "当前项目与正常暂停检查点不一致。";
                    return false;
                }
                if (!string.Equals(
                        current.ConfigurationSha256,
                        ComputeConfigurationHash(config),
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "项目或设备配置已变更，必须完整学习。";
                    return false;
                }
                if (!string.Equals(
                        current.ExecutableSha256,
                        ComputeFileHash(GetExecutablePath()),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        current.BuildVersion,
                        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "程序构建已变更，必须完整学习。";
                    return false;
                }
                var safetyHash = ComputeFileHash(
                    AppDomain.CurrentDomain.SetupInformation.ConfigurationFile);
                if (string.Equals(safetyHash, "unavailable", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.ProgramSafetySha256, safetyHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = "程序安全配置已变更，必须完整学习。";
                    return false;
                }
                var adaptiveHash = ComputeFileHash(Path.Combine(
                    ConfigLoader.GetProjectConfigDir(config.Test.StoreDir, config.Test.TestName),
                    "EpbAdaptiveProfiles.xml"));
                if (string.Equals(adaptiveHash, "unavailable", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.AdaptiveProfilesSha256, adaptiveHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = "自适应模型缺失或已变更，必须完整学习。";
                    return false;
                }

                checkpoint = current;
                return true;
            }
        }

        internal static bool IsGracefulPauseArmed()
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                return checkpoint?.Armed == true && checkpoint.GracefulPaused;
            }
        }

        internal static void ClearGracefulPause(string reason)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null) return;
                checkpoint.Armed = false;
                checkpoint.GracefulPaused = false;
                checkpoint.RestartPending = false;
                checkpoint.LastReason = reason ?? "GracefulPauseConsumed";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static void ClearForProject(string storeDir, string testName, string reason)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null) return;
                if (!string.Equals(
                        checkpoint.StoreDir ?? string.Empty,
                        storeDir ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        checkpoint.TestName ?? string.Empty,
                        testName ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase))
                    return;
                checkpoint.Armed = false;
                checkpoint.GracefulPaused = false;
                checkpoint.RestartPending = false;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.RemainingFormalCycles = new Dictionary<string, int>();
                checkpoint.LastReason = reason ?? "ProjectProgressCleared";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static string ComputeConfigurationHash(GlobalConfig config)
        {
            try
            {
                var candidates = new List<string>();
                var configDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                if (Directory.Exists(configDirectory))
                    candidates.AddRange(Directory.GetFiles(configDirectory, "*.xml", SearchOption.TopDirectoryOnly));
                if (config?.Test != null)
                {
                    var projectConfig = ConfigLoader.GetProjectTestConfigPath(
                        config.Test.StoreDir,
                        config.Test.TestName);
                    if (File.Exists(projectConfig)) candidates.Add(projectConfig);
                }

                using (var sha = SHA256.Create())
                using (var buffer = new MemoryStream())
                {
                    foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        var name = Encoding.UTF8.GetBytes(Path.GetFileName(path).ToLowerInvariant() + "\n");
                        buffer.Write(name, 0, name.Length);
                        var content = ReadStableConfiguration(path);
                        buffer.Write(content, 0, content.Length);
                        buffer.WriteByte((byte)'\n');
                    }
                    buffer.Position = 0;
                    return ToHex(sha.ComputeHash(buffer));
                }
            }
            catch
            {
                return "unavailable";
            }
        }

        private static byte[] ReadStableConfiguration(string path)
        {
            try
            {
                var document = new XmlDocument { PreserveWhitespace = false };
                document.Load(path);
                var records = document.SelectNodes("//EpbRecords");
                if (records != null)
                    foreach (XmlNode node in records.Cast<XmlNode>().ToArray())
                        node.ParentNode?.RemoveChild(node);
                return Encoding.UTF8.GetBytes(document.OuterXml);
            }
            catch
            {
                return File.ReadAllBytes(path);
            }
        }

        private static void DisarmUnsafe(UnattendedRunCheckpoint checkpoint, string reason)
        {
            checkpoint.Armed = false;
            checkpoint.RestartPending = false;
            checkpoint.InProcessRecoveryPending = false;
            checkpoint.GracefulPaused = false;
            checkpoint.RecoveryNonce = string.Empty;
            checkpoint.LastReason = reason;
            checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            SaveUnsafe(checkpoint);
        }

        private static UnattendedRunCheckpoint LoadUnsafe()
        {
            try
            {
                if (!File.Exists(CheckpointPath)) return null;
                return Json.Deserialize<UnattendedRunCheckpoint>(File.ReadAllText(CheckpointPath, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static void SaveUnsafe(UnattendedRunCheckpoint checkpoint)
        {
            var directory = Path.GetDirectoryName(CheckpointPath);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".checkpoint-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(checkpoint));
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(CheckpointPath))
                    File.Replace(temporary, CheckpointPath, null, true);
                else
                    File.Move(temporary, CheckpointPath);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static string GetExecutablePath()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    return process.MainModule?.FileName;
            }
            catch
            {
                return Assembly.GetEntryAssembly()?.Location;
            }
        }

        private static string ComputeFileHash(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return ToHex(sha.ComputeHash(stream));
            }
            catch
            {
                return "unavailable";
            }
        }

        private static string ToHex(byte[] bytes)
        {
            return string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static bool TryParseUtc(string value, out DateTime utc)
        {
            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out utc);
        }
    }

    internal static class UnattendedRecoveryCoordinator
    {
        private sealed class RecoveryTaskLogger : Config.IAppLogger
        {
            public void Info(string message, string category = null)
                => ProjectLogHub.Write(ProjectLogLevel.Info, message, category ?? "无人值守恢复");

            public void Warn(string message, string category = null)
                => ProjectLogHub.Write(ProjectLogLevel.Warning, message, category ?? "无人值守恢复");

            public void Error(string message, string category = null, Exception ex = null)
                => ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    ex == null ? message : message + Environment.NewLine + ex,
                    category ?? "无人值守恢复");
        }

        private static readonly object Sync = new object();
        private static readonly TaskSupervisor RecoveryTasks =
            new TaskSupervisor(new RecoveryTaskLogger());
        private static EpbManager _manager;
        private static GlobalConfig _config;
        private static Func<Task> _quiesceAndFlush;
        private static int _restartStarted;
        private static int _inProcessRecoveryStarted;

        internal static void Attach(EpbManager manager, GlobalConfig config)
        {
            if (manager == null || config == null) return;
            lock (Sync)
            {
                if (ReferenceEquals(_manager, manager)) return;
                if (_manager != null)
                {
                    _manager.SystemFaultRaised -= OnSystemFaultRaised;
                    _manager.RunAuthorizationRevoking -= OnRunAuthorizationRevoking;
                    _manager.ChannelCycleCompleted -= OnFormalCycleCompleted;
                }
                _manager = manager;
                _config = config;
                manager.SystemFaultRaised += OnSystemFaultRaised;
                manager.RunAuthorizationRevoking += OnRunAuthorizationRevoking;
                manager.ChannelCycleCompleted += OnFormalCycleCompleted;
            }
        }

        internal static void Arm(GlobalConfig config, IEnumerable<int> channels, Guid runId)
        {
            UnattendedRunCheckpointStore.Arm(config, channels, runId);
        }

        internal static void RegisterQuiesceAndFlush(Func<Task> callback)
        {
            lock (Sync) _quiesceAndFlush = callback;
        }

        internal static void Disarm(string reason)
        {
            UnattendedRunCheckpointStore.Disarm(reason);
        }

        internal static void RequestFatalRestart(string source, Exception exception)
        {
            var reason = source + ": " + (exception?.Message ?? "unknown fatal exception");
            try
            {
                RestartAsync(reason, Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
            }
            catch { }
        }

        private static void OnRunAuthorizationRevoking(StopContext context)
        {
            // 正常暂停后关闭监视窗口仍需执行统一硬件关闭确认，但不能撤销用户明确保存的
            // 暂停恢复检查点；人工“停止试验”和所有报警路径仍会撤销。
            if (context?.Source == StopSource.ApplicationClosing)
            {
                var graceful = UnattendedRunCheckpointStore.Load();
                if (graceful?.Armed == true && graceful.GracefulPaused)
                    return;
            }

            // A system-fault restart registers a one-time recovery nonce before it asks
            // EpbManager to perform the common StopAll safety sequence.  That internal
            // StopAll must not revoke the nonce it is protecting.  Manual stop/close and
            // hardware alarm paths still disarm the checkpoint before cancellation.
            if (context?.Source == StopSource.SystemFault)
            {
                var checkpoint = UnattendedRunCheckpointStore.Load();
                if (checkpoint?.RestartPending == true)
                    return;
            }
            UnattendedRunCheckpointStore.DisarmIfRunMatches(
                context?.RunId,
                $"{context?.Source}: {context?.Reason ?? "Run authorization revoked"}");
        }

        private static void OnSystemFaultRaised(ControlFault fault)
        {
            if (fault?.RecoveryPolicy == FaultRecoveryPolicy.UnattendedBatchRecycle)
            {
                RecoveryTasks.Observe(
                    Task.Run(() => RecoverInProcessOrRestartAsync(fault)),
                    "UnattendedInProcessRecovery",
                    fault.CorrelationId,
                    fault.AffectedChannels?.FirstOrDefault() ?? 0);
                return;
            }
            var correlationId = fault?.CorrelationId ?? Guid.NewGuid();
            RecoveryTasks.Observe(
                Task.Run(() => RestartAsync(
                    fault?.Reason ?? "SystemFault",
                    correlationId.ToString("N"))),
                "UnattendedProcessRestart",
                correlationId,
                fault?.AffectedChannels?.FirstOrDefault() ?? 0);
        }

        internal static Task<bool> DrainBackgroundTasksAsync(int timeoutMs)
            => RecoveryTasks.DrainAsync(timeoutMs);

        private static void OnFormalCycleCompleted(int channel, int sessionRunCount)
        {
            UnattendedRunCheckpointStore.RecordFormalCycleCommitted(channel);
        }

        private static async Task RecoverInProcessOrRestartAsync(ControlFault fault)
        {
            if (Interlocked.CompareExchange(ref _inProcessRecoveryStarted, 1, 0) != 0) return;
            var reason = fault?.Reason ?? "SoftwareRecoveryCircuitOpen";
            var correlationId = fault?.CorrelationId.ToString("N") ?? Guid.NewGuid().ToString("N");
            var affected = string.Join(",", (fault?.AffectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel));
            var fingerprint =
                $"{fault?.Code ?? "SystemFault"}|{fault?.Scope}|{fault?.GroupId}|" +
                $"Channels={affected}|State={reason}";
            try
            {
                EpbManager manager;
                GlobalConfig config;
                lock (Sync)
                {
                    manager = _manager;
                    config = _config;
                }
                UnattendedRunCheckpoint checkpoint = null;
                var registrationError = manager == null
                    ? "ManagerUnavailable"
                    : config == null
                        ? "ConfigUnavailable"
                        : string.Empty;
                if (manager == null || config == null ||
                    !UnattendedRunCheckpointStore.TryRegisterInProcessRecovery(
                        config,
                        fingerprint,
                        fault?.CorrelationId.ToString("N"),
                        out checkpoint,
                        out registrationError))
                {
                    if (string.Equals(registrationError, "RunIdMismatch", StringComparison.Ordinal))
                    {
                        ProjectLogHub.Write(
                            ProjectLogLevel.Warning,
                            $"忽略迟到旧运行的无人值守恢复请求。" +
                            $"FaultRunId={fault?.CorrelationId:N}; Fault={reason}",
                            "无人值守恢复");
                        return;
                    }
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        $"同进程自动恢复不可用，升级到进程自重启。" +
                        $"Reason={registrationError}; Fault={reason}",
                        "无人值守恢复");
                    Interlocked.Exchange(ref _inProcessRecoveryStarted, 0);
                    await RestartAsync(reason, correlationId, fault?.CorrelationId.ToString("N"))
                        .ConfigureAwait(false);
                    return;
                }

                ProjectLogHub.Write(
                    ProjectLogLevel.Warning,
                    $"启动同进程无人值守恢复：冻结旧批次→StopAll→逻辑清场→完整学习。" +
                    $"Fingerprint={fingerprint}",
                    "无人值守恢复");
                var safety = await manager.PrepareForFreshRestartAsync(
                        new StopContext
                        {
                            Source = StopSource.SystemFault,
                            Reason = "同进程无人值守恢复：" + reason,
                            Initiator = nameof(UnattendedRecoveryCoordinator),
                            CorrelationId = correlationId,
                            RequestedUtc = DateTime.UtcNow
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!safety.CanRestartInProcess)
                    throw new InvalidOperationException(
                        "同进程恢复清场不变量未通过：" + safety.LogicalError);

                var remaining = (checkpoint.RemainingFormalCycles ??
                                 new Dictionary<string, int>())
                    .Select(pair => new
                    {
                        Parsed = int.TryParse(
                            pair.Key,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var channel),
                        Channel = channel,
                        Remaining = Math.Max(0, pair.Value)
                    })
                    .Where(item => item.Parsed && item.Channel >= 1 && item.Channel <= 12 &&
                                   item.Remaining > 0)
                    .ToDictionary(item => item.Channel, item => item.Remaining);
                var selected = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                    .Where(channel => remaining.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                if (selected.Length == 0)
                {
                    UnattendedRunCheckpointStore.CompleteInProcessRecovery(true, "NoRemainingCycles");
                    UnattendedRunCheckpointStore.Disarm("FormalRunAlreadyCompleted");
                    return;
                }

                manager.EpbTestCycle = remaining;
                var learnCycles = Math.Max(5, checkpoint.LearnCycles);
                await manager.StartBatchSynchronizedWithResultAsync(
                        selected,
                        learnCycles,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var rebuildSummary = safety.LogicalState?.HydraulicGroups == null
                    ? "Hydraulics=Unavailable"
                    : "Hydraulics=" + string.Join(
                        " / ",
                        safety.LogicalState.HydraulicGroups.Select(group => group.ToString()));
                UnattendedRunCheckpointStore.CompleteInProcessRecovery(
                    true,
                    "Recovered; " + rebuildSummary);
                ProjectLogHub.Write(
                    ProjectLogLevel.Info,
                    $"同进程无人值守恢复完成：Channels=[{string.Join(",", selected)}] " +
                    $"LearnCycles={learnCycles}",
                    "无人值守恢复");
            }
            catch (Exception ex)
            {
                UnattendedRunCheckpointStore.CompleteInProcessRecovery(false, ex.Message);
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    $"同进程无人值守恢复失败，升级到进程自重启：{ex}",
                    "无人值守恢复");
                Interlocked.Exchange(ref _inProcessRecoveryStarted, 0);
                await RestartAsync(reason, correlationId, fault?.CorrelationId.ToString("N"))
                    .ConfigureAwait(false);
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _inProcessRecoveryStarted, 0);
                ProjectLogHub.Flush(true);
            }
        }

        private static async Task RestartAsync(
            string reason,
            string correlationId,
            string expectedRunId = null)
        {
            if (Interlocked.CompareExchange(ref _restartStarted, 1, 0) != 0) return;
            try
            {
                if (!UnattendedRunCheckpointStore.TryRegisterRestart(
                        correlationId,
                        expectedRunId,
                        out var intent,
                        out var registrationError))
                {
                    if (string.Equals(registrationError, "RunIdMismatch", StringComparison.Ordinal))
                    {
                        ProjectLogHub.Write(
                            ProjectLogLevel.Warning,
                            $"忽略迟到旧运行的进程自重启请求。ExpectedRunId={expectedRunId}; " +
                            $"CorrelationId={correlationId}; Reason={reason}",
                            "无人值守恢复");
                        return;
                    }
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        $"系统故障保持安全停机，不再自重启：{registrationError}; Reason={reason}",
                        "无人值守恢复");
                    await SafeStopOnlyAsync(reason, correlationId).ConfigureAwait(false);
                    return;
                }

                var manager = _manager;
                if (manager == null)
                {
                    UnattendedRunCheckpointStore.CancelPendingRestart("ManagerUnavailable");
                    return;
                }

                StopSafetyResult safety;
                using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    safety = await manager.StopAllAsync(
                            new StopContext
                            {
                                Source = StopSource.SystemFault,
                                Reason = reason,
                                Initiator = nameof(UnattendedRecoveryCoordinator),
                                CorrelationId = correlationId,
                                RequestedUtc = DateTime.UtcNow
                            },
                            stopTimeout.Token)
                        .ConfigureAwait(false);
                // Do not quiesce/dispose acquisition or persistence before validating the
                // StopAll durability and physical-safety result. A canceled restart must
                // leave the original process able to continue retrying its accepted data.
                if (safety == null || !safety.FullyConfirmed)
                {
                    UnattendedRunCheckpointStore.CancelPendingRestart("SafetyStopUnconfirmed");
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "自重启已取消：电机DO、程控电源、安全压力或数据耐久边界未全部确认。",
                        "无人值守恢复");
                    ProjectLogHub.Flush(true);
                    return;
                }
                Func<Task> quiesceAndFlush;
                lock (Sync) quiesceAndFlush = _quiesceAndFlush;
                if (quiesceAndFlush != null)
                    await quiesceAndFlush().ConfigureAwait(false);
                if (!await manager.ShutdownPersistenceAsync(10000).ConfigureAwait(false))
                    throw new IOException("DAQ持久化队列未能在10秒内安全关闭。");
                manager.ReleaseHardwareForRestart();
                ProjectLogHub.Flush(true);

                StartRecoveryProcess(intent);
                Environment.Exit(86);
            }
            catch (Exception ex)
            {
                UnattendedRunCheckpointStore.CancelPendingRestart("RestartFailed: " + ex.Message);
                ProjectLogHub.Write(ProjectLogLevel.Error, ex.ToString(), "无人值守恢复");
                ProjectLogHub.Flush(true);
            }
        }

        private static async Task SafeStopOnlyAsync(string reason, string correlationId)
        {
            try
            {
                var manager = _manager;
                if (manager == null) return;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    await manager.StopAllAsync(
                            new StopContext
                            {
                                Source = StopSource.SystemFault,
                                Reason = reason,
                                Initiator = nameof(UnattendedRecoveryCoordinator),
                                CorrelationId = correlationId,
                                RequestedUtc = DateTime.UtcNow
                            },
                            timeout.Token)
                        .ConfigureAwait(false);
            }
            catch { }
            finally
            {
                ProjectLogHub.Flush(true);
            }
        }

        private static void StartRecoveryProcess(RecoveryStartupIntent intent)
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName ??
                             Assembly.GetEntryAssembly()?.Location;
            var arguments = string.Format(
                CultureInfo.InvariantCulture,
                "--epb-recover {0} --wait-parent {1} --parent-start-ticks {2}",
                intent.Nonce,
                intent.ParentPid,
                intent.ParentStartUtcTicks);
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
    }

    internal static class RecoveryProcessBootstrap
    {
        internal const string MutexName = @"Local\MTTFTest_V2_10_2_4_SingleInstance";

        internal static RecoveryStartupIntent Parse(string[] args)
        {
            if (args == null) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index + 1 < args.Length; index += 2)
                values[args[index]] = args[index + 1];
            if (!values.TryGetValue("--epb-recover", out var nonce) ||
                !values.TryGetValue("--wait-parent", out var parentText) ||
                !int.TryParse(parentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid))
                return null;
            values.TryGetValue("--parent-start-ticks", out var ticksText);
            long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks);
            return new RecoveryStartupIntent
            {
                Nonce = nonce,
                ParentPid = parentPid,
                ParentStartUtcTicks = ticks
            };
        }

        internal static void WaitForParent(RecoveryStartupIntent intent)
        {
            if (intent == null || intent.ParentPid <= 0) return;
            try
            {
                using (var parent = Process.GetProcessById(intent.ParentPid))
                {
                    if (!MatchesParent(parent, intent)) return;
                    if (parent.WaitForExit(15000)) return;
                    if (MatchesParent(parent, intent)) parent.Kill();
                    parent.WaitForExit(5000);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        private static bool MatchesParent(Process process, RecoveryStartupIntent intent)
        {
            try
            {
                if (process.Id != intent.ParentPid || process.HasExited) return false;
                if (intent.ParentStartUtcTicks > 0 &&
                    process.StartTime.ToUniversalTime().Ticks != intent.ParentStartUtcTicks)
                    return false;
                var parentPath = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
                var currentPath = Path.GetFullPath(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
                return string.Equals(parentPath, currentPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
