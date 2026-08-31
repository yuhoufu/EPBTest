using System;
using System.Diagnostics;
using System.Threading;
using MTTFTest.SafetyHardware;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.SafetyAgent
{
    internal sealed class ProductionSafetyHardwareFactory : ISafetyHardwareFactory
    {
        public ISafetyHardware Create(string configDirectory, SafetyRuntimeSnapshot runtime) =>
            new ProductionSafetyHardware(configDirectory, runtime);
    }

    internal sealed class ProductionSafetyHardware : ISafetyHardware
    {
        private readonly SafetyRuntimeSnapshot _runtime;
        private readonly SafetyHardwareConfiguration _configuration;

        internal ProductionSafetyHardware(string configDirectory, SafetyRuntimeSnapshot runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _runtime.Validate();
            _configuration = SafetyHardwareConfiguration.Load(
                configDirectory,
                _runtime.PressureChannels);
        }

        public bool ConfirmDoOff()
        {
            using (var controller = new SafetyDigitalOutputController())
                return controller.ConfirmAllOff(_configuration.DoPhysicalLines);
        }

        public bool ConfirmAoZero()
        {
            using (var controller = new SafetyAnalogOutputController())
                return controller.ConfirmLiteralZero(_configuration.AoPhysicalChannels);
        }

        public bool ConfirmPowerOff()
        {
            return new SafetyPowerOutputController().ConfirmAllOff(
                _configuration.PowerSupplies,
                _runtime.ReleaseTimeoutMs);
        }

        public bool ConfirmPressureSafe()
        {
            using (var probe = new SafetyPressureProbe(
                       _configuration.PressureChannels,
                       _runtime.SampleRateHz,
                       _runtime.SamplesPerChannel))
            {
                foreach (var channel in _configuration.PressureChannels)
                {
                    var runtimeIndex = Array.FindIndex(
                        _runtime.PressureChannels,
                        value => string.Equals(
                            value,
                            channel.Name,
                            StringComparison.OrdinalIgnoreCase));
                    if (runtimeIndex < 0)
                        throw new SafetyHardwareConfigurationException(
                            "PressureChannelBindingMissing");

                    var deadline = Stopwatch.StartNew();
                    long stableSince = 0;
                    while (deadline.ElapsedMilliseconds <= _runtime.ReleaseTimeoutMs)
                    {
                        var sample = probe.Read(channel.HydraulicId);
                        var now = Stopwatch.GetTimestamp();
                        var safe = sample.IsFinite &&
                                   sample.AgeMs <= _runtime.PressureSampleMaxAgeMs &&
                                   sample.ValueBar <=
                                   _runtime.ReleaseSafePressureBar[runtimeIndex];
                        if (safe)
                        {
                            if (stableSince == 0) stableSince = now;
                            if ((now - stableSince) * 1000.0 / Stopwatch.Frequency >=
                                _runtime.ReleaseStableMs)
                                break;
                        }
                        else
                        {
                            stableSince = 0;
                        }
                        Thread.Sleep(10);
                    }

                    if (stableSince == 0 ||
                        (Stopwatch.GetTimestamp() - stableSince) * 1000.0 /
                        Stopwatch.Frequency < _runtime.ReleaseStableMs)
                        return false;
                }
            }
            return true;
        }

        public void Dispose()
        {
        }
    }
}
