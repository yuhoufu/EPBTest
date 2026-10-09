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
using MTTFTest.Watchdog.Protocol;
using MTTFTest.FallbackGuard;
using IAppLogger = Config.IAppLogger;

namespace AdaptiveControlTests
{
    internal static class MonitorHardwareSmokeTests
    {
        internal static int PrepareIndependentRecovery(string userSid)
        {
            var root = RequireIndependentFixture();
            IndependentProtectedFiles.RequireTrustedDirectory(root);
            var project = Path.Combine(root, "D", "P");
            var stateDirectory = Path.Combine(root, "S");
            Directory.CreateDirectory(stateDirectory);
            var registrationPath = Path.Combine(stateDirectory, "registration.json");
            IndependentRegistrationExport.Export(project, stateDirectory, userSid,
                Guid.NewGuid().ToString("N"), registrationPath);
            var registration = BoundedJson.Read<IndependentExecutorRegistration>(registrationPath);
            registration.ExecutablePath = Path.Combine(root, "AdaptiveControlTests.exe");
            registration.ExecutableSha256 = SupervisorProtocol.ComputeSha256(registration.ExecutablePath);
            // Keep all registered executable roles in the installation directory.
            // The separate worker uses an isolated AppDomain rooted in
            // SafetyWorker, preserving the main's fail-closed legacy stub.
            registration.SafetyExecutablePath = Path.Combine(root, "IndependentRecovery.ProcessTests.exe");
            registration.SafetyExecutableSha256 = SupervisorProtocol.ComputeSha256(registration.SafetyExecutablePath);
            File.WriteAllText(Path.Combine(registration.ConfigDirectory, "SIMULATED-HARDWARE-ONLY.txt"), "success");
            registration.Validate();
            BoundedJson.Write(registrationPath, registration);
            new IndependentProjectStateStore(stateDirectory).Update(0, state => true);
            BoundedJson.Write(Path.Combine(root, "before-recovery-database.json"),
                RecoveryDatabaseEvidence.Read(registration.DatabasePath, new[] { 4, 7 }));
            Console.WriteLine("PREPARED real monitor simulation; no run armed; hardware stages simulated");
            return 0;
        }

        internal static int ArmIndependentRecovery()
        {
            var root = RequireIndependentFixture();
            var registration = IndependentExecutorRegistration.LoadTrusted(Path.Combine(root, "S", "registration.json"));
            using (var user = System.Security.Principal.WindowsIdentity.GetCurrent())
                if (user.User.Value != registration.InteractiveUserSid)
                    throw new InvalidOperationException("Fixture must be armed by registered interactive user");
            var config = ConfigLoader.LoadTest(Path.Combine(registration.ProjectDirectory, "Config", "TestConfig.xml"), null);
            var store = new IndependentProjectStateStore(registration.StateDirectory);
            using (var process = Process.GetCurrentProcess())
            {
                var identity = new IndependentProcessIdentity
                {
                    Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    WindowsSessionId = process.SessionId, ExecutablePath = process.MainModule.FileName,
                    SessionToken = Guid.NewGuid().ToString("N")
                };
                var intent = new IndependentRunIntent
                {
                    Revision = 1, RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1, Armed = true,
                    ProjectDirectory = registration.ProjectDirectory, DatabasePath = registration.DatabasePath,
                    DatabaseCreationUtcTicks = registration.DatabaseCreationUtcTicks,
                    ExecutablePath = registration.ExecutablePath, ConfigurationSha256 = registration.ConfigurationSha256,
                    SelectedChannels = config.EpbRecords.Where(record => record.Enabled).Select(record => record.Id).ToArray(),
                    PeriodMs = config.PeriodMs,
                    StartupBudgetMs = registration.StartupPositioningBudgetMs + (config.LearnCycles + 2L) * config.PeriodMs,
                    MechanicalTargets = config.EpbRecords.Select(record => new IndependentMechanicalTarget
                    { Channel = record.Id, TotalCount = record.TotalCount > 0 ? record.TotalCount : config.TestTarget }).ToArray()
                };
                if (!intent.SelectedChannels.OrderBy(channel => channel).SequenceEqual(new[] { 4, 7 }))
                    throw new InvalidOperationException("Fixture must preserve selected channels 4 and 7");
                store.ArmManualRun(store.Read().Revision, intent, identity, DateTime.UtcNow.Ticks);
                BoundedJson.Write(Path.Combine(root, "arming-process.json"), identity);
            }
            Console.WriteLine("ARMED simulation exits; production executor must detect the exited process and launch recovery");
            return 0;
        }

