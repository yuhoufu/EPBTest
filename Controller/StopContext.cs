using System;

namespace Controller
{
    public enum StopSource
    {
        ManualUi,
        ApplicationClosing,
        AlarmInterlock,
        SystemFault,
        StartupRollback,
        ProgramExit,
        UnknownLegacy
    }

    public sealed class StopContext
    {
        public StopSource Source { get; set; } = StopSource.UnknownLegacy;
        public string Reason { get; set; }
        public string Initiator { get; set; }
        public string CorrelationId { get; set; }
        public FaultScope? FaultScope { get; set; }
        public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;

        public static StopContext Legacy(string caller)
        {
            var manual = !string.IsNullOrWhiteSpace(caller) &&
                         caller.IndexOf("BtnStop", StringComparison.OrdinalIgnoreCase) >= 0;
            return new StopContext
            {
                Source = manual ? StopSource.ManualUi : StopSource.UnknownLegacy,
                Reason = manual ? "现场操作员点击停止" : "旧版无上下文停止入口",
                Initiator = string.IsNullOrWhiteSpace(caller) ? "Unknown" : caller,
                CorrelationId = Guid.NewGuid().ToString("N")
            };
        }

        public string ToLogText()
        {
            return $"Source={Source}; Reason={Reason ?? "-"}; Initiator={Initiator ?? "-"}; " +
                   $"CorrelationId={CorrelationId ?? "-"}; FaultScope={FaultScope?.ToString() ?? "-"}; " +
                   $"RequestedUtc={RequestedUtc:O}";
        }
    }
}
