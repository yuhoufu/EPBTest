using Config;
using Controller;
using Controller.Alarm;
using IO.NI;
using IAppLogger = Config.IAppLogger;

namespace MTEmbTest
{
    // Instance-scoped composition: a test monitor must supply every physical boundary.
    // There is no environment variable or persisted configuration that selects this factory.
    internal interface IMonitorHardwareFactory
    {
        DoController CreateDigitalOutput(DoConfig config, IAppLogger logger);
        AoController CreateAnalogOutput(AoConfig config, IAppLogger logger);
        TwoDeviceAiAcquirer CreateAcquirer(AiConfigDetail config, DaqRuntimeSettings settings, IAppLogger logger);
        IPowerSupplyCoordinator CreatePowerSupply(GlobalConfig config, IAppLogger logger);
        IDaqHardwareProbe CreateDaqProbe();
        AlarmManager CreateAlarm(AlarmConfig config, IAppLogger logger);
    }
}
