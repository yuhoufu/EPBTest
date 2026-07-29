using PowerSupplyDebugger.Models;
using PowerSupplyDebugger.Services;

namespace PowerSupplyDebugger.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData("GW-INSTEK,PSW-30-72,SN,1.70", "PSW 30-72", 30, 72)]
    [InlineData("GW-INSTEK,PSW 30-108,SN,1.61", "PSW 30-108", 30, 108)]
    public void IdentityRecognizesSupportedModels(string identity, string model, double volts, double amps)
    {
        Assert.True(PswProtocol.IsVerifiedIdentity(identity));
        var capabilities = PswCapabilities.FromIdentity(identity);
        Assert.Equal(model, capabilities.Model);
        Assert.Equal(volts, capabilities.MaxVoltage);
        Assert.Equal(amps, capabilities.MaxCurrent);
    }

    [Fact]
    public void IdentityRejectsUnknownEquipment()
    {
        Assert.False(PswProtocol.IsVerifiedIdentity("OTHER,PSW-30-72,SN,1.70"));
        Assert.False(PswProtocol.IsVerifiedIdentity("GW-INSTEK,PSM-2010,SN,1.70"));
    }

    [Fact]
    public void MeasurementAndStatusBitsAreParsed()
    {
        var measurement = PswProtocol.ParseMeasurement("12.5,3.2,40");
        Assert.Equal(12.5, measurement.Voltage);
        Assert.Equal(3.2, measurement.Current);
        Assert.Equal(40, measurement.Power);

        var snapshot = new PowerSupplySnapshot
        {
            OperationStatus = 256 | 1024,
            QuestionableStatus = 256 | 512 | 4096
        };
        Assert.True(snapshot.IsConstantVoltage);
        Assert.True(snapshot.IsConstantCurrent);
        Assert.True(snapshot.IsVoltageLimited);
        Assert.True(snapshot.IsCurrentLimited);
        Assert.True(snapshot.IsPowerLimited);
    }

    [Fact]
    public void SetpointValidationRejectsOutOfRangeAndInvalidNumbers()
    {
        PswProtocol.ValidateSetpoint(30, 30, "VSET");
        Assert.Throws<ArgumentOutOfRangeException>(() => PswProtocol.ValidateSetpoint(50, 30, "VSET"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PswProtocol.ValidateSetpoint(-1, 30, "VSET"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PswProtocol.ValidateSetpoint(double.NaN, 30, "VSET"));
    }

    [Fact]
    public void InvalidProtectionRangeDisablesWrites()
    {
        var capabilities = PswCapabilities.FromIdentity("GW-INSTEK,PSW-30-72,SN,1.70")
            .WithProtectionRanges(33, 1, null, 80);
        Assert.False(capabilities.CanWriteOvp);
        Assert.False(capabilities.CanWriteOcp);
    }
}
