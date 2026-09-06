namespace PowerSupplyDebugger.Models;

public sealed record PowerSupplySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool IsConnected { get; init; }
    public string Identity { get; init; } = string.Empty;
    public bool IsVerifiedPsw { get; init; }
    public PswCapabilities Capabilities { get; init; } = new();
    public bool OutputEnabled { get; init; }
    public double SetVoltage { get; init; }
    public double SetCurrent { get; init; }
    public double? Ovp { get; init; }
    public double? Ocp { get; init; }
    public double MeasuredVoltage { get; init; }
    public double MeasuredCurrent { get; init; }
    public double MeasuredPower { get; init; }
    public bool ProtectionTripped { get; init; }
    public int OperationStatus { get; init; }
    public int QuestionableStatus { get; init; }
    public bool IsConstantVoltage => (OperationStatus & 256) != 0;
    public bool IsConstantCurrent => (OperationStatus & 1024) != 0;
    public bool IsVoltageLimited => (QuestionableStatus & 256) != 0;
    public bool IsCurrentLimited => (QuestionableStatus & 512) != 0;
    public bool IsPowerLimited => (QuestionableStatus & 4096) != 0;
    public string ControlState { get; init; } = "未查询";
    public string LastError { get; init; } = string.Empty;
}
