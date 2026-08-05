using System;

namespace Config
{
    /// <summary>
    /// 故障处置分类。只有 HardwareConfirmed 允许进入通道硬件报警、灯和蜂鸣器链路。
    /// </summary>
    public enum FaultClassification
    {
        SoftwareTransient = 0,
        SystemFault = 1,
        HardwareConfirmed = 2
    }

    /// <summary>可审计的独立硬件证据。</summary>
    public sealed class HardwareEvidence
    {
        public string Source { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public bool Confirmed { get; set; }
    }
}
