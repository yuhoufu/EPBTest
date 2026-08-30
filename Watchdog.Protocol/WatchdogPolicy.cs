using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MTTFTest.Watchdog.Protocol
{
    public static class WatchdogTransportPolicy
    {
        // Both endpoints use the same bounded policy. A stale pipe must be
        // abandoned quickly enough that UI/control operations never inherit
        // an unbounded StreamWriter wait.
        public const int SendGateWaitMs = 100;
        public const int SendWriteTimeoutMs = 500;
        public const int ReconnectMaxAttempts = 8;
        public const int HeartbeatSuspectMs = 3000;
        public const int HeartbeatTimeoutMs = 5000;
        public const int UiProbeTimeoutMs = 500;
        public const int ClientAckRetireJitterMs = 250;
        // Host 在 5 秒边界开始做 500ms UI 探针。Client 必须晚于该判定窗口
        // 才退休连接，否则双方同时拆管道会制造可避免的重连风暴。
        public const int ClientHeartbeatAckRetireMs =
            HeartbeatTimeoutMs + UiProbeTimeoutMs + ClientAckRetireJitterMs;

        // Keep the transport timing contract in one assembly.  These names
        // are deliberately descriptive rather than scattered literals in the
        // client/runtime; changing one stage must not silently change the
        // enclosing session budget.
        public const int PipeConnect5000 = 5000;
        public const int Guard5500 = 5500;
        public const int Handshake5000 = 5000;
        public const int ConnectFailureJoin1000 = 1000;
        public const int ReconnectInitial250 = 250;
        public const int SidecarLaunchAllowance5000 = 5000;
        public const int LaunchClosureJoin1000 = 1000;
        public const int AttachDispatchGuard1000 = 1000;
        public const int ProcessExitJoin1000 = 1000;
        // Runtime callback dispatch is deliberately bounded so a closing
        // session cannot leave the process waiting indefinitely on callbacks.
        public const int RuntimeCallbackDrainMs = 2000;
        public const int RecoveryFailureReceiptDeadlineMs = 90000;
        public const int RecoveryFailureRequestRetryMs = 750;

        // These are the reviewed envelope values used by the attach gate.
        // The first and fifth values are derived directly from the primitive
        // stages/backoff schedule; the remaining envelopes are named values
        // so consumers cannot accidentally substitute a local timeout.
        public const int ConnectAttemptBudgetMs =
            Guard5500 + ConnectFailureJoin1000;
        public const int GuardedConnectBudgetMs = 11100;
        public const int HandshakeBudgetMs = 19000;
        public const int RecoveryConnectBudgetMs = 75600;
        public const int ReconnectBackoffBudgetMs = 23750;
        public const int ReconnectSupervisorBudgetMs = 75750;
        public const int SessionAttachDeadline = 158850;

        // Suffix aliases make the unit explicit for callers that expose
        // policy values in diagnostics/configuration.
        public static int ConnectAttemptBudgetMilliseconds => ConnectAttemptBudgetMs;
        public static int GuardedConnectBudgetMilliseconds => GuardedConnectBudgetMs;
        public static int HandshakeBudgetMilliseconds => HandshakeBudgetMs;
        public static int RecoveryConnectBudgetMilliseconds => RecoveryConnectBudgetMs;
        public static int ReconnectBackoffBudgetMilliseconds => ReconnectBackoffBudgetMs;
        public static int ReconnectSupervisorBudgetMilliseconds => ReconnectSupervisorBudgetMs;
        public static int SessionAttachDeadlineMs => SessionAttachDeadline;

        /// <summary>
        /// One session owns one reconnect worker.  The first retry is short,
        /// then the delay is capped so a dead pipe cannot create a launch storm.
        /// </summary>
        public static int SelectReconnectBackoffMs(int attempt)
        {
            if (attempt < 0) attempt = 0;
            switch (attempt)
            {
                case 0: return 250;
                case 1: return 500;
                case 2: return 1000;
                case 3: return 2000;
                default: return 5000;
            }
        }
    }

    /// <summary>
    /// BatchStartFailed 只有在已有武装检查点且 Run 身份一致时才允许跨进程接管。
    /// 新试验尚未提交正式运行时，启动失败必须停留在安全空闲态，禁止杀进程重启。
    /// </summary>
    public static class BatchStartTakeoverPolicy
    {
        public static bool ShouldTakeover(WatchdogBatchStartFailureContext context)
        {
            if (context == null || !context.CheckpointArmed) return false;
            if (!TryNormalizeRunId(context.CheckpointRunId, out var checkpointRunId)) return false;
            if (!TryNormalizeRunId(context.RunId, out var runId) || runId != checkpointRunId) return false;
            return context.RecoveryProcess || context.FormalRunCommitted;
        }

        public static string DescribeRejection(WatchdogBatchStartFailureContext context)
        {
            if (context == null) return "ContextMissing";
            if (!context.CheckpointArmed) return "CheckpointDisarmed";
            if (!TryNormalizeRunId(context.CheckpointRunId, out var checkpointRunId))
                return "CheckpointRunIdMissing";
            if (!TryNormalizeRunId(context.RunId, out var runId)) return "CurrentRunIdMissing";
            if (runId != checkpointRunId) return "RunIdMismatch";
            if (!context.RecoveryProcess && !context.FormalRunCommitted)
                return "FormalRunNotCommitted";
            return "Allowed";
        }

        private static bool TryNormalizeRunId(string value, out Guid runId)
        {
            if (!Guid.TryParse(value, out runId) || runId == Guid.Empty)
            {
                runId = Guid.Empty;
                return false;
            }
            return true;
        }
    }

    public static class WatchdogLifecyclePolicy
    {
        public static bool IsTerminalMessage(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.RunStopped, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RunCompleted, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ApplicationClosing, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ApplicationExitRequested, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ShutdownExpected, StringComparison.Ordinal);
        }

        public static bool IsRetryableFailure(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.BatchStartFailed, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RecoveryAttemptFailed, StringComparison.Ordinal);
        }
    }

    public static class RecoveryTransitionPolicy
    {
        public const string OperatorStopButtonText = "停止自动恢复并关闭";

        public static string FormatCountdown(int remainingSeconds)
        {
            if (remainingSeconds <= 0) return "正在处理，请稍候…";
            var remaining = TimeSpan.FromSeconds(remainingSeconds);
            var clock = remaining.TotalHours >= 1
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D2}:{1:D2}:{2:D2}",
                    (int)remaining.TotalHours,
                    remaining.Minutes,
                    remaining.Seconds)
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:D2}:{1:D2}",
                    (int)remaining.TotalMinutes,
                    remaining.Seconds);
            return string.Format(
                CultureInfo.InvariantCulture,
                "预计 {0} 后继续（剩余 {1} 秒）",
                clock,
                remainingSeconds);
        }

        public static bool ShouldHide(WatchdogHeartbeat heartbeat)
        {
            if (heartbeat == null || !heartbeat.RunActive) return false;
            return string.Equals(heartbeat.Phase, "Formal", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(heartbeat.Phase, "Learning", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(heartbeat.Phase, "ManualPaused", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MustSuppressAutomaticRestart(
            bool transitionOperatorStopStarted,
            bool sessionRevoked)
        {
            return transitionOperatorStopStarted || sessionRevoked;
        }
    }

    /// <summary>
    /// 人工暂停的 Sidecar 安全策略。暂停等待以控制器发布的动态硬截止为准，
    /// 心跳正常且仍在持续收敛时绝不能使用固定五秒门限误接管。
    /// </summary>
    public static class ManualPauseSafetyPolicy
    {
        public const double RequiredNoProgressSeconds = 5.0;

        public static int SelectHardDeadlineMilliseconds(int expectedCyclePeriodMs)
        {
            var period = Math.Max(1, expectedCyclePeriodMs);
            var calculated = Math.Max(30000L, period * 2L + 30000L);
            return (int)Math.Min(300000L, calculated);
        }

        public static bool ShouldTakeover(
            bool manualPausePending,
            bool manualPauseActive,
            bool controllerSafetyFault,
            int energizedChannelCount,
            long hardDeadlineUtcTicks,
            long nowUtcTicks,
            double noProgressSeconds)
        {
            if (!manualPausePending && !manualPauseActive) return false;
            if (controllerSafetyFault) return true;
            // Paused 是已承诺的安全终态；若仍然带电，则无需再等动态圈周期，
            // 只给遥测/心跳五秒去抖窗口后立即接管。
            if (manualPauseActive)
                return energizedChannelCount > 0 &&
                       noProgressSeconds >= RequiredNoProgressSeconds;
            if (!manualPausePending) return false;
            if (energizedChannelCount <= 0) return false;
            if (hardDeadlineUtcTicks <= 0 || nowUtcTicks < hardDeadlineUtcTicks) return false;
            return noProgressSeconds >= RequiredNoProgressSeconds;
        }
    }

    public sealed class RecoveryFailureDecision
    {
        public string Fingerprint { get; set; }
        public int ConsecutiveCount { get; set; }
        public bool ProcessRelaunchAllowed { get; set; }
        public bool SameFingerprint { get; set; }
        public bool SameProgressToken { get; set; }
        public bool ProgressTokenChanged { get; set; }
        public bool SafeIdleRecoveryBlocked { get; set; }
        // True when this failure is otherwise eligible for a relaunch but a
        // different failure report already owns the single outstanding
        // relaunch permit.  It is not a safety block and must not schedule a
        // second relaunch loop.
        public bool RelaunchPermitAlreadyPending { get; set; }
        /// <summary>
        /// The strict authority was temporarily busy and did not mutate
        /// durable bytes.  The exact request/correlation may be retried; this
        /// is neither approval nor a permanent safe-idle circuit decision.
        /// </summary>
        public bool DurableDecisionRetryPending { get; set; }
        public long RelaunchPermitGeneration { get; set; }
        public bool RelaunchBudgetExhausted { get; set; }
        public RecoveryFailureReport Report { get; set; }
    }

    /// <summary>
    /// Linearizable one-shot permit gate for recovery-process launches.
    ///
    /// The watchdog journal remains the durable source of the generation
    /// number; this gate owns only the in-memory reservation/consumption
    /// transition.  A caller must persist the new generation before starting
    /// a process and must revoke it when persistence fails.  Keeping the
    /// reservation separate from the relaunch loop prevents two concurrent
    /// failure reports from both turning the same failure into Process.Start.
    /// </summary>
    public sealed class RecoveryRelaunchPermitGate
    {
        private readonly object _gate = new object();
        private long _nextGeneration;
        private long _activeGeneration;
        private long _consumedGeneration;

        public long PendingGeneration
        {
            get
            {
                lock (_gate)
                    return _activeGeneration > _consumedGeneration
                        ? _activeGeneration
                        : 0;
            }
        }

        public long ConsumedGeneration
        {
            get { lock (_gate) return _consumedGeneration; }
        }

        public RecoveryRelaunchPermitDecision TryApprove(
            long durableGeneration,
            bool blocked,
            bool manualStopRequested,
            bool sessionRevoked,
            int consecutiveFailures,
            int maximumProcessRelaunches,
            out long generation)
        {
            lock (_gate)
            {
                generation = 0;
                if (blocked || manualStopRequested || sessionRevoked)
                    return RecoveryRelaunchPermitDecision.Denied;
                if (_activeGeneration > _consumedGeneration)
                {
                    generation = _activeGeneration;
                    return RecoveryRelaunchPermitDecision.AlreadyPending;
                }
                if (maximumProcessRelaunches <= 0 ||
                    consecutiveFailures >= maximumProcessRelaunches)
                    return RecoveryRelaunchPermitDecision.Denied;

                _nextGeneration = Math.Max(
                    Math.Max(_nextGeneration, durableGeneration),
                    _consumedGeneration) + 1;
                _activeGeneration = _nextGeneration;
                generation = _activeGeneration;
                return RecoveryRelaunchPermitDecision.Approved;
            }
        }

        public bool TryConsume(long generation)
        {
            lock (_gate)
            {
                if (generation <= 0 ||
                    _activeGeneration != generation ||
                    generation <= _consumedGeneration)
                    return false;
                _consumedGeneration = generation;
                _activeGeneration = 0;
                return true;
            }
        }

        public void Revoke()
        {
            lock (_gate) _activeGeneration = 0;
        }
    }

    public enum RecoveryRelaunchPermitDecision
    {
        Denied = 0,
        AlreadyPending = 1,
        Approved = 2
    }

    /// <summary>
    /// Machine-readable recovery failure evidence.  Only these stable fields
    /// participate in the circuit-breaker fingerprint.  Timestamps, PIDs,
    /// attempt counters and exception prose deliberately stay outside the
    /// identity so a repeated failure cannot evade the durable gate by changing
    /// incidental text.
    /// </summary>
    public sealed class RecoveryFailureReport
    {
        public string RootCode { get; set; }
        public string DeviceOrChannelGroup { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryProgressToken { get; set; }
        public string RecoveryProcessSource { get; set; }

        public RecoveryFailureReport Clone() => new RecoveryFailureReport
        {
            RootCode = RootCode,
            DeviceOrChannelGroup = DeviceOrChannelGroup,
            RunId = RunId,
            RunEpoch = RunEpoch,
            RecoveryStage = RecoveryStage,
            RecoveryProgressToken = RecoveryProgressToken,
            RecoveryProcessSource = RecoveryProcessSource
        };
    }

    public sealed class RecoveryFailureClassification
    {
        public string Code { get; internal set; }
        public bool Permanent { get; internal set; }
        public int MaximumProcessRelaunches { get; internal set; }
    }

    /// <summary>
    /// 对恢复进程失败做同指纹熔断。硬件通信失败应在恢复进程内处理；
    /// 该门禁负责兜住初始化/附着等仍需进程重拉的故障，防止无界 Process.Start。
    /// </summary>
    public sealed class RecoveryFailureCircuitBreaker
    {
        public const int DefaultConsecutiveLimit = 5;
        private readonly int _limit;
        private string _lastFingerprint = string.Empty;
        private int _consecutiveCount;

        public RecoveryFailureCircuitBreaker(int limit = DefaultConsecutiveLimit)
        {
            _limit = Math.Max(1, limit);
        }

        public RecoveryFailureDecision Observe(string reason)
        {
            var fingerprint = RecoveryFailurePolicy.BuildFingerprint(reason);
            if (string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal))
                _consecutiveCount++;
            else
            {
                _lastFingerprint = fingerprint;
                _consecutiveCount = 1;
            }
            return new RecoveryFailureDecision
            {
                Fingerprint = fingerprint,
                ConsecutiveCount = _consecutiveCount,
                ProcessRelaunchAllowed = _consecutiveCount < _limit
            };
        }

        public void Reset()
        {
            _lastFingerprint = string.Empty;
            _consecutiveCount = 0;
        }
    }

    public static class RecoveryFailurePolicy
    {
        public const string InitialProcessSource = "InitialProcess";
        public const string RecoveryProcessSource = "RecoveryProcess";
        public const string SafeIdleRecoveryBlockedState = "SafeIdleRecoveryBlocked";

        public static RecoveryFailureReport NormalizeReport(RecoveryFailureReport report)
        {
            report = report ?? new RecoveryFailureReport();
            return new RecoveryFailureReport
            {
                RootCode = NormalizeStable(report.RootCode, "UnknownFailure"),
                DeviceOrChannelGroup = NormalizeStable(
                    report.DeviceOrChannelGroup,
                    "UnknownDeviceOrChannelGroup"),
                RunId = NormalizeStable(report.RunId, "UnknownRun"),
                RunEpoch = Math.Max(0, report.RunEpoch),
                RecoveryStage = NormalizeStable(report.RecoveryStage, "UnknownStage"),
                RecoveryProgressToken = NormalizeToken(report.RecoveryProgressToken),
                RecoveryProcessSource = NormalizeStable(
                    report.RecoveryProcessSource,
                    InitialProcessSource)
            };
        }

        /// <summary>
        /// Stable failure identity.  The digest covers exactly the four stable
        /// dimensions and never includes token/source/time/PID/attempt/prose.
        /// </summary>
        public static string BuildFingerprint(RecoveryFailureReport report)
        {
            var normalized = NormalizeReport(report);
            var canonical = string.Join("|", new[]
            {
                normalized.RootCode,
                normalized.DeviceOrChannelGroup,
                normalized.RunId,
                normalized.RecoveryStage
            }).ToLowerInvariant();
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                return "RFP3-" + BitConverter.ToString(bytes).Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        /// <summary>
        /// Applies the durable same-fingerprint/same-token gate.  A changed
        /// real progress token permits one new meaningful attempt; the next
        /// failure at that token is terminal.  Permanent and unavailable
        /// infrastructure classes never receive a process relaunch budget.
        /// </summary>
        public static RecoveryFailureDecision Evaluate(
            RecoveryFailureReport report,
            RecoveryFailureClassification classification,
            string previousFingerprint,
            string previousProgressToken,
            string previousProcessSource,
            int previousCount,
            bool alreadyBlocked)
        {
            var normalized = NormalizeReport(report);
            classification = classification ?? Classify(normalized.RootCode, false, string.Empty);
            var fingerprint = BuildFingerprint(normalized);
            var sameFingerprint = !string.IsNullOrWhiteSpace(previousFingerprint) &&
                                  string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal);
            var previousToken = NormalizeToken(previousProgressToken);
            var hasCurrentToken = !string.IsNullOrWhiteSpace(normalized.RecoveryProgressToken);
            var hasPreviousToken = !string.IsNullOrWhiteSpace(previousToken);
            // Missing/invalid versions never count as progress.  Only two
            // real P0-4 stage versions can establish a changed token.
            var sameToken = sameFingerprint &&
                            (!hasCurrentToken || !hasPreviousToken ||
                             string.Equals(
                                 previousToken,
                                 normalized.RecoveryProgressToken,
                                 StringComparison.Ordinal));
            var tokenChanged = sameFingerprint && hasCurrentToken && hasPreviousToken &&
                               !string.Equals(
                                   previousToken,
                                   normalized.RecoveryProgressToken,
                                   StringComparison.Ordinal);
            var count = sameFingerprint ? Math.Max(1, previousCount + 1) : 1;

            // A second failure at the same stable point is not a new attempt,
            // regardless of which process reported it.  This specifically
            // limits the initial process to one recovery child and makes a
            // recovery child failure immediately durable SafeIdle.
            var samePointBlocked = sameFingerprint && sameToken;
            var unavailable = IsHardwareOrDaqUnavailable(classification.Code);
            var budget = classification.Permanent || unavailable
                ? 0
                : Math.Max(0, classification.MaximumProcessRelaunches);
            // ConsecutiveCount is the number of failures for the current
            // stable fingerprint, independent of progress token/source.  A
            // token change is meaningful only while the previous count is
            // still below the configured process-relaunch budget.  Once the
            // old count reaches the budget, do not grant another relaunch by
            // changing only the progress token.
            var budgetExhausted = sameFingerprint &&
                                  budget > 0 &&
                                  previousCount >= budget;
            var blocked = alreadyBlocked ||
                          classification.Permanent ||
                          samePointBlocked ||
                          budgetExhausted;
            return new RecoveryFailureDecision
            {
                Fingerprint = fingerprint,
                ConsecutiveCount = count,
                ProcessRelaunchAllowed = !blocked && budget > 0,
                SameFingerprint = sameFingerprint,
                SameProgressToken = sameToken,
                ProgressTokenChanged = tokenChanged,
                SafeIdleRecoveryBlocked = blocked,
                RelaunchBudgetExhausted = budgetExhausted,
                Report = normalized
            };
        }

        public static bool IsRecoveryProcessSource(string source) =>
            string.Equals(source, RecoveryProcessSource, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "Recovery", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "WatchdogRecovery", StringComparison.OrdinalIgnoreCase);

        public static bool IsHardwareOrDaqUnavailable(string code)
        {
            return Contains(code, "HardwareUnavailable") ||
                   Contains(code, "DaqUnavailable") ||
                   Contains(code, "DAQUnavailable") ||
                   Contains(code, "DaqCallbackStale") ||
                   Contains(code, "DaqSampleStale") ||
                   Contains(code, "DaqRecoveryFailed") ||
                   Contains(code, "DaqStartPreflightFailed") ||
                   Contains(code, "OffCurrentUnverifiableDaqStale");
        }

        public static RecoveryFailureClassification Classify(
            string code,
            bool permanent,
            string reason)
        {
            var text = ((code ?? string.Empty) + " " + (reason ?? string.Empty)).Trim();
            var deterministicConfigurationFailure =
                Contains(text, "ConfigDuplicateEpbId") ||
                Contains(text, "ConfigEpbRecordInvariant") ||
                Contains(text, "已添加了具有相同键的项") ||
                Contains(text, "RecoveryCheckpointRejected") ||
                Contains(text, "CheckpointInvariant") ||
                Contains(text, "PackageManifest") ||
                Contains(text, "PackageVerification") ||
                Contains(text, "AssemblyLoad") ||
                Contains(text, "BadImageFormat");
            if (permanent || deterministicConfigurationFailure)
            {
                return new RecoveryFailureClassification
                {
                    Code = string.IsNullOrWhiteSpace(code)
                        ? InferPermanentCode(text)
                        : code,
                    Permanent = true,
                    MaximumProcessRelaunches = 0
                };
            }

            var transientHardware =
                Contains(text, "HardwareUnavailable") ||
                Contains(text, "DaqUnavailable") ||
                Contains(text, "PowerSupply") ||
                Contains(text, "Timeout") ||
                Contains(text, "PortInUse") ||
                Contains(text, "RecoveryAttachFailed");
            var unavailable = Contains(text, "HardwareUnavailable") ||
                              Contains(text, "DaqUnavailable") ||
                              Contains(text, "DaqCallbackStale") ||
                              Contains(text, "DaqSampleStale") ||
                              Contains(text, "DaqRecoveryFailed") ||
                              Contains(text, "DaqStartPreflightFailed") ||
                              Contains(text, "OffCurrentUnverifiableDaqStale");
            return new RecoveryFailureClassification
            {
                Code = string.IsNullOrWhiteSpace(code)
                    ? (transientHardware ? "TransientInfrastructure" : "UnhandledSoftwareStartup")
                    : code,
                Permanent = false,
                MaximumProcessRelaunches = unavailable
                    ? 0
                    : transientHardware
                        ? RecoveryFailureCircuitBreaker.DefaultConsecutiveLimit
                    : 2
            };
        }

        public static bool CanLaunchMainProcess(bool recoveryBlocked) => !recoveryBlocked;

        /// <summary>
        /// Watchdog 已发出 RequestStopAll 后，恢复进程中的等待任务会按设计收到取消。
        /// 该取消是接管流程的结果，不是新的启动失败，不能消耗进程重启预算。
        /// </summary>
        public static bool IsSupersededByWatchdogTakeover(
            bool takeoverInProgress,
            string activeTakeoverCorrelationId,
            string failureOwner,
            string failureCorrelationId,
            string failureCode,
            string reason,
            string detail)
        {
            if (!takeoverInProgress ||
                !string.Equals(
                    failureOwner,
                    "WatchdogTakeover",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(activeTakeoverCorrelationId) ||
                !string.Equals(
                    activeTakeoverCorrelationId,
                    failureCorrelationId,
                    StringComparison.Ordinal))
                return false;

            var text = string.Join(
                " ",
                new[] { failureCode, reason, detail }.Where(value =>
                    !string.IsNullOrWhiteSpace(value)));
            return Contains(text, "OperationCanceledException") ||
                   Contains(text, "TaskCanceledException") ||
                   Contains(text, "已取消该操作") ||
                   Contains(text, "operation was canceled") ||
                   Contains(text, "operation was cancelled") ||
                   Contains(text, "canceled") ||
                   Contains(text, "cancelled");
        }

        /// <summary>
        /// CircuitProbe 只能探测设备存在性；sidecar 永远不得用它启动完整主程序。
        /// </summary>
        public static bool AllowsMainProcessCircuitProbe => false;

        public static string BuildFingerprint(string reason)
        {
            // Legacy callers only have prose.  Keep compatibility while
            // preventing the prose itself from becoming an unbounded identity.
            var text = (reason ?? string.Empty).Trim();
            var root = text;
            var separator = text.IndexOfAny(new[] { ':', ';', '|', '\r', '\n', ' ' });
            if (separator > 0) root = text.Substring(0, separator);
            var classification = Classify(root, false, text);
            return BuildFingerprint(new RecoveryFailureReport
            {
                RootCode = classification.Code,
                DeviceOrChannelGroup = "UnknownDeviceOrChannelGroup",
                RunId = "UnknownRun",
                RecoveryStage = "UnknownStage"
            });
        }

        public static int SelectInProcessProbeDelaySeconds(int consecutiveAttempt)
        {
            if (consecutiveAttempt <= 1) return 5;
            if (consecutiveAttempt == 2) return 15;
            if (consecutiveAttempt == 3) return 30;
            return 60;
        }

        public static int SelectProcessRelaunchDelaySeconds(int consecutiveAttempt)
        {
            if (consecutiveAttempt <= 1) return 5;
            if (consecutiveAttempt == 2) return 15;
            if (consecutiveAttempt == 3) return 30;
            return 60;
        }

        public static int SelectCircuitProbeDelayMinutes(int probeAttempt)
        {
            if (probeAttempt <= 1) return 2;
            if (probeAttempt == 2) return 5;
            return 15;
        }

        private static bool Contains(string value, string token) =>
            (value ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string InferPermanentCode(string text)
        {
            if (Contains(text, "Duplicate") || Contains(text, "相同键"))
                return "ConfigDuplicateEpbId";
            if (Contains(text, "Checkpoint")) return "CheckpointInvariant";
            if (Contains(text, "Package") || Contains(text, "Assembly") ||
                Contains(text, "BadImageFormat"))
                return "PackageVerificationFailed";
            return "PermanentRecoveryFailure";
        }

        private static string NormalizeStable(string value, string fallback)
        {
            var normalized = string.Join(" ", (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(normalized)) normalized = fallback;
            if (normalized.Length > 256) normalized = normalized.Substring(0, 256);
            return normalized;
        }

        private static string NormalizeToken(string value)
        {
            // RecoveryProgressToken is the serialized P0-4 monotonic stage
            // version.  Do not let detail/prose (for example "Preflight" or
            // an exception message) masquerade as progress and reopen the
            // relaunch budget.  Empty/invalid tokens mean that no real stage
            // progress was observed.
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            if (!long.TryParse(
                    value.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var version) || version < 0)
                return string.Empty;
            return version.ToString(CultureInfo.InvariantCulture);
        }
    }

    public sealed class WatchdogRuntimeContract
    {
        public string LifecyclePhase { get; internal set; }
        public int Revision { get; internal set; }
        public bool MechanicalProgressExpected { get; internal set; }
        public bool TimerRequired { get; internal set; }
        public bool RunnerRequired { get; internal set; }
        public bool RequireTimerRunnerParity { get; internal set; }
        public bool ManualPauseOwnerRequired { get; internal set; }
        public bool RecoveryOwnerRequired { get; internal set; }
        public bool ResourcesMustBeInactive { get; internal set; }
    }

    /// <summary>
    /// 通道阶段与执行资源之间的唯一权威契约。Timer 只代表正式计数调度，
    /// Learning/Qualification 合法地只拥有 Runner；机械进展监督与 Timer 要求不可混为一谈。
    /// </summary>
    public static class WatchdogRuntimeContractPolicy
    {
        public const int CurrentRevision = 3;

        public static WatchdogRuntimeContract Resolve(string state)
        {
            var phase = (state ?? string.Empty).Trim();
            var contract = new WatchdogRuntimeContract
            {
                LifecyclePhase = phase,
                Revision = CurrentRevision
            };

            if (EqualsPhase(phase, "Learning") || EqualsPhase(phase, "Qualification"))
            {
                contract.MechanicalProgressExpected = true;
                contract.RunnerRequired = true;
            }
            else if (EqualsPhase(phase, "Running") || EqualsPhase(phase, "WarningRunning"))
            {
                contract.MechanicalProgressExpected = true;
                contract.TimerRequired = true;
                contract.RunnerRequired = true;
            }
            else if (EqualsPhase(phase, "WaitingForSlotBarrier"))
            {
                // 屏障等待期间当前通道已经完成自身动作，不应继续收取机械进展期限；
                // 但正式 Timer/Runner 仍必须存在，否则会形成永远无法退出的假等待。
                contract.TimerRequired = true;
                contract.RunnerRequired = true;
            }
            else if (EqualsPhase(phase, "Recovering") || EqualsPhase(phase, "ResumeChecking"))
            {
                contract.RequireTimerRunnerParity = true;
                contract.RecoveryOwnerRequired = true;
            }
            else if (EqualsPhase(phase, "Paused") || EqualsPhase(phase, "PausePending"))
            {
                contract.ManualPauseOwnerRequired = true;
            }
            else if (EqualsPhase(phase, "NotEnabled") ||
                     EqualsPhase(phase, "AlarmStopped") ||
                     EqualsPhase(phase, "InterlockStopped") ||
                     EqualsPhase(phase, "ManualStopped") ||
                     EqualsPhase(phase, "Completed") ||
                     EqualsPhase(phase, "StartBlocked") ||
                     EqualsPhase(phase, "SystemFault"))
            {
                contract.ResourcesMustBeInactive = true;
            }

            // Starting/未知阶段不臆造 Timer 或 Runner 要求；上层启动与恢复硬截止继续兜底。
            return contract;
        }

        public static bool PublishedContractMatches(
            WatchdogChannelProgress progress,
            WatchdogRuntimeContract expected)
        {
            if (progress == null || expected == null ||
                progress.RuntimeContractRevision != CurrentRevision)
                return true;
            return string.Equals(
                       progress.LifecyclePhase,
                       expected.LifecyclePhase,
                       StringComparison.OrdinalIgnoreCase) &&
                   progress.MechanicalProgressExpected == expected.MechanicalProgressExpected &&
                   progress.TimerRequired == expected.TimerRequired &&
                   progress.RunnerRequired == expected.RunnerRequired &&
                   progress.ResourcesMustBeInactive == expected.ResourcesMustBeInactive;
        }

        public static bool HasExplicitRecoveryOwner(
            WatchdogChannelProgress progress,
            long runEpoch)
        {
            if (progress == null ||
                !string.Equals(progress.State, "Recovering", StringComparison.OrdinalIgnoreCase))
                return false;
            return !string.IsNullOrWhiteSpace(progress.RecoveryOwnerKind) &&
                   !string.Equals(progress.RecoveryOwnerKind, "None", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(progress.RecoveryOwnerKind, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(progress.RecoveryOwnerId) &&
                   progress.RecoveryOwnerGeneration > 0 &&
                   progress.RecoveryOwnerGeneration == runEpoch &&
                   !string.IsNullOrWhiteSpace(progress.RecoveryTargetPhase) &&
                   !string.Equals(progress.RecoveryTargetPhase, "None", StringComparison.OrdinalIgnoreCase);
        }

        private static bool EqualsPhase(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// RecoveryActive 必须同时具备可核对的 owner/事故/阶段或通道恢复态。
    /// 单独一个 SoftwareRecoveryCount（现场曾由活动圈数量误填）不是恢复证据。
    /// </summary>
    public static class WatchdogRecoveryTelemetryPolicy
    {
        public static bool ShouldPublishRecoveryActive(
            bool manualPauseCommanded,
            bool controllerRecoveryActive,
            int daqRecoveryCount,
            int softwareRecoveryCount,
            int recoveryOwnerCount,
            bool hasRecoveryLifecycle,
            bool hasRecoveryIdentity)
        {
            if (manualPauseCommanded) return false;
            var owned = daqRecoveryCount > 0 || recoveryOwnerCount > 0;
            var structured = hasRecoveryLifecycle || hasRecoveryIdentity;
            return owned || (structured &&
                             (controllerRecoveryActive || softwareRecoveryCount > 0));
        }

        public static bool IsUnstructuredRecoveryClaim(WatchdogHeartbeat heartbeat)
        {
            if (heartbeat?.RecoveryActive != true) return false;
            var hasOwnedRecovery = heartbeat.DaqRecoveryCount > 0 ||
                                   heartbeat.RecoveryOwnerCount > 0;
            var hasIdentity = !string.IsNullOrWhiteSpace(heartbeat.RecoveryCode) ||
                              !string.IsNullOrWhiteSpace(heartbeat.RecoveryStage) ||
                              !string.IsNullOrWhiteSpace(heartbeat.RecoveryIncident) ||
                              !string.IsNullOrWhiteSpace(heartbeat.RecoveryContext);
            var hasRecoveryLifecycle = (heartbeat.ChannelProgress ??
                                        Array.Empty<WatchdogChannelProgress>())
                .Any(item => item != null &&
                    (string.Equals(item.State, "Recovering", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.State, "ResumeChecking", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.State, "SystemFault", StringComparison.OrdinalIgnoreCase)));
            return !hasOwnedRecovery && !hasIdentity && !hasRecoveryLifecycle;
        }

        public static bool ShouldTreatAsRecoveryActive(WatchdogHeartbeat heartbeat) =>
            heartbeat?.RecoveryActive == true && !IsUnstructuredRecoveryClaim(heartbeat);
    }

    /// <summary>
    /// Pure takeover decision helpers.  The process host supplies evidence and
    /// performs no policy inference from UI text or persistence counters.
    /// </summary>
    public static class WatchdogTakeoverPolicy
    {
        public const int DefaultStopStageNoProgressGraceMs = 5000;

        public static bool IsManualPauseCommanded(bool manualPauseActive, bool manualPausePending)
        {
            return manualPauseActive || manualPausePending;
        }

        public static double SelectFormalProgressTimeoutSeconds(int expectedCyclePeriodMs)
        {
            var periodSeconds = Math.Max(1, expectedCyclePeriodMs) / 1000.0;
            return Math.Min(3600, Math.Max(60, periodSeconds * 3));
        }

        public static bool IsChannelProgressStalled(
            bool manualPauseActive,
            bool active,
            double progressAgeSeconds,
            int expectedCyclePeriodMs)
        {
            return !manualPauseActive && active &&
                   progressAgeSeconds >= SelectFormalProgressTimeoutSeconds(expectedCyclePeriodMs);
        }

        public static bool ShouldTakeover(
            bool sessionRevoked,
            bool manualStopRequested,
            bool alreadyTakingOver,
            bool processAlive,
            double heartbeatAgeSeconds,
            bool recoveryActive,
            bool orphanPaused,
            bool powerDisablePending,
            double stageAgeSeconds,
            bool hasRecoveryEligibleChannels,
            bool stopAllActive = false,
            bool logicalResidue = false,
            bool inconsistentRecoveryEvidence = false,
            bool formalProgressStalled = false,
            bool manualPauseActive = false,
            bool manualPauseUnsafe = false,
            long recoveryHardDeadlineUtcTicks = 0,
            long nowUtcTicks = 0,
            double recoveryNoProgressSeconds = 0,
            long stopStageHardDeadlineUtcTicks = 0,
            double stopNoProgressSeconds = -1,
            long stopStageNoProgressGraceMs = 0,
            long stopHardDeadlineUtcTicks = 0)
        {
            if (sessionRevoked || alreadyTakingOver)
                return false;
            if (!processAlive || heartbeatAgeSeconds >= 5)
                return true;
            if (stopAllActive)
            {
                // Immediate OFF and hydraulic release have different bounded
                // deadlines.  The controller publishes the active stage deadline
                // and the sidecar independently tracks material progress.  A
                // missing deadline is not evidence for a guessed global timeout.
                var stopDeadlineExceeded = stopStageHardDeadlineUtcTicks > 0
                    && nowUtcTicks > 0
                    && nowUtcTicks >= stopStageHardDeadlineUtcTicks;
                var noProgress = stopNoProgressSeconds >= 0
                    ? stopNoProgressSeconds
                    : 0;
                var graceMs = stopStageNoProgressGraceMs > 0
                    ? stopStageNoProgressGraceMs
                    : DefaultStopStageNoProgressGraceMs;
                var totalDeadlineExceeded = stopHardDeadlineUtcTicks > 0 &&
                                            nowUtcTicks > 0 &&
                                            nowUtcTicks >= stopHardDeadlineUtcTicks;
                // The 45s process-wide escape deadline is an independent hard
                // safety boundary.  Once it expires, a changing Detail or
                // stale callback cannot defer takeover; before it expires only
                // the current stage deadline plus real material progress grace
                // can authorize takeover.
                if (totalDeadlineExceeded) return true;
                return stopDeadlineExceeded &&
                       noProgress >= graceMs / 1000.0;
            }
            // A healthy manual pause is a commanded safe state.  Recovery
            // counters and old recovery timestamps are irrelevant here.
            if (manualPauseActive)
                return manualPauseUnsafe;
            if (logicalResidue && stageAgeSeconds >= 5)
                return true;
            if (inconsistentRecoveryEvidence)
                return true;
            if (formalProgressStalled && hasRecoveryEligibleChannels)
                return true;
            if (!hasRecoveryEligibleChannels && !manualStopRequested)
                return false;
            if (orphanPaused && stageAgeSeconds >= 5)
                return true;
            if (powerDisablePending && stageAgeSeconds >= 5)
                return true;
            if (!recoveryActive) return false;
            var hardDeadlineExceeded = recoveryHardDeadlineUtcTicks > 0
                ? nowUtcTicks > 0 && nowUtcTicks >= recoveryHardDeadlineUtcTicks
                : stageAgeSeconds >= 60;
            return hardDeadlineExceeded && recoveryNoProgressSeconds >= 5;
        }
    }

    /// <summary>
    /// Per-channel mechanical progress supervision owned by the sidecar.
    /// DO commands, peak-cutoff watermarks and runtime-state revisions are
    /// diagnostics only: they must never reset the mechanical-completion
    /// deadline, otherwise an actively drawing but non-counting channel can
    /// remain in a fake-running state forever.
    /// </summary>
    public sealed class WatchdogChannelSupervisionEvaluation
    {
        public string TakeoverReason { get; internal set; }
        public bool RefreshRequested { get; internal set; }
        public string RefreshReason { get; internal set; }
    }

    public sealed class WatchdogChannelProgressTracker
    {
        private readonly object _gate = new object();
        private readonly Dictionary<int, string> _mechanicalSignatures =
            new Dictionary<int, string>();
        private readonly Dictionary<int, long> _mechanicalProgressTimestamps =
            new Dictionary<int, long>();
        private readonly Dictionary<int, long> _invariantTimestamps =
            new Dictionary<int, long>();
        private readonly Dictionary<int, string> _invariantSignatures =
            new Dictionary<int, string>();
        private readonly Dictionary<int, string> _refreshRequestedSignatures =
            new Dictionary<int, string>();
        private string _runIdentity = string.Empty;
        private string _logicalSourceIdentity = string.Empty;
        private long _logicalSourceObservedTimestamp;
        private string _staleLogicalSourceIdentity = string.Empty;
        private long _staleLogicalSourceObservedTimestamp;

        public const double LogicalSourceFreshnessSeconds = 3d;
        public const double LogicalSourceRefreshGraceSeconds = 5d;

        public void Reset()
        {
            lock (_gate)
            {
                _mechanicalSignatures.Clear();
                _mechanicalProgressTimestamps.Clear();
                _invariantTimestamps.Clear();
                _invariantSignatures.Clear();
                _refreshRequestedSignatures.Clear();
                _runIdentity = string.Empty;
                _logicalSourceIdentity = string.Empty;
                _logicalSourceObservedTimestamp = 0;
                _staleLogicalSourceIdentity = string.Empty;
                _staleLogicalSourceObservedTimestamp = 0;
            }
        }

        public string Evaluate(
            WatchdogHeartbeat heartbeat,
            IEnumerable<int> eligibleChannels,
            bool manualPauseCommanded,
            long nowTimestamp,
            long timestampFrequency,
            long nowUtcTicks = 0)
        {
            return EvaluateDetailed(
                heartbeat,
                eligibleChannels,
                manualPauseCommanded,
                nowTimestamp,
                timestampFrequency,
                nowUtcTicks).TakeoverReason;
        }

        public WatchdogChannelSupervisionEvaluation EvaluateDetailed(
            WatchdogHeartbeat heartbeat,
            IEnumerable<int> eligibleChannels,
            bool manualPauseCommanded,
            long nowTimestamp,
            long timestampFrequency,
            long nowUtcTicks = 0)
        {
            if (heartbeat == null || manualPauseCommanded)
                return new WatchdogChannelSupervisionEvaluation();

            var eligible = new HashSet<int>(eligibleChannels ?? Enumerable.Empty<int>());
            var frequency = Math.Max(1L, timestampFrequency);
            lock (_gate)
            {
                var runIdentity = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}:{3}:{4}:{5}",
                    heartbeat.SessionId ?? string.Empty,
                    heartbeat.ProcessId,
                    heartbeat.ProcessStartUtcTicks,
                    heartbeat.AttachEpoch,
                    heartbeat.RunId ?? string.Empty,
                    heartbeat.RunEpoch);
                if (!string.Equals(_runIdentity, runIdentity, StringComparison.Ordinal))
                {
                    _mechanicalSignatures.Clear();
                    _mechanicalProgressTimestamps.Clear();
                    _invariantTimestamps.Clear();
                    _invariantSignatures.Clear();
                    _refreshRequestedSignatures.Clear();
                    _logicalSourceIdentity = string.Empty;
                    _logicalSourceObservedTimestamp = 0;
                    _staleLogicalSourceIdentity = string.Empty;
                    _staleLogicalSourceObservedTimestamp = 0;
                    _runIdentity = runIdentity;
                }

                // v4 fields are additive: legacy peers leave both values at 0
                // and retain the old supervision behavior.  A current peer
                // must prove that the logical/channel-progress source itself
                // was committed recently; aggregate traffic from DAQ or Stop
                // is not accepted as a substitute.
                if (heartbeat.LogicalSourceVersion > 0 &&
                    heartbeat.LogicalCapturedUtcTicks > 0 &&
                    nowUtcTicks > 0)
                {
                    var logicalSourceIdentity = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}:{1}",
                        heartbeat.LogicalSourceVersion,
                        heartbeat.LogicalCapturedUtcTicks);
                    if (!string.Equals(
                            _logicalSourceIdentity,
                            logicalSourceIdentity,
                            StringComparison.Ordinal))
                    {
                        _logicalSourceIdentity = logicalSourceIdentity;
                        _logicalSourceObservedTimestamp = nowTimestamp;
                    }
                    var sourceWallClockAgeSeconds = Math.Max(
                        0d,
                        (nowUtcTicks - heartbeat.LogicalCapturedUtcTicks) /
                        (double)TimeSpan.TicksPerSecond);
                    var sourceMonotonicAgeSeconds = Math.Max(
                        0d,
                        (nowTimestamp - _logicalSourceObservedTimestamp) /
                        (double)frequency);
                    if (sourceWallClockAgeSeconds > LogicalSourceFreshnessSeconds ||
                        sourceMonotonicAgeSeconds > LogicalSourceFreshnessSeconds)
                    {
                        var staleIdentity = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}:{1}:{2}",
                            runIdentity,
                            heartbeat.LogicalSourceVersion,
                            heartbeat.LogicalCapturedUtcTicks);
                        var staleReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "WatchdogLogicalSourceStale:Version={0};CapturedUtcTicks={1};" +
                            "WallClockAgeSeconds={2:F1};MonotonicAgeSeconds={3:F1}",
                            heartbeat.LogicalSourceVersion,
                            heartbeat.LogicalCapturedUtcTicks,
                            sourceWallClockAgeSeconds,
                            sourceMonotonicAgeSeconds);
                        if (!string.Equals(
                                _staleLogicalSourceIdentity,
                                staleIdentity,
                                StringComparison.Ordinal))
                        {
                            _staleLogicalSourceIdentity = staleIdentity;
                            _staleLogicalSourceObservedTimestamp = nowTimestamp;
                            return Refresh(staleReason);
                        }

                        var staleObservedAge =
                            (nowTimestamp - _staleLogicalSourceObservedTimestamp) /
                            (double)frequency;
                        if (staleObservedAge >= LogicalSourceRefreshGraceSeconds)
                            return Takeover(staleReason + string.Format(
                                CultureInfo.InvariantCulture,
                                ";RefreshGraceSeconds={0:F1}",
                                staleObservedAge));

                        // While the source is stale, never charge a channel
                        // mechanical deadline from the obsolete snapshot.
                        return new WatchdogChannelSupervisionEvaluation();
                    }

                    _staleLogicalSourceIdentity = string.Empty;
                    _staleLogicalSourceObservedTimestamp = 0;
                }

                foreach (var item in heartbeat.ChannelProgress ?? Array.Empty<WatchdogChannelProgress>())
                {
                    if (item == null)
                        continue;

                    var active = item.TimerActive || item.RunnerActive || item.Energized;
                    var contract = WatchdogRuntimeContractPolicy.Resolve(item.State);
                    var recoveryEligible = heartbeat.RunActive && eligible.Contains(item.Channel);
                    var terminalResidue = contract.ResourcesMustBeInactive && active;
                    // 已完成、永久报警或人工禁用通道通常不在恢复候选集中，但它们
                    // 仍必须接受“终态不得残留执行资源”的物理安全监督。
                    if (!recoveryEligible && !terminalResidue)
                    {
                        _mechanicalSignatures.Remove(item.Channel);
                        _mechanicalProgressTimestamps.Remove(item.Channel);
                        _invariantTimestamps.Remove(item.Channel);
                        _invariantSignatures.Remove(item.Channel);
                        _refreshRequestedSignatures.Remove(item.Channel);
                        continue;
                    }

                    if (recoveryEligible && item.ConsecutiveSoftwareAbortCount >= 2)
                        return Takeover(
                            $"ChannelConsecutiveSoftwareAbort:EPB={item.Channel};" +
                            $"Count={item.ConsecutiveSoftwareAbortCount}");

                    if (recoveryEligible && active && contract.MechanicalProgressExpected)
                    {
                        // Only a mechanically completed circle is progress.
                        // DO/Peak/State can keep changing during the exact fake-running
                        // failure this supervisor is required to catch.
                        var mechanicalSignature = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}:{1}:{2}:{3}:{4}:{5}",
                            item.LifecyclePhase ?? item.State ?? string.Empty,
                            item.RuntimeContractRevision,
                            item.ProgressKind ?? string.Empty,
                            item.ProgressVersion,
                            item.MechanicalCompletedCount,
                            item.LastMechanicalCompletedUtcTicks);
                        if (!_mechanicalSignatures.TryGetValue(item.Channel, out var previous) ||
                            !string.Equals(previous, mechanicalSignature, StringComparison.Ordinal))
                        {
                            _mechanicalSignatures[item.Channel] = mechanicalSignature;
                            _mechanicalProgressTimestamps[item.Channel] = nowTimestamp;
                        }

                        if (!_mechanicalProgressTimestamps.TryGetValue(
                                item.Channel,
                                out var progressSince))
                        {
                            progressSince = nowTimestamp;
                            _mechanicalProgressTimestamps[item.Channel] = nowTimestamp;
                        }

                        var progressAge = (nowTimestamp - progressSince) / (double)frequency;
                        if (WatchdogTakeoverPolicy.IsChannelProgressStalled(
                                false,
                                true,
                                progressAge,
                                heartbeat.ExpectedCyclePeriodMs))
                            return Takeover(string.Format(
                                CultureInfo.InvariantCulture,
                                "ChannelProgressStalled:EPB={0};AgeSeconds={1:F1};" +
                                "Timer={2};Runner={3};Energized={4};Mechanical={5};" +
                                "LastMechanicalUtcTicks={6};DO={7};PeakGeneration={8};Peak={9}",
                                item.Channel,
                                progressAge,
                                item.TimerActive,
                                item.RunnerActive,
                                item.Energized,
                                item.MechanicalCompletedCount,
                                item.LastMechanicalCompletedUtcTicks,
                                item.DoCommandSequence,
                                item.PeakCutoffGeneration,
                                item.PeakCutoffSequence));
                    }
                    else
                    {
                        // A later transition back into an active cycle state starts
                        // a fresh deadline; an intentional idle period is not charged.
                        _mechanicalSignatures.Remove(item.Channel);
                        _mechanicalProgressTimestamps.Remove(item.Channel);
                    }

                    var invariantCode = SelectInvariantCode(
                        item,
                        contract,
                        active,
                        heartbeat.RunEpoch);
                    if (string.IsNullOrEmpty(invariantCode))
                    {
                        _invariantTimestamps.Remove(item.Channel);
                        _invariantSignatures.Remove(item.Channel);
                        _refreshRequestedSignatures.Remove(item.Channel);
                    }
                    else
                    {
                        var invariantSignature = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}|{1}|EPB={2}|Code={3}|State={4}|Expected={5},{6},{7}|Actual={8},{9},{10}|Owners={11},{12}",
                            heartbeat.RunId ?? string.Empty,
                            heartbeat.RunEpoch,
                            item.Channel,
                            invariantCode,
                            item.State ?? string.Empty,
                            contract.TimerRequired,
                            contract.RunnerRequired,
                            contract.ResourcesMustBeInactive,
                            item.TimerActive,
                            item.RunnerActive,
                            item.Energized,
                            item.ManualPauseOwned,
                            item.RecoveryOwned);
                        if (!_invariantSignatures.TryGetValue(item.Channel, out var previousInvariant) ||
                            !string.Equals(previousInvariant, invariantSignature, StringComparison.Ordinal))
                        {
                            _invariantSignatures[item.Channel] = invariantSignature;
                            _invariantTimestamps[item.Channel] = nowTimestamp;
                        }
                        if (RequiresStructuredRefresh(invariantCode) &&
                            (!_refreshRequestedSignatures.TryGetValue(
                                 item.Channel,
                                 out var refreshSignature) ||
                             !string.Equals(
                                 refreshSignature,
                                 invariantSignature,
                                 StringComparison.Ordinal)))
                        {
                            _refreshRequestedSignatures[item.Channel] = invariantSignature;
                            return Refresh(BuildInvariantReason(
                                invariantCode,
                                heartbeat,
                                item,
                                contract,
                                0));
                        }
                        if (!_invariantTimestamps.TryGetValue(item.Channel, out var invariantSince))
                        {
                            invariantSince = nowTimestamp;
                            _invariantTimestamps[item.Channel] = nowTimestamp;
                        }
                        var invariantAge = (nowTimestamp - invariantSince) / (double)frequency;
                        var invariantDeadlineSeconds =
                            string.Equals(
                                invariantCode,
                                "ChannelRecoveryInvariantStalled",
                                StringComparison.Ordinal)
                                ? 30
                                : 5;
                        if (invariantAge >= invariantDeadlineSeconds)
                            return Takeover(BuildInvariantReason(
                                invariantCode,
                                heartbeat,
                                item,
                                contract,
                                invariantAge));
                    }
                }
            }
            return new WatchdogChannelSupervisionEvaluation();
        }

        private static WatchdogChannelSupervisionEvaluation Takeover(string reason) =>
            new WatchdogChannelSupervisionEvaluation { TakeoverReason = reason };

        private static WatchdogChannelSupervisionEvaluation Refresh(string reason) =>
            new WatchdogChannelSupervisionEvaluation
            {
                RefreshRequested = true,
                RefreshReason = reason
            };

        private static bool RequiresStructuredRefresh(string invariantCode)
        {
            return string.Equals(
                       invariantCode,
                       "ChannelRuntimeContractInconsistent",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       invariantCode,
                       "ChannelExpectedRuntimeMissing",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       invariantCode,
                       "LearningRunnerMissing",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       invariantCode,
                       "QualificationRunnerMissing",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       invariantCode,
                       "ChannelRecoveryOwnerMissing",
                       StringComparison.Ordinal);
        }

        private static string BuildInvariantReason(
            string invariantCode,
            WatchdogHeartbeat heartbeat,
            WatchdogChannelProgress item,
            WatchdogRuntimeContract contract,
            double invariantAge)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:EPB={1};AgeSeconds={2:F1};Timer={3};Runner={4};" +
                "Energized={5};State={6};ExpectedTimer={7};ExpectedRunner={8};" +
                "ContractRevision={9};RunId={10};RunEpoch={11}",
                invariantCode,
                item.Channel,
                invariantAge,
                item.TimerActive,
                item.RunnerActive,
                item.Energized,
                item.State,
                contract.TimerRequired,
                contract.RunnerRequired,
                WatchdogRuntimeContractPolicy.CurrentRevision,
                heartbeat.RunId,
                heartbeat.RunEpoch);
        }

        private static string SelectInvariantCode(
            WatchdogChannelProgress item,
            WatchdogRuntimeContract contract,
            bool active,
            long runEpoch)
        {
            if (!WatchdogRuntimeContractPolicy.PublishedContractMatches(item, contract))
                return "ChannelRuntimeContractInconsistent";
            if (contract.ManualPauseOwnerRequired && !item.ManualPauseOwned)
                return "ChannelPausedWithoutManualOwner";
            if (contract.RecoveryOwnerRequired &&
                item.RuntimeContractRevision == WatchdogRuntimeContractPolicy.CurrentRevision &&
                (!item.RecoveryOwned ||
                 (string.Equals(item.State, "Recovering", StringComparison.OrdinalIgnoreCase) &&
                  !WatchdogRuntimeContractPolicy.HasExplicitRecoveryOwner(item, runEpoch))))
                return "ChannelRecoveryOwnerMissing";
            if (contract.RunnerRequired && !item.RunnerActive &&
                string.Equals(item.State, "Learning", StringComparison.OrdinalIgnoreCase))
                return "LearningRunnerMissing";
            if (contract.RunnerRequired && !item.RunnerActive &&
                string.Equals(item.State, "Qualification", StringComparison.OrdinalIgnoreCase))
                return "QualificationRunnerMissing";
            if ((contract.TimerRequired && !item.TimerActive) ||
                (contract.RunnerRequired && !item.RunnerActive))
                return "ChannelExpectedRuntimeMissing";
            if (contract.RequireTimerRunnerParity && item.TimerActive != item.RunnerActive)
                return "ChannelRecoveryInvariantStalled";
            if (contract.ResourcesMustBeInactive && active)
                return "ChannelTerminalResourcesActive";
            return null;
        }
    }

    public static class RecoveryProgressSignature
    {
        public static string Build(
            IEnumerable<string> stageStates,
            int daqRecoveryCount,
            int softwareRecoveryCount,
            int recoveryOwnerCount,
            int stageOrdinal,
            string recoveryIncident,
            string recoveryContext,
            bool orphanPaused,
            bool powerDisablePending)
        {
            var states = string.Join("|", (stageStates ?? Enumerable.Empty<string>())
                .Where(value => value != null)
                .OrderBy(value => value, StringComparer.Ordinal));
            return string.Format(
                "{0}|D={1}|S={2}|O={3}|Stage={4}|Incident={5}|Context={6}|Orphan={7}|PowerPending={8}",
                states,
                daqRecoveryCount,
                softwareRecoveryCount,
                recoveryOwnerCount,
                stageOrdinal,
                recoveryIncident ?? string.Empty,
                recoveryContext ?? string.Empty,
                orphanPaused,
                powerDisablePending);
        }
    }

    public static class SessionRevocationPolicy
    {
        public static bool IsRevoked(bool markerExists, bool manualStopRequested, bool explicitLifecycleEnd)
        {
            // ManualStopIntent keeps the sidecar's kill authority alive until
            // StopCompleted or the 15-second manual deadline. It is not a
            // session revocation marker in protocol v2.
            return markerExists || explicitLifecycleEnd;
        }
    }

    public static class WatchdogProcessIdentityPolicy
    {
        public static bool IsValidChallengeNonce(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
                return false;
            for (var index = 0; index < value.Length; index++)
            {
                var c = value[index];
                var hex = (c >= '0' && c <= '9') ||
                          (c >= 'a' && c <= 'f') ||
                          (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        public static bool HasAuthoritativeIdentity(
            string expectedSessionId,
            string authoritySessionId,
            int processId,
            long processStartUtcTicks,
            string instanceNonce)
        {
            return !string.IsNullOrWhiteSpace(expectedSessionId) &&
                   string.Equals(expectedSessionId, authoritySessionId, StringComparison.Ordinal) &&
                   processId > 0 &&
                   processStartUtcTicks > 0 &&
                   !string.IsNullOrWhiteSpace(instanceNonce);
        }

        public static bool Matches(int expectedPid, long expectedStartTicks, int actualPid, long actualStartTicks)
        {
            return expectedPid > 0 && expectedStartTicks > 0 &&
                   expectedPid == actualPid && expectedStartTicks == actualStartTicks;
        }

        public static bool CanKillOldProcess(
            bool sessionRevoked,
            bool manualStopRequested,
            bool currentIdentityMatches)
        {
            return !sessionRevoked && currentIdentityMatches;
        }
    }
}
