using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
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
        internal static int RunPlantUnit()
        {
            long tick = Stopwatch.Frequency;
            var plant = new SimulatedEpbPlant(() => tick);
            var digital = new DoConfig();
            digital.Epb.Add(new DoEpbRecord { Enabled = true, Channel = 4, Pos = "p", Neg = "n", PowerGroup = 2 });
            digital.Pressure.Add(new DoPressureRecord { Enabled = true, Id = 1, Physical = "v" });
            var analog = new AoConfig();
            analog.Devices.Add("Cylinder1", new AoDevice { Name = "Cylinder1", PhysicalChannel = "a", ScaleK = 10, Offset = -2 });
            plant.Configure(digital);
            plant.Configure(analog);
            var supply = new PowerSupplyCoordinatorTests.FakePswClient(2);
            plant.AttachPower(2, supply);
            var current = new AiConfigDetailRecord { 参数名 = "EPB4_current", 变换斜率 = 10, 变换截距 = 0.2, 零位漂移 = 0.01 };
            var pressure = new AiConfigDetailRecord { 参数名 = "Pressure_1", 变换斜率 = 10 };
            Func<AiConfigDetailRecord, double> read = record =>
                (plant.Read(new[] { record }, 1, 2000)[0, 0] - record.零位漂移) * record.变换斜率 + record.变换截距;
            plant.WriteDigital(new[] { "p", "n", "v" }, new[] { true, false, true });
            if (Math.Abs(read(current)) > 0.001) throw new Exception("Unpowered simulated motor produced current");
            supply.OutputEnabled = true;
            tick += Stopwatch.Frequency;
            if (read(current) < 13) throw new Exception("Motor output did not generate rising load");
            plant.WriteDigital(new[] { "p", "n", "v" }, new[] { false, false, true });
            if (Math.Abs(read(current)) > 0.001) throw new Exception("Motor OFF did not remove current");
            plant.WriteAnalog("a", 10.2);
            read(pressure);
            tick += Stopwatch.Frequency;
            if (read(pressure) < 99) throw new Exception("AO/valve did not build pressure");
            plant.WriteDigital(new[] { "p", "n", "v" }, new[] { false, false, false });
            tick += Stopwatch.Frequency;
            if (read(pressure) > 1) throw new Exception("Closed valve did not release pressure");
            Console.WriteLine("PASS plant power/output/current, AO/valve/pressure and inverse calibration");
            return 0;
        }

        internal static int Run(string isolatedRoot, bool trial = false, bool mdi = false)
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
                    using (var main = trial || mdi ? new MtEmbTest.Main_Frm() : null)
                    using (var monitor = new FrmEpbMainMonitor(new DaqRuntimeSettings(2000, 20), hardware))
                    using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
                    {
                        var start = DateTime.UtcNow;
                        var closing = false;
                        var started = false;
                        var stopping = false;
                        var lastCloseReport = -1;
                        var formal = new ConcurrentDictionary<int, int>();
                        var selected = config.Test.EpbRecords.Where(record => record.Enabled).Select(record => record.Id).ToArray();
                        timer.Tick += (sender, args) =>
                        {
                            if (closing)
                            {
                                var second = (int)(DateTime.UtcNow - start).TotalSeconds;
                                if (second != lastCloseReport)
                                {
                                    lastCloseReport = second;
                                    Console.WriteLine("CLOSE_WAIT MonitorDisposed=" + monitor.IsDisposed +
                                        " Handle=" + monitor.IsHandleCreated +
                                        " Lifecycle=" + monitor.MonitorLifecycle +
                                        " Reentry=" + DescribeField(monitor, "_closingReentry") +
                                        " MainDisposed=" + main?.IsDisposed +
                                        " MainClose=" + DescribeField(main, "_watchdogCloseTask") +
                                        " Shutdown=" + DescribeField(main, "_watchdogShutdownTask"));
                                }
                                return;
                            }
                            if (monitor.MonitorLifecycle == EpbMonitorLifecycle.InitializationFailed)
                            {
                                failure = new InvalidOperationException("Monitor initialization failed; inspect isolated project logs");
                                closing = true;
                                monitor.Close();
                            }
                            else if (monitor.MonitorLifecycle == EpbMonitorLifecycle.Idle && hardware.SamplesRead >= 20)
                            {
                                if (trial && !started)
                                {
                                    started = true;
                                    var manager = (EpbManager)typeof(FrmEpbMainMonitor).GetField("_epb", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(monitor);
                                    manager.ChannelCycleCompleted += (channel, count) =>
                                    {
                                        formal.AddOrUpdate(channel, 1, (_, value) => value + 1);
                                        Console.WriteLine("FORMAL_COMPLETED Channel=" + channel + " Count=" + count);
                                    };
                                    Console.WriteLine("TRIAL_START_REQUEST Targets=" + string.Join(",", selected));
                                    Click(monitor, "BtnStartTest");
                                }
                                else if (!trial || stopping)
                                {
                                    Console.WriteLine("MONITOR_INITIALIZED RealWindow=true RealAcquirer=true RealManager=true SamplesRead=" + hardware.SamplesRead);
                                    closing = true;
                                    monitor.Close();
                                }
                            }
                            if (trial && started && !stopping &&
                                (selected.All(channel => formal.TryGetValue(channel, out var count) && count >= 3) ||
                                 (DateTime.UtcNow - start).TotalSeconds > 180))
                            {
                                stopping = true;
                                if (!selected.All(channel => formal.TryGetValue(channel, out var count) && count >= 3))
                                    failure = new TimeoutException("Three formal cycles were not observed for every selected channel");
                                Console.WriteLine("TRIAL_STOP_REQUEST");
                                Click(monitor, "BtnStop");
                            }
                            else if ((DateTime.UtcNow - start).TotalSeconds > (trial ? 220 : 30))
                            {
                                failure = new TimeoutException("Monitor initialization deadline exceeded");
                                closing = true;
                                monitor.Close();
                            }
                        };
                        timer.Start();
                        if (main == null) Application.Run(monitor);
                        else
                        {
                            main.Shown += (sender, args) => main.OpenChildForm(monitor);
                            // A hidden MDI child loses its HWND, so Close disposes
                            // it without FormClosed. Observe actual disposal and
                            // then simulate the subsequent main-window close.
                            EventHandler disposed = null;
                            disposed = (sender, args) =>
                            {
                                monitor.Disposed -= disposed;
                                Console.WriteLine("MONITOR_DISPOSED MainHandle=" + main.IsHandleCreated);
                                main.BeginInvoke(new Action(main.Close));
                            };
                            monitor.Disposed += disposed;
                            Application.Run(main);
                        }
                        if (!closing || hardware.SamplesRead < 20)
                            throw new InvalidOperationException("Window closed before real acquisition was observed");
                    }
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(trial ? 235000 : 45000)) throw new TimeoutException("Monitor UI thread did not terminate");
            if (trial || mdi) WatchdogRuntime.ShutdownRuntimeWithReceipt();
            if (failure != null) throw failure;
            Console.WriteLine(trial ? "PASS three formal completion events; database evidence requires separate verification; restart NOT tested"
                : "PASS monitor initialization and idle close; trial recovery NOT tested");
            return 0;
        }

        private static void Click(Control monitor, string name)
        {
            var button = monitor.Controls.Find(name, true).Single();
            if (!button.Enabled) throw new InvalidOperationException("Button is disabled: " + name);
            button.GetType().GetMethod("PerformClick", Type.EmptyTypes).Invoke(button, null);
        }

        private static string DescribeField(object owner, string name)
        {
            if (owner == null) return "NoOwner";
            var value = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);
            if (value is Task task) return task.Status.ToString();
            return value == null ? "Null" : Convert.ToString(value);
        }

        private sealed class SmokeHardware : IMonitorHardwareFactory, IDisposable
        {
            private readonly List<IDisposable> _resources = new List<IDisposable>();
            private readonly SimulatedEpbPlant _plant = new SimulatedEpbPlant();
            private int _samplesRead;
            internal int SamplesRead => Volatile.Read(ref _samplesRead);
            private T Own<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
            public DoController CreateDigitalOutput(DoConfig config, IAppLogger logger)
            {
                _plant.Configure(config);
                return Own(new DoController(config, logger, name => new DigitalOutput(_plant)));
            }
            public AoController CreateAnalogOutput(AoConfig config, IAppLogger logger)
            {
                _plant.Configure(config);
                return Own(new AoController(config, logger, device =>
                    new AnalogOutput(value => _plant.WriteAnalog(device.PhysicalChannel, value))));
            }
            public TwoDeviceAiAcquirer CreateAcquirer(AiConfigDetail config, DaqRuntimeSettings settings, IAppLogger logger) =>
                Own(new TwoDeviceAiAcquirer(config, settings.SampleRateHz, settings.SamplesPerChannel, 10, logger,
                    (name, channels, min, max, terminal) =>
                    {
                        var records = channels.Select(channel => config.Records.Single(record =>
                            string.Equals(record.物理通道, channel, StringComparison.OrdinalIgnoreCase))).ToArray();
                        return new AnalogInput(samples => _plant.Read(records, samples, settings.SampleRateHz),
                            settings.SampleRateHz, () => Interlocked.Increment(ref _samplesRead));
                    }));
            public IPowerSupplyCoordinator CreatePowerSupply(GlobalConfig config, IAppLogger logger) =>
                Own(new PowerSupplyCoordinator(PowerSupplyConfigLoader.Load(RuntimeConfigPaths.GetPath("PowerSupplyConfig.xml")),
                    config.Test.Groups, logger, device =>
                    {
                        var client = new PowerSupplyCoordinatorTests.FakePswClient(device.Id);
                        _plant.AttachPower(device.Id, client);
                        return client;
                    }));
            public IDaqHardwareProbe CreateDaqProbe() => new Probe();
            public AlarmManager CreateAlarm(AlarmConfig config, IAppLogger logger) => null;
            public void Dispose()
            {
                foreach (var resource in _resources.AsEnumerable().Reverse()) resource.Dispose();
            }
        }

        private sealed class DigitalOutput : IDigitalOutputSession
        {
            private readonly SimulatedEpbPlant _plant;
            private readonly List<string> _lines = new List<string>();
            internal DigitalOutput(SimulatedEpbPlant plant) { _plant = plant; }
            public bool IsReady { get; private set; }
            public void AddLine(string physicalLine, string logicalName) { _lines.Add(physicalLine); }
            public void Verify() { IsReady = true; }
            public void Write(bool[] states)
            {
                if (!IsReady) throw new InvalidOperationException("DO not initialized");
                _plant.WriteDigital(_lines, states);
            }
            public void Dispose() { IsReady = false; }
        }
        private sealed class AnalogOutput : IAoVoltageOutput
        {
            private readonly Action<double> _write;
            internal AnalogOutput(Action<double> write) { _write = write; }
            public void Write(double voltage) { _write(voltage); }
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
            private readonly Func<int, double[,]> _samples;
            private readonly Action _read;
            private readonly AutoResetEvent _ready = new AutoResetEvent(false);
            private readonly object _gate = new object();
            private Thread _worker;
            private TaskCompletionSource<double[,]> _pending;
            private AsyncCallback _callback;
            private int _count;
            private long _nextTick;
            private int _stopped;
            internal AnalogInput(Func<int, double[,]> samples, double rate, Action read) { _samples = samples; SampleClockRate = rate; _read = read; }
            public double SampleClockRate { get; }
            public void Start()
            {
                Volatile.Write(ref _stopped, 0);
                _nextTick = Stopwatch.GetTimestamp();
                _worker = new Thread(Pump) { IsBackground = true, Name = "Simulated-AI" };
                _worker.Start();
            }
            public void Stop() { Volatile.Write(ref _stopped, 1); _ready.Set(); }
            public void Dispose()
            {
                Stop();
                if (_worker != null && Thread.CurrentThread != _worker) _worker.Join(1000);
            }
            public void BeginReadMultiSample(int samples, AsyncCallback callback, object state)
            {
                if (Volatile.Read(ref _stopped) != 0) throw new ObjectDisposedException(nameof(AnalogInput));
                lock (_gate)
                {
                    if (_pending != null) throw new InvalidOperationException("Overlapping simulated AI reads");
                    _pending = new TaskCompletionSource<double[,]>(state);
                    _callback = callback;
                    _count = samples;
                }
                _ready.Set();
            }
            private void Pump()
            {
                while (Volatile.Read(ref _stopped) == 0)
                {
                    _ready.WaitOne(100);
                    TaskCompletionSource<double[,]> pending;
                    AsyncCallback callback;
                    int samples;
                    lock (_gate)
                    {
                        pending = _pending; callback = _callback; samples = _count;
                        _pending = null; _callback = null;
                    }
                    if (pending == null) continue;
                    _nextTick += (long)(samples * Stopwatch.Frequency / SampleClockRate);
                    while (Stopwatch.GetTimestamp() < _nextTick && Volatile.Read(ref _stopped) == 0)
                        Thread.Sleep(1);
                    try { pending.SetResult(_samples(samples)); }
                    catch (Exception error) { pending.SetException(error); }
                    callback(pending.Task);
                }
            }
            public double[,] EndReadMultiSample(IAsyncResult result) { _read(); return ((Task<double[,]>)result).GetAwaiter().GetResult(); }
        }
    }
}
