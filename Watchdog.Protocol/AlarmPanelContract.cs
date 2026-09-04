using System;
using System.Globalization;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // These operations affect annunciation only, never actuator authority or isolation.
    public sealed class AlarmPanelCommand
    {
        public string PanelInstanceId { get; set; } = string.Empty;
        public long BaseRevision { get; set; }
        public bool BuzzerEnabled { get; set; }
        public string ResourceScope { get; set; } = "AlarmPanel";

        public AlarmPanelCommand Clone() => (AlarmPanelCommand)MemberwiseClone();
        public bool IsStructurallyValid() => RecoveryProtocolV7.IsGuid(PanelInstanceId) && BaseRevision > 0 && ResourceScope == "AlarmPanel";
        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256(string.Join("|", "AlarmPanel/v1",
            PanelInstanceId, BaseRevision.ToString(CultureInfo.InvariantCulture), BuzzerEnabled ? "1" : "0", ResourceScope));
        public static bool IsPanelOperation(OperatorCommandKind kind) =>
            kind == OperatorCommandKind.AcknowledgeAlarms || kind == OperatorCommandKind.SetBuzzerEnabled;
    }

    public sealed class AlarmPanelStatus
    {
        public bool Available { get; set; }
        public string PanelInstanceId { get; set; } = string.Empty;
        public long Revision { get; set; }
        public bool TransportOpen { get; set; }
        public bool BuzzerEnabled { get; set; }
        public int[] ActiveChannels { get; set; } = Array.Empty<int>();
        public string Detail { get; set; } = "报警板未就绪";

        public bool IsStructurallyValid() => ActiveChannels != null && ActiveChannels.Length <= 12 &&
            ActiveChannels.All(c => c >= 1 && c <= 12) && ActiveChannels.Distinct().Count() == ActiveChannels.Length &&
            (!Available || RecoveryProtocolV7.IsGuid(PanelInstanceId) && Revision > 0);
    }

    public sealed class OperatorExecutionReceipt
    {
        public string CommandId { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public bool Succeeded { get; set; }
        public string Detail { get; set; } = string.Empty;
        public long CompletedUtcTicks { get; set; }

        public bool Matches(OperatorCommand command) => command != null && CommandId == command.CommandId &&
            Fingerprint == OperatorCommandAdmission.GetFingerprint(command) && EngineUiContract.IsUtcTicks(CompletedUtcTicks);
    }
}
