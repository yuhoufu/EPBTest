using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using Controller;
using Controller.Alarm;
using IO.NI;
using MTEmbTest;
using IAppLogger = Config.IAppLogger;

namespace AdaptiveControlTests
{
    internal static class MonitorHardwareSmokeTests
    {
        internal static int Run(string isolatedRoot)
        {
            var root = Path.GetFullPath(isolatedRoot).TrimEnd('\\') + "\\";
            if (!string.Equals(root, Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run only from a copied, isolated test directory");
            var config = ConfigLoader.LoadAll(RuntimeConfigPaths.Directory, Config.NullLogger.Instance);
            if (!Path.GetFullPath(config.Test.StoreDir).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Project data must remain inside the isolated test directory");
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var hardware = new SmokeHardware())
                    using (var monitor = new FrmEpbMainMonitor(new DaqRuntimeSettings(2000, 20), hardware))
                    using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
                    {
                        var start = DateTime.UtcNow;
                        var closing = false;
                        timer.Tick += (sender, args) =>
                        {
                            if (closing) return;
                            if (monitor.MonitorLifecycle == EpbMonitorLifecycle.InitializationFailed)
                            {
                                failure = new InvalidOperationException("Monitor initialization failed; inspect isolated project logs");
                                closing = true;
                                monitor.Close();
                            }
                            else if (monitor.MonitorLifecycle == EpbMonitorLifecycle.Idle && hardware.SamplesRead >= 20)
                            {
                                Console.WriteLine("MONITOR_INITIALIZED RealWindow=true RealAcquirer=true RealManager=true SamplesRead=" + hardware.SamplesRead);
                                closing = true;
                                monitor.Close();
                            }
                            else if ((DateTime.UtcNow - start).TotalSeconds > 30)
                            {
                                failure = new TimeoutException("Monitor initialization deadline exceeded");
                                closing = true;
                                monitor.Close();
                            }
                        };
                        timer.Start();
                        Application.Run(monitor);
                        if (!closing || hardware.SamplesRead < 20)
                            throw new InvalidOperationException("Window closed before real acquisition was observed");
                    }
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(45000)) throw new TimeoutException("Monitor UI thread did not terminate");
            if (failure != null) throw failure;
            Console.WriteLine("PASS monitor initialization and idle close; trial recovery NOT tested");
            return 0;
        }

        private sealed class SmokeHardware : IMonitorHardwareFactory, IDisposable
        {
            private readonly List<IDisposable> _resources = new List<IDisposable>();
            private int _samplesRead;
            internal int SamplesRead => Volatile.Read(ref _samplesRead);
            private T Own<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
            public DoController CreateDigitalOutput(DoConfig config, IAppLogger logger) =>
                Own(new DoController(config, logger, name => new DigitalOutput()));
            public AoController CreateAnalogOutput(AoConfig config, IAppLogger logger) =>
                Own(new AoController(config, logger, device => new AnalogOutput()));
            public TwoDeviceAiAcquirer CreateAcquirer(AiConfigDetail config, DaqRuntimeSettings settings, IAppLogger logger) =>
                Own(new TwoDeviceAiAcquirer(config, settings.SampleRateHz, settings.SamplesPerChannel, 10, logger,
                    (name, channels, min, max, terminal) => new AnalogInput(channels.Length,
                        settings.SampleRateHz, () => Interlocked.Increment(ref _samplesRead))));
            public IPowerSupplyCoordinator CreatePowerSupply(GlobalConfig config, IAppLogger logger) =>
                Own(new PowerSupplyCoordinator(PowerSupplyConfigLoader.Load(RuntimeConfigPaths.GetPath("PowerSupplyConfig.xml")),
                    config.Test.Groups, logger, device => new PowerSupplyCoordinatorTests.FakePswClient(device.Id)));
            public IDaqHardwareProbe CreateDaqProbe() => new Probe();
            public AlarmManager CreateAlarm(AlarmConfig config, IAppLogger logger) => null;
            public void Dispose()
            {
                foreach (var resource in _resources.AsEnumerable().Reverse()) resource.Dispose();
            }
        }

        private sealed class DigitalOutput : IDigitalOutputSession
        {
            public bool IsReady { get; private set; }
            public void AddLine(string physicalLine, string logicalName) { }
            public void Verify() { IsReady = true; }
            public void Write(bool[] states) { if (!IsReady) throw new InvalidOperationException("DO not initialized"); }
            public void Dispose() { IsReady = false; }
        }
        private sealed class AnalogOutput : IAoVoltageOutput
        {
            public void Write(double voltage) { }
            public void Dispose() { }
        }
        private sealed class Probe : IDaqHardwareProbe
        {
            public Task<DaqHardwareProbeResult> ProbeAsync(string device, CancellationToken token) =>
                Task.FromResult(new DaqHardwareProbeResult { Device = device, EnumerationSucceeded = true,
                    DevicePresent = true, SelfTestAttempted = true, SelfTestSucceeded = true, TimestampUtc = DateTime.UtcNow });
        }
        private sealed class AnalogInput : IAiInputSession
        {
            private readonly int _channels;
            private readonly Action _read;
            private int _stopped;
            internal AnalogInput(int channels, double rate, Action read) { _channels = channels; SampleClockRate = rate; _read = read; }
            public double SampleClockRate { get; }
            public void Start() { Volatile.Write(ref _stopped, 0); }
            public void Stop() { Volatile.Write(ref _stopped, 1); }
            public void Dispose() { Stop(); }
            public void BeginReadMultiSample(int samples, AsyncCallback callback, object state)
            {
                if (Volatile.Read(ref _stopped) != 0) throw new ObjectDisposedException(nameof(AnalogInput));
                var result = new TaskCompletionSource<double[,]>(state);
                Task.Delay(Math.Max(1, (int)Math.Round(samples * 1000.0 / SampleClockRate))).ContinueWith(_ =>
                {
                    result.SetResult(new double[_channels, samples]);
                    callback(result.Task);
                }, TaskScheduler.Default);
            }
            public double[,] EndReadMultiSample(IAsyncResult result) { _read(); return ((Task<double[,]>)result).GetAwaiter().GetResult(); }
        }
    }
}
