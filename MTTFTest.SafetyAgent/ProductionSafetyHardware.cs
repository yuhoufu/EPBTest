using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using MTTFTest.SafetyHardware;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.SafetyAgent
{
    internal interface ISafetyPowerEvidence
    {
        SafetyPowerOffReport LastPowerOffReport { get; }
    }

    internal sealed class ProductionSafetyHardwareFactory : ISafetyHardwareFactory
    {
        public ISafetyHardware Create(string configDirectory, SafetyRuntimeSnapshot runtime) =>
            new ProductionSafetyHardware(configDirectory, runtime);
    }

    internal sealed class ProductionSafetyHardware : ISafetyHardware, ISafetyPowerEvidence
    {
        private readonly SafetyRuntimeSnapshot _runtime;
        private readonly SafetyHardwareConfiguration _configuration;
        private readonly SafetyPhysicalChannel[] _feedbackChannels;

        public SafetyPowerOffReport LastPowerOffReport { get; private set; }

        internal ProductionSafetyHardware(string configDirectory, SafetyRuntimeSnapshot runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _runtime.Validate();
            _configuration = SafetyHardwareConfiguration.Load(
                configDirectory,
                _runtime.PressureChannels);
            _feedbackChannels = SafetyPhysicalChannel.Load(configDirectory, _runtime.PressureChannels);
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
            LastPowerOffReport = new SafetyPowerOutputController().ConfirmAllOffDetailed(
                _configuration.PowerSupplies,
                _runtime.ReleaseTimeoutMs);
            return LastPowerOffReport.AllObservedOff;
        }

        public bool ConfirmCurrentAndPressureSafe()
        {
            var commandsCompletedMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            var limits = _feedbackChannels.Select(channel => channel.IsCurrent
                ? _runtime.SafeCurrentThresholdA
                : _runtime.ReleaseSafePressureBar[Array.IndexOf(_runtime.PressureChannels, channel.Name)]).ToArray();
            var window = new SafetyFeedbackWindow(_feedbackChannels.Select(channel => channel.Name).ToArray(),
                _feedbackChannels.Select(channel => channel.IsCurrent ? 0.0 : -double.MaxValue).ToArray(),
                limits, commandsCompletedMs,
                _runtime.PressureSampleMaxAgeMs, Math.Max(100, _runtime.ReleaseStableMs));
            using (var probe = new SafetyPhysicalFeedbackProbe(
                       _feedbackChannels,
                       _runtime.SampleRateHz,
                       _runtime.SamplesPerChannel,
                       Math.Min(_runtime.PressureSampleMaxAgeMs, _runtime.ReleaseTimeoutMs)))
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds <= _runtime.ReleaseTimeoutMs)
                {
                    var frame = probe.Read();
                    if (deadline.ElapsedMilliseconds > _runtime.ReleaseTimeoutMs) return false;
                    if (window.Observe(frame, Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency))
                        return true;
                    Thread.Sleep(10);
                }
            }
            return false;
        }

        public void Dispose()
        {
        }
    }
}
