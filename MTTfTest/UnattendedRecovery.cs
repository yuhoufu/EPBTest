using System;
using System.Collections.Concurrent;
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
using DataOperation;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal sealed class RecoveryStartupIntent
    {
        public string Nonce { get; set; }
        public int ParentPid { get; set; }
        public long ParentStartUtcTicks { get; set; }
    }

    internal sealed class WatchdogTakeoverHandoffReceipt
    {
        internal bool Prepared { get; set; }
        internal string SessionId { get; set; }
        internal string RunId { get; set; }
        internal string CorrelationId { get; set; }
        internal long Revision { get; set; }
        internal string Error { get; set; }
    }

    internal sealed class UnattendedRunCheckpoint
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        [ScriptIgnore]
        public string LastLoadSource { get; set; }
        [ScriptIgnore]
        public string LastLoadStatus { get; set; }
        [ScriptIgnore]
        public string LastLoadSha256 { get; set; }
        public string LastRecoveryLoadSource { get; set; }
        public string LastRecoveryLoadSha256 { get; set; }
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
        public string EffectiveRuntimeSafetyParameters { get; set; }
        /// <summary>首次人工授权本次无人值守运行链时的 RunId，进程重启后保持不变。</summary>
        public string RootRunId { get; set; }
        /// <summary>创建当前执行 RunId 的上一进程 RunId；首次人工启动为空。</summary>
        public string ParentRunId { get; set; }
        public string RunId { get; set; }
        public int RestartGeneration { get; set; }
        /// <summary>单调恢复代次；用于 manifest/证据身份，禁止固定为0。</summary>
        public long RunEpoch { get; set; }
        /// <summary>恢复子进程已消费 nonce，等待新执行 RunId 首次 Arm。</summary>
        public bool RecoveryChainPendingStart { get; set; }
        public string ActiveFaultCorrelationId { get; set; }
        public string RecoveryNonce { get; set; }
        public string LastRecoveryNonceSha256 { get; set; }
        public string LastReason { get; set; }
        public string UpdatedUtc { get; set; }
        public List<string> RestartHistoryUtc { get; set; } = new List<string>();
        public bool InProcessRecoveryPending { get; set; }
        public string InProcessRecoveryFingerprint { get; set; }
        public List<string> InProcessRecoveryHistory { get; set; } = new List<string>();
        public string LastInProcessRecoveryResult { get; set; }
        /// <summary>本轮按试验生命周期启动的独立看门狗会话；不绑定程序版本。</summary>
        public string WatchdogSessionId { get; set; }
    }

    internal static class UnattendedRunCheckpointStore
    {
        // v6: RemainingFormalCycles 字段名为兼容旧 JSON 保留，值改为剩余机械耐久圈；
        // v5 检查点必须拒绝，防止学习/资格圈被再次当作“不计目标”。
        private const int CurrentSchemaVersion = 6;
        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, byte> RevokedRuns =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static readonly string CheckpointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MTTFTest",
            "unattended-run-checkpoint.json");
        internal static readonly string CheckpointBackupPath = CheckpointPath + ".bak";
        internal static readonly string CheckpointAuditPath = Path.Combine(
            Path.GetDirectoryName(CheckpointPath),
            "unattended-run-checkpoint.audit.jsonl");

        internal static UnattendedRunChainTransition Arm(
            GlobalConfig config,
            IEnumerable<int> channels,
            Guid runId,
            bool requireRecoveryPending = false,
            long runEpoch = 0)
        {
            if (config?.Test == null)
                return default;
            var selected = (channels ?? Enumerable.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0)
                return default;

            lock (Sync)
            {
                var checkpoint = LoadUnsafe() ?? new UnattendedRunCheckpoint();
                var checkpointRunId = NormalizeRunId(checkpoint.RunId);
                var revocationObserved = checkpointRunId.Length > 0 &&
                                         RevokedRuns.ContainsKey(checkpointRunId);
                if (requireRecoveryPending &&
                    !EpbManager.CanCommitUnattendedRecovery(
                        checkpoint.Armed,
                        checkpoint.RecoveryChainPendingStart,
                        checkpoint.InProcessRecoveryPending,
                        revocationObserved))
                    throw new InvalidOperationException(
                        "自动恢复完成确认时授权已被撤销或不再处于待提交状态。");
                var transition = EpbManager.SelectUnattendedRunChainTransition(
                    checkpoint.RunId,
                    checkpoint.RootRunId,
                    checkpoint.ParentRunId,
                    checkpoint.RestartGeneration,
                    checkpoint.Armed,
                    checkpoint.RecoveryChainPendingStart,
                    checkpoint.InProcessRecoveryPending,
                    runId == Guid.Empty ? string.Empty : runId.ToString("N"));
                checkpoint.SchemaVersion = CurrentSchemaVersion;
                checkpoint.Armed = true;
                checkpoint.RestartPending = false;
                checkpoint.InProcessRecoveryPending = false;
                checkpoint.GracefulPaused = false;
                checkpoint.RecoveryChainPendingStart = false;
                checkpoint.StoreDir = config.Test.StoreDir ?? string.Empty;
                checkpoint.TestName = config.Test.TestName ?? string.Empty;
                checkpoint.SelectedChannels = selected;
                checkpoint.LearnCycles = Math.Max(5, config.Test.LearnCycles);
                checkpoint.ConfigurationSha256 = ComputeConfigurationHash(config);
                checkpoint.ExecutableSha256 = ComputeFileHash(GetExecutablePath());
                checkpoint.BuildVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                checkpoint.EffectiveRuntimeSafetyParameters =
                    CaptureEffectiveRuntimeSafetyParameters();
                checkpoint.RootRunId = transition.RootRunId;
                checkpoint.ParentRunId = transition.ParentRunId;
                checkpoint.RunId = transition.CurrentRunId;
                checkpoint.RestartGeneration = transition.RestartGeneration;
                var requestedEpoch = Math.Max(0, runEpoch);
                if (transition.RecoveryContinuation)
                    checkpoint.RunEpoch = Math.Max(
                        checkpoint.RunEpoch + 1,
                        requestedEpoch);
                else if (transition.NewAuthorizationChain)
                    checkpoint.RunEpoch = Math.Max(1, requestedEpoch);
                else
                    checkpoint.RunEpoch = Math.Max(checkpoint.RunEpoch, requestedEpoch);
                if (transition.NewAuthorizationChain)
                {
                    RevokedRuns.Clear();
                    checkpoint.RestartHistoryUtc = new List<string>();
                    checkpoint.ActiveFaultCorrelationId = string.Empty;
                    checkpoint.LastRecoveryNonceSha256 = string.Empty;
                }
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.LastReason = "FormalRunArmed";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                checkpoint.RemainingFormalCycles = selected.ToDictionary(
                    channel => channel.ToString(CultureInfo.InvariantCulture),
                    channel =>
                    {
                        var record = config.Test.GetEpbRecord(channel);
                        return record.GetRemainingMechanicalCycles(config.Test.TestTarget);
                    });
                SaveUnsafe(checkpoint);
                return transition;
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
                checkpoint.RecoveryChainPendingStart = false;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.WatchdogSessionId = string.Empty;
                checkpoint.LastReason = string.IsNullOrWhiteSpace(reason) ? "AuthorizationRevoked" : reason;
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static void BindWatchdogSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            lock (Sync)
            {
                var checkpoint = LoadUnsafe() ?? new UnattendedRunCheckpoint();
                checkpoint.SchemaVersion = CurrentSchemaVersion;
                checkpoint.WatchdogSessionId = sessionId.Trim();
                checkpoint.LastReason = "WatchdogSessionAttached";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        /// <summary>
        /// 在任何破坏性清场之前持久化 Watchdog 接管所有权。只有回读仍为同一
        /// Session/Run 且 Armed+RestartPending 的记录才算准备完成。
        /// </summary>
        internal static WatchdogTakeoverHandoffReceipt PrepareWatchdogTakeoverHandoff(
            string sessionId,
            string correlationId)
        {
            var receipt = new WatchdogTakeoverHandoffReceipt
            {
                SessionId = (sessionId ?? string.Empty).Trim(),
                CorrelationId = (correlationId ?? string.Empty).Trim(),
                Error = string.Empty
            };
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null || !checkpoint.Armed)
                {
                    receipt.Error = "CheckpointNotArmed";
                    return receipt;
                }
                if (string.IsNullOrWhiteSpace(receipt.SessionId) ||
                    !string.Equals(
                        checkpoint.WatchdogSessionId,
                        receipt.SessionId,
                        StringComparison.Ordinal))
                {
                    receipt.Error = "WatchdogSessionMismatch";
                    return receipt;
                }
                if (!Guid.TryParse(checkpoint.RunId, out var runId) || runId == Guid.Empty)
                {
                    receipt.Error = "CheckpointRunIdMissing";
                    return receipt;
                }

                checkpoint.RestartPending = true;
                checkpoint.InProcessRecoveryPending = false;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.ActiveFaultCorrelationId = receipt.CorrelationId;
                checkpoint.LastReason = "WatchdogTakeoverHandoffPrepared";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);

                var persisted = LoadUnsafe();
                if (persisted == null || !persisted.Armed || !persisted.RestartPending ||
                    !string.Equals(
                        persisted.WatchdogSessionId,
                        receipt.SessionId,
                        StringComparison.Ordinal) ||
                    !EpbManager.AreSameNonEmptyRunIds(persisted.RunId, checkpoint.RunId) ||
                    persisted.Revision < checkpoint.Revision)
                {
                    receipt.Error = "CheckpointDurableReadbackMismatch";
                    return receipt;
                }

                receipt.Prepared = true;
                receipt.RunId = persisted.RunId;
                receipt.Revision = persisted.Revision;
                return receipt;
            }
        }

        internal static bool TryConsumeWatchdogRecovery(
            string sessionId,
            GlobalConfig config,
            out UnattendedRunCheckpoint checkpoint,
            out string error)
        {
            checkpoint = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                error = "WatchdogSessionIdMissing";
                return false;
            }
            lock (Sync)
            {
                DurableJsonReadResult<UnattendedRunCheckpoint> readResult;
                var current = LoadForRecoveryUnsafe(config, out readResult);
                if (current == null || !current.Armed || IsRunRevokedInMemory(current.RunId))
                {
                    if (current == null)
                    {
                        ArchiveCheckpointReadFailure(config, readResult);
                        error = "CheckpointReadFailed:" + FormatReadAttempts(readResult);
                    }
                    else
                        error = current.Armed
                            ? "CheckpointRunRevoked"
                            : "CheckpointDisarmed:" + (current.LastReason ?? "Unknown");
                    return false;
                }
                if (!string.Equals(current.WatchdogSessionId, sessionId, StringComparison.Ordinal))
                {
                    error = "WatchdogSessionMismatch";
                    return false;
                }
                if (config?.Test == null ||
                    !string.Equals(current.StoreDir, config.Test.StoreDir, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(current.TestName, config.Test.TestName, StringComparison.OrdinalIgnoreCase))
                {
                    error = "当前项目无法与本轮试验检查点对应。";
                    return false;
                }

                // Watchdog 恢复不以版本、哈希、构建时间或重试预算为许可条件。
                // 唯一授权边界是仍 Armed 的同一 Session；人工停止会先清除此字段。
                current.RestartPending = false;
                current.InProcessRecoveryPending = false;
                current.RecoveryNonce = string.Empty;
                current.RecoveryChainPendingStart = true;
                current.RestartHistoryUtc ??= new List<string>();
                current.RestartHistoryUtc.Add(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                current.LastRecoveryLoadSource = current.LastLoadSource ?? string.Empty;
                current.LastRecoveryLoadSha256 = current.LastLoadSha256 ?? string.Empty;
                current.LastReason = "WatchdogRecoveryInstanceValidated";
                current.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(current);
                checkpoint = current;
                return true;
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
                checkpoint.LastReason = "FormalCycleCommitted";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static void RecordMechanicalCycleCompleted(int channel)
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
                checkpoint.LastReason = "MechanicalCycleCompleted";
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
                if (checkpoint == null || !checkpoint.Armed ||
                    IsRunRevokedInMemory(checkpoint.RunId))
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
                    error = "检查点缺少V5运行链身份，拒绝自动续测。";
                    return false;
                }
                if (!EpbManager.AreSameNonEmptyRunIds(
                        checkpoint.RootRunId,
                        checkpoint.RootRunId))
                {
                    DisarmUnsafe(checkpoint, "RootRunIdMissingBeforeRestart");
                    error = "检查点缺少首次人工授权的根RunId，拒绝自动续测。";
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
                if (checkpoint.RestartHistoryUtc.Count >= EpbManager.UnattendedProcessRestartBudget)
                {
                    checkpoint.Armed = false;
                    checkpoint.RestartPending = false;
                    checkpoint.LastReason = "RestartBudgetExhausted";
                    checkpoint.UpdatedUtc = now.ToString("O", CultureInfo.InvariantCulture);
                    SaveUnsafe(checkpoint);
                    error = $"10分钟内已执行{EpbManager.UnattendedProcessRestartBudget}次自重启，重启预算耗尽。";
                    return false;
                }

                var nonce = Guid.NewGuid().ToString("N");
                checkpoint.RestartHistoryUtc.Add(now.ToString("O", CultureInfo.InvariantCulture));
                checkpoint.RestartPending = true;
                checkpoint.RecoveryNonce = nonce;
                checkpoint.LastRecoveryNonceSha256 = ComputeTextHash(nonce);
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
            string faultCorrelationId,
            out UnattendedRunCheckpoint checkpoint,
            out string error)
        {
            checkpoint = null;
            error = string.Empty;
            lock (Sync)
            {
                var current = LoadUnsafe();
                if (current == null || !current.Armed ||
                    IsRunRevokedInMemory(current.RunId))
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
                    error = "检查点版本低于V5，拒绝同进程自动续测。";
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

                // Schema 只能单调前进。V5 包含根/父/当前 RunId，恢复登记不得降级。
                current.SchemaVersion = CurrentSchemaVersion;
                current.InProcessRecoveryPending = true;
                current.InProcessRecoveryFingerprint = normalized;
                current.ActiveFaultCorrelationId = faultCorrelationId ?? string.Empty;
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
                if (current.SchemaVersion < CurrentSchemaVersion ||
                    !EpbManager.AreSameNonEmptyRunIds(current.RootRunId, current.RootRunId))
                {
                    DisarmUnsafe(current, "RecoveryChainIdentityMissing");
                    error = "恢复检查点缺少V5根RunId，拒绝自动续测。";
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
                current.RecoveryChainPendingStart = true;
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
                checkpoint.RecoveryChainPendingStart = false;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.LastReason = reason ?? "RestartCancelled";
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
            }
        }

        internal static bool ReleasePendingRestartForRetry(
            string expectedRecoveryNonce,
            string expectedRunId,
            string reason,
            out int attemptsInWindow,
            out string error)
        {
            attemptsInWindow = 0;
            error = string.Empty;
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                if (checkpoint == null)
                {
                    error = "CheckpointMissing";
                    return false;
                }
                if (!EpbManager.AreSameNonEmptyRunIds(checkpoint.RunId, expectedRunId))
                {
                    error = "RunIdMismatch";
                    return false;
                }
                if (!checkpoint.Armed)
                {
                    error = "AuthorizationRevoked";
                    return false;
                }
                if (!checkpoint.RestartPending ||
                    string.IsNullOrWhiteSpace(expectedRecoveryNonce) ||
                    !string.Equals(
                        checkpoint.RecoveryNonce,
                        expectedRecoveryNonce,
                        StringComparison.Ordinal))
                {
                    error = "RecoveryNonceMismatch";
                    return false;
                }

                // 保留当前 Armed 状态和 RestartHistoryUtc：失败的交接仍计入既有
                // 10分钟/3次预算，但只要人工没有撤销授权，当前恢复序列可主动重试。
                // 绝不能在这里把 Armed 强制设回 true，否则会覆盖并发的人工停止。
                var now = DateTime.UtcNow;
                checkpoint.RestartHistoryUtc = (checkpoint.RestartHistoryUtc ?? new List<string>())
                    .Where(value => TryParseUtc(value, out var timestamp) &&
                                    now - timestamp <= TimeSpan.FromMinutes(10))
                    .ToList();
                attemptsInWindow = checkpoint.RestartHistoryUtc.Count;
                checkpoint.RestartPending = false;
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.RecoveryChainPendingStart = false;
                checkpoint.LastReason = string.IsNullOrWhiteSpace(reason)
                    ? "RestartReleasedForRetry"
                    : reason;
                checkpoint.UpdatedUtc = now.ToString("O", CultureInfo.InvariantCulture);

                if (!EpbManager.ShouldScheduleUnattendedProcessRestartRetry(
                        nonceReleased: true,
                        armed: checkpoint.Armed,
                        checkpointRunId: checkpoint.RunId,
                        expectedRunId: expectedRunId,
                        attemptsInWindow: attemptsInWindow))
                {
                    var terminalReason = attemptsInWindow >= EpbManager.UnattendedProcessRestartBudget
                        ? "RestartBudgetExhaustedAfterFailedAttempt"
                        : "RestartRetryAuthorizationInvalid";
                    DisarmUnsafe(checkpoint, terminalReason);
                    error = terminalReason;
                    return false;
                }

                SaveUnsafe(checkpoint);
                return true;
            }
        }

        internal static bool IsPendingRestartAuthorized(
            string expectedRecoveryNonce,
            string expectedRunId)
        {
            lock (Sync)
            {
                var checkpoint = LoadUnsafe();
                return checkpoint != null &&
                       checkpoint.Armed &&
                       checkpoint.RestartPending &&
                       EpbManager.AreSameNonEmptyRunIds(checkpoint.RunId, expectedRunId) &&
                       !string.IsNullOrWhiteSpace(expectedRecoveryNonce) &&
                       string.Equals(
                           checkpoint.RecoveryNonce,
                           expectedRecoveryNonce,
                           StringComparison.Ordinal);
            }
        }

        internal static UnattendedRunCheckpoint Load()
        {
            lock (Sync) return LoadUnsafe();
        }

        internal static void MarkRunRevokedInMemory(string runId)
        {
            var normalized = NormalizeRunId(runId);
            if (normalized.Length > 0) RevokedRuns[normalized] = 0;
        }

        private static bool IsRunRevokedInMemory(string runId)
        {
            var normalized = NormalizeRunId(runId);
            return normalized.Length > 0 && RevokedRuns.ContainsKey(normalized);
        }

        private static string NormalizeRunId(string runId)
        {
            var text = (runId ?? string.Empty).Trim().Replace("-", string.Empty);
            return Guid.TryParseExact(text, "N", out var parsed)
                ? parsed.ToString("N")
                : string.Empty;
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
                checkpoint.RecoveryChainPendingStart = false;
                checkpoint.StoreDir = config.Test.StoreDir ?? string.Empty;
                checkpoint.TestName = config.Test.TestName ?? string.Empty;
                checkpoint.SelectedChannels = selected;
                checkpoint.LearnCycles = Math.Max(5, config.Test.LearnCycles);
                checkpoint.ConfigurationSha256 = ComputeConfigurationHash(config);
                checkpoint.ExecutableSha256 = ComputeFileHash(GetExecutablePath());
                checkpoint.BuildVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
                checkpoint.EffectiveRuntimeSafetyParameters =
                    CaptureEffectiveRuntimeSafetyParameters();
                checkpoint.RootRunId = runId.ToString("N");
                checkpoint.ParentRunId = string.Empty;
                checkpoint.RunId = runId.ToString("N");
                checkpoint.RestartGeneration = 0;
                checkpoint.RunEpoch = Math.Max(1, checkpoint.RunEpoch);
                checkpoint.RestartHistoryUtc = new List<string>();
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
                        return record.GetRemainingMechanicalCycles(config.Test.TestTarget);
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
                checkpoint.RecoveryChainPendingStart = false;
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
                checkpoint.RecoveryChainPendingStart = false;
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
                var configDirectory = RuntimeConfigPaths.Directory;
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
                // EpbRecords 同时包含运行进度和试验授权配置。旧实现删除整个节点，
                // 会把 TotalCount/Enabled 的人工变更也从身份哈希中删除，使恢复子进程
                // 可能在目标圈数已变化后继续上电。只剔除每圈提交会变化的运行字段，
                // 保留 Id、Enabled、TotalCount 参与配置身份验证。
                var runtimeFields = document.SelectNodes(
                    "//EpbRecords/Record/StartTime | " +
                    "//EpbRecords/Record/LatestStartTime | " +
                    "//EpbRecords/Record/RunTime | " +
                    "//EpbRecords/Record/RunCount | " +
                    "//EpbRecords/Record/Status");
                if (runtimeFields != null)
                    foreach (XmlNode node in runtimeFields.Cast<XmlNode>().ToArray())
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
            checkpoint.RecoveryChainPendingStart = false;
            checkpoint.RecoveryNonce = string.Empty;
            checkpoint.WatchdogSessionId = string.Empty;
            checkpoint.LastReason = reason;
            checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            SaveUnsafe(checkpoint);
        }

        private static UnattendedRunCheckpoint LoadUnsafe()
        {
            var result = DurableJsonFileStore.ReadLatestValid<UnattendedRunCheckpoint>(
                checkpoint => checkpoint?.Revision ?? 0,
                CheckpointPath,
                CheckpointBackupPath);
            var local = ApplyLoadMetadata(result);
            if (local == null) return null;
            var projectPath = GetProjectCheckpointPath(local.StoreDir, local.TestName);
            if (string.IsNullOrWhiteSpace(projectPath)) return local;
            return ApplyLoadMetadata(
                DurableJsonFileStore.ReadLatestValid<UnattendedRunCheckpoint>(
                    checkpoint => checkpoint?.Revision ?? 0,
                    CheckpointPath,
                    CheckpointBackupPath,
                    projectPath,
                    projectPath + ".bak"));
        }

        private static void SaveUnsafe(UnattendedRunCheckpoint checkpoint)
        {
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            var projectPath = GetProjectCheckpointPath(checkpoint.StoreDir, checkpoint.TestName);
            var previousRead = DurableJsonFileStore.ReadLatestValid<UnattendedRunCheckpoint>(
                candidate => candidate?.Revision ?? 0,
                CheckpointPath,
                CheckpointBackupPath,
                projectPath,
                string.IsNullOrWhiteSpace(projectPath) ? string.Empty : projectPath + ".bak");
            var previous = previousRead.Value;
            checkpoint.Revision = Math.Max(
                checkpoint.Revision,
                previous?.Revision ?? 0) + 1;
            var bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(checkpoint));
            var sha256 = DurableJsonFileStore.ComputeSha256(bytes);
            DurableJsonFileStore.WriteAtomicWithBackup(CheckpointPath, bytes);
            var mirrorWriteError = string.Empty;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                try { DurableJsonFileStore.WriteAtomicWithBackup(projectPath, bytes); }
                catch (Exception ex) { mirrorWriteError = ex.GetBaseException().Message; }
            }
            if (ShouldAuditCheckpointTransition(previous, checkpoint) ||
                !string.IsNullOrWhiteSpace(mirrorWriteError))
                AppendCheckpointAudit(
                    checkpoint,
                    previous?.Armed,
                    sha256,
                    projectPath,
                    mirrorWriteError);
            checkpoint.LastLoadSource = CheckpointPath;
            checkpoint.LastLoadStatus = DurableJsonReadStatus.Valid.ToString();
            checkpoint.LastLoadSha256 = sha256;
        }

        private static UnattendedRunCheckpoint LoadForRecoveryUnsafe(
            GlobalConfig config,
            out DurableJsonReadResult<UnattendedRunCheckpoint> result)
        {
            var projectPath = GetProjectCheckpointPath(
                config?.Test?.StoreDir,
                config?.Test?.TestName);
            result = DurableJsonFileStore.ReadLatestValid<UnattendedRunCheckpoint>(
                checkpoint => checkpoint?.Revision ?? 0,
                CheckpointPath,
                CheckpointBackupPath,
                projectPath,
                string.IsNullOrWhiteSpace(projectPath) ? string.Empty : projectPath + ".bak");
            return ApplyLoadMetadata(result);
        }

        private static UnattendedRunCheckpoint ApplyLoadMetadata(
            DurableJsonReadResult<UnattendedRunCheckpoint> result)
        {
            var value = result?.Value;
            if (value == null) return null;
            value.LastLoadSource = result.SourcePath ?? string.Empty;
            value.LastLoadStatus = result.Status.ToString();
            value.LastLoadSha256 = result.Attempts.FirstOrDefault(attempt =>
                string.Equals(attempt.Path, result.SourcePath, StringComparison.OrdinalIgnoreCase))
                ?.Sha256 ?? string.Empty;
            return value;
        }

        private static string GetProjectCheckpointPath(string storeDir, string testName)
        {
            if (string.IsNullOrWhiteSpace(storeDir) || string.IsNullOrWhiteSpace(testName))
                return string.Empty;
            try
            {
                return Path.Combine(
                    Path.GetFullPath(storeDir),
                    testName,
                    "Recovery",
                    "unattended-run-checkpoint.json");
            }
            catch { return string.Empty; }
        }

        private static void AppendCheckpointAudit(
            UnattendedRunCheckpoint checkpoint,
            bool? previousArmed,
            string sha256,
            string projectPath,
            string mirrorWriteError)
        {
            var record = Json.Serialize(new Dictionary<string, object>
            {
                ["TimestampUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["Revision"] = checkpoint.Revision,
                ["PreviousArmed"] = previousArmed,
                ["Armed"] = checkpoint.Armed,
                ["Reason"] = checkpoint.LastReason ?? string.Empty,
                ["RunId"] = checkpoint.RunId ?? string.Empty,
                ["RunEpoch"] = checkpoint.RunEpoch,
                ["WatchdogSessionId"] = checkpoint.WatchdogSessionId ?? string.Empty,
                ["ProcessId"] = Process.GetCurrentProcess().Id,
                ["Sha256"] = sha256 ?? string.Empty,
                ["PrimaryPath"] = CheckpointPath,
                ["ProjectMirrorPath"] = projectPath ?? string.Empty,
                ["ProjectMirrorWriteError"] = mirrorWriteError ?? string.Empty
            });
            AppendAuditLine(CheckpointAuditPath, record);
            if (!string.IsNullOrWhiteSpace(projectPath))
                AppendAuditLine(Path.Combine(
                    Path.GetDirectoryName(projectPath),
                    "unattended-run-checkpoint.audit.jsonl"), record);
        }

        private static bool ShouldAuditCheckpointTransition(
            UnattendedRunCheckpoint previous,
            UnattendedRunCheckpoint current)
        {
            if (current == null || previous == null) return true;
            if (previous.Armed != current.Armed ||
                previous.RestartPending != current.RestartPending ||
                previous.InProcessRecoveryPending != current.InProcessRecoveryPending ||
                previous.RecoveryChainPendingStart != current.RecoveryChainPendingStart ||
                !string.Equals(previous.RunId, current.RunId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    previous.WatchdogSessionId,
                    current.WatchdogSessionId,
                    StringComparison.Ordinal))
                return true;
            var reason = current.LastReason ?? string.Empty;
            return reason.IndexOf("Watchdog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("Recovery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("Restart", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("GracefulPause", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(reason, "FormalRunArmed", StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendAuditLine(string path, string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var bytes = new UTF8Encoding(false).GetBytes((line ?? string.Empty) + Environment.NewLine);
                using (var stream = new FileStream(
                           path,
                           FileMode.Append,
                           FileAccess.Write,
                           FileShare.Read,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
            catch { }
        }

        private static string FormatReadAttempts(
            DurableJsonReadResult<UnattendedRunCheckpoint> result)
        {
            if (result?.Attempts == null || result.Attempts.Count == 0)
                return "NoCandidate";
            return string.Join(";", result.Attempts.Select(attempt =>
                $"{attempt.Path}|{attempt.Status}|{attempt.Detail}"));
        }

        private static void ArchiveCheckpointReadFailure(
            GlobalConfig config,
            DurableJsonReadResult<UnattendedRunCheckpoint> result)
        {
            try
            {
                if (config?.Test == null || string.IsNullOrWhiteSpace(config.Test.StoreDir) ||
                    string.IsNullOrWhiteSpace(config.Test.TestName)) return;
                var directory = Path.Combine(
                    config.Test.StoreDir,
                    config.Test.TestName,
                    "Recovery",
                    "CheckpointFailures",
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                foreach (var attempt in result?.Attempts ?? new List<DurableJsonReadAttempt>())
                {
                    if (string.IsNullOrWhiteSpace(attempt.Path) || !File.Exists(attempt.Path)) continue;
                    var name = Path.GetFileName(attempt.Path);
                    File.Copy(attempt.Path, Path.Combine(directory, name), true);
                }
                File.WriteAllText(
                    Path.Combine(directory, "read-result.txt"),
                    FormatReadAttempts(result),
                    new UTF8Encoding(false));
            }
            catch { }
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

        private static string ComputeTextHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string CaptureEffectiveRuntimeSafetyParameters()
        {
            double ReadDouble(string key, double fallback, double min, double max)
            {
                try
                {
                    return double.TryParse(
                               System.Configuration.ConfigurationManager.AppSettings[key],
                               NumberStyles.Float,
                               CultureInfo.InvariantCulture,
                               out var value) && value >= min && value <= max
                        ? value
                        : fallback;
                }
                catch { return fallback; }
            }
            int ReadInt(string key, int fallback, int min, int max)
            {
                try
                {
                    return int.TryParse(
                               System.Configuration.ConfigurationManager.AppSettings[key],
                               NumberStyles.Integer,
                               CultureInfo.InvariantCulture,
                               out var value) && value >= min && value <= max
                        ? value
                        : fallback;
                }
                catch { return fallback; }
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "DaqWarnMs={0:F0};DaqSuspectMs={1:F0};DaqTripMs={2:F0};" +
                "FormalAdmissionMs={3};OrphanGraceMs={4};NoProgressMs={5};MaxTotalMs={6};" +
                "PowerOffProofA={7:F3};PowerOffProofMaxAgeMs={8}",
                ReadDouble("DaqLivenessWarnThresholdMs", 250, 100, 5000),
                ReadDouble("DaqLivenessSuspectThresholdMs", 1500, 200, 30000),
                ReadDouble("DaqLivenessTripThresholdMs", 5000, 300, 300000),
                ReadInt("GlobalFormalSlotAdmissionWindowMs", 300, 200, 500),
                ReadInt("RecoveryOrphanGraceMs", 10000, 10000, 60000),
                ReadInt("InProcessRecoveryNoProgressMs", 60000, 10000, 300000),
                ReadInt("InProcessRecoveryMaxTotalMs", 300000, 60000, 900000),
                ReadDouble("EpbPowerSupplyOffProofThresholdA", 0.5, 0.01, 20.0),
                ReadInt("EpbPowerSupplyOffProofMaxAgeMs", 1000, 100, 10000));
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

    internal static class InProcessRecoveryLeasePolicy
    {
        internal static string SelectHandoffBoundary(
            DateTime startedUtc,
            DateTime lastProgressUtc,
            DateTime nowUtc,
            int noProgressMs,
            int maxTotalMs)
        {
            if ((nowUtc - startedUtc).TotalMilliseconds >= maxTotalMs)
                return "MaxTotal";
            if ((nowUtc - lastProgressUtc).TotalMilliseconds >= noProgressMs)
                return "NoMaterialProgress";
            return string.Empty;
        }
    }

    internal static class UnattendedRecoveryCoordinator
    {
        private sealed class InProcessRecoveryProgressLease
        {
            private long _lastProgressUtcTicks;
            private long _progressVersion;
            private string _stage;

            public InProcessRecoveryProgressLease()
            {
                StartedUtc = DateTime.UtcNow;
                _lastProgressUtcTicks = StartedUtc.Ticks;
                _stage = "Created";
            }

            public DateTime StartedUtc { get; }
            public DateTime LastProgressUtc =>
                new DateTime(Interlocked.Read(ref _lastProgressUtcTicks), DateTimeKind.Utc);
            public long ProgressVersion => Interlocked.Read(ref _progressVersion);
            public string Stage => Volatile.Read(ref _stage) ?? string.Empty;

            public void Report(string stage)
            {
                Volatile.Write(ref _stage, string.IsNullOrWhiteSpace(stage) ? "Progress" : stage);
                Interlocked.Exchange(ref _lastProgressUtcTicks, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref _progressVersion);
            }
        }

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
        private static CancellationTokenSource _restartSequenceCancellation;
        private static EventWaitHandle _activeHandoffRevocation;
        private static int _restartStarted;
        private static int _inProcessRecoveryStarted;
        private static int _recoveryProcessMode;
        private static InProcessRecoveryProgressLease _activeInProcessRecoveryLease;

        internal static bool IsRecoveryProcessMode =>
            Volatile.Read(ref _recoveryProcessMode) != 0;

        internal static void SetRecoveryProcessMode(bool enabled)
            => Volatile.Write(ref _recoveryProcessMode, enabled ? 1 : 0);

        internal static void Attach(EpbManager manager, GlobalConfig config)
        {
            if (manager == null || config == null) return;
            lock (Sync)
            {
                if (ReferenceEquals(_manager, manager)) return;
                if (_manager != null)
                {
                    _manager.SystemFaultRaised -= OnSystemFaultRaised;
                    _manager.RunAuthorizationRevocationBarrier -=
                        OnRunAuthorizationRevocationBarrier;
                    _manager.RunAuthorizationRevoking -= OnRunAuthorizationRevoking;
                    _manager.ChannelMechanicalCycleCompleted -= OnMechanicalCycleCompleted;
                }
                _manager = manager;
                _config = config;
                manager.SystemFaultRaised += OnSystemFaultRaised;
                manager.RunAuthorizationRevocationBarrier +=
                    OnRunAuthorizationRevocationBarrier;
                manager.RunAuthorizationRevoking += OnRunAuthorizationRevoking;
                manager.ChannelMechanicalCycleCompleted += OnMechanicalCycleCompleted;
            }
        }

        internal static void Arm(
            GlobalConfig config,
            IEnumerable<int> channels,
            Guid runId,
            long runEpoch = 0)
        {
            CancelRestartRetrySequence();
            ReconcileMechanicalProgressForCheckpoint(config, channels);
            var transition = UnattendedRunCheckpointStore.Arm(
                config, channels, runId, runEpoch: runEpoch);
            if (string.IsNullOrWhiteSpace(transition.CurrentRunId)) return;
            var checkpoint = UnattendedRunCheckpointStore.Load();
            ProjectLogHub.Write(
                ProjectLogLevel.Info,
                $"FieldMetric RECOVERY_CHAIN RootRunId={transition.RootRunId} " +
                $"ParentRunId={(string.IsNullOrWhiteSpace(transition.ParentRunId) ? "none" : transition.ParentRunId)} " +
                $"CurrentRunId={transition.CurrentRunId} " +
                $"RestartGeneration={transition.RestartGeneration} " +
                $"SameRun={transition.SameRun} " +
                $"RecoveryContinuation={transition.RecoveryContinuation} " +
                $"RecoveryMode={transition.RecoveryMode} " +
                $"FaultCorrelationId={(string.IsNullOrWhiteSpace(checkpoint?.ActiveFaultCorrelationId) ? "none" : checkpoint.ActiveFaultCorrelationId)} " +
                $"RecoveryNonceSha256={(string.IsNullOrWhiteSpace(checkpoint?.LastRecoveryNonceSha256) ? "none" : checkpoint.LastRecoveryNonceSha256)}",
                "FIELD");
        }

        internal static void ConfirmRecoveryBatchStarted(
            GlobalConfig config,
            IEnumerable<int> channels,
            Guid runId,
            long runEpoch = 0)
        {
            if (runId == Guid.Empty)
                throw new InvalidOperationException("自动恢复完成确认缺少新执行 RunId。");
            CancelRestartRetrySequence();
            ReconcileMechanicalProgressForCheckpoint(config, channels);
            var transition = UnattendedRunCheckpointStore.Arm(
                config,
                channels,
                runId,
                requireRecoveryPending: true,
                runEpoch: runEpoch);
            var checkpoint = UnattendedRunCheckpointStore.Load();
            ProjectLogHub.Write(
                ProjectLogLevel.Info,
                $"FieldMetric RECOVERY_CHAIN RootRunId={transition.RootRunId} " +
                $"ParentRunId={(string.IsNullOrWhiteSpace(transition.ParentRunId) ? "none" : transition.ParentRunId)} " +
                $"CurrentRunId={transition.CurrentRunId} " +
                $"RestartGeneration={transition.RestartGeneration} " +
                $"SameRun={transition.SameRun} " +
                $"RecoveryContinuation={transition.RecoveryContinuation} " +
                $"RecoveryMode={transition.RecoveryMode} " +
                $"FaultCorrelationId={(string.IsNullOrWhiteSpace(checkpoint?.ActiveFaultCorrelationId) ? "none" : checkpoint.ActiveFaultCorrelationId)} " +
                $"RecoveryNonceSha256={(string.IsNullOrWhiteSpace(checkpoint?.LastRecoveryNonceSha256) ? "none" : checkpoint.LastRecoveryNonceSha256)}",
                "FIELD");
        }

        private static void ReconcileMechanicalProgressForCheckpoint(
            GlobalConfig config,
            IEnumerable<int> channels)
        {
            EpbManager manager;
            lock (Sync) manager = _manager;
            if (manager == null || config?.Test == null) return;
            foreach (var channel in (channels ?? Enumerable.Empty<int>())
                         .Where(value => value >= 1 && value <= 12)
                         .Distinct())
            {
                var record = config.Test.GetEpbRecord(channel);
                if (record == null) continue;
                record.MechanicalCycleCount = Math.Max(
                    record.EffectiveMechanicalCycleCount,
                    manager.GetDurableMechanicalCycleCount(channel));
            }
        }

        internal static void RegisterQuiesceAndFlush(Func<Task> callback)
        {
            lock (Sync) _quiesceAndFlush = callback;
        }

        internal static void Disarm(string reason)
        {
            CancelRestartRetrySequence();
            UnattendedRunCheckpointStore.Disarm(reason);
        }

        internal static Task DisarmAsync(string reason)
        {
            // 先在调用线程撤销仍在等待的恢复/交接，再把 WriteThrough + Flush(true)
            // 检查点持久化移出 UI 线程。磁盘或网络盘抖动不能挡在物理断电之前。
            CancelRestartRetrySequence();
            return Task.Run(() => UnattendedRunCheckpointStore.Disarm(reason));
        }

        private static void CancelRestartRetrySequence()
        {
            CancellationTokenSource cancellation;
            EventWaitHandle handoffRevocation;
            lock (Sync)
            {
                cancellation = _restartSequenceCancellation;
                handoffRevocation = _activeHandoffRevocation;
            }
            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            try { handoffRevocation?.Set(); } catch (ObjectDisposedException) { }
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

        /// <summary>
        /// 恢复子进程已经消费一次性 nonce 后，初始化或批量启动仍可能因瞬态设备、
        /// 配置读取或 UI 初始化失败。此时不能弹框后静默停住，也不能清空既有授权链；
        /// 使用当前检查点 RunId 和跨进程重启历史重新进入同一 10 分钟/3 次预算。
        /// </summary>
        internal static void RequestRecoveryStartupRestart(string source, Exception exception)
        {
            var correlationId = Guid.NewGuid();
            var reason = source + ": " + (exception?.Message ?? "unknown recovery startup exception");
            var checkpoint = UnattendedRunCheckpointStore.Load();
            ProjectLogHub.Write(
                ProjectLogLevel.Error,
                $"FieldMetric RECOVERY_STARTUP Result=RetryScheduled " +
                $"RootRunId={NormalizeMetric(checkpoint?.RootRunId)} " +
                $"CurrentRunId={NormalizeMetric(checkpoint?.RunId)} " +
                $"RestartGeneration={Math.Max(0, checkpoint?.RestartGeneration ?? 0)} " +
                $"AttemptInWindow={checkpoint?.RestartHistoryUtc?.Count ?? 0} " +
                $"RecoveryNonceSha256={NormalizeMetric(checkpoint?.LastRecoveryNonceSha256)} " +
                $"CorrelationId={correlationId:N} ReasonCode=RecoveryStartupFailed",
                "FIELD",
                exception);
            RecoveryTasks.Observe(
                Task.Run(() => RestartAsync(reason, correlationId.ToString("N"))),
                "RecoveryStartupProcessRestart",
                correlationId);
        }

        internal static void LogRecoveryStartupRecovered(Guid startedRunId, IEnumerable<int> channels)
        {
            var checkpoint = UnattendedRunCheckpointStore.Load();
            ProjectLogHub.Write(
                ProjectLogLevel.Info,
                $"FieldMetric RECOVERY_STARTUP Result=Recovered " +
                $"RootRunId={NormalizeMetric(checkpoint?.RootRunId)} " +
                $"ParentRunId={NormalizeMetric(checkpoint?.ParentRunId)} " +
                $"CurrentRunId={(startedRunId == Guid.Empty ? NormalizeMetric(checkpoint?.RunId) : startedRunId.ToString("N"))} " +
                $"RestartGeneration={Math.Max(0, checkpoint?.RestartGeneration ?? 0)} " +
                $"AttemptInWindow={checkpoint?.RestartHistoryUtc?.Count ?? 0} " +
                $"RecoveryNonceSha256={NormalizeMetric(checkpoint?.LastRecoveryNonceSha256)} " +
                $"Channels={string.Join(",", (channels ?? Array.Empty<int>()).Distinct().OrderBy(x => x))}",
                "FIELD");
        }

        private static void LogProcessRestartMetric(
            string result,
            string correlationId,
            string reasonCode)
        {
            var checkpoint = UnattendedRunCheckpointStore.Load();
            ProjectLogHub.Write(
                string.Equals(result, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result, "TerminalSafeStop", StringComparison.OrdinalIgnoreCase)
                    ? ProjectLogLevel.Error
                    : ProjectLogLevel.Info,
                $"FieldMetric PROCESS_RESTART Result={NormalizeMetric(result)} " +
                $"RootRunId={NormalizeMetric(checkpoint?.RootRunId)} " +
                $"CurrentRunId={NormalizeMetric(checkpoint?.RunId)} " +
                $"RestartGeneration={Math.Max(0, checkpoint?.RestartGeneration ?? 0)} " +
                $"AttemptInWindow={checkpoint?.RestartHistoryUtc?.Count ?? 0} " +
                $"RecoveryNonceSha256={NormalizeMetric(checkpoint?.LastRecoveryNonceSha256)} " +
                $"FaultCorrelationId={NormalizeMetric(checkpoint?.ActiveFaultCorrelationId)} " +
                $"CorrelationId={NormalizeMetric(correlationId)} " +
                $"ReasonCode={NormalizeMetric(reasonCode)}",
                "FIELD");
        }

        private static string NormalizeMetric(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "none";
            return new string(value
                .Trim()
                .Select(character => char.IsLetterOrDigit(character) ||
                                     character == '-' || character == '_' || character == '.'
                    ? character
                    : '_')
                .ToArray());
        }

        private static void OnRunAuthorizationRevoking(StopContext context)
        {
            // 正常暂停后关闭监视窗口仍需执行统一硬件关闭确认，但不能撤销用户明确保存的
            // 暂停恢复检查点；人工“停止试验”和所有报警路径仍会撤销。
            if (context?.Source == StopSource.ApplicationClosing)
            {
                var graceful = UnattendedRunCheckpointStore.Load();
                if (graceful?.Armed == true && graceful.GracefulPaused)
                {
                    CancelRestartRetrySequence();
                    return;
                }
            }

            // A system-fault restart registers a one-time recovery nonce before it asks
            // EpbManager to perform the common StopAll safety sequence.  That internal
            // StopAll must not revoke the nonce it is protecting.  Manual stop/close and
            // hardware alarm paths still disarm the checkpoint before cancellation.
            if (context?.Source == StopSource.SystemFault)
            {
                var checkpoint = UnattendedRunCheckpointStore.Load();
                if (checkpoint?.RestartPending == true ||
                    checkpoint?.InProcessRecoveryPending == true)
                    return;
            }
            CancelRestartRetrySequence();
            UnattendedRunCheckpointStore.DisarmIfRunMatches(
                context?.RunId,
                $"{context?.Source}: {context?.Reason ?? "Run authorization revoked"}");
        }

        private static void OnRunAuthorizationRevocationBarrier(StopContext context)
        {
            UnattendedRunCheckpointStore.MarkRunRevokedInMemory(context?.RunId);
            CancelRestartRetrySequence();
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
                    correlationId.ToString("N"),
                    fault?.RunId.ToString("N"))),
                "UnattendedProcessRestart",
                correlationId,
                fault?.AffectedChannels?.FirstOrDefault() ?? 0);
        }

        internal static Task<bool> DrainBackgroundTasksAsync(int timeoutMs)
            => RecoveryTasks.DrainAsync(timeoutMs);

        private static void OnMechanicalCycleCompleted(
            int channel,
            CycleAttemptKind kind,
            int cycleNumber)
        {
            UnattendedRunCheckpointStore.RecordMechanicalCycleCompleted(channel);
            Volatile.Read(ref _activeInProcessRecoveryLease)?.Report(
                $"MechanicalCycle:EPB{channel}:{kind}:{cycleNumber}");
        }

        private static async Task RecoverInProcessOrRestartAsync(ControlFault fault)
        {
            if (Interlocked.CompareExchange(ref _inProcessRecoveryStarted, 1, 0) != 0)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Info,
                    "同进程恢复已有唯一 owner，重复故障请求已合并。",
                    "无人值守恢复");
                return;
            }
            var noProgressMs = ReadRecoveryDurationSetting(
                "InProcessRecoveryNoProgressMs",
                60000,
                10000,
                300000);
            var maxTotalMs = ReadRecoveryDurationSetting(
                "InProcessRecoveryMaxTotalMs",
                300000,
                60000,
                900000);
            var lease = new InProcessRecoveryProgressLease();
            var releaseOwnerInFinally = true;
            try
            {
                using (var workerCancellation = new CancellationTokenSource())
                {
                    Volatile.Write(ref _activeInProcessRecoveryLease, lease);
                    var recovery = RecoverInProcessOrRestartCoreAsync(
                        fault,
                        lease,
                        workerCancellation.Token);
                    string boundaryReason = null;
                    while (!recovery.IsCompleted)
                    {
                        await Task.WhenAny(recovery, Task.Delay(500)).ConfigureAwait(false);
                        if (recovery.IsCompleted) break;
                        var now = DateTime.UtcNow;
                        boundaryReason = InProcessRecoveryLeasePolicy.SelectHandoffBoundary(
                            lease.StartedUtc,
                            lease.LastProgressUtc,
                            now,
                            noProgressMs,
                            maxTotalMs);
                        if (!string.IsNullOrWhiteSpace(boundaryReason))
                        {
                            break;
                        }
                    }

                    if (boundaryReason == null)
                    {
                        await recovery.ConfigureAwait(false);
                        return;
                    }

                    workerCancellation.Cancel();
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        $"同进程恢复到达外部接管边界，先取消恢复 worker 并执行有界安全清场。" +
                        $"Boundary={boundaryReason}; ProgressVersion={lease.ProgressVersion}; " +
                        $"Stage={lease.Stage}; TotalMs={(DateTime.UtcNow - lease.StartedUtc).TotalMilliseconds:F0}; " +
                        $"IdleMs={(DateTime.UtcNow - lease.LastProgressUtc).TotalMilliseconds:F0}",
                        "无人值守恢复");
                    await Task.WhenAny(recovery, Task.Delay(TimeSpan.FromSeconds(30)))
                        .ConfigureAwait(false);
                    if (!recovery.IsCompleted)
                    {
                        releaseOwnerInFinally = false;
                        RecoveryTasks.Observe(
                            ReleaseInProcessRecoveryOwnerWhenWorkerStopsAsync(recovery, lease),
                            "CancelledInProcessRecoveryWorker",
                            fault?.CorrelationId ?? Guid.Empty,
                            fault?.AffectedChannels?.FirstOrDefault() ?? 0);
                    }

                    EpbManager manager;
                    lock (Sync) manager = _manager;
                    var reason = fault?.Reason ?? "SoftwareRecoveryCircuitOpen";
                    if (manager != null)
                    {
                        using (var safetyCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                        {
                            try
                            {
                                await manager.PrepareForFreshRestartAsync(
                                        new StopContext
                                        {
                                            Source = StopSource.SystemFault,
                                            Reason = $"外部恢复安全交接：{boundaryReason}:{reason}",
                                            Initiator = nameof(UnattendedRecoveryCoordinator),
                                            CorrelationId = fault?.CorrelationId.ToString("N"),
                                            RequestedUtc = DateTime.UtcNow
                                        },
                                        safetyCancellation.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                ProjectLogHub.Write(
                                    ProjectLogLevel.Error,
                                    "外部恢复接管前的有界安全清场未正常完成。",
                                    "无人值守恢复",
                                    ex);
                            }
                        }
                    }

                    var externalReason = $"InProcessRecovery{boundaryReason}:{reason}";
                    try { manager?.RevokeExecutionForExternalRecovery(externalReason); }
                    catch { }
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "同进程恢复已取消并完成有界安全交接，现撤销当前进程上电授权并交由独立 Watchdog 接管。",
                        "无人值守恢复");
                    WatchdogRuntime.RequestExternalRecovery(
                        externalReason +
                        ";CorrelationId=" + (fault?.CorrelationId.ToString("N") ?? string.Empty));
                }
            }
            finally
            {
                if (releaseOwnerInFinally)
                {
                    Interlocked.CompareExchange(ref _activeInProcessRecoveryLease, null, lease);
                    Interlocked.Exchange(ref _inProcessRecoveryStarted, 0);
                }
            }
        }

        private static async Task ReleaseInProcessRecoveryOwnerWhenWorkerStopsAsync(
            Task recovery,
            InProcessRecoveryProgressLease lease)
        {
            try
            {
                await recovery.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _activeInProcessRecoveryLease, null, lease);
                Interlocked.Exchange(ref _inProcessRecoveryStarted, 0);
            }
        }

        private static async Task RecoverInProcessOrRestartCoreAsync(
            ControlFault fault,
            InProcessRecoveryProgressLease progress,
            CancellationToken cancellationToken)
        {
            var recoveryBatchCommitted = false;
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
                progress.Report("RegistrationStarted");
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
                        fault?.RunId.ToString("N"),
                        correlationId,
                        out checkpoint,
                        out registrationError))
                {
                    if (string.Equals(registrationError, "RunIdMismatch", StringComparison.Ordinal))
                    {
                        ProjectLogHub.Write(
                            ProjectLogLevel.Warning,
                            $"忽略迟到旧运行的无人值守恢复请求。" +
                            $"FaultRunId={fault?.RunId:N}; CorrelationId={fault?.CorrelationId:N}; Fault={reason}",
                            "无人值守恢复");
                        return;
                    }
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        $"同进程自动恢复不可用，升级到进程自重启。" +
                        $"Reason={registrationError}; Fault={reason}",
                        "无人值守恢复");
                    await RestartAsync(reason, correlationId, fault?.RunId.ToString("N"))
                        .ConfigureAwait(false);
                    return;
                }

                ProjectLogHub.Write(
                    ProjectLogLevel.Warning,
                    $"启动同进程无人值守恢复：冻结旧批次→StopAll→逻辑清场→完整学习。" +
                    $"Fingerprint={fingerprint}",
                    "无人值守恢复");
                progress.Report("StopAllStarted");
                var safety = await manager.PrepareForFreshRestartAsync(
                        new StopContext
                        {
                            Source = StopSource.SystemFault,
                            Reason = "同进程无人值守恢复：" + reason,
                            Initiator = nameof(UnattendedRecoveryCoordinator),
                            CorrelationId = correlationId,
                            RequestedUtc = DateTime.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                progress.Report("StopAllCompleted");
                if (!safety.CanRestartInProcess)
                    throw new InvalidOperationException(
                        "同进程恢复清场不变量未通过：" + safety.LogicalError);

                var authorized = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                    .Where(channel => channel >= 1 && channel <= 12)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                var durableRemaining = authorized.ToDictionary(
                    channel => channel,
                    channel =>
                    {
                        var record = config.Test.GetEpbRecord(channel);
                        var total = record.TotalCount > 0
                            ? record.TotalCount
                            : config.Test.TestTarget;
                        return (int)Math.Max(
                            0L,
                            total - manager.GetDurableMechanicalCycleCount(channel));
                    });
                var remainingPlan = EpbManager.BuildUnattendedRemainingCyclePlan(
                    authorized,
                    checkpoint.RemainingFormalCycles,
                    durableRemaining);
                progress.Report("RemainingPlanValidated");
                if (!remainingPlan.IsValid)
                    throw new InvalidOperationException(
                        remainingPlan.Error +
                        "；拒绝在正式圈进度证据不一致时同进程自动上电。");
                var selected = remainingPlan.Channels;
                foreach (var channel in authorized)
                {
                    var key = channel.ToString(CultureInfo.InvariantCulture);
                    var checkpointValue = checkpoint.RemainingFormalCycles[key];
                    var durableValue = remainingPlan.RemainingCycles[channel];
                    if (durableValue < checkpointValue)
                        ProjectLogHub.Write(
                            ProjectLogLevel.Info,
                            $"FieldMetric RECOVERY_PROGRESS Result=DurableAhead EPB={channel} " +
                            $"DurableRemaining={durableValue} " +
                            $"CheckpointRemaining={checkpointValue} RecoveryMode=InProcess",
                            "FIELD");
                }
                if (selected.Length == 0)
                {
                    UnattendedRunCheckpointStore.CompleteInProcessRecovery(true, "NoRemainingCycles");
                    UnattendedRunCheckpointStore.Disarm("FormalRunAlreadyCompleted");
                    return;
                }

                manager.EpbTestCycle = remainingPlan.RemainingCycles
                    .Where(pair => pair.Value > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                var learnCycles = Math.Max(5, checkpoint.LearnCycles);
                progress.Report("BatchRecoveryStarted");
                var startResult = await manager.StartBatchSynchronizedWithResultAsync(
                        selected,
                        learnCycles,
                        new RunChainIdentity(
                            Guid.NewGuid(),
                            Guid.TryParse(checkpoint.RootRunId, out var rootRunId) && rootRunId != Guid.Empty
                                ? rootRunId
                                : (Guid.TryParse(checkpoint.RunId, out var fallbackRootRunId)
                                    ? fallbackRootRunId
                                    : Guid.Empty),
                            Guid.TryParse(checkpoint.RunId, out var parentRunId)
                                ? parentRunId
                                : Guid.Empty,
                            Math.Max(0, checkpoint.RestartGeneration + 1),
                            Math.Max(1, checkpoint.RunEpoch + 1)),
                        cancellationToken)
                    .ConfigureAwait(false);
                progress.Report("BatchRecoveryCompleted");
                if (EpbManager.HasInfrastructureHardwareIsolationPersistenceFailure(startResult))
                {
                    UnattendedRunCheckpointStore.CompleteInProcessRecovery(
                        false,
                        "InfrastructureHardwareIsolationPersistenceFailed");
                    UnattendedRunCheckpointStore.Disarm(
                        "InfrastructureHardwareIsolationPersistenceFailed");
                    recoveryBatchCommitted = true;
                    var persistenceFaultSummary = string.Join(
                        ";",
                        startResult.Faults
                            .Where(item => item != null)
                            .Select(item => $"EPB{item.Channel}:{item.Reason}"));
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "本次实时硬件证据已使故障通道安全停机，但项目Enabled=false未能耐久保存；" +
                        "已关闭无人值守重启授权，禁止从旧XML重新选中故障通道。" +
                        $"Faults=[{persistenceFaultSummary}]",
                        "无人值守恢复");
                    return;
                }
                var startValidation = EpbManager.ValidateUnattendedBatchStartResult(
                    selected,
                    startResult);
                if (!string.IsNullOrWhiteSpace(startValidation))
                    throw new InvalidOperationException(
                        "同进程无人值守恢复未启动全部授权通道：" + startValidation);
                if (EpbManager.IsInfrastructureHardwareOnlyStartResult(startResult))
                {
                    UnattendedRunCheckpointStore.Disarm(
                        "AllRemainingChannelsPermanentlyIsolatedByHardwareEvidence");
                    recoveryBatchCommitted = true;
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        $"同进程恢复的全部剩余通道已由本次实时硬件证据永久隔离；" +
                        $"Channels=[{string.Join(",", startResult.Faults.Select(item => item.Channel).Distinct())}]，" +
                        "已关闭旧恢复授权，不再触发无意义的整批进程重启。",
                        "无人值守恢复");
                    return;
                }
                if (startResult.StartedChannels.Length == 0 &&
                    startResult.CompletedDuringStartChannels.Length > 0)
                {
                    UnattendedRunCheckpointStore.Disarm(
                        "MechanicalTargetCompletedDuringRecoveryLearning");
                    recoveryBatchCommitted = true;
                    ProjectLogHub.Write(
                        ProjectLogLevel.Info,
                        $"同进程恢复的学习/资格阶段已消费全部剩余机械目标圈；" +
                        $"Channels=[{string.Join(",", startResult.CompletedDuringStartChannels)}]，" +
                        "已关闭恢复授权且不会再启动正式圈。",
                        "无人值守恢复");
                    return;
                }
                ConfirmRecoveryBatchStarted(
                    config,
                    startResult.StartedChannels,
                    startResult.TestRunId);
                recoveryBatchCommitted = true;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                UnattendedRunCheckpointStore.CompleteInProcessRecovery(
                    false,
                    "CancelledForSafeExternalHandoff");
                ProjectLogHub.Write(
                    ProjectLogLevel.Warning,
                    "同进程恢复 worker 已响应取消，等待外部接管安全清场。",
                    "无人值守恢复");
                return;
            }
            catch (Exception ex)
            {
                if (!EpbManager.ShouldRestartAfterRecoveryStartupFailure(
                        recoveryBatchCommitted))
                {
                    // 新 Run 已经通过完整通道启动验证并原子提交。提交后的检查点附加
                    // 状态或观察日志失败不得把健康的新执行再次 StopAll/重启。
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "同进程无人值守恢复已提交新批次，但提交后观察性处理失败；" +
                        "保留新批次继续运行。",
                        "无人值守恢复",
                        ex);
                    return;
                }
                UnattendedRunCheckpointStore.CompleteInProcessRecovery(false, ex.Message);
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    $"同进程无人值守恢复失败，升级到进程自重启：{ex}",
                    "无人值守恢复");
                await RestartAsync(reason, correlationId, fault?.RunId.ToString("N"))
                    .ConfigureAwait(false);
                return;
            }
            finally
            {
                ProjectLogHub.Flush(true);
            }
        }

        private static int ReadRecoveryDurationSetting(
            string key,
            int fallback,
            int min,
            int max)
        {
            try
            {
                return int.TryParse(
                           System.Configuration.ConfigurationManager.AppSettings[key],
                           NumberStyles.Integer,
                           CultureInfo.InvariantCulture,
                           out var value) && value >= min && value <= max
                    ? value
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static async Task RestartAsync(
            string reason,
            string correlationId,
            string expectedRunId = null)
        {
            if (Interlocked.CompareExchange(ref _restartStarted, 1, 0) != 0) return;
            if (WatchdogRuntime.IsAttached)
            {
                try
                {
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        $"进程内恢复已到达外部接管边界，交由独立 Watchdog 整批恢复。" +
                        $"CorrelationId={correlationId};Reason={reason}",
                        "独立看门狗");
                    WatchdogRuntime.RequestExternalRecovery(
                        $"{reason};CorrelationId={correlationId};ExpectedRunId={expectedRunId}");
                    return;
                }
                finally
                {
                    Interlocked.Exchange(ref _restartStarted, 0);
                }
            }
            var handoffCommitted = false;
            var checkpointAtStart = UnattendedRunCheckpointStore.Load();
            var guardedRunId = string.IsNullOrWhiteSpace(expectedRunId)
                ? checkpointAtStart?.RunId
                : expectedRunId;
            var sequenceCancellation = new CancellationTokenSource();
            CancellationTokenSource staleCancellation;
            lock (Sync)
            {
                staleCancellation = _restartSequenceCancellation;
                _restartSequenceCancellation = sequenceCancellation;
            }
            if (staleCancellation != null && !ReferenceEquals(staleCancellation, sequenceCancellation))
            {
                try { staleCancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
            var handoffReady = false;

            try
            {
                while (!sequenceCancellation.IsCancellationRequested)
                {
                    RecoveryStartupIntent intent = null;
                    var retryable = false;
                    var retryReason = "RestartAttemptDidNotHandoff";
                    if (!UnattendedRunCheckpointStore.TryRegisterRestart(
                            correlationId,
                            guardedRunId,
                            out intent,
                            out var registrationError))
                    {
                        if (string.Equals(registrationError, "RunIdMismatch", StringComparison.Ordinal))
                        {
                            ProjectLogHub.Write(
                                ProjectLogLevel.Warning,
                                $"忽略迟到旧运行的进程自重启请求。ExpectedRunId={guardedRunId}; " +
                                $"CorrelationId={correlationId}; Reason={reason}",
                                "无人值守恢复");
                            return;
                        }
                        ProjectLogHub.Write(
                            ProjectLogLevel.Error,
                            $"系统故障保持安全停机，不再自重启：{registrationError}; Reason={reason}",
                            "无人值守恢复");
                        LogProcessRestartMetric(
                            "Rejected",
                            correlationId,
                            string.IsNullOrWhiteSpace(registrationError)
                                ? "RegistrationRejected"
                                : registrationError);
                        await SafeStopOnlyAsync(reason, correlationId).ConfigureAwait(false);
                        return;
                    }

                    retryable = true;
                    LogProcessRestartMetric("Registered", correlationId, "PendingHandoff");
                    try
                    {
                        if (!EpbManager.ShouldRepeatProcessRestartSafetyTeardown(handoffReady))
                        {
                            if (sequenceCancellation.IsCancellationRequested ||
                                !UnattendedRunCheckpointStore.IsPendingRestartAuthorized(
                                    intent.Nonce,
                                    guardedRunId))
                            {
                                retryable = false;
                                retryReason = "RestartAuthorizationRevokedAtHandoffRetry";
                            }
                            else
                            {
                                StartRecoveryProcess(intent);
                                handoffCommitted = true;
                                CompleteCommittedProcessHandoff(correlationId, ref handoffCommitted);
                                return;
                            }
                        }
                        else
                        {
                            var manager = _manager;
                            if (manager == null)
                            {
                                retryReason = "ManagerUnavailable";
                            }
                            else
                            {
                                StopSafetyResult safety = null;
                                try
                                {
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
                                }
                                catch (Exception stopError)
                                {
                                    retryReason = "SafetyStopFailed: " + stopError.Message;
                                    ProjectLogHub.Write(
                                        ProjectLogLevel.Error,
                                        stopError.ToString(),
                                        "无人值守恢复");
                                }

                                // Do not quiesce/dispose acquisition or persistence before validating
                                // StopAll. A failed attempt keeps the process safely stopped and retries
                                // the complete invariant with a fresh nonce and the same RunId.
                                if (safety == null || !safety.FullyConfirmed)
                                {
                                    if (string.Equals(
                                            retryReason,
                                            "RestartAttemptDidNotHandoff",
                                            StringComparison.Ordinal))
                                        retryReason = "SafetyStopUnconfirmed";
                                    ProjectLogHub.Write(
                                        ProjectLogLevel.Error,
                                        "自重启本次未交接：电机DO、程控电源、安全压力或数据耐久边界未全部确认；" +
                                        "保持安全停机并按有界预算主动重试。",
                                        "无人值守恢复");
                                    ProjectLogHub.Flush(true);
                                }
                                else if (sequenceCancellation.IsCancellationRequested ||
                                         !UnattendedRunCheckpointStore.IsPendingRestartAuthorized(
                                             intent.Nonce,
                                             guardedRunId))
                                {
                                    retryable = false;
                                    retryReason = "RestartAuthorizationRevokedBeforeQuiesce";
                                }
                                else
                                {
                                    Func<Task> quiesceAndFlush;
                                    lock (Sync) quiesceAndFlush = _quiesceAndFlush;
                                    if (quiesceAndFlush != null)
                                        await quiesceAndFlush().ConfigureAwait(false);
                                    if (!await manager.ShutdownPersistenceAsync(10000).ConfigureAwait(false))
                                        throw new IOException("DAQ持久化队列未能在10秒内安全关闭。");

                                    if (sequenceCancellation.IsCancellationRequested ||
                                        !UnattendedRunCheckpointStore.IsPendingRestartAuthorized(
                                            intent.Nonce,
                                            guardedRunId))
                                    {
                                        retryable = false;
                                        retryReason = "RestartAuthorizationRevokedBeforeHandoff";
                                    }
                                    else
                                    {
                                        manager.ReleaseHardwareForRestart();
                                        handoffReady = true;
                                        ProjectLogHub.Flush(true);

                                        // 最后一次令牌复核必须紧贴进程创建，防止5s/15s旧重试在
                                        // 人工撤权或新Run已经Arm之后复活旧检查点。
                                        if (sequenceCancellation.IsCancellationRequested ||
                                            !UnattendedRunCheckpointStore.IsPendingRestartAuthorized(
                                                intent.Nonce,
                                                guardedRunId))
                                        {
                                            retryable = false;
                                            retryReason = "RestartAuthorizationRevokedAtHandoff";
                                        }
                                        else
                                        {
                                            StartRecoveryProcess(intent);
                                            handoffCommitted = true;
                                            CompleteCommittedProcessHandoff(correlationId, ref handoffCommitted);
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        retryReason = "RestartFailed: " + ex.Message;
                        ProjectLogHub.Write(ProjectLogLevel.Error, ex.ToString(), "无人值守恢复");
                        ProjectLogHub.Flush(true);
                    }

                    // 能运行到这里就证明本次注册没有成功交接到子进程；无论原因是
                    // StopAll 未确认、授权被撤销还是 Process.Start 异常，都输出唯一
                    // 的结构化失败终态，然后才释放 nonce 进入下一次预算。
                    LogProcessRestartMetric("AttemptFailed", correlationId, retryReason);
                    var releasedForRetry = false;
                    var attemptsInWindow = 0;
                    var releaseError = string.Empty;
                    try
                    {
                        releasedForRetry = UnattendedRunCheckpointStore.ReleasePendingRestartForRetry(
                            intent.Nonce,
                            guardedRunId,
                            retryReason,
                            out attemptsInWindow,
                            out releaseError);
                    }
                    catch (Exception releaseException)
                    {
                        ProjectLogHub.Write(
                            ProjectLogLevel.Error,
                            "释放进程自重启单飞检查点失败。",
                            "无人值守恢复",
                            releaseException);
                    }

                    if (!retryable || !releasedForRetry)
                    {
                        LogProcessRestartMetric(
                            "TerminalSafeStop",
                            correlationId,
                            string.IsNullOrWhiteSpace(releaseError)
                                ? retryReason
                                : releaseError);
                        ProjectLogHub.Write(
                            ProjectLogLevel.Error,
                            $"进程自重启序列终止并保持安全停机。" +
                            $"Retryable={retryable}; Release={releasedForRetry}; " +
                            $"Gate={releaseError}; RunId={guardedRunId}",
                            "无人值守恢复");
                        if (!string.Equals(releaseError, "RunIdMismatch", StringComparison.Ordinal))
                            await SafeStopOnlyAsync(reason, correlationId).ConfigureAwait(false);
                        return;
                    }

                    var retryDelayMs = EpbManager.SelectUnattendedProcessRestartRetryDelayMs(
                        attemptsInWindow);
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        $"进程自重启第{attemptsInWindow}次交接失败，" +
                        $"将在{retryDelayMs}ms后使用同一RunId和新nonce主动重试；" +
                        $"Reason={retryReason}; RunId={guardedRunId}",
                        "无人值守恢复");
                    ProjectLogHub.Flush(true);
                    try
                    {
                        await Task.Delay(retryDelayMs, sequenceCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        ProjectLogHub.Write(
                            ProjectLogLevel.Info,
                            $"进程自重启退避已因人工撤权或运行代次变化取消。RunId={guardedRunId}",
                            "无人值守恢复");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "进程自重启协调器异常，保持安全停机。",
                    "无人值守恢复",
                    ex);
                ProjectLogHub.Flush(true);
                await SafeStopOnlyAsync(reason, correlationId).ConfigureAwait(false);
            }
            finally
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_restartSequenceCancellation, sequenceCancellation))
                        _restartSequenceCancellation = null;
                }
                sequenceCancellation.Dispose();
                if (!handoffCommitted)
                    Interlocked.Exchange(ref _restartStarted, 0);
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

        private static void CompleteCommittedProcessHandoff(
            string correlationId,
            ref bool handoffCommitted)
        {
            // 子进程已经接管跨进程撤权门，此刻交接成为不可逆提交。结构化日志是观察性
            // 证据，不能再通过同步 Flush 把成功的进程交接拖回重试路径。若 Exit 本身被
            // 环境拒绝，则先置位撤权门，让等待中的子进程安全退出，再回到父进程安全停机。
            try
            {
                LogProcessRestartMetric("ChildCreated", correlationId, "HandoffLaunched");
            }
            catch { }
            try
            {
                Environment.Exit(86);
            }
            catch
            {
                CancelRestartRetrySequence();
                handoffCommitted = false;
                throw;
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
            var revocation = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                RecoveryProcessBootstrap.GetRevocationEventName(intent.Nonce));
            using (var attached = new EventWaitHandle(
                       false,
                       EventResetMode.ManualReset,
                       RecoveryProcessBootstrap.GetAttachedEventName(intent.Nonce)))
            {
                EventWaitHandle previous;
                lock (Sync)
                {
                    previous = _activeHandoffRevocation;
                    _activeHandoffRevocation = revocation;
                }
                try { previous?.Dispose(); } catch { }

                Process child = null;
                var attachedToRevocationGate = false;
                try
                {
                    child = Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = arguments,
                        WorkingDirectory = Environment.CurrentDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = false
                    });
                    if (child == null)
                        throw new InvalidOperationException("恢复子进程创建未返回进程句柄。");
                    if (!attached.WaitOne(5000))
                        throw new TimeoutException("恢复子进程未在5秒内接管跨进程撤权门。");
                    if (revocation.WaitOne(0))
                        throw new OperationCanceledException("恢复子进程交接期间运行授权已撤销。");
                    attachedToRevocationGate = true;
                }
                finally
                {
                    if (!attachedToRevocationGate)
                    {
                        lock (Sync)
                        {
                            if (ReferenceEquals(_activeHandoffRevocation, revocation))
                                _activeHandoffRevocation = null;
                        }
                        try { revocation.Dispose(); } catch { }
                        try
                        {
                            if (child != null && !child.HasExited) child.Kill();
                        }
                        catch { }
                    }
                    try { child?.Dispose(); } catch { }
                }
            }
            // 成功时父进程必须继续持有 revocation，直到 Environment.Exit。人工停止即使
            // 恰好发生在 Process.Start 与父进程退出之间，也会同步置位；子进程已持有
            // 同一个内核事件，并会在等待父进程退出后、读取检查点和初始化硬件前拒绝续测。
        }
    }

    internal static class RecoveryProcessBootstrap
    {
        internal const string MutexName = @"Local\MTTFTest_V2_10_2_4_SingleInstance";

        internal static string GetRevocationEventName(string nonce)
            => @"Local\MTTFTest_RecoveryRevoked_" + NormalizeNonce(nonce);

        internal static string GetAttachedEventName(string nonce)
            => @"Local\MTTFTest_RecoveryAttached_" + NormalizeNonce(nonce);

        internal static RecoveryHandoffAttachment AttachHandoff(RecoveryStartupIntent intent)
        {
            if (intent == null) return null;
            var nonce = NormalizeNonce(intent.Nonce);
            if (nonce.Length == 0) return null;
            EventWaitHandle revocation = null;
            EventWaitHandle attached = null;
            try
            {
                revocation = EventWaitHandle.OpenExisting(GetRevocationEventName(nonce));
                attached = EventWaitHandle.OpenExisting(GetAttachedEventName(nonce));
                attached.Set();
                return new RecoveryHandoffAttachment(revocation);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                revocation?.Dispose();
                return null;
            }
            finally
            {
                attached?.Dispose();
            }
        }

        private static string NormalizeNonce(string nonce)
        {
            var text = (nonce ?? string.Empty).Trim().Replace("-", string.Empty);
            return Guid.TryParseExact(text, "N", out var parsed)
                ? parsed.ToString("N")
                : string.Empty;
        }

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

    internal sealed class RecoveryHandoffAttachment : IDisposable
    {
        private EventWaitHandle _revocation;

        internal RecoveryHandoffAttachment(EventWaitHandle revocation)
        {
            _revocation = revocation ?? throw new ArgumentNullException(nameof(revocation));
        }

        internal bool IsRevoked
        {
            get
            {
                try { return _revocation == null || _revocation.WaitOne(0); }
                catch (ObjectDisposedException) { return true; }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _revocation, null)?.Dispose();
        }
    }
}
