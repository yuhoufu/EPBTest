using System;

namespace MTTFTest.Watchdog.Protocol
{
    // Display evidence only; neither this object nor a successful output receipt
    // grants permission to energize or substitutes for an independent safety proof.
    public sealed class EngineUiPressureMaintenance
    {
        public RecoveryIdentity Identity { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string EngineInstanceId { get; set; } = string.Empty;
        public int HydraulicId { get; set; }
        public long CapturedUtcTicks { get; set; }
        public long LeaseRevision { get; set; }
        public bool AuthorityValid { get; set; }
        public bool Prepared { get; set; }
        public bool OutputActive { get; set; }
        public bool MayBeEnergized { get; set; }
        public long OutputGeneration { get; set; }
        public string OutputCommandId { get; set; } = string.Empty;
        public double CommandPressureBar { get; set; }
        public double Voltage { get; set; }
        public UiMeasurement Pressure { get; set; } = new UiMeasurement();
        public int PressureSampleMaximumAgeMilliseconds { get; set; }
        public bool IsStructurallyValid() => Identity?.IsStructurallyValid() == true && Identity.ResourceScope == "System" &&
            RecoveryProtocolV7.IsGuid(OwnerId) && RecoveryProtocolV7.IsGuid(EngineInstanceId) && HydraulicId >= 1 && HydraulicId <= 2 &&
            EngineUiContract.IsUtcTicks(CapturedUtcTicks) && LeaseRevision >= 0 && OutputGeneration >= 0 && Pressure != null &&
            PressureSampleMaximumAgeMilliseconds > 0 && PressureSampleMaximumAgeMilliseconds <= 3000 &&
            EngineUiContract.IsFinite(CommandPressureBar) && EngineUiContract.IsFinite(Voltage) &&
            CommandPressureBar >= 0 && CommandPressureBar <= PressureMaintenanceProtocol.MaximumPressureBar && Voltage >= -10 && Voltage <= 10 &&
            (!OutputActive || Prepared && AuthorityValid && MayBeEnergized && RecoveryProtocolV7.IsGuid(OutputCommandId)) &&
            (string.IsNullOrEmpty(OutputCommandId) || RecoveryProtocolV7.IsGuid(OutputCommandId));
        public bool Binds(PressureMaintenanceLease lease) => IsStructurallyValid() && lease?.Binds(Identity, OwnerId) == true &&
            EngineInstanceId == lease.EngineInstanceId && HydraulicId == lease.HydraulicId && LeaseRevision <= lease.Revision;
    }
}
