using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    public static class WatchdogLifecyclePolicy
    {
        public static bool IsTerminalMessage(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.RunStopped, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RunCompleted, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ApplicationClosing, StringComparison.Ordinal) ||
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
            return new RecoveryFailureClassification
            {
                Code = string.IsNullOrWhiteSpace(code)
                    ? (transientHardware ? "TransientInfrastructure" : "UnhandledSoftwareStartup")
                    : code,
                Permanent = false,
                MaximumProcessRelaunches = transientHardware
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
            var normalized = string.Join(" ", (reason ?? "UnknownFailure")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length <= 1024 ? normalized : normalized.Substring(0, 1024);
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
        public const int CurrentRevision = 1;

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
            double recoveryNoProgressSeconds = 0)
        {
            if (sessionRevoked || alreadyTakingOver)
                return false;
            if (!processAlive || heartbeatAgeSeconds >= 5)
                return true;
            if (stopAllActive && stageAgeSeconds >= 5)
                return true;
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
        private string _runIdentity = string.Empty;

        public void Reset()
        {
            lock (_gate)
            {
                _mechanicalSignatures.Clear();
                _mechanicalProgressTimestamps.Clear();
                _invariantTimestamps.Clear();
                _invariantSignatures.Clear();
                _runIdentity = string.Empty;
            }
        }

        public string Evaluate(
            WatchdogHeartbeat heartbeat,
            IEnumerable<int> eligibleChannels,
            bool manualPauseCommanded,
            long nowTimestamp,
            long timestampFrequency)
        {
            if (heartbeat == null || manualPauseCommanded)
                return null;

            var eligible = new HashSet<int>(eligibleChannels ?? Enumerable.Empty<int>());
            var frequency = Math.Max(1L, timestampFrequency);
            lock (_gate)
            {
                var runIdentity = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}",
                    heartbeat.RunId ?? string.Empty,
                    heartbeat.RunEpoch);
                if (!string.Equals(_runIdentity, runIdentity, StringComparison.Ordinal))
                {
                    _mechanicalSignatures.Clear();
                    _mechanicalProgressTimestamps.Clear();
                    _invariantTimestamps.Clear();
                    _invariantSignatures.Clear();
                    _runIdentity = runIdentity;
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
                        continue;
                    }

                    if (recoveryEligible && item.ConsecutiveSoftwareAbortCount >= 2)
                        return $"ChannelConsecutiveSoftwareAbort:EPB={item.Channel};" +
                               $"Count={item.ConsecutiveSoftwareAbortCount}";

                    if (recoveryEligible && active && contract.MechanicalProgressExpected)
                    {
                        // Only a mechanically completed circle is progress.
                        // DO/Peak/State can keep changing during the exact fake-running
                        // failure this supervisor is required to catch.
                        var mechanicalSignature = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}:{1}",
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
                            return string.Format(
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
                                item.PeakCutoffSequence);
                    }
                    else
                    {
                        // A later transition back into an active cycle state starts
                        // a fresh deadline; an intentional idle period is not charged.
                        _mechanicalSignatures.Remove(item.Channel);
                        _mechanicalProgressTimestamps.Remove(item.Channel);
                    }

                    var invariantCode = SelectInvariantCode(item, contract, active);
                    if (string.IsNullOrEmpty(invariantCode))
                    {
                        _invariantTimestamps.Remove(item.Channel);
                        _invariantSignatures.Remove(item.Channel);
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
                }
            }
            return null;
        }

        private static string SelectInvariantCode(
            WatchdogChannelProgress item,
            WatchdogRuntimeContract contract,
            bool active)
        {
            if (!WatchdogRuntimeContractPolicy.PublishedContractMatches(item, contract))
                return "ChannelRuntimeContractInconsistent";
            if (contract.ManualPauseOwnerRequired && !item.ManualPauseOwned)
                return "ChannelPausedWithoutManualOwner";
            if (contract.RecoveryOwnerRequired &&
                item.RuntimeContractRevision == WatchdogRuntimeContractPolicy.CurrentRevision &&
                !item.RecoveryOwned)
                return "ChannelRecoveryOwnerMissing";
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
