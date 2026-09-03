using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public enum WatchdogApplicationExitState
    {
        Requested = 1,
        DiagnosticsCaptured = 2,
        GracefulCompleted = 3,
        ForcedDeadlineExit = 4
    }

    public enum WatchdogExitDisposition
    {
        OperatorExit = 1,
        NormalCompletionExit = 2,
        TakeoverReplacementExit = 3
    }

    public enum WatchdogRelaunchDisposition
    {
        Forbidden = 1,
        PreserveApprovedPermit = 2
    }

    public static class WatchdogExitDispositionPolicy
    {
        public static WatchdogExitDisposition ResolveApplicationExit(
            bool preserveApprovedPermit,
            WatchdogExitDisposition durableClosingDisposition)
        {
            if (preserveApprovedPermit)
                return WatchdogExitDisposition.TakeoverReplacementExit;
            return durableClosingDisposition ==
                   WatchdogExitDisposition.NormalCompletionExit
                ? WatchdogExitDisposition.NormalCompletionExit
                : WatchdogExitDisposition.OperatorExit;
        }
    }

    /// <summary>
    /// Durable, exact-process exit deadline shared by the WinForms process
    /// and the independent Watchdog host.
    /// </summary>
    public sealed class WatchdogApplicationExitReceipt
    {
        public int SchemaVersion { get; set; } = 2;
        public string SessionId { get; set; } = string.Empty;
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public string ExitIntentId { get; set; } = string.Empty;
        public string StopSafetyTransactionId { get; set; } = string.Empty;
        public string TakeoverTransactionId { get; set; } = string.Empty;
        public WatchdogExitDisposition ExitDisposition { get; set; } =
            WatchdogExitDisposition.OperatorExit;
        public WatchdogRelaunchDisposition RelaunchDisposition { get; set; } =
            WatchdogRelaunchDisposition.Forbidden;
        public long RelaunchPermitGeneration { get; set; }
        public string RelaunchPermitId { get; set; } = string.Empty;
        public string RelaunchPermitNonceSha256 { get; set; } = string.Empty;
        public long Revision { get; set; }
        public WatchdogApplicationExitState State { get; set; }
        public int MainProcessId { get; set; }
        public long MainProcessStartUtcTicks { get; set; }
        public long RequestedUtcTicks { get; set; }
        public long DiagnosticDeadlineUtcTicks { get; set; }
        public long HardDeadlineUtcTicks { get; set; }
        public bool MotorsOff { get; set; }
        public bool PowerOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool PersistenceDrained { get; set; }
        public bool LogicalQuiescent { get; set; }
        public bool DataContinuityVerified { get; set; }
        public bool ProcessTerminationRequested { get; set; }
        public long OperatorNoticeAcknowledgedUtcTicks { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public long UpdatedUtcTicks { get; set; }

        public bool IsValidFor(string sessionId)
        {
            Guid parsed;
            var common = (SchemaVersion == 1 || SchemaVersion == 2) &&
                   Revision > 0 && SessionGeneration > 0 &&
                   SessionLease > 0 && MainProcessId > 0 &&
                   MainProcessStartUtcTicks > 0 && RequestedUtcTicks > 0 &&
                   DiagnosticDeadlineUtcTicks >= RequestedUtcTicks &&
                   HardDeadlineUtcTicks > DiagnosticDeadlineUtcTicks &&
                   HardDeadlineUtcTicks <= RequestedUtcTicks + TimeSpan.FromSeconds(31).Ticks &&
                   !string.IsNullOrWhiteSpace(SessionId) &&
                   string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
                   Guid.TryParseExact(ExitIntentId ?? string.Empty, "N", out parsed) &&
                   State >= WatchdogApplicationExitState.Requested &&
                   State <= WatchdogApplicationExitState.ForcedDeadlineExit;
            if (!common || SchemaVersion == 1) return common;
            if (!Enum.IsDefined(typeof(WatchdogExitDisposition), ExitDisposition) ||
                !Enum.IsDefined(typeof(WatchdogRelaunchDisposition), RelaunchDisposition))
                return false;
            if (RelaunchDisposition == WatchdogRelaunchDisposition.Forbidden)
                return RelaunchPermitGeneration == 0 &&
                       string.IsNullOrWhiteSpace(RelaunchPermitId) &&
                       string.IsNullOrWhiteSpace(RelaunchPermitNonceSha256);
            return ExitDisposition == WatchdogExitDisposition.TakeoverReplacementExit &&
                   RelaunchPermitGeneration > 0 &&
                   Guid.TryParseExact(RelaunchPermitId ?? string.Empty, "N", out parsed) &&
                   RecoveryFailureReceipt.IsSha256(RelaunchPermitNonceSha256) &&
                   Guid.TryParseExact(TakeoverTransactionId ?? string.Empty, "N", out parsed);
        }

        public bool PreservesApprovedPermit =>
            SchemaVersion >= 2 &&
            ExitDisposition == WatchdogExitDisposition.TakeoverReplacementExit &&
            RelaunchDisposition == WatchdogRelaunchDisposition.PreserveApprovedPermit;
    }

    public enum WatchdogManualPauseStage
    {
        None = 0,
        CurrentCycleDrain = 1,
        PhysicalOffConfirm = 2,
        PersistenceDrain = 3,
        Completed = 4,
        SafetyFault = 5,
        Resumed = 6
    }

    public sealed class WatchdogManualPauseReceipt
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; } = string.Empty;
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public string PauseCommandId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public WatchdogManualPauseStage Stage { get; set; }
        public int[] Channels { get; set; } = Array.Empty<int>();
        public int[] EnergizedChannels { get; set; } = Array.Empty<int>();
        public bool MotorsOff { get; set; }
        public bool PowerOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool PersistenceDrained { get; set; }
        public bool SafetyFault { get; set; }
        public string Detail { get; set; } = string.Empty;
        public long StartedUtcTicks { get; set; }
        public long HardDeadlineUtcTicks { get; set; }
        public long UpdatedUtcTicks { get; set; }

        public bool IsValidFor(string sessionId)
        {
            return SchemaVersion == 1 && Revision > 0 && SessionGeneration > 0 &&
                   SessionLease > 0 && !string.IsNullOrWhiteSpace(SessionId) &&
                   string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
                   Stage >= WatchdogManualPauseStage.CurrentCycleDrain &&
                   Stage <= WatchdogManualPauseStage.Resumed;
        }
    }

    public enum WatchdogSafetyHandoffState
    {
        Requested = 1,
        Accepted = 2,
        WorkerStarted = 3,
        Completed = 4,
        Failed = 5
    }

    public enum WatchdogSafetyStage
    {
        None = 0,
        AgentStarted = 1,
        DoOffConfirmed = 2,
        AoZeroConfirmed = 3,
        PowerOffConfirmed = 4,
        PressureSafeConfirmed = 5,
        Completed = 6
    }

    public enum RecoveryFailureDomain
    {
        None = 0,
        SafetyAgent = 1,
        EvidenceBinding = 2,
        MainLaunch = 3,
        HardwareUnavailable = 4
    }

    /// <summary>
    /// 数据修复状态与物理安全证明分开记录。数据不完整不得阻断断能证明，
    /// 但在重新进入正式试验前必须完成修复或将活动圈封为非完成圈。
    /// </summary>
    public enum WatchdogDataAuditState
    {
        Unknown = 0,
        Drained = 1,
        CrashRepairRequired = 2,
        Repaired = 3,
        DataIncomplete = 4
    }

    public enum WatchdogSafetyEvidenceOwner
    {
        Unknown = 0,
        MainProcess = 1,
        SupervisorService = 2,
        SafetyAgent = 3
    }

    public enum RecoveryReplacementState
    {
        None = 0,
        Approved = 1,
        OldProcessExitProven = 2,
        SafetyAgentRunning = 3,
        SafetyCompleted = 4,
        MainLaunchIntent = 5,
        MainStarted = 6,
        Attached = 7,
        CheckpointCommitted = 8
    }

    /// <summary>
    /// 主程序在会话附着前固化的只读安全恢复种子。它只提供经过哈希绑定的
    /// 可执行文件与不可变配置，不提供任何“设备已安全”结论。
    /// </summary>
    public sealed class WatchdogCrashRecoverySeed
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; } = string.Empty;
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public string SeedId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public string ProjectDirectory { get; set; } = string.Empty;
        public string MainExecutablePath { get; set; } = string.Empty;
        public string MainExecutableSha256 { get; set; } = string.Empty;
        public string SafetyAgentExecutablePath { get; set; } = string.Empty;
        public string SafetyAgentExecutableSha256 { get; set; } = string.Empty;
        public string ConfigSnapshotPath { get; set; } = string.Empty;
        public string ConfigSnapshotManifestPath { get; set; } = string.Empty;
        public string ConfigSnapshotManifestSha256 { get; set; } = string.Empty;
        public int ConfigSnapshotSchemaVersion { get; set; }
        public string BuildIdentity { get; set; } = string.Empty;
        public long UpdatedUtcTicks { get; set; }

        public bool IsValidFor(string sessionId)
        {
            Guid parsed;
            return SchemaVersion == 1 && Revision > 0 &&
                   SessionGeneration > 0 && SessionLease > 0 &&
                   string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(SeedId ?? string.Empty, "N", out parsed) &&
                   RecoveryFailureReceipt.IsSha256(MainExecutableSha256) &&
                   RecoveryFailureReceipt.IsSha256(SafetyAgentExecutableSha256) &&
                   RecoveryFailureReceipt.IsSha256(ConfigSnapshotManifestSha256) &&
                   !string.IsNullOrWhiteSpace(MainExecutablePath) &&
                   !string.IsNullOrWhiteSpace(SafetyAgentExecutablePath) &&
                   !string.IsNullOrWhiteSpace(ConfigSnapshotPath) &&
                   !string.IsNullOrWhiteSpace(ConfigSnapshotManifestPath) &&
                   ConfigSnapshotSchemaVersion == 2;
        }
    }

    public static class WatchdogCrashRecoverySeedStore
    {
        public static WatchdogCrashRecoverySeed WriteThrough(
            string projectDirectory,
            WatchdogCrashRecoverySeed seed)
        {
            return WatchdogSafetyReceiptStore.WriteThrough(
                projectDirectory,
                seed,
                value => value.SessionId,
                value => value.SessionGeneration,
                value => value.SessionLease,
                value => value.Revision,
                WatchdogJournalPaths.LocalCrashRecoverySeedPath,
                WatchdogJournalPaths.ProjectCrashRecoverySeedPath,
                value => value.UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                value => value.IsValidFor(value.SessionId),
                (previous, current) =>
                    string.Equals(previous.SeedId, current.SeedId, StringComparison.Ordinal) &&
                    string.Equals(previous.MainExecutablePath,
                        current.MainExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.MainExecutableSha256,
                        current.MainExecutableSha256, StringComparison.Ordinal) &&
                    string.Equals(previous.SafetyAgentExecutablePath,
                        current.SafetyAgentExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.SafetyAgentExecutableSha256,
                        current.SafetyAgentExecutableSha256, StringComparison.Ordinal) &&
                    string.Equals(previous.ConfigSnapshotPath,
                        current.ConfigSnapshotPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.ConfigSnapshotManifestPath,
                        current.ConfigSnapshotManifestPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.ConfigSnapshotManifestSha256,
                        current.ConfigSnapshotManifestSha256, StringComparison.Ordinal));
        }

        public static bool TryRead(
            string projectDirectory,
            string sessionId,
            out WatchdogCrashRecoverySeed seed)
        {
            return WatchdogSafetyReceiptStore.TryRead(
                projectDirectory,
                sessionId,
                WatchdogJournalPaths.LocalCrashRecoverySeedPath,
                WatchdogJournalPaths.ProjectCrashRecoverySeedPath,
                value => value.Revision,
                value => value.IsValidFor(sessionId),
                out seed);
        }
    }

    public sealed class WatchdogSafetyHandoffReceipt
    {
        public int SchemaVersion { get; set; } = 7;
        public string SessionId { get; set; } = string.Empty;
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public string HandoffId { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string StopSafetyTransactionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public long Revision { get; set; }
        public WatchdogSafetyHandoffState State { get; set; }
        public WatchdogSafetyStage Stage { get; set; }
        public string PreviousStageReceiptSha256 { get; set; } = string.Empty;
        public double StageMonotonicElapsedMs { get; set; }
        public long StageUtcTicks { get; set; }
        public bool MotorsOff { get; set; }
        public bool PowerOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool PersistenceDrained { get; set; }
        public bool LogicalQuiescent { get; set; }
        public bool HardwareResourcesReleased { get; set; }
        public bool ExecutionAuthorizationRevoked { get; set; }
        public bool CallbacksIsolated { get; set; }
        public bool CrashRecovery { get; set; }
        public bool OldProcessExitProven { get; set; }
        public int OldProcessId { get; set; }
        public long OldProcessStartUtcTicks { get; set; }
        public DurableRelaunchProcessObservation OldProcessObservation { get; set; }
        public long OldProcessExitObservedUtcTicks { get; set; }
        public WatchdogSafetyEvidenceOwner OldProcessExitEvidenceOwner { get; set; }
        public string OldProcessExitEvidenceSource { get; set; } = string.Empty;
        public WatchdogDataAuditState DataAuditState { get; set; }
        public int SidecarProcessId { get; set; }
        public long SidecarProcessStartUtcTicks { get; set; }
        public int WorkerProcessId { get; set; }
        public long WorkerProcessStartUtcTicks { get; set; }
        public int AttemptCount { get; set; }
        public string ProjectDirectory { get; set; } = string.Empty;
        public string MainExecutablePath { get; set; } = string.Empty;
        public string MainExecutableSha256 { get; set; } = string.Empty;
        public string SafetyAgentExecutablePath { get; set; } = string.Empty;
        public string SafetyAgentExecutableSha256 { get; set; } = string.Empty;
        public string ConfigSnapshotPath { get; set; } = string.Empty;
        public string ConfigSnapshotManifestPath { get; set; } = string.Empty;
        public string ConfigSnapshotManifestSha256 { get; set; } = string.Empty;
        public int ConfigSnapshotSchemaVersion { get; set; }
        public WatchdogRelaunchDisposition RelaunchDisposition { get; set; } =
            WatchdogRelaunchDisposition.Forbidden;
        public long RelaunchPermitGeneration { get; set; }
        public string RelaunchPermitId { get; set; } = string.Empty;
        public string RelaunchPermitNonceSha256 { get; set; } = string.Empty;
        public string FailureCode { get; set; } = string.Empty;
        public RecoveryFailureDomain FailureDomain { get; set; }
        public string Detail { get; set; } = string.Empty;
        public long UpdatedUtcTicks { get; set; }

        public bool IsValidFor(string sessionId)
        {
            Guid parsed;
            var common = (SchemaVersion == 1 || SchemaVersion == 2 ||
                          SchemaVersion == 3 || SchemaVersion == 4 ||
                          SchemaVersion == 5 || SchemaVersion == 6 ||
                          SchemaVersion == 7) &&
                   Revision > 0 && SessionGeneration > 0 &&
                   SessionLease > 0 && !string.IsNullOrWhiteSpace(SessionId) &&
                   string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
                   Guid.TryParseExact(HandoffId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(Nonce) &&
                   State >= WatchdogSafetyHandoffState.Requested &&
                   State <= WatchdogSafetyHandoffState.Failed;
            if (!common || SchemaVersion == 1) return common;
            var expectedSnapshotSchema = SchemaVersion >= 3 ? 2 : 1;
            if (ConfigSnapshotSchemaVersion != expectedSnapshotSchema ||
                string.IsNullOrWhiteSpace(ConfigSnapshotPath) ||
                string.IsNullOrWhiteSpace(ConfigSnapshotManifestPath) ||
                !RecoveryFailureReceipt.IsSha256(ConfigSnapshotManifestSha256) ||
                !Enum.IsDefined(typeof(WatchdogRelaunchDisposition), RelaunchDisposition))
                return false;
            if (SchemaVersion >= 3 &&
                (!Enum.IsDefined(typeof(WatchdogSafetyStage), Stage) ||
                 !Enum.IsDefined(typeof(RecoveryFailureDomain), FailureDomain) ||
                 !RecoveryFailureReceipt.IsSha256(MainExecutableSha256) ||
                 string.IsNullOrWhiteSpace(SafetyAgentExecutablePath) ||
                 !RecoveryFailureReceipt.IsSha256(SafetyAgentExecutableSha256)))
                return false;
            if (SchemaVersion >= 4 && CrashRecovery && !OldProcessExitProven)
                return false;
            if (SchemaVersion >= 5)
            {
                if (!Enum.IsDefined(typeof(WatchdogDataAuditState), DataAuditState) ||
                    !Enum.IsDefined(typeof(WatchdogSafetyEvidenceOwner),
                        OldProcessExitEvidenceOwner) ||
                    !Enum.IsDefined(typeof(DurableRelaunchProcessObservation),
                        OldProcessObservation))
                    return false;
                if (CrashRecovery &&
                    (!OldProcessExitProven || OldProcessId <= 0 ||
                     OldProcessStartUtcTicks <= 0 || OldProcessExitObservedUtcTicks <= 0 ||
                     OldProcessExitEvidenceOwner !=
                         WatchdogSafetyEvidenceOwner.SupervisorService ||
                     (OldProcessObservation != DurableRelaunchProcessObservation.Dead &&
                      OldProcessObservation !=
                          DurableRelaunchProcessObservation.IdentityMismatch)))
                    return false;
            }
            if (RelaunchDisposition == WatchdogRelaunchDisposition.Forbidden)
                return RelaunchPermitGeneration == 0 &&
                       string.IsNullOrWhiteSpace(RelaunchPermitId) &&
                       string.IsNullOrWhiteSpace(RelaunchPermitNonceSha256);
            return RelaunchPermitGeneration > 0 &&
                   Guid.TryParseExact(RelaunchPermitId ?? string.Empty, "N", out parsed) &&
                   RecoveryFailureReceipt.IsSha256(RelaunchPermitNonceSha256);
        }

        public bool CanExitApplication =>
            State >= WatchdogSafetyHandoffState.Accepted &&
            State <= WatchdogSafetyHandoffState.Completed &&
            PersistenceDrained && HardwareResourcesReleased &&
            ExecutionAuthorizationRevoked && CallbacksIsolated;

        public bool IsSafetyCompleted =>
            State == WatchdogSafetyHandoffState.Completed &&
            (SchemaVersion < 3 || Stage == WatchdogSafetyStage.Completed) &&
            MotorsOff && PowerOff && PressureSafe;

        public bool IsTerminal =>
            State == WatchdogSafetyHandoffState.Completed ||
            State == WatchdogSafetyHandoffState.Failed;
    }

    public static class WatchdogManualPauseReceiptStore
    {
        public static WatchdogManualPauseReceipt WriteThrough(
            string projectDirectory,
            WatchdogManualPauseReceipt receipt)
        {
            return WatchdogSafetyReceiptStore.WriteThrough(
                projectDirectory,
                receipt,
                value => value.SessionId,
                value => value.SessionGeneration,
                value => value.SessionLease,
                value => value.Revision,
                WatchdogJournalPaths.LocalManualPausePath,
                WatchdogJournalPaths.ProjectManualPausePath,
                value => value.UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                value => value.IsValidFor(value.SessionId),
                (previous, current) =>
                    !string.Equals(previous.PauseCommandId, current.PauseCommandId,
                        StringComparison.OrdinalIgnoreCase) ||
                    current.Stage >= previous.Stage);
        }

        public static bool TryRead(
            string projectDirectory,
            string sessionId,
            out WatchdogManualPauseReceipt receipt)
        {
            return WatchdogSafetyReceiptStore.TryRead(
                projectDirectory,
                sessionId,
                WatchdogJournalPaths.LocalManualPausePath,
                WatchdogJournalPaths.ProjectManualPausePath,
                value => value.Revision,
                value => value.IsValidFor(sessionId),
                out receipt);
        }
    }

    public static class WatchdogApplicationExitReceiptStore
    {
        public static WatchdogApplicationExitReceipt WriteThrough(
            string projectDirectory,
            WatchdogApplicationExitReceipt receipt)
        {
            return WatchdogSafetyReceiptStore.WriteThrough(
                projectDirectory,
                receipt,
                value => value.SessionId,
                value => value.SessionGeneration,
                value => value.SessionLease,
                value => value.Revision,
                WatchdogJournalPaths.LocalApplicationExitPath,
                WatchdogJournalPaths.ProjectApplicationExitPath,
                value => value.UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                value => value.IsValidFor(value.SessionId),
                (previous, current) =>
                    string.Equals(previous.ExitIntentId, current.ExitIntentId, StringComparison.Ordinal) &&
                    previous.MainProcessId == current.MainProcessId &&
                    previous.MainProcessStartUtcTicks == current.MainProcessStartUtcTicks &&
                    previous.RequestedUtcTicks == current.RequestedUtcTicks &&
                    previous.DiagnosticDeadlineUtcTicks == current.DiagnosticDeadlineUtcTicks &&
                    previous.HardDeadlineUtcTicks == current.HardDeadlineUtcTicks &&
                    previous.ExitDisposition == current.ExitDisposition &&
                    previous.RelaunchDisposition == current.RelaunchDisposition &&
                    previous.RelaunchPermitGeneration == current.RelaunchPermitGeneration &&
                    string.Equals(previous.RelaunchPermitId, current.RelaunchPermitId,
                        StringComparison.Ordinal) &&
                    string.Equals(previous.RelaunchPermitNonceSha256,
                        current.RelaunchPermitNonceSha256, StringComparison.Ordinal) &&
                    string.Equals(previous.TakeoverTransactionId,
                        current.TakeoverTransactionId, StringComparison.Ordinal) &&
                    current.State >= previous.State);
        }

        public static bool TryRead(
            string projectDirectory,
            string sessionId,
            out WatchdogApplicationExitReceipt receipt)
        {
            return WatchdogSafetyReceiptStore.TryRead(
                projectDirectory,
                sessionId,
                WatchdogJournalPaths.LocalApplicationExitPath,
                WatchdogJournalPaths.ProjectApplicationExitPath,
                value => value.Revision,
                value => value.IsValidFor(sessionId),
                out receipt);
        }

        public static bool TryReadLatestForced(out WatchdogApplicationExitReceipt receipt)
        {
            receipt = null;
            try
            {
                var directory = WatchdogJournalPaths.LocalControlDirectory;
                if (!Directory.Exists(directory)) return false;
                var serializer = new JavaScriptSerializer();
                receipt = Directory.GetFiles(
                        directory,
                        "session-*.application-exit.json",
                        SearchOption.TopDirectoryOnly)
                    .Select(path =>
                    {
                        try
                        {
                            return serializer.Deserialize<WatchdogApplicationExitReceipt>(
                                File.ReadAllText(path, new UTF8Encoding(false)));
                        }
                        catch { return null; }
                    })
                    .Where(value => value != null &&
                                    value.IsValidFor(value.SessionId) &&
                                    value.State == WatchdogApplicationExitState.ForcedDeadlineExit &&
                                    value.OperatorNoticeAcknowledgedUtcTicks <= 0)
                    .OrderByDescending(value => value.UpdatedUtcTicks)
                    .FirstOrDefault();
                return receipt != null;
            }
            catch
            {
                receipt = null;
                return false;
            }
        }
    }

    public static class WatchdogSafetyHandoffReceiptStore
    {
        public static WatchdogSafetyHandoffReceipt WriteThrough(
            string projectDirectory,
            WatchdogSafetyHandoffReceipt receipt)
        {
            return WatchdogSafetyReceiptStore.WriteThrough(
                projectDirectory,
                receipt,
                value => value.SessionId,
                value => value.SessionGeneration,
                value => value.SessionLease,
                value => value.Revision,
                WatchdogJournalPaths.LocalSafetyHandoffPath,
                WatchdogJournalPaths.ProjectSafetyHandoffPath,
                value => value.UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                value => value.IsValidFor(value.SessionId),
                (previous, current) =>
                    IsSameHandoffTransition(previous, current) ||
                    IsPostExitEvidenceAugmentation(previous, current) ||
                    IsNextPermitHandoff(previous, current));
        }

        private static bool IsSameHandoffTransition(
            WatchdogSafetyHandoffReceipt previous,
            WatchdogSafetyHandoffReceipt current)
        {
            return string.Equals(previous.HandoffId, current.HandoffId, StringComparison.Ordinal) &&
                    current.SchemaVersion >= previous.SchemaVersion &&
                    (!previous.CrashRecovery || current.CrashRecovery) &&
                    (!previous.OldProcessExitProven || current.OldProcessExitProven) &&
                    string.Equals(previous.Nonce, current.Nonce, StringComparison.Ordinal) &&
                    string.Equals(previous.StopSafetyTransactionId,
                        current.StopSafetyTransactionId, StringComparison.OrdinalIgnoreCase) &&
                    previous.RelaunchDisposition == current.RelaunchDisposition &&
                    previous.RelaunchPermitGeneration == current.RelaunchPermitGeneration &&
                    string.Equals(previous.RelaunchPermitId, current.RelaunchPermitId,
                        StringComparison.Ordinal) &&
                    string.Equals(previous.RelaunchPermitNonceSha256,
                        current.RelaunchPermitNonceSha256, StringComparison.Ordinal) &&
                    string.Equals(previous.ConfigSnapshotPath, current.ConfigSnapshotPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.ConfigSnapshotManifestPath,
                        current.ConfigSnapshotManifestPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.ConfigSnapshotManifestSha256,
                        current.ConfigSnapshotManifestSha256, StringComparison.Ordinal) &&
                    previous.ConfigSnapshotSchemaVersion == current.ConfigSnapshotSchemaVersion &&
                    IsSchema5EvidenceMonotonic(previous, current) &&
                    previous.State != WatchdogSafetyHandoffState.Completed &&
                    previous.State != WatchdogSafetyHandoffState.Failed &&
                    current.State >= previous.State &&
                    (current.SchemaVersion < 3 || current.Stage >= previous.Stage);
        }

        /// <summary>
        /// schema 7 唯一允许对终态回执进行的修改：监督服务在精确验证旧 PID、
        /// 启动时间及 permit 后，单调补齐退出与资源释放证据。任何安全位、身份、
        /// 配置或 permit 回退都会被拒绝。
        /// </summary>
        private static bool IsPostExitEvidenceAugmentation(
            WatchdogSafetyHandoffReceipt previous,
            WatchdogSafetyHandoffReceipt current)
        {
            return previous.State == WatchdogSafetyHandoffState.Completed &&
                   current.State == WatchdogSafetyHandoffState.Completed &&
                   current.Stage == previous.Stage &&
                   current.SchemaVersion == 7 &&
                   current.SchemaVersion >= previous.SchemaVersion &&
                   HasSameImmutableHandoffIdentity(previous, current) &&
                   previous.MotorsOff == current.MotorsOff &&
                   previous.PowerOff == current.PowerOff &&
                   previous.PressureSafe == current.PressureSafe &&
                   (!previous.PersistenceDrained || current.PersistenceDrained) &&
                   (!previous.LogicalQuiescent || current.LogicalQuiescent) &&
                   (!previous.HardwareResourcesReleased ||
                    current.HardwareResourcesReleased) &&
                   (!previous.ExecutionAuthorizationRevoked ||
                    current.ExecutionAuthorizationRevoked) &&
                   (!previous.CallbacksIsolated || current.CallbacksIsolated) &&
                   current.CrashRecovery && current.OldProcessExitProven &&
                   current.LogicalQuiescent && current.HardwareResourcesReleased &&
                   current.ExecutionAuthorizationRevoked && current.CallbacksIsolated &&
                   current.OldProcessId > 0 && current.OldProcessStartUtcTicks > 0 &&
                   current.OldProcessExitObservedUtcTicks > 0 &&
                   current.OldProcessExitEvidenceOwner ==
                       WatchdogSafetyEvidenceOwner.SupervisorService &&
                   (current.OldProcessObservation ==
                        DurableRelaunchProcessObservation.Dead ||
                    current.OldProcessObservation ==
                        DurableRelaunchProcessObservation.IdentityMismatch) &&
                   IsSchema5EvidenceMonotonic(previous, current);
        }

        private static bool HasSameImmutableHandoffIdentity(
            WatchdogSafetyHandoffReceipt previous,
            WatchdogSafetyHandoffReceipt current)
        {
            return string.Equals(previous.HandoffId, current.HandoffId,
                       StringComparison.Ordinal) &&
                   string.Equals(previous.Nonce, current.Nonce,
                       StringComparison.Ordinal) &&
                   string.Equals(previous.StopSafetyTransactionId,
                       current.StopSafetyTransactionId,
                       StringComparison.OrdinalIgnoreCase) &&
                   previous.RelaunchDisposition == current.RelaunchDisposition &&
                   previous.RelaunchPermitGeneration ==
                       current.RelaunchPermitGeneration &&
                   string.Equals(previous.RelaunchPermitId,
                       current.RelaunchPermitId, StringComparison.Ordinal) &&
                   string.Equals(previous.RelaunchPermitNonceSha256,
                       current.RelaunchPermitNonceSha256, StringComparison.Ordinal) &&
                   string.Equals(previous.ConfigSnapshotPath,
                       current.ConfigSnapshotPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(previous.ConfigSnapshotManifestPath,
                       current.ConfigSnapshotManifestPath,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(previous.ConfigSnapshotManifestSha256,
                       current.ConfigSnapshotManifestSha256,
                       StringComparison.Ordinal) &&
                   previous.ConfigSnapshotSchemaVersion ==
                       current.ConfigSnapshotSchemaVersion;
        }

        private static bool IsSchema5EvidenceMonotonic(
            WatchdogSafetyHandoffReceipt previous,
            WatchdogSafetyHandoffReceipt current)
        {
            if (previous.SchemaVersion < 5) return true;
            if (previous.OldProcessExitProven &&
                (previous.OldProcessId != current.OldProcessId ||
                 previous.OldProcessStartUtcTicks != current.OldProcessStartUtcTicks ||
                 previous.OldProcessObservation != current.OldProcessObservation ||
                 previous.OldProcessExitObservedUtcTicks !=
                     current.OldProcessExitObservedUtcTicks ||
                 previous.OldProcessExitEvidenceOwner !=
                     current.OldProcessExitEvidenceOwner ||
                 !string.Equals(previous.OldProcessExitEvidenceSource,
                     current.OldProcessExitEvidenceSource, StringComparison.Ordinal)))
                return false;
            return previous.DataAuditState == WatchdogDataAuditState.Unknown ||
                   previous.DataAuditState == current.DataAuditState ||
                   (previous.DataAuditState == WatchdogDataAuditState.CrashRepairRequired &&
                    (current.DataAuditState == WatchdogDataAuditState.Repaired ||
                     current.DataAuditState == WatchdogDataAuditState.DataIncomplete));
        }

        private static bool IsNextPermitHandoff(
            WatchdogSafetyHandoffReceipt previous,
            WatchdogSafetyHandoffReceipt current)
        {
            return (previous.SchemaVersion == 3 || previous.SchemaVersion == 4 ||
                    previous.SchemaVersion == 5 || previous.SchemaVersion == 6 ||
                    previous.SchemaVersion == 7) &&
                   previous.IsSafetyCompleted &&
                   (current.SchemaVersion == 3 || current.SchemaVersion == 4 ||
                    current.SchemaVersion == 5 || current.SchemaVersion == 6 ||
                    current.SchemaVersion == 7) &&
                   current.RelaunchDisposition ==
                       WatchdogRelaunchDisposition.PreserveApprovedPermit &&
                   current.RelaunchPermitGeneration >
                       previous.RelaunchPermitGeneration &&
                   !string.Equals(
                       current.RelaunchPermitId,
                       previous.RelaunchPermitId,
                       StringComparison.Ordinal) &&
                   current.Stage == WatchdogSafetyStage.None &&
                   (current.State == WatchdogSafetyHandoffState.Requested ||
                    current.State == WatchdogSafetyHandoffState.Accepted);
        }

        public static bool TryRead(
            string projectDirectory,
            string sessionId,
            out WatchdogSafetyHandoffReceipt receipt)
        {
            return WatchdogSafetyReceiptStore.TryRead(
                projectDirectory,
                sessionId,
                WatchdogJournalPaths.LocalSafetyHandoffPath,
                WatchdogJournalPaths.ProjectSafetyHandoffPath,
                value => value.Revision,
                value => value.IsValidFor(sessionId),
                out receipt);
        }
    }

    internal static class WatchdogSafetyReceiptStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static T WriteThrough<T>(
            string projectDirectory,
            T value,
            Func<T, string> sessionSelector,
            Func<T, long> generationSelector,
            Func<T, long> leaseSelector,
            Func<T, long> revisionSelector,
            Func<string, string> localPath,
            Func<string, string, string> projectPath,
            Action<T> stamp,
            Func<T, bool> validator,
            Func<T, T, bool> transitionValidator) where T : class
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!validator(value)) throw new InvalidOperationException("Watchdog safety receipt is invalid.");
            var sessionId = sessionSelector(value);
            T previous;
            if (TryRead(projectDirectory, sessionId, localPath, projectPath,
                    revisionSelector, validator, out previous))
            {
                if (generationSelector(previous) != generationSelector(value) ||
                    leaseSelector(previous) != leaseSelector(value) ||
                    revisionSelector(value) < revisionSelector(previous) ||
                    transitionValidator?.Invoke(previous, value) == false)
                    throw new InvalidOperationException("Watchdog safety receipt revision or identity regressed.");
                if (revisionSelector(value) == revisionSelector(previous) &&
                    !string.Equals(Serializer.Serialize(previous), Serializer.Serialize(value),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("Watchdog safety receipt revision was reused with different content.");
            }
            stamp(value);
            var bytes = Utf8.GetBytes(Serializer.Serialize(value));
            DurableJsonFileStore.WriteAtomicWithBackup(localPath(sessionId), bytes);
            if (!string.IsNullOrWhiteSpace(projectDirectory))
                DurableJsonFileStore.WriteAtomicWithBackup(projectPath(projectDirectory, sessionId), bytes);
            return value;
        }

        internal static bool TryRead<T>(
            string projectDirectory,
            string sessionId,
            Func<string, string> localPath,
            Func<string, string, string> projectPath,
            Func<T, long> revisionSelector,
            Func<T, bool> validator,
            out T value) where T : class
        {
            var candidates = string.IsNullOrWhiteSpace(projectDirectory)
                ? new[] { localPath(sessionId), localPath(sessionId) + ".bak" }
                : new[]
                {
                    localPath(sessionId), localPath(sessionId) + ".bak",
                    projectPath(projectDirectory, sessionId),
                    projectPath(projectDirectory, sessionId) + ".bak"
                };
            var result = DurableJsonFileStore.ReadLatestValid(revisionSelector, candidates);
            value = result.Value;
            return value != null && validator(value);
        }
    }
}