        private static string RequireIndependentFixture()
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var marker = Path.Combine(root, "REAL-MONITOR-SIMULATION-ONLY.txt");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 ||
                File.ReadAllText(marker).Trim() != "SIMULATED_NO_HARDWARE")
                throw new InvalidOperationException("Independent monitor simulation marker required");
            return root;
        }

        internal static int RunIndependentRecovery(string[] arguments)
        {
            var root = RequireIndependentFixture();
            var pid = Process.GetCurrentProcess().Id;
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using (var output = new StreamWriter(Path.Combine(root, "recovery-" + pid + ".stdout.log")) { AutoFlush = true })
            using (var errors = new StreamWriter(Path.Combine(root, "recovery-" + pid + ".stderr.log")) { AutoFlush = true })
            {
                Console.SetOut(output);
                Console.SetError(errors);
                try
                {
                    var code = RunIndependentRecoveryCore(arguments);
                    BoundedJson.Write(Path.Combine(root, "real-monitor-result.json"), new { pid, exitCode = code });
                    return code;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    BoundedJson.Write(Path.Combine(root, "real-monitor-error-" + pid + ".json"),
                        new { pid, error = error.ToString() });
                    throw;
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalError);
                }
            }
        }

        private static int RunIndependentRecoveryCore(string[] arguments)
        {
            var root = RequireIndependentFixture();
            var startup = IndependentRecoveryStartup.Parse(arguments);
            if (startup == null || !startup.IsRecoveryLaunch)
                throw new InvalidOperationException("One-time independent recovery ticket required");
            startup.ConsumeAndBind();
            if (!startup.Registration.ProjectDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulation project escaped isolated executable directory");
            UnattendedRecoveryCoordinator.SetRecoveryProcessMode(true);
            var targets = startup.ValidateCurrent().RecoveryChannels();
            var store = new IndependentProjectStateStore(startup.Registration.StateDirectory);
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var hardware = new SmokeHardware())
                    using (var main = new MtEmbTest.Main_Frm(hardware))
                    using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
                    {
                        FrmEpbMainMonitor monitor = null;
                        var formal = new ConcurrentDictionary<int, int>();
                        var attached = false;
                        var stopping = false;
                        var closing = false;
                        var clock = Stopwatch.StartNew();
                        var lastReport = -1;
                        timer.Tick += (sender, args) =>
                        {
                            try
                            {
                                if (closing) return;
                                if (main.IndependentRecoveryStartupFailure != null)
                                    throw new InvalidOperationException("Independent main startup failed",
                                        main.IndependentRecoveryStartupFailure);
                                monitor = monitor ?? main.MdiChildren.OfType<FrmEpbMainMonitor>().SingleOrDefault();
                                var report = (int)(clock.Elapsed.TotalSeconds / 10);
                                if (report != lastReport)
                                {
                                    lastReport = report;
                                    Console.WriteLine("RECOVERY_WAIT Seconds=" + clock.Elapsed.TotalSeconds.ToString("F0") +
                                        " Monitor=" + (monitor?.MonitorLifecycle.ToString() ?? "Absent") +
                                        " Samples=" + hardware.SamplesRead);
                                    if (main.Cfg != null)
                                    {
                                        try { startup.ValidateConfiguration(main.Cfg); }
                                        catch (Exception error) { Console.Error.WriteLine("RECOVERY_CONFIGURATION " + error.Message); }
                                    }
                                }
                                if (monitor != null && !attached)
                                {
                                    var manager = (EpbManager)typeof(FrmEpbMainMonitor).GetField("_epb",
                                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(monitor);
                                    if (manager != null)
                                    {
                                        attached = true;
                                        manager.ChannelCycleCompleted += (channel, count) =>
                                        {
                                            if (!targets.Contains(channel))
                                                failure = new InvalidOperationException("Excluded channel completed a recovery cycle");
                                            formal.AddOrUpdate(channel, 1, (_, value) => value + 1);
                                            Console.WriteLine("RECOVERY_FORMAL Channel=" + channel + " Count=" + count);
                                        };
                                        EventHandler disposed = null;
                                        disposed = (_, __) =>
                                        {
                                            monitor.Disposed -= disposed;
                                            main.BeginInvoke(new Action(main.Close));
                                        };
                                        monitor.Disposed += disposed;
                                    }
                                }
                                if (attached && !stopping &&
                                    targets.All(channel => formal.TryGetValue(channel, out var count) && count >= 3))
                                {
                                    var state = store.Read();
                                    if (state.Transaction?.Phase == IndependentRecoveryPhase.Verified &&
                                        startup.Identity.Matches(state.Controller))
                                    {
                                        var faultPath = Path.Combine(root, "fault-injected.json");
                                        if (File.Exists(Path.Combine(root, "INJECT-UI-AND-SAMPLING-HANG.txt")) &&
                                            !File.Exists(faultPath))
                                        {
                                            hardware.FreezeSampling();
                                            BoundedJson.Write(Path.Combine(root, "before-stall-verified.json"), state);
                                            BoundedJson.Write(Path.Combine(root, "before-stall-database.json"),
                                                RecoveryDatabaseEvidence.Read(startup.Registration.DatabasePath, targets));
                                            BoundedJson.Write(faultPath, new { identity = startup.Identity,
                                                utcTicks = DateTime.UtcNow.Ticks, targets,
                                                fault = "UI thread blocked and simulated AI callbacks stalled" });
                                            Console.WriteLine("INJECT_UI_AND_SAMPLING_HANG Pid=" + startup.Identity.Pid);
                                            timer.Stop();
                                            // Deliberately unresponsive simulated controller. Only the
                                            // external executor may retire it; no test stop receipt.
                                            Thread.Sleep(Timeout.Infinite);
                                        }
                                        BoundedJson.Write(Path.Combine(root, "real-monitor-verified.json"), state);
                                        Console.WriteLine("RECOVERY_VERIFIED Targets=" + string.Join(",", targets));
                                        stopping = true;
                                        Click(monitor, "BtnStop");
                                    }
                                }
                                if (stopping && monitor.MonitorLifecycle == EpbMonitorLifecycle.Idle)
                                {
                                    closing = true;
                                    monitor.Close();
                                }
                                if (clock.Elapsed.TotalSeconds > 300)
                                    throw new TimeoutException("Real recovery did not reach verified formal progress and stop within 300 seconds");
                            }
                            catch (Exception error)
                            {
                                failure = error;
                                timer.Stop();
                                Console.Error.WriteLine(error);
                                BoundedJson.Write(Path.Combine(root, "real-monitor-error-" +
                                    Process.GetCurrentProcess().Id + ".json"),
                                    new { error = error.ToString() });
                                // The external test owner retires this isolated process.
                                // Do not fabricate manual stop or safety authorization.
                            }
                        };
                        timer.Start();
                        Application.Run(main); // Production OnShown owns opening and resuming the monitor.
                        if (!closing || failure != null)
                            throw failure ?? new InvalidOperationException("Main window exited before verified recovery");
                    }
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(315000)) throw new TimeoutException("Independent recovery monitor did not exit");
            WatchdogRuntime.ShutdownRuntimeWithReceipt();
            if (failure != null) throw failure;
            Console.WriteLine("PASS independent ticket, real monitor continuation, executor verification and normal exit");
            return 0;
        }

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
                    Guid? protectedRoot = mdi ? Guid.NewGuid() : (Guid?)null;
                    using (var hardware = new SmokeHardware())
                    using (var main = trial || mdi ? new MtEmbTest.Main_Frm(hardware) : null)
                    using (var monitor = main == null
                        ? new FrmEpbMainMonitor(new DaqRuntimeSettings(2000, 20), hardware)
                        : main.CreateMonitor(new DaqRuntimeSettings(2000, 20), protectedRoot))
                    using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
                    {
                        if (protectedRoot.HasValue &&
                            (Guid)typeof(FrmEpbMainMonitor).GetField("_protectedLearningRootId",
                                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(monitor) != protectedRoot.Value)
                            throw new InvalidOperationException("Recovery monitor lost its protected learning root");
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
            private int _freezeSampling;
            internal void FreezeSampling() => Volatile.Write(ref _freezeSampling, 1);
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
                        return new AnalogInput(samples =>
                        {
                            if (Volatile.Read(ref _freezeSampling) != 0) Thread.Sleep(Timeout.Infinite);
                            return _plant.Read(records, samples, settings.SampleRateHz);
                        },
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
