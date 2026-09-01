using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public enum WatchdogClosingTombstoneState
    {
        Closing = 1,
        Terminal = 2
    }

    public enum WatchdogClosingSafetyStage
    {
        ClosingIntent = 1,
        PhysicalSafe = 2,
        DataDrained = 3,
        LogicalQuiescent = 4,
        Terminal = 5
    }

    /// <summary>
    /// Cross-process, write-through fence for one exact Watchdog session.
    /// Once written, launch/reconnect authorization for that session can never
    /// be restored by an in-memory rollback or a replacement sidecar.
    /// </summary>
    public sealed class WatchdogClosingTombstone
    {
        public int SchemaVersion { get; set; } = 5;
        public string SessionId { get; set; } = string.Empty;
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public string CloseIntent { get; set; } = string.Empty;
        public string TakeoverTransactionId { get; set; } = string.Empty;
        public WatchdogExitDisposition ExitDisposition { get; set; } =
            WatchdogExitDisposition.OperatorExit;
        public WatchdogRelaunchDisposition RelaunchDisposition { get; set; } =
            WatchdogRelaunchDisposition.Forbidden;
        public long RelaunchPermitGeneration { get; set; }
        public string RelaunchPermitId { get; set; } = string.Empty;
        public string RelaunchPermitNonceSha256 { get; set; } = string.Empty;
        public string StopSafetyTransactionId { get; set; } = string.Empty;
        public string StopRunId { get; set; } = string.Empty;
        public long StopRunEpoch { get; set; }
        public long StopSafetyBoundaryGeneration { get; set; }
        public long StateVersion { get; set; }
        public WatchdogClosingTombstoneState State { get; set; }
        public WatchdogClosingSafetyStage SafetyStage { get; set; } =
            WatchdogClosingSafetyStage.ClosingIntent;
        /// <summary>
        /// Controller progress is diagnostic only. It must never synthesize
        /// any of the final safety evidence flags below.
        /// </summary>
        public int ControllerStopStage { get; set; }
        public long ControllerProgressVersion { get; set; }
        public string ControllerProgressDetail { get; set; } = string.Empty;
        public bool FinalSafetyResultCommitted { get; set; }
        public bool MotorsOff { get; set; }
        public bool PowerOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool PersistenceDrained { get; set; }
        public bool LogicalQuiescent { get; set; }
        public bool DataContinuityVerified { get; set; }
        public bool OldProcessExitProven { get; set; }
        public int OldProcessId { get; set; }
        public long OldProcessStartUtcTicks { get; set; }
        public DurableRelaunchProcessObservation OldProcessObservation { get; set; }
        public long OldProcessExitObservedUtcTicks { get; set; }
        public WatchdogSafetyEvidenceOwner OldProcessExitEvidenceOwner { get; set; }
        public string OldProcessExitEvidenceSource { get; set; } = string.Empty;
        public WatchdogDataAuditState DataAuditState { get; set; }
        public string SafetyOwner { get; set; } = string.Empty;
        public string SafetyHandoffId { get; set; } = string.Empty;
        public string TerminalReason { get; set; } = string.Empty;
        public long UpdatedUtcTicks { get; set; }

        public bool IsValidFor(string sessionId)
        {
            var common = (SchemaVersion == 1 || SchemaVersion == 2 ||
                          SchemaVersion == 3 || SchemaVersion == 4 ||
                          SchemaVersion == 5) &&
                   !string.IsNullOrWhiteSpace(SessionId) &&
                   string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
                   SessionGeneration > 0 && SessionLease > 0 && StateVersion > 0 &&
                   (State == WatchdogClosingTombstoneState.Closing ||
                    State == WatchdogClosingTombstoneState.Terminal);
            if (!common || SchemaVersion < 4) return common;
            if (!Enum.IsDefined(typeof(WatchdogExitDisposition), ExitDisposition) ||
                !Enum.IsDefined(typeof(WatchdogRelaunchDisposition), RelaunchDisposition) ||
                SchemaVersion >= 5 &&
                (!Enum.IsDefined(typeof(DurableRelaunchProcessObservation),
                     OldProcessObservation) ||
                 !Enum.IsDefined(typeof(WatchdogSafetyEvidenceOwner),
                     OldProcessExitEvidenceOwner) ||
                 !Enum.IsDefined(typeof(WatchdogDataAuditState), DataAuditState)))
                return false;
            if (RelaunchDisposition == WatchdogRelaunchDisposition.Forbidden)
                return RelaunchPermitGeneration == 0 &&
                       string.IsNullOrWhiteSpace(RelaunchPermitId) &&
                       string.IsNullOrWhiteSpace(RelaunchPermitNonceSha256);
            Guid parsed;
            return ExitDisposition == WatchdogExitDisposition.TakeoverReplacementExit &&
                   RelaunchPermitGeneration > 0 &&
                   Guid.TryParseExact(RelaunchPermitId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(TakeoverTransactionId ?? string.Empty, "N", out parsed) &&
                   RecoveryFailureReceipt.IsSha256(RelaunchPermitNonceSha256);
        }

        public bool PreservesApprovedPermit =>
            SchemaVersion >= 4 &&
            ExitDisposition == WatchdogExitDisposition.TakeoverReplacementExit &&
            RelaunchDisposition == WatchdogRelaunchDisposition.PreserveApprovedPermit;

        public bool IsSafetyTerminal =>
            SchemaVersion >= 2 &&
            State == WatchdogClosingTombstoneState.Terminal &&
            SafetyStage == WatchdogClosingSafetyStage.Terminal &&
            MotorsOff && PowerOff && PressureSafe && PersistenceDrained && LogicalQuiescent;

        public bool HasExactOldProcessExitProof =>
            SchemaVersion >= 5 && OldProcessExitProven && OldProcessId > 0 &&
            OldProcessStartUtcTicks > 0 && OldProcessExitObservedUtcTicks > 0 &&
            OldProcessExitEvidenceOwner == WatchdogSafetyEvidenceOwner.SupervisorService &&
            (OldProcessObservation == DurableRelaunchProcessObservation.Dead ||
             OldProcessObservation == DurableRelaunchProcessObservation.IdentityMismatch);

        public WatchdogClosingSafetyStage EffectiveSafetyStage =>
            SchemaVersion == 1 || SafetyStage == 0
                ? WatchdogClosingSafetyStage.ClosingIntent
                : SafetyStage;
    }

    public static class WatchdogClosingTombstoneStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static WatchdogClosingTombstone WriteThrough(
            string projectDirectory,
            WatchdogClosingTombstone tombstone)
        {
            if (tombstone == null) throw new ArgumentNullException(nameof(tombstone));
            if (!tombstone.IsValidFor(tombstone.SessionId))
                throw new InvalidOperationException("Watchdog closing tombstone identity is incomplete.");
            WatchdogClosingTombstone previous;
            if (TryRead(projectDirectory, tombstone.SessionId, out previous))
            {
                if (previous.SessionGeneration != tombstone.SessionGeneration ||
                    previous.SessionLease != tombstone.SessionLease ||
                    tombstone.SchemaVersion < previous.SchemaVersion ||
                    tombstone.StateVersion < previous.StateVersion ||
                    tombstone.State < previous.State ||
                    tombstone.EffectiveSafetyStage < previous.EffectiveSafetyStage ||
                    tombstone.ControllerProgressVersion < previous.ControllerProgressVersion ||
                    previous.FinalSafetyResultCommitted && !tombstone.FinalSafetyResultCommitted ||
                    previous.OldProcessExitProven && !tombstone.OldProcessExitProven ||
                    previous.OldProcessExitProven &&
                    (previous.OldProcessId != tombstone.OldProcessId ||
                     previous.OldProcessStartUtcTicks !=
                         tombstone.OldProcessStartUtcTicks ||
                     previous.OldProcessObservation != tombstone.OldProcessObservation ||
                     previous.OldProcessExitObservedUtcTicks !=
                         tombstone.OldProcessExitObservedUtcTicks ||
                     previous.OldProcessExitEvidenceOwner !=
                         tombstone.OldProcessExitEvidenceOwner) ||
                    !SameOptionalIdentity(previous.StopSafetyTransactionId,
                        tombstone.StopSafetyTransactionId) ||
                    previous.SchemaVersion >= 4 &&
                    (previous.ExitDisposition != tombstone.ExitDisposition ||
                     previous.RelaunchDisposition != tombstone.RelaunchDisposition ||
                     previous.RelaunchPermitGeneration != tombstone.RelaunchPermitGeneration ||
                     !string.Equals(previous.RelaunchPermitId,
                         tombstone.RelaunchPermitId, StringComparison.Ordinal) ||
                     !string.Equals(previous.RelaunchPermitNonceSha256,
                         tombstone.RelaunchPermitNonceSha256, StringComparison.Ordinal) ||
                     !string.Equals(previous.TakeoverTransactionId,
                         tombstone.TakeoverTransactionId, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Watchdog closing tombstone revision or identity regressed.");
                if (tombstone.StateVersion == previous.StateVersion &&
                    !string.Equals(Serializer.Serialize(previous), Serializer.Serialize(tombstone),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("Watchdog closing tombstone revision was reused with different content.");
            }
            tombstone.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            var payload = Serializer.Serialize(tombstone);
            AtomicWrite(WatchdogJournalPaths.LocalClosingPath(tombstone.SessionId), payload);
            AtomicWrite(
                WatchdogJournalPaths.ProjectClosingPath(projectDirectory, tombstone.SessionId),
                payload);
            return tombstone;
        }

        public static bool TryRead(
            string projectDirectory,
            string sessionId,
            out WatchdogClosingTombstone tombstone)
        {
            tombstone = CandidatePaths(projectDirectory, sessionId)
                .Select(TryReadFile)
                .Where(value => value?.IsValidFor(sessionId) == true)
                .OrderByDescending(value => value.StateVersion)
                .ThenByDescending(value => value.UpdatedUtcTicks)
                .FirstOrDefault();
            return tombstone != null;
        }

        private static bool SameOptionalIdentity(string previous, string current)
        {
            return string.IsNullOrWhiteSpace(previous) ||
                   string.Equals(previous, current, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> CandidatePaths(string projectDirectory, string sessionId)
        {
            yield return WatchdogJournalPaths.LocalClosingPath(sessionId);
            if (!string.IsNullOrWhiteSpace(projectDirectory))
                yield return WatchdogJournalPaths.ProjectClosingPath(projectDirectory, sessionId);
        }

        private static WatchdogClosingTombstone TryReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(
                           path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                    return Serializer.Deserialize<WatchdogClosingTombstone>(reader.ReadToEnd());
            }
            catch { return null; }
        }

        private static void AtomicWrite(string path, string payload)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Watchdog closing tombstone directory is invalid.");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, payload ?? string.Empty, new UTF8Encoding(false));
                using (var stream = new FileStream(
                           temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }
    }
}
