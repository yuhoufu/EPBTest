using System;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class ManualChannelState
    {
        public string EngineInstanceId { get; set; } = string.Empty;
        public int PauseMask { get; set; }
        public int ResumeMask { get; set; }
        public bool IsStructurallyValid() => RecoveryProtocolV7.IsGuid(EngineInstanceId) &&
            PauseMask >= 0 && PauseMask <= 4095 && ResumeMask >= 0 && ResumeMask <= 4095 && (PauseMask & ResumeMask) == 0;
    }

    // A typed per-channel/operator payload. Pause continuation authority is
    // process-local; qualification retry carries no old pause Owner and must
    // be admitted by the Recovery Kernel as a new safety transaction.
    public sealed class ManualBatchCommand
    {
        public string EngineInstanceId { get; set; } = string.Empty;
        public string PauseIncidentId { get; set; } = string.Empty;
        public string PauseOwnerId { get; set; } = string.Empty;
        public int Channel { get; set; }
        public static bool IsQualificationRetry(OperatorCommandKind kind) => kind == OperatorCommandKind.RetryQualification;
        public static bool IsChannelOperation(OperatorCommandKind kind) => kind == OperatorCommandKind.PauseChannel ||
            kind == OperatorCommandKind.ResumeChannel || IsQualificationRetry(kind);
        public static bool IsPause(OperatorCommandKind kind) => kind == OperatorCommandKind.Pause || kind == OperatorCommandKind.PauseChannel;
        public static bool IsOperation(OperatorCommandKind kind) => kind == OperatorCommandKind.Pause || kind == OperatorCommandKind.Resume || IsChannelOperation(kind);
        public bool IsStructurallyValid(OperatorCommandKind kind) => RecoveryProtocolV7.IsGuid(EngineInstanceId) &&
            IsOperation(kind) && (IsChannelOperation(kind) ? Channel >= 1 && Channel <= 12 : Channel == 0) &&
            (IsQualificationRetry(kind)
                ? string.IsNullOrEmpty(PauseIncidentId) && string.IsNullOrEmpty(PauseOwnerId)
                : IsPause(kind) && string.IsNullOrEmpty(PauseIncidentId) && string.IsNullOrEmpty(PauseOwnerId) ||
                  RecoveryProtocolV7.IsGuid(PauseIncidentId) && RecoveryProtocolV7.IsGuid(PauseOwnerId));
        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256(string.Join("|", EngineInstanceId, PauseIncidentId, PauseOwnerId,
            Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public ManualBatchCommand Clone() => (ManualBatchCommand)MemberwiseClone();
    }
}
