using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// V3 separates executable identity from recovery authority.  A launch
    /// capability is valid for exactly one role and cannot be replayed to
    /// substitute the UI for the hardware host (or vice versa).
    /// </summary>
    public enum ProcessRole
    {
        Unknown = 0,
        EngineHost = 1,
        UserInterface = 2,
        SafetyAgent = 3,
        SessionAgent = 4,
        Supervisor = 5
    }

    public enum SystemTerminalState
    {
        Unknown = 0,
        Running = 1,
        RunningDegraded = 2,
        SafeIdleAlarmed = 3,
        StoppedByOperator = 4
    }

    public enum RecoveryStage
    {
        None = 0,
        IntentCommitted = 1,
        AwaitingSafetyProof = 2,
        LocalRebuild = 3,
        ReplaceCurrentEngine = 4,
        ActivateLastKnownGood = 5,
        PassivePreflight = 6,
        Qualification = 7,
        FormalValidation = 8,
        StableObservation = 9,
        Completed = 10,
        SafeIdleAlarmed = 11,
        StoppedByOperator = 12
    }

    public enum RecoveryCommandKind
    {
        None = 0,
        DisableOutputs = 1,
        SealActiveCycle = 2,
        RebuildResource = 3,
        ReplaceEngineHost = 4,
        ActivateLastKnownGood = 5,
        RunPassivePreflight = 6,
        RunQualificationCycle = 7,
        ResumeFormalRun = 8,
        IsolateResource = 9,
        EnterSafeIdle = 10,
        StopByOperator = 11,
        EnsureUserInterface = 12
    }

    public enum FaultSeverity
    {
        Information = 0,
        Degraded = 1,
        RecoveryRequired = 2,
        SafetyCritical = 3
    }

    public enum ResourceKind
    {
        Unknown = 0,
        Channel = 1,
        ElectricalGroup = 2,
        HydraulicGroup = 3,
        DaqDevice = 4,
        Storage = 5,
        EngineHost = 6,
        UserInterface = 7,
        SafetyChain = 8,
        System = 9
    }

    public enum OperatorCommandKind
    {
        None = 0,
        Start = 1,
        Stop = 2,
        Pause = 3,
        Resume = 4,
        RetryQualification = 5,
        ShowUserInterface = 6,
        CommitConfiguration = 7
    }

    public static class RecoveryProtocolV7
    {
        public const int SchemaVersion = 7;
        public const int PackageSchemaVersion = 6;
        public const string ArchitectureGeneration = "EPB-RecoveryKernel-V3";
        public const string EnginePipeName = "MTTFTest.EngineHost.Control.v7";
        public const int MaximumTextLength = 1024 * 1024;

        public static bool IsGuid(string value)
        {
            return Guid.TryParseExact(value ?? string.Empty, "N", out _);
        }

        public static bool HasText(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumTextLength;
        }

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }

        public static string ComputeIdempotencyKey(
            RecoveryIdentity identity,
            RecoveryCommandKind kind,
            long commandSequence)
        {
            if (identity?.IsStructurallyValid() != true)
                throw new InvalidOperationException("RecoveryIdentityInvalid");
            using (var sha = SHA256.Create())
            {
                var canonical = identity.ToCanonicalString() + "\n" +
                                ((int)kind).ToString(CultureInfo.InvariantCulture) + "\n" +
                                commandSequence.ToString(CultureInfo.InvariantCulture);
                return BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
            }
        }
    }

    /// <summary>
    /// Identity shared by every observation, decision, command and receipt.
    /// A bootstrap/start transaction receives a RunId before the first output
    /// can be energized, so pre-formal failures are recoverable work and can
    /// never collapse into the V2.15 "RejectedNoWork" path.
    /// </summary>
    public sealed class RecoveryIdentity
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string ResourceScope { get; set; } = string.Empty;
        public long Generation { get; set; }
        public long Revision { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   RecoveryProtocolV7.IsGuid(SessionId) &&
                   RecoveryProtocolV7.IsGuid(RunId) &&
                   RunEpoch > 0 &&
                   RecoveryProtocolV7.IsGuid(IncidentId) &&
                   RecoveryProtocolV7.HasText(ResourceScope) &&
                   Generation > 0 && Revision > 0;
        }

        public string ToCanonicalString()
        {
            return string.Join("\n", new[]
            {
                SchemaVersion.ToString(CultureInfo.InvariantCulture),
                SessionId ?? string.Empty,
                RunId ?? string.Empty,
                RunEpoch.ToString(CultureInfo.InvariantCulture),
                IncidentId ?? string.Empty,
                ResourceScope ?? string.Empty,
                Generation.ToString(CultureInfo.InvariantCulture),
                Revision.ToString(CultureInfo.InvariantCulture)
            });
        }

        public RecoveryIdentity Clone()
        {
            return (RecoveryIdentity)MemberwiseClone();
        }
    }

    public sealed class FaultObservation
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string ObservationId { get; set; } = string.Empty;
        public RecoveryIdentity Identity { get; set; }
        public ResourceKind ResourceKind { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public string FaultCode { get; set; } = string.Empty;
        public string ObservableProperty { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public FaultSeverity Severity { get; set; }
        public bool ScopeProven { get; set; }
        public bool HardwareConfirmed { get; set; }
        public bool OutputOffConfirmed { get; set; }
        public bool PressureSafe { get; set; }
        public bool DataBoundaryClosed { get; set; }
        public bool SafetyChainHealthy { get; set; }
        public long ObservedUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   RecoveryProtocolV7.IsGuid(ObservationId) &&
                   Identity?.IsStructurallyValid() == true &&
                   ResourceKind != ResourceKind.Unknown &&
                   RecoveryProtocolV7.HasText(ResourceId) &&
                   RecoveryProtocolV7.HasText(FaultCode) &&
                   RecoveryProtocolV7.HasText(ObservableProperty) &&
                   ObservedUtcTicks > 0;
        }
    }

    public sealed class RecoveryIntent
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public RecoveryIdentity Identity { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public RecoveryStage Stage { get; set; }
        public SystemTerminalState DesiredTerminalState { get; set; }
        public bool HardwareConfirmed { get; set; }
        public bool AutomaticReplayForbidden { get; set; }
        public int LocalRebuildAttempts { get; set; }
        public int CurrentEngineReplacementAttempts { get; set; }
        public int LastKnownGoodAttempts { get; set; }
        public int ActiveQualificationAttempts { get; set; }
        public bool IsOperatorStart { get; set; }
        public bool RequiresEngineReplacement { get; set; }
        public long CreatedUtcTicks { get; set; }
        public long UpdatedUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   Identity?.IsStructurallyValid() == true &&
                   RecoveryProtocolV7.IsGuid(OwnerId) &&
                   Stage != RecoveryStage.None &&
                   DesiredTerminalState != SystemTerminalState.Unknown &&
                   LocalRebuildAttempts >= 0 &&
                   CurrentEngineReplacementAttempts >= 0 &&
                   LastKnownGoodAttempts >= 0 &&
                   ActiveQualificationAttempts >= 0 &&
                   CreatedUtcTicks > 0 && UpdatedUtcTicks >= CreatedUtcTicks;
        }
    }

    public sealed class RecoveryCommand
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public RecoveryIdentity Identity { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string CommandId { get; set; } = string.Empty;
        public long CommandSequence { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public RecoveryCommandKind Kind { get; set; }
        public string TargetResource { get; set; } = string.Empty;
        public long DeadlineUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   Identity?.IsStructurallyValid() == true &&
                   RecoveryProtocolV7.IsGuid(OwnerId) &&
                   RecoveryProtocolV7.IsGuid(CommandId) &&
                   CommandSequence > 0 &&
                   string.Equals(
                       IdempotencyKey,
                       RecoveryProtocolV7.ComputeIdempotencyKey(
                           Identity, Kind, CommandSequence),
                       StringComparison.Ordinal) &&
                   Kind != RecoveryCommandKind.None &&
                   RecoveryProtocolV7.HasText(TargetResource) &&
                   DeadlineUtcTicks > DateTime.MinValue.Ticks;
        }
    }

    public sealed class SafetyProof
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public RecoveryIdentity Identity { get; set; }
        public string ProofId { get; set; } = string.Empty;
        public string SafetyAgentInstanceId { get; set; } = string.Empty;
        public bool OutputsOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool DataBoundaryClosed { get; set; }
        public bool OldProcessIsolated { get; set; }
        public bool SafetyChainHealthy { get; set; }
        public long CapturedUtcTicks { get; set; }
        public long ValidUntilUtcTicks { get; set; }

        public bool IsComplete => OutputsOff && PressureSafe && DataBoundaryClosed &&
                                  OldProcessIsolated && SafetyChainHealthy;

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   Identity?.IsStructurallyValid() == true &&
                   RecoveryProtocolV7.IsGuid(ProofId) &&
                   RecoveryProtocolV7.IsGuid(SafetyAgentInstanceId) &&
                   CapturedUtcTicks > 0 && ValidUntilUtcTicks > CapturedUtcTicks;
        }
    }

    public sealed class QualificationReceipt
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public RecoveryIdentity Identity { get; set; }
        public string ReceiptId { get; set; } = string.Empty;
        public int QualificationCyclesCompleted { get; set; }
        public int FormalCyclesCompleted { get; set; }
        public long StableSinceUtcTicks { get; set; }
        public long CapturedUtcTicks { get; set; }
        public bool InterruptedCycleCounted { get; set; }

        public bool IsBudgetResetQualified(long nowUtcTicks)
        {
            return IsStructurallyValid() && QualificationCyclesCompleted >= 2 &&
                   FormalCyclesCompleted >= 5 && !InterruptedCycleCounted &&
                   StableSinceUtcTicks > 0 &&
                   nowUtcTicks - StableSinceUtcTicks >= TimeSpan.FromMinutes(10).Ticks;
        }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   Identity?.IsStructurallyValid() == true &&
                   RecoveryProtocolV7.IsGuid(ReceiptId) &&
                   QualificationCyclesCompleted >= 0 &&
                   FormalCyclesCompleted >= 0 &&
                   CapturedUtcTicks > 0;
        }
    }

    public sealed class SystemDesiredState
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public long Revision { get; set; }
        public SystemTerminalState State { get; set; }
        public string Reason { get; set; } = string.Empty;
        public long UpdatedUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   RecoveryProtocolV7.IsGuid(SessionId) &&
                   RecoveryProtocolV7.IsGuid(RunId) && RunEpoch > 0 &&
                   Revision > 0 && State != SystemTerminalState.Unknown &&
                   RecoveryProtocolV7.HasText(Reason) && UpdatedUtcTicks > 0;
        }
    }

    public sealed class ResourceHealthLease
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public RecoveryIdentity Identity { get; set; }
        public string LeaseId { get; set; } = string.Empty;
        public ResourceKind ResourceKind { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public long ResourceGeneration { get; set; }
        public long IssuedUtcTicks { get; set; }
        public long ExpiresUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   Identity?.IsStructurallyValid() == true &&
                   RecoveryProtocolV7.IsGuid(LeaseId) &&
                   ResourceKind != ResourceKind.Unknown &&
                   RecoveryProtocolV7.HasText(ResourceId) &&
                   ResourceGeneration > 0 && IssuedUtcTicks > 0 &&
                   ExpiresUtcTicks > IssuedUtcTicks;
        }
    }

    public sealed class EngineStateSnapshot
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string EngineInstanceId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public long Revision { get; set; }
        public long PulseSequence { get; set; }
        public SystemTerminalState State { get; set; }
        public string RecoveryIncidentId { get; set; } = string.Empty;
        public string RecoveryOwnerId { get; set; } = string.Empty;
        public bool HardwareInitialized { get; set; }
        public bool OutputsEnergized { get; set; }
        public int QualificationCyclesCompleted { get; set; }
        public int FormalCyclesSinceRecovery { get; set; }
        public long StableSinceUtcTicks { get; set; }
        public long CapturedUtcTicks { get; set; }
        public string[] IsolatedResources { get; set; } = Array.Empty<string>();

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   RecoveryProtocolV7.IsGuid(EngineInstanceId) &&
                   RecoveryProtocolV7.IsGuid(SessionId) &&
                   RecoveryProtocolV7.IsGuid(RunId) && RunEpoch > 0 &&
                   Revision > 0 && PulseSequence > 0 &&
                   State != SystemTerminalState.Unknown && CapturedUtcTicks > 0 &&
                   QualificationCyclesCompleted >= 0 &&
                   FormalCyclesSinceRecovery >= 0 &&
                   (string.IsNullOrEmpty(RecoveryIncidentId) ||
                    RecoveryProtocolV7.IsGuid(RecoveryIncidentId)) &&
                   (string.IsNullOrEmpty(RecoveryOwnerId) ||
                    RecoveryProtocolV7.IsGuid(RecoveryOwnerId));
        }
    }

    public sealed class OperatorCommand
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string CommandId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public long BaseRevision { get; set; }
        public string PayloadSha256 { get; set; } = string.Empty;
        public OperatorCommandKind Kind { get; set; }
        public long IssuedUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   RecoveryProtocolV7.IsGuid(CommandId) &&
                   RecoveryProtocolV7.IsGuid(SessionId) &&
                   RecoveryProtocolV7.IsGuid(RunId) && RunEpoch > 0 &&
                   BaseRevision >= 0 && RecoveryFailureReceipt.IsSha256(PayloadSha256) &&
                   Kind != OperatorCommandKind.None && IssuedUtcTicks > 0;
        }
    }
}
