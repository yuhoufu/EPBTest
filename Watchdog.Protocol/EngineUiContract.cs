using System;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // Display contracts carry facts, never hardware objects or execution permits.
    public static class EngineUiContract
    {
        public const int Version = 1;
        public const int MaximumLogs = 200;
        public const int MaximumCurvePoints = 4096;
        public const string Monitor = "Monitor";
        public const string Configuration = "Configuration";
        public const string TestConfiguration = "TestConfiguration";
        public const string DaqConfiguration = "DaqConfigurationV1";
        public const string AoCalibration = "AoCalibrationV1";
        public const string PressureMaintenance = "PressureMaintenanceV1";
        public const string ProjectSwitch = "ProjectSwitchV1";
        public const string ProjectCreation = "ProjectCreationV1";
        public const string ProjectReset = "ProjectResetV1";
        public const string Calibration = "Calibration";
        public const string AlarmCommands = "AlarmCommands";
        public const string ChannelCommands = "ChannelCommands";
        public const string ManualBatchControl = "ManualBatchControl";
        public const string ManualChannelControl = "ManualChannelControl";
        public const string ChannelQualificationRecovery = "ChannelQualificationRecoveryV1";

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static bool IsUtcTicks(long value) => value > 0 && value <= DateTime.MaxValue.Ticks;
    }

    public sealed class UiMeasurement
    {
        public bool Valid { get; set; }
        public double Value { get; set; }
        public long CapturedUtcTicks { get; set; }
        public string Reason { get; set; } = "未就绪";

        public bool IsUsable(long nowUtcTicks) => Valid && EngineUiContract.IsFinite(Value) &&
            CapturedUtcTicks > 0 && CapturedUtcTicks <= nowUtcTicks &&
            nowUtcTicks - CapturedUtcTicks <= TimeSpan.FromSeconds(3).Ticks;
    }

    public sealed class EngineUiChannel
    {
        public int Channel { get; set; }
        public bool Selected { get; set; }
        public bool Isolated { get; set; }
        public bool Running { get; set; }
        public string State { get; set; } = "未就绪";
        public string Reason { get; set; } = string.Empty;
        public bool CountsValid { get; set; }
        public long FormalCycles { get; set; }
        public long MechanicalCycles { get; set; }
        public long RemainingCycles { get; set; }
        public long RunTimeTicks { get; set; }
        public UiMeasurement Current { get; set; } = new UiMeasurement();
    }

    public sealed class EngineUiPowerSupply
    {
        public int Group { get; set; }
        public bool Valid { get; set; }
        public bool Connected { get; set; }
        public bool OutputEnabled { get; set; }
        public string Mode { get; set; } = "未就绪";
        public double Voltage { get; set; }
        public double Current { get; set; }
        public long CapturedUtcTicks { get; set; }
    }

    public sealed class EngineUiLogEntry
    {
        public long Sequence { get; set; }
        public long CapturedUtcTicks { get; set; }
        public string Level { get; set; } = "INFO";
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public sealed class EngineUiCurve
    {
        public string Key { get; set; } = string.Empty;
        public long[] UtcTicks { get; set; } = Array.Empty<long>();
        public double[] Values { get; set; } = Array.Empty<double>();
        public bool[] BreakBefore { get; set; } = Array.Empty<bool>();
    }

    public sealed class EngineUiProjectSelection
    {
        public long Revision { get; set; }
        public string ConfigurationPath { get; set; } = string.Empty;
        public string ProjectFileSha256 { get; set; } = string.Empty;
        public string LastResetArchivePath { get; set; } = string.Empty;
        public EngineUiProjectSelection Clone() => (EngineUiProjectSelection)MemberwiseClone();
        public bool IsStructurallyValid() => Revision > 0 && ProjectSwitchPlan.IsProjectConfigurationPath(ConfigurationPath) &&
            ProjectSwitchPlan.IsSha256(ProjectFileSha256) && LastResetArchivePath?.Length <= 2048;
    }

    public sealed class EngineUiSnapshot
    {
        public int ContractVersion { get; set; } = EngineUiContract.Version;
        public string[] Capabilities { get; set; } = Array.Empty<string>();
        public EngineStateSnapshot Engine { get; set; }
        public EngineUiKernelState Kernel { get; set; } = new EngineUiKernelState();
        public long Sequence { get; set; }
        public long CapturedUtcTicks { get; set; }
        public string TestName { get; set; } = string.Empty;
        public double PeriodSeconds { get; set; }
        public long TargetCycles { get; set; }
        public bool SharedTargetCycles { get; set; }
        public long ConfigurationRevision { get; set; }
        public string ConfigurationSha256 { get; set; } = string.Empty;
        public EngineTestConfiguration TestConfiguration { get; set; }
        public EngineDaqConfiguration DaqConfiguration { get; set; }
        public long DaqConfigurationRevision { get; set; }
        public string DaqConfigurationSha256 { get; set; } = string.Empty;
        public EngineAoConfiguration AoConfiguration { get; set; }
        public EngineUiPressureMaintenance PressureMaintenance { get; set; }
        public long AoConfigurationRevision { get; set; }
        public string AoConfigurationSha256 { get; set; } = string.Empty;
        public EngineUiProjectSelection ProjectSelection { get; set; }
        public string StatusDetail { get; set; } = string.Empty;
        public EngineUiChannel[] Channels { get; set; } = Array.Empty<EngineUiChannel>();
        public EngineUiPowerSupply[] PowerSupplies { get; set; } = Array.Empty<EngineUiPowerSupply>();
        public UiMeasurement[] Pressures { get; set; } = Array.Empty<UiMeasurement>();
        public UiMeasurement Force { get; set; } = new UiMeasurement();
        public EngineUiCurve[] Curves { get; set; } = Array.Empty<EngineUiCurve>();
        public int CurveWindowSeconds { get; set; } = 60;
        public EngineUiLogEntry[] Logs { get; set; } = Array.Empty<EngineUiLogEntry>();
        public bool LogsTruncated { get; set; }
        public bool BatchPauseAvailable { get; set; }
        public bool BatchResumeAvailable { get; set; }
        public AlarmPanelStatus AlarmPanel { get; set; } = new AlarmPanelStatus();

        public bool IsStructurallyValid()
        {
            return ContractVersion == EngineUiContract.Version && Engine?.IsStructurallyValid() == true && Kernel?.IsStructurallyValid() == true &&
                AlarmPanel?.IsStructurallyValid() == true &&
                (ProjectSelection == null || ProjectSelection.IsStructurallyValid()) &&
                Sequence > 0 && EngineUiContract.IsUtcTicks(CapturedUtcTicks) &&
                (TestConfiguration == null || TestConfiguration.IsStructurallyValid() && ConfigurationRevision >= 0 &&
                    ConfigurationSha256 == TestConfiguration.ComputeSha256()) &&
                (DaqConfiguration == null || DaqConfiguration.IsStructurallyValid() && DaqConfigurationRevision >= 0 &&
                    DaqConfigurationSha256 == DaqConfiguration.ComputeSha256()) &&
                (AoConfiguration == null || AoConfiguration.IsStructurallyValid() && AoConfigurationRevision >= 0 &&
                    AoConfigurationSha256 == AoConfiguration.ComputeSha256()) &&
                (PressureMaintenance == null || PressureMaintenance.IsStructurallyValid() &&
                    PressureMaintenance.EngineInstanceId == Engine.EngineInstanceId && PressureMaintenance.Identity.SessionId == Engine.SessionId &&
                    PressureMaintenance.Identity.RunId == Engine.RunId && PressureMaintenance.Identity.RunEpoch == Engine.RunEpoch) &&
                Enum.IsDefined(typeof(SystemTerminalState), Engine.State) &&
                Capabilities != null && Capabilities.Length <= 32 && Capabilities.All(c => !string.IsNullOrWhiteSpace(c)) &&
                (!Capabilities.Contains(EngineUiContract.DaqConfiguration) || DaqConfiguration != null) &&
                (!Capabilities.Contains(EngineUiContract.AoCalibration) || AoConfiguration != null) &&
                (!Capabilities.Contains(EngineUiContract.ProjectSwitch) || ProjectSelection != null && TestConfiguration != null) &&
                (!Capabilities.Contains(EngineUiContract.ProjectCreation) || Capabilities.Contains(EngineUiContract.ProjectSwitch)) &&
                (!Capabilities.Contains(EngineUiContract.ProjectReset) || Capabilities.Contains(EngineUiContract.ProjectSwitch)) &&
                Channels?.Length == 12 && Channels.All(c => c != null && c.Channel >= 1 && c.Channel <= 12 &&
                    c.FormalCycles >= 0 && c.MechanicalCycles >= 0 && c.RemainingCycles >= 0 && c.RunTimeTicks >= 0 &&
                    c.Current != null) && Channels.Select(c => c.Channel).Distinct().Count() == 12 &&
                PowerSupplies?.Length == 4 && PowerSupplies.All(p => p != null && p.Group >= 1 && p.Group <= 4 &&
                    (!p.Valid || EngineUiContract.IsUtcTicks(p.CapturedUtcTicks) &&
                        EngineUiContract.IsFinite(p.Voltage) && EngineUiContract.IsFinite(p.Current))) &&
                PowerSupplies.Select(p => p.Group).Distinct().Count() == 4 &&
                Pressures?.Length == 2 && Pressures.All(p => p != null) && Force != null &&
                CurveWindowSeconds >= 1 && CurveWindowSeconds <= 3600 &&
                Curves != null && Curves.Length <= 32 && Curves.All(c => c != null && !string.IsNullOrWhiteSpace(c.Key) && c.UtcTicks != null &&
                    c.Values != null && c.Values.Length == c.UtcTicks.Length &&
                    c.BreakBefore != null && (c.BreakBefore.Length == 0 || c.BreakBefore.Length == c.Values.Length) &&
                    c.Values.Length <= EngineUiContract.MaximumCurvePoints && c.Values.All(EngineUiContract.IsFinite) &&
                    c.UtcTicks.All(EngineUiContract.IsUtcTicks) &&
                    c.UtcTicks.Zip(c.UtcTicks.Skip(1), (previous, next) => next > previous).All(ordered => ordered)) &&
                Curves.Select(c => c.Key).Distinct().Count() == Curves.Length &&
                Logs != null && Logs.Length <= EngineUiContract.MaximumLogs && Logs.All(l => l != null &&
                    EngineUiContract.IsUtcTicks(l.CapturedUtcTicks) && l.Sequence > 0 && l.Message?.Length <= 2048);
        }
    }
}
