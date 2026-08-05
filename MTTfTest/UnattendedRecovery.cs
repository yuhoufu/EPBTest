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
        public string StoreDir { get; set; }
        public string TestName { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public Dictionary<string, int> RemainingFormalCycles { get; set; } =
            new Dictionary<string, int>();
        public int LearnCycles { get; set; }
        public string ConfigurationSha256 { get; set; }
        public string ExecutableSha256 { get; set; }
        public string BuildVersion { get; set; }
        public string ActiveFaultCorrelationId { get; set; }
        public string RecoveryNonce { get; set; }
        public string LastReason { get; set; }
        public string UpdatedUtc { get; set; }
        public List<string> RestartHistoryUtc { get; set; } = new List<string>();
    }

    internal static class UnattendedRunCheckpointStore
    {
        private static readonly object Sync = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static readonly string CheckpointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MTTFTest",
            "unattended-run-checkpoint.json");

        internal static void Arm(GlobalConfig config, IEnumerable<int> channels)
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
                checkpoint.Armed = true;
                checkpoint.RestartPending = false;
                checkpoint.StoreDir = config.Test.StoreDir ?? string.Empty;
                checkpoint.TestName = config.Test.TestName ?? string.Empty;
                checkpoint.SelectedChannels = selected;
                checkpoint.LearnCycles = Math.Max(5, config.Test.LearnCycles);
                checkpoint.ConfigurationSha256 = ComputeConfigurationHash(config);
                checkpoint.ExecutableSha256 = ComputeFileHash(GetExecutablePath());
                checkpoint.BuildVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
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
                checkpoint.RecoveryNonce = string.Empty;
                checkpoint.LastReason = string.IsNullOrWhiteSpace(reason) ? "AuthorizationRevoked" : reason;
                checkpoint.UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                SaveUnsafe(checkpoint);
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
                DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal,
                out utc);
        }
    }

    internal static class UnattendedRecoveryCoordinator
    {
        private static readonly object Sync = new object();
        private static EpbManager _manager;
        private static GlobalConfig _config;
        private static Func<Task> _quiesceAndFlush;
        private static int _restartStarted;

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

        internal static void Arm(GlobalConfig config, IEnumerable<int> channels)
        {
            UnattendedRunCheckpointStore.Arm(config, channels);
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
            UnattendedRunCheckpointStore.Disarm(
                $"{context?.Source}: {context?.Reason ?? "Run authorization revoked"}");
        }

        private static void OnSystemFaultRaised(ControlFault fault)
        {
            _ = Task.Run(() => RestartAsync(
                fault?.Reason ?? "SystemFault",
                fault?.CorrelationId.ToString("N") ?? Guid.NewGuid().ToString("N")));
        }

        private static void OnFormalCycleCompleted(int channel, int sessionRunCount)
        {
            UnattendedRunCheckpointStore.RecordFormalCycleCommitted(channel);
        }

        private static async Task RestartAsync(string reason, string correlationId)
        {
            if (Interlocked.CompareExchange(ref _restartStarted, 1, 0) != 0) return;
            try
            {
                if (!UnattendedRunCheckpointStore.TryRegisterRestart(
                        correlationId,
                        out var intent,
                        out var registrationError))
                {
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
                Func<Task> quiesceAndFlush;
                lock (Sync) quiesceAndFlush = _quiesceAndFlush;
                if (quiesceAndFlush != null)
                    await quiesceAndFlush().ConfigureAwait(false);
                if (!await manager.ShutdownPersistenceAsync(10000).ConfigureAwait(false))
                    throw new IOException("DAQ持久化队列未能在10秒内安全关闭。");
                manager.ReleaseHardwareForRestart();
                ProjectLogHub.Flush(true);

                if (safety == null || !safety.CanReleaseAcquisition)
                {
                    UnattendedRunCheckpointStore.CancelPendingRestart("SafetyStopUnconfirmed");
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "自重启已取消：电机DO或程控电源关闭未确认。",
                        "无人值守恢复");
                    ProjectLogHub.Flush(true);
                    return;
                }

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
