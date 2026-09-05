using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using MTEmbTest;
using MtEmbTest;
using MTTFTest.Watchdog.Protocol;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    private static int _passed;
    private static int _failed;
    private static string _filter;

    [STAThread]
    private static int Main(string[] args)
    {
        _filter = args.Length > 0 ? args[0] : null;
        if (_filter != null) Console.WriteLine("Test filter: " + _filter);
        var repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        var dependencies = Path.Combine(repo, "MTTfTest", "bin", new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory).Name);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var path = Path.Combine(dependencies, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        // Render the 1440x900 baseline at 96 DPI even on a 200% development desktop.
        // Real 125%/150% monitor acceptance is separately required, not simulated here.
        SetThreadDpiAwarenessContext(new IntPtr(-1));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Run("Original layout baseline ignores screen-clamped form size", OriginalLayoutBaseline);
        Run("Monitor startup binds selection before acquisition and retains a close control", MonitorStartupSelection);
        Run("Production UI excludes hardware assemblies and legacy bootstrap", () =>
        {
            var assembly = typeof(Main_Frm).Assembly;
            var names = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
            foreach (var hardware in new[] { "Controller", "IO.NI", "PowerSupply.Core", "ZlgCanComm", "NationalInstruments.DAQmx" })
                Assert(!names.Contains(hardware));
            Assert(assembly.GetType("MtEmbTest.FirstRunBootstrap", false) == null);
            Assert(assembly.GetType("MTEmbTest.V3EngineClientForm", false) == null);
        });
        Run("Contract rejects duplicate channels", () =>
        {
            var value = Snapshot(); value.Channels[11].Channel = 1; Assert(!value.IsStructurallyValid());
        });
        Run("Contract rejects unknown UI generation", () =>
        {
            var value = Snapshot(); value.ContractVersion++; Assert(!value.IsStructurallyValid());
        });
        Run("Contract rejects malformed dates and nonfinite power", () =>
        {
            var value = Snapshot(); value.CapturedUtcTicks = long.MaxValue; Assert(!value.IsStructurallyValid());
            value = Snapshot(); value.PowerSupplies[0].Voltage = double.NaN; Assert(!value.IsStructurallyValid());
            value = Snapshot(); value.Logs = new[] { new EngineUiLogEntry { Sequence = 1, CapturedUtcTicks = long.MaxValue } };
            Assert(!value.IsStructurallyValid());
        });
        Run("Measurement missing is not zero", () => Assert(!new UiMeasurement().IsUsable(DateTime.UtcNow.Ticks)));
        Run("Measurement stale is invalid", () => Assert(!new UiMeasurement { Valid = true, Value = 12,
            CapturedUtcTicks = DateTime.UtcNow.AddSeconds(-10).Ticks }.IsUsable(DateTime.UtcNow.Ticks)));
        Run("Display envelope preserves extrema and time order", () =>
        {
            var buffer = new EngineUiCurveBuffer(); var values = new double[1, 1000];
            values[0, 101] = 87; values[0, 102] = -42;
            Assert(buffer.TryAppend("A1", values, 0, DateTime.UtcNow.Ticks, 10000));
            var curve = buffer.Snapshot().Single();
            Assert(curve.Values.Contains(87) && curve.Values.Contains(-42));
            Assert(curve.UtcTicks.Zip(curve.UtcTicks.Skip(1), (a, b) => b > a).All(v => v));
        });
        Run("Display buffer bounded and late batches discarded", () =>
        {
            var buffer = new EngineUiCurveBuffer(); var end = DateTime.UtcNow.Ticks;
            for (var n = 0; n < 5000; n++) buffer.TryAppend("A1", new double[,] { { n } }, 0, end + n * 10000, 1000);
            buffer.TryAppend("A1", new double[,] { { -999 } }, 0, end, 1000);
            var curve = buffer.Snapshot().Single(); Assert(curve.Values.Length <= 4096 && !curve.Values.Contains(-999));
        });
        Run("Display envelope preserves gaps and merges cross-batch buckets", () =>
        {
            var buffer = new EngineUiCurveBuffer(); var end = DateTime.UtcNow.Ticks;
            buffer.TryAppend("A1", new double[,] { { 1, 7, double.NaN, -9, 3 } }, 0, end, 1000);
            buffer.TryAppend("A1", new double[,] { { 4, 5 } }, 0, end + TimeSpan.TicksPerSecond * 2, 1000);
            var curve = buffer.Snapshot().Single();
            Assert(curve.Values.Contains(7) && curve.Values.Contains(-9) && curve.BreakBefore.Count(b => b) == 2);
            for (var n = 0; n < 10000; n++)
                buffer.TryAppend("A2", new double[,] { { n % 2 == 0 ? 87 : -42 } }, 0, end + n * 10000, 1000);
            curve = buffer.Snapshot().Single(c => c.Key == "A2");
            Assert(curve.Values.Length < 500 && curve.Values.Contains(87) && curve.Values.Contains(-42));
            var snapshot = Snapshot(); snapshot.Curves = new[] { curve }; Assert(snapshot.IsStructurallyValid());
            curve.BreakBefore = new[] { true }; Assert(!snapshot.IsStructurallyValid());
        });
        Run("Display long windows stay bounded and retain early peaks", () =>
        {
            var buffer = new EngineUiCurveBuffer(1200); var begin = DateTime.UtcNow.AddSeconds(-1200).Ticks;
            for (var n = 0; n < 1200; n++)
                buffer.TryAppend("A1", new double[,] { { n == 2 ? 123 : 1, n == 2 ? -37 : 1 } }, 0, begin + n * TimeSpan.TicksPerSecond, 2);
            var curve = buffer.Snapshot().Single();
            Assert(curve.Values.Length <= 4096 && curve.Values.Contains(123) && curve.Values.Contains(-37));
        });
        Run("Display preferences are presentation-only and survive reopen", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.UiPreferences." + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "preferences.xml");
            try
            {
                var prefs = new OriginalMonitorPreferences(path); prefs.WindowSeconds = 30;
                prefs.Curves["A4"] = false; prefs.Curves["F"] = true; prefs.Save(); Assert(prefs.Error == "");
                prefs = new OriginalMonitorPreferences(path);
                Assert(prefs.WindowSeconds == 30 && !prefs.Curves["A4"] && prefs.Curves["F"]);
                Assert(!File.ReadAllText(path).Contains("RunCount") && Directory.GetFiles(root).Length == 1);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        Run("Log pages are bounded filtered ordered and explicit about retention loss", () =>
        {
            var buffer = new EngineUiLogBuffer();
            for (var n = 1; n <= 3000; n++)
                buffer.TryAppend(n % 2 == 0 ? "WARN" : "INFO", "synthetic", "message " + n, DateTime.UtcNow.Ticks);
            var page = buffer.Read(new EngineUiLogQuery { Level = "WARN", PageSize = 37 });
            Assert(page.IsStructurallyValid() && page.Entries.Length == 37 && page.Entries.Last().Sequence == 3000 &&
                page.HasEarlier && page.RetentionTruncated && page.OldestRetainedSequence == 953);
            var cursor = page.NextBeforeSequence;
            page.Entries[0].Message = "UI mutation";
            Assert(!buffer.Read(new EngineUiLogQuery()).Entries.Any(e => e.Message == "UI mutation"));
            page = buffer.Read(new EngineUiLogQuery { Level = "WARN", BeforeSequence = cursor, PageSize = 37 });
            Assert(page.IsStructurallyValid() && page.Entries.Last().Sequence < cursor);
            page = buffer.Read(new EngineUiLogQuery { BeforeSequence = 10 });
            Assert(page.IsStructurallyValid() && page.RetentionTruncated && page.Entries.Length == 0);
            Assert(!new EngineUiLogQuery { PageSize = 201 }.IsStructurallyValid());
            Assert(!new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ReadUiSnapshot,
                UiLogQuery = new EngineUiLogQuery() }.IsStructurallyValid());
        });
        Run("Log reader cannot block producers and exposes dropped display messages", () =>
        {
            var buffer = new EngineUiLogBuffer(); var gate = typeof(EngineUiLogBuffer).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(buffer);
            var elapsed = Stopwatch.StartNew();
            lock (gate) Task.Run(() => buffer.TryAppend("INFO", "test", "dropped", DateTime.UtcNow.Ticks)).GetAwaiter().GetResult();
            Assert(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            Assert(buffer.Read(new EngineUiLogQuery()).DroppedEntries == 1);
        });
        Run("Production UI pipe validates request and response", () => PipeCase(false).GetAwaiter().GetResult());
        Run("Production UI pipe rejects foreign response", () => PipeCase(true).GetAwaiter().GetResult());
        Run("Production log pipe binds cursor page size and Engine identity", () => LogPipeCase().GetAwaiter().GetResult());
        Run("Late log pages cannot override live mode or replacement Engine", () => LateLogPage().GetAwaiter().GetResult());
        Run("Engine replacement requires fresh Supervisor attachment", () => ReplacementAttachment().GetAwaiter().GetResult());
        Run("Project switch requires durable destination approval before UI attachment", () => ReplacementAttachment(true).GetAwaiter().GetResult());
        Run("UI attachment rejects missing, stale, conflicting and regressed desired identity", AttachmentPolicy);
        Run("Project browser only reads bounded local files and binds original source revisions", () => ProjectBrowserReads().GetAwaiter().GetResult());
        Run("Original project selector confirms and submits without clearing counts or auto-start", () => OriginalProjectSelector());
        Run("Closing project settings before the command receipt cannot update disposed controls", () => OriginalProjectSelector(true));
        Run("New project browser validates absence and capability without writes", () => NewProjectBrowser().GetAwaiter().GetResult());
        Run("New project original name and save submit once and preserve old progress", () => NewProjectOriginalSave("submit"));
        Run("New project cancellation sends no command", () => NewProjectOriginalSave("cancel"));
        Run("New project existing directory is never overwritten by Save", () => NewProjectOriginalSave("existing"));
        Run("New project late receipt after closing cannot update disposed controls", () => NewProjectOriginalSave("close"));
        Run("New project capability and running state gate the original controls", () => NewProjectOriginalSave("capability"));
        Run("New project wire queries original ID and retains complete metadata", () => AlarmPanelPendingQuery(true, true).GetAwaiter().GetResult());
        Run("Project reset original button confirms active identity and never zeroes local counts", () => ResetOriginalButton("submit"));
        Run("Project reset cancellation sends no transaction", () => ResetOriginalButton("cancel"));
        Run("Project reset rejected command preserves editable values and history", () => ResetOriginalButton("reject"));
        Run("Project reset late receipt cannot update a disposed settings form", () => ResetOriginalButton("close"));
        Run("Project reset capability running state and config conflict gate the original button", () => ResetOriginalButton("gates"));
        Run("Project reset wire preserves mode and queries the identical command", () => AlarmPanelPendingQuery(true, false, true).GetAwaiter().GetResult());
        Run("Project switch pending admission queries original ID until final execution receipt", () => AlarmPanelPendingQuery(true).GetAwaiter().GetResult());
        Run("Lost command response resolves by original ID query", () => LostOperatorResponse(true).GetAwaiter().GetResult());
        Run("Missing command query retries the identical command", () => LostOperatorResponse(false).GetAwaiter().GetResult());
        Run("Stop stays available while start response is pending", () => StopWhileStartPending().GetAwaiter().GetResult());
        Run("Configuration command wire preserves typed payload and fingerprint", ConfigurationWireRoundTrip);
        Run("DAQ settings original grid saves typed metadata without touching history", () => DaqOriginalGrid("save"));
        Run("DAQ settings reject conflicts and missing capability", () => DaqOriginalGrid("gates"));
        Run("DAQ settings do not replace unsaved project edits", () => DaqOriginalGrid("independent"));
        Run("DAQ settings late receipt after form close is safe", () => DaqOriginalGrid("close"));
        Run("DAQ settings rejection retains editable values", () => DaqOriginalGrid("reject"));
        Run("DAQ settings lost receipt cannot duplicate save", () => DaqOriginalGrid("unknown"));
        Run("DAQ settings draft separates metadata revisions and rejects old Run", DaqDraftIdentity);
        Run("DAQ settings wire retains mode and original ID query", () => AlarmPanelPendingQuery(daq: true).GetAwaiter().GetResult());
        Run("AO metadata rejects stale hash and incompatible capability", AoSnapshotMetadata);
        Run("AO client requires its own capability and completed stop", AoClientCapabilityGate);
        Run("AO calibration wire queries original ID until execution completes", () => AlarmPanelPendingQuery(ao: true).GetAwaiter().GetResult());
        RunPressurePageTests(repo);
        Run("Manual batch wire preserves engine and paused owner binding", ManualBatchWireRoundTrip);
        Run("Original main action button pauses and continues without resetting counts", ManualBatchOriginalButton);
        Run("Original twelve channel toggles preserve mapping counts and healthy peers", ManualChannelOriginalButtons);
        Run("Original alarm channel toggle confirms one bounded qualification retry", QualificationRetryOriginalButton);
        Run("Stop preempts pending manual pause and ignores its late response", () => StopWhilePausePending().GetAwaiter().GetResult());
        Run("Original settings binds editable metadata without counts", SettingsBindWithoutWrites);
        Run("Settings draft ignores telemetry but rejects metadata and run changes", SettingsDraftIdentity);
        Run("Original settings loads after delayed attachment without commands", () => SettingsLifecycle("attach"));
        Run("Original settings retains conflicting edits and refuses save", () => SettingsLifecycle("conflict"));
        Run("Original settings accepted admission is not configuration completion", () => SettingsLifecycle("pending"));
        Run("Original settings explicit rejection retains editable draft", () => SettingsLifecycle("reject"));
        Run("Original settings lost receipt cannot create another transaction", () => SettingsLifecycle("unknown"));
        Run("Legacy feature playback reads readonly 77-byte records and preserves source", FeaturePlaybackReadonly);
        Run("Legacy raw playback reads both record widths and handles short files", RawPlaybackReadonly);
        RunPlaybackHistoryTests();
        Run("Connected silent pipe meets total deadline", () => SilentPeer().GetAwaiter().GetResult());
        Run("Cancelled pipe releases pending read", () => CancelPeer().GetAwaiter().GetResult());
        Run("Single flight refresh", () => SingleFlight().GetAwaiter().GetResult());
        Run("Running and recovery prevent close", () =>
        {
            var fake = new FakeClient(); using (var session = new V3MonitorSession(fake))
            {
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(session.CanClose);
                fake.Value.Engine.State = SystemTerminalState.Running; fake.Value.Sequence++;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose && !session.CanStart);
                fake.Value.Engine.State = SystemTerminalState.SafeIdleAlarmed; fake.Value.Engine.RecoveryIncidentId = RecoveryProtocolV7.NewId();
                fake.Value.Sequence++; session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose);
                fake.Value.Engine.RecoveryIncidentId = string.Empty;
                fake.Value.Engine.RecoveryOwnerId = RecoveryProtocolV7.NewId();
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose);
                fake.Value.Engine.RecoveryOwnerId = string.Empty;
                fake.Value.Engine.OutputsEnergized = true;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose);
            }
        });
        Run("Original forms render nonzero counters without hardware", () => RenderOriginal(repo));
        Run("Close waits for independent Supervisor stop completion", () =>
        {
            var fake = new FakeClient(); using (var session = new V3MonitorSession(fake))
            {
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(session.CanClose);
                fake.Value.Kernel.ActiveIncidentCount = 1;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose && !session.CanStart);
                fake.Value.Kernel.ActiveIncidentCount = 0; fake.Value.Kernel.CommandPending = true;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose && !session.CanStart);
                fake.Value.Kernel.CommandPending = false; fake.Value.Kernel.DesiredState = SystemTerminalState.SafeIdleAlarmed;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose && session.CanStart);
                fake.Value.Kernel.DesiredState = SystemTerminalState.StoppedByOperator;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(session.CanClose && session.CanStart);
                fake.Value.Kernel.Available = false;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanClose && !session.CanStart);
            }
        });
        Run("Alarm panel typed wire payload and execution receipt", AlarmPanelWireRoundTrip);
        Run("Released stopped host remains startable without claiming initialized hardware", () =>
        {
            var fake = new FakeClient(); using (var session = new V3MonitorSession(fake))
            {
                fake.Value.Engine.HardwareInitialized = false;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanStart);
                fake.Value.Engine.HardwareRecompositionReady = true;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(session.CanStart && session.CanClose);
                Assert(fake.Commands == 0 && session.Latest.Channels[3].FormalCycles == 21504);
                fake.Value.Kernel.ActiveIncidentCount = 1;
                session.RefreshAsync().GetAwaiter().GetResult(); Assert(!session.CanStart && !session.CanClose);
                fake.Value.Engine.HardwareInitialized = true;
                Assert(!fake.Value.Engine.IsStructurallyValid());
            }
        });
        Run("Alarm panel pending admission queries original command until execution completes", () => AlarmPanelPendingQuery().GetAwaiter().GetResult());
        Run("Original alarm controls bind commands without clearing isolation or counts", AlarmPanelControls);
        Console.WriteLine("OriginalUiTests: " + _passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void RenderOriginal(string repo)
    {
        var fake = new FakeClient();
        using (var form = new Main_Frm(fake, Path.Combine(repo, "artifacts", "R26-original-ui", "isolated-preferences.xml")))
        {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.WindowState = FormWindowState.Normal; form.Location = new Point(-20000, -20000);
            form.Size = new Size(1440, 900); form.Show();
            // WinForms applies the designer DPI scale during the first Show; size the test viewport afterwards.
            form.Size = new Size(1440, 900);
            Application.DoEvents();
            var monitor = form.MdiChildren.OfType<FrmEpbMainMonitor>().Single();
            foreach (var name in new[] { "ChkEpb4", "LedRunTime", "RuntimeStateEpb4", "BtnStartTest" })
            {
                var control = monitor.Controls.Find(name, true).Single();
                if (!(control.Height >= control.Font.Height && control.Bottom <= control.Parent.ClientSize.Height &&
                    control.Right <= control.Parent.ClientSize.Width))
                {
                    var failureDirectory = Path.Combine(repo, "artifacts", "R26-original-ui"); Directory.CreateDirectory(failureDirectory);
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(failureDirectory, "layout-failure-1440x900.png"));
                    }
                    throw new Exception("Original layout overflow: " + name + ";Bounds=" + control.Bounds +
                        ";FontHeight=" + control.Font.Height + ";Parent=" + control.Parent.Name + ";ParentSize=" + control.Parent.ClientSize +
                        ";FormSize=" + form.Size + ";ClientSize=" + form.ClientSize + ";Dpi=" + form.DeviceDpi);
                }
            }
            var menu = form.Controls.OfType<MenuStrip>().Single();
            Assert(menu.Visible && menu.Items.Count >= 4 && menu.Height > 0);
            Assert(monitor.Controls.Find("uiPanel5", true).Single().Width > 700);
            Assert(monitor.Controls.Find("uiGroupBox1", true).Single().Width > 500 &&
                monitor.Controls.Find("uiGroupBox1", true).Single().Width < 650);
            ExpectText(monitor, "LabEpb4", "21508");
            ExpectText(monitor, "TxtTestName", "10243-028");
            Assert(monitor.Controls.Find("textEditCurrent4", true).Single().Text.Contains("0.400"));
            ExpectText(monitor, "comboBoxEditCurrentRecord", "EPB-4");
            ExpectText(monitor, "LedRunCycles", "21508");
            ExpectText(monitor, "LedLastCycles", "178492");
            Assert(fake.Commands == 0);
            var directory = Path.Combine(repo, "artifacts", "R26-original-ui"); Directory.CreateDirectory(directory);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                // Diagnostic render only: MDI/transparent custom control rasterization is
                // not a substitute for on-screen DPI acceptance. Paint the top-level menu
                // last because DrawToBitmap reverses the MDI child ordering.
                menu.DrawToBitmap(bitmap, new Rectangle(form.PointToClient(menu.PointToScreen(Point.Empty)) +
                    new Size((form.Width - form.ClientSize.Width) / 2, form.Height - form.ClientSize.Height -
                    (form.Width - form.ClientSize.Width) / 2), menu.Size));
                bitmap.Save(Path.Combine(directory, "original-ui-1440x900.png"));
            }
            var originalWidth = monitor.Controls.Find("uiGroupBox1", true).Single().Width;
            foreach (var size in new[] { new Size(1800, 1125), new Size(2160, 1350), new Size(1440, 900) })
            {
                form.Size = size; Application.DoEvents();
                ExpectText(monitor, "LabEpb4", "21508");
                var start = monitor.Controls.Find("BtnStartTest", true).Single();
                Assert(start.Visible && start.Width > 150 && start.Height >= 20);
                var close = monitor.Controls.Find("BtnCloseMonitor", true).Single();
                Assert(close.Visible && close.Width >= 60 && close.Right <= close.Parent.ClientSize.Width && close.Left >= 0);
            }
            Assert(Math.Abs(monitor.Controls.Find("uiGroupBox1", true).Single().Width - originalWidth) <= 1);
        }
    }

    private static void OriginalLayoutBaseline()
    {
        var capture = typeof(Main_Frm).Assembly.GetType("MTEmbTest.OriginalMonitorLayout")
            .GetMethod("CaptureUnclippedDesignSize", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var scale in new[] { 0.5F, 1F, 1.5F })
        using (var form = new Form { AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(900, 600) })
        {
            var table = new TableLayoutPanel { Name = "uiTableLayoutPanel1", ColumnCount = 2, RowCount = 3 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18 * scale));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1084 * scale));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 18 * scale));
            form.Controls.Add(table);
            var baseline = (Size)capture.Invoke(null, new object[] { form });
            Assert(baseline == new Size((int)Math.Round(2808 * scale), (int)Math.Round(1682 * scale)));
            form.ClientSize = new Size(1200, 700);
            Assert((Size)capture.Invoke(null, new object[] { form }) == baseline);
        }
    }

    private static void MonitorStartupSelection()
    {
        var fake = ConfigurationClient();
        fake.Value.Engine.HardwareInitialized = false;
        fake.Value.StatusDetail = "后台初始化失败：DaqFrequency missing or invalid";
        fake.Value.Curves = Array.Empty<EngineUiCurve>();
        foreach (var channel in fake.Value.Channels) channel.CountsValid = false;
        using (var session = new V3MonitorSession(fake))
        using (var form = new FrmEpbMainMonitor(session, Path.Combine(Path.GetTempPath(), RecoveryProtocolV7.NewId(), "display.xml")))
        {
            session.RefreshAsync().GetAwaiter().GetResult();
            var close = form.Controls.Find("BtnCloseMonitor", true).Single();
            Assert(close.Text == "关闭监控" && close.Enabled && !session.CanStart);
            Func<int, Control> checkbox = channel => form.Controls.Find("CheckEpbA" + channel, true).Single();
            Func<Control, bool> isChecked = control => (bool)control.GetType().GetProperty("Checked").GetValue(control);
            foreach (var channel in fake.Value.Channels)
                Assert(isChecked(checkbox(channel.Channel)) == channel.Selected);
            var graph = form.Controls.Find("zedGraphRealChart", true).Single();
            var pane = graph.GetType().GetProperty("GraphPane").GetValue(graph);
            var firstCurve = ((System.Collections.IEnumerable)pane.GetType().GetProperty("CurveList").GetValue(pane)).Cast<object>().First();
            Assert(!(bool)firstCurve.GetType().GetProperty("IsVisible").GetValue(firstCurve));
            Assert(form.Controls.Find("RtbInfo", true).Single().Text.Contains("DaqFrequency"));
            var draw = checkbox(4);
            // Binding refresh must not undo a local display choice.
            WriteField(form, "_binding", true); draw.GetType().GetProperty("Checked").SetValue(draw, false); WriteField(form, "_binding", false);
            session.RefreshAsync().GetAwaiter().GetResult(); Assert(!isChecked(draw));
            fake.Value.Channels[0].Selected = true;
            fake.Value.Channels[3].Selected = false;
            session.RefreshAsync().GetAwaiter().GetResult();
            Assert(isChecked(checkbox(1)) && !isChecked(draw));
            Assert((bool)firstCurve.GetType().GetProperty("IsVisible").GetValue(firstCurve));
        }
    }

    private static object InvokePrivate(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args);
    private static T ReadField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    private static void WriteField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

    private static void FeaturePlaybackReadonly()
    {
        var root = Path.Combine(Path.GetTempPath(), "MTTFTest.Playback." + RecoveryProtocolV7.NewId()); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Stat.bin"); var bad = Path.Combine(root, "truncated.bin");
        try
        {
            using (var writer = new BinaryWriter(File.Create(source)))
                for (var n = 0; n < 2; n++)
                {
                    writer.Write(21508 + n); writer.Write(DateTime.UtcNow.AddSeconds(n).ToFileTimeUtc());
                    writer.Write(25.5 + n); writer.Write(4.5 + n); writer.Write(8.5 + n); writer.Write(6.5 + n); writer.Write(new byte[33]);
                }
            var original = File.ReadAllBytes(source); File.SetAttributes(source, FileAttributes.ReadOnly);
            using (var form = new FrmPlayBack())
            {
                InvokePrivate(form, "InitializeCurve"); InvokePrivate(form, "ReadData", source);
                Assert(ReadField<int[]>(form, "BrakeNo").SequenceEqual(new[] { 21508, 21509 }));
                Assert(ReadField<double[]>(form, "DaqCurrent").SequenceEqual(new[] { 6.5, 7.5 }));
                InvokePrivate(form, "bgwA_Completed", null, new System.ComponentModel.RunWorkerCompletedEventArgs(null, null, false));
                var args = new object[] { ReadField<DateTime[]>(form, "SourceTime"), ReadField<double[]>(form, "RelTime"),
                    ReadField<int[]>(form, "BrakeNo"), ReadField<double[]>(form, "CanForce"), ReadField<double[]>(form, "CanCurrent"),
                    ReadField<double[]>(form, "DaqCurrent"), ReadField<double[]>(form, "DaqTorque"), Path.Combine(root, "export.csv") };
                var previousCulture = Thread.CurrentThread.CurrentCulture;
                try { Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR"); InvokePrivate(form, "ExportData", args); }
                finally { Thread.CurrentThread.CurrentCulture = previousCulture; }
                Assert(File.ReadAllLines((string)args[7]).Skip(1).All(l => l.Split(',').Length == 7));
                args[7] = source;
                try { InvokePrivate(form, "ExportData", args); throw new Exception("Source overwrite accepted"); }
                catch (TargetInvocationException ex) { Assert(ex.InnerException is InvalidOperationException); }
                File.Copy(source, bad); File.SetAttributes(bad, FileAttributes.Normal);
                using (var stream = new FileStream(bad, FileMode.Open, FileAccess.Write)) stream.SetLength(153);
                try { InvokePrivate(form, "ReadData", bad); throw new Exception("Truncated feature accepted"); }
                catch (TargetInvocationException ex) { Assert(ex.InnerException is InvalidDataException); }
                Assert(original.SequenceEqual(File.ReadAllBytes(source)));
            }
        }
        finally { File.SetAttributes(source, FileAttributes.Normal); Directory.Delete(root, true); }
    }

    private static void RawPlaybackReadonly()
    {
        var root = Path.Combine(Path.GetTempPath(), "MTTFTest.RawPlayback." + RecoveryProtocolV7.NewId()); Directory.CreateDirectory(root);
        try
        {
            foreach (var channel in new[] { 4, 12 })
            {
                var source = Path.Combine(root, "raw" + channel + ".bin");
                using (var writer = new BinaryWriter(File.Create(source)))
                    for (var n = 0; n < 2; n++)
                    {
                        writer.Write(21508 + n); writer.Write(DateTime.UtcNow.AddSeconds(n).ToFileTimeUtc());
                        for (var row = 0; row < (channel <= 8 ? 8 : 7); row++) writer.Write((double)(10 * n + row));
                    }
                var original = File.ReadAllBytes(source); File.SetAttributes(source, FileAttributes.ReadOnly);
                try
                {
                    using (var form = new FrmRawPlayBack())
                    {
                        WriteField(form, "EpbNo", channel); WriteField(form, "EpbName", "EPB" + channel);
                        foreach (var name in new[] { "ParaNameToScale", "ParaNameToOffset", "ParaNameToZeroValue" })
                        {
                            var map = ReadField<System.Collections.Concurrent.ConcurrentDictionary<string, double>>(form, name);
                            map["EPB" + channel] = name.EndsWith("Scale") ? 2 : name.EndsWith("Offset") ? 1 : 0.5;
                        }
                        InvokePrivate(form, "InitializeCurve"); InvokePrivate(form, "ReadData", source);
                        Assert(ReadField<double[]>(form, "DaqCurrent").SequenceEqual(new[] { 6.0, 26.0 }));
                        InvokePrivate(form, "bgwA_Completed", null, new System.ComponentModel.RunWorkerCompletedEventArgs(null, null, false));
                        Assert(ReadField<double[]>(form, "filterCurrent").Length > 0 && ReadField<DateTime[]>(form, "FilterDaqTime").Length > 0);
                        var export = Path.Combine(root, "export" + channel + ".csv");
                        InvokePrivate(form, "ExportDaqData", ReadField<DateTime[]>(form, "FilterDaqTime"),
                            ReadField<double[]>(form, "FilterDaqRelTime"), ReadField<int[]>(form, "FilterDaqBrakeNo"),
                            ReadField<double[]>(form, "filterCurrent"), export);
                        Assert(File.ReadAllLines(export).Length > 1 && original.SequenceEqual(File.ReadAllBytes(source)));
                        InvokePrivate(form, "bgwA_Completed", null,
                            new System.ComponentModel.RunWorkerCompletedEventArgs(null, new IOException("synthetic truncated"), false));
                        Assert(ReadField<double[]>(form, "filterCurrent") == null);
                    }
                }
                finally { File.SetAttributes(source, FileAttributes.Normal); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task SingleFlight()
    {
        var fake = new FakeClient { Delay = new TaskCompletionSource<bool>() };
        using (var session = new V3MonitorSession(fake))
        {
            var first = session.RefreshAsync(); await session.RefreshAsync(); Assert(fake.Reads == 1);
            fake.Delay.SetResult(true); await first; Assert(session.Latest.TestName == "10243-028");
        }
    }

    private static async Task PipeCase(bool foreign)
    {
        var value = Snapshot();
        var name = "MTTFTest.UiTest." + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var exchange = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                var reader = new BinaryReader(server, Encoding.UTF8, true);
                var json = new JavaScriptSerializer();
                var request = json.Deserialize<EngineHostRequest>(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
                var response = new EngineHostResponse { Accepted = true, RequestId = foreign ? RecoveryProtocolV7.NewId() : request.RequestId, UiSnapshot = value };
                var bytes = Encoding.UTF8.GetBytes(json.Serialize(response));
                var writer = new BinaryWriter(server, Encoding.UTF8, true); writer.Write(bytes.Length); writer.Write(bytes); writer.Flush();
            });
            var identity = V3EngineIdentity.Parse(new[] { SessionAgentProtocol.SessionArgument, value.Engine.SessionId,
                "--engine-run", value.Engine.RunId, "--engine-epoch", "1" });
            var client = new V3EngineHostClient(identity, name);
            var rejected = false;
            try { var result = await client.ReadUiSnapshotAsync(CancellationToken.None); Assert(result.Channels[3].MechanicalCycles == 21508); }
            catch (InvalidDataException) { rejected = true; }
            await exchange; Assert(rejected == foreign);
        }
    }

    private static async Task LogPipeCase()
    {
        foreach (var foreign in new[] { false, true })
        {
            var engine = Snapshot().Engine; var expectedId = engine.EngineInstanceId;
            var name = "MTTFTest.UiLogTest." + RecoveryProtocolV7.NewId();
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var exchange = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync();
                    var reader = new BinaryReader(server, Encoding.UTF8, true); var json = new JavaScriptSerializer();
                    var request = json.Deserialize<EngineHostRequest>(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
                    Assert(request.Kind == EngineHostRequestKind.ReadUiLogs && request.UiLogQuery.BeforeSequence == 100 &&
                        request.UiLogQuery.Level == "WARN" && request.UiLogQuery.PageSize == 7);
                    var copy = json.Deserialize<EngineStateSnapshot>(json.Serialize(engine));
                    if (foreign) copy.EngineInstanceId = RecoveryProtocolV7.NewId();
                    var response = new EngineHostResponse { RequestId = request.RequestId, Accepted = true, Snapshot = copy,
                        UiLogPage = new EngineUiLogPage { Level = "WARN", BeforeSequence = 100, OldestRetainedSequence = 1,
                            NewestSequence = 100, Entries = new[] { new EngineUiLogEntry { Sequence = 98, Level = "WARN",
                                CapturedUtcTicks = DateTime.UtcNow.Ticks, Message = "synthetic" } } } };
                    var bytes = Encoding.UTF8.GetBytes(json.Serialize(response));
                    var writer = new BinaryWriter(server, Encoding.UTF8, true); writer.Write(bytes.Length); writer.Write(bytes); writer.Flush();
                });
                var client = new V3EngineHostClient(V3EngineIdentity.Parse(new[] { SessionAgentProtocol.SessionArgument, engine.SessionId,
                    "--engine-run", engine.RunId, "--engine-epoch", "1" }), name);
                var rejected = false;
                try
                {
                    var page = await client.ReadLogsAsync(new EngineUiLogQuery { Level = "WARN", BeforeSequence = 100, PageSize = 7 },
                        engine, CancellationToken.None); Assert(page.Entries.Single().Sequence == 98);
                }
                catch (InvalidDataException) { rejected = true; }
                await exchange; Assert(rejected == foreign && engine.EngineInstanceId == expectedId);
            }
        }
    }

    private static async Task LateLogPage()
    {
        var fake = new FakeClient();
        using (var session = new V3MonitorSession(fake))
        {
            await session.RefreshAsync();
            fake.PendingLogPage = new TaskCompletionSource<EngineUiLogPage>();
            var pending = session.ReadLogPageAsync("WARN", 0);
            session.FollowLiveLogs(); fake.PendingLogPage.SetResult(new EngineUiLogPage { Level = "WARN" });
            await pending; Assert(session.LogPage == null);
            fake.PendingLogPage = new TaskCompletionSource<EngineUiLogPage>();
            pending = session.ReadLogPageAsync("WARN", 0);
            fake.Value.Engine.EngineInstanceId = RecoveryProtocolV7.NewId(); await session.RefreshAsync();
            fake.PendingLogPage.SetResult(new EngineUiLogPage { Level = "WARN" });
            await pending; Assert(session.LogPage == null);
        }
    }

    private static async Task ReplacementAttachment(bool changeRun = false)
    {
        var first = Snapshot();
        var json = new JavaScriptSerializer();
        var replacement = json.Deserialize<EngineUiSnapshot>(json.Serialize(first));
        replacement.Engine.RunEpoch++;
        if (changeRun) replacement.Engine.RunId = RecoveryProtocolV7.NewId();
        replacement.Engine.EngineInstanceId = RecoveryProtocolV7.NewId();
        var engineName = "MTTFTest.UiReplace." + Guid.NewGuid().ToString("N");
        var supervisorName = engineName + ".Supervisor";
        var engineTask = Task.Run(async () =>
        {
            foreach (var value in new[] { first, replacement, replacement })
                using (var pipe = new NamedPipeServerStream(engineName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    await pipe.WaitForConnectionAsync();
                    var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                    var request = json.Deserialize<EngineHostRequest>(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
                    var bytes = Encoding.UTF8.GetBytes(json.Serialize(new EngineHostResponse
                    { RequestId = request.RequestId, Accepted = true, UiSnapshot = value }));
                    var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
                    writer.Write(bytes.Length); writer.Write(bytes); writer.Flush();
                }
        });
        var supervisorTask = Task.Run(async () =>
        {
            foreach (var value in new[] { first, first, replacement, replacement })
                using (var pipe = new NamedPipeServerStream(supervisorName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    await pipe.WaitForConnectionAsync();
                    var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                    var magic = reader.ReadString();
                    var request = SupervisorUiAttachmentRequest.ReadBodyFrom(reader);
                    Assert(request.IsStructurallyValid() && request.RequesterProcessId == PipePeerIdentity.ClientProcessId(pipe));
                    if (magic == SupervisorUiAttachmentRequest.StateRequestMagic)
                    {
                        new SupervisorUiStateResponse
                        {
                            RequestId = request.RequestId, ChallengeNonce = request.ChallengeNonce,
                            Accepted = true, State = KernelState(value.Engine)
                        }.WriteTo(new BinaryWriter(pipe, Encoding.UTF8, true));
                        continue;
                    }
                    Assert(magic == SupervisorUiAttachmentRequest.Magic);
                    using (var process = Process.GetCurrentProcess())
                        new SupervisorUiAttachmentResponse
                        {
                            RequestId = request.RequestId, ChallengeNonce = request.ChallengeNonce,
                            Accepted = true, Engine = value.Engine, EngineProcessId = process.Id,
                            ApprovedDesiredState = KernelState(value.Engine),
                            EngineProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks
                        }.WriteTo(new BinaryWriter(pipe, Encoding.UTF8, true));
                }
        });
        var identity = V3EngineIdentity.Parse(new[] { SessionAgentProtocol.SessionArgument, first.Engine.SessionId,
            "--engine-run", first.Engine.RunId, "--engine-epoch", "1" });
        var client = new V3EngineHostClient(identity, engineName, supervisorName);
        Assert((await client.ReadUiSnapshotAsync(CancellationToken.None)).Engine.RunEpoch == 1);
        var rejected = false;
        try { await client.ReadUiSnapshotAsync(CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected);
        Assert((await client.ReadUiSnapshotAsync(CancellationToken.None)).Engine.RunEpoch == 2);
        await Task.WhenAll(engineTask, supervisorTask);
    }

    private static void AttachmentPolicy()
    {
        var snapshot = Snapshot().Engine;
        var previousRun = snapshot.RunId;
        var response = new SupervisorUiAttachmentResponse { Accepted = true, Engine = snapshot,
            EngineProcessId = 1, EngineProcessStartUtcTicks = 1, ApprovedDesiredState = KernelState(snapshot) };
        Assert(response.ApprovesRun(snapshot.SessionId, previousRun, 1, DateTime.UtcNow.Ticks));
        response.ApprovedDesiredState = null;
        Assert(!response.ApprovesRun(snapshot.SessionId, previousRun, 1, DateTime.UtcNow.Ticks));
        response.InitialObservationOnly = true;
        snapshot.State = SystemTerminalState.SafeIdleAlarmed; snapshot.HardwareInitialized = false;
        Assert(response.ApprovesRun(snapshot.SessionId, previousRun, 1, DateTime.UtcNow.Ticks));
        snapshot.OutputsEnergized = true;
        Assert(!response.ApprovesRun(snapshot.SessionId, previousRun, 1, DateTime.UtcNow.Ticks));
        snapshot.OutputsEnergized = false;
        Assert(!response.ApprovesRun(snapshot.SessionId, RecoveryProtocolV7.NewId(), 1, DateTime.UtcNow.Ticks));
        response.InitialObservationOnly = false;
        snapshot.RunId = RecoveryProtocolV7.NewId(); snapshot.RunEpoch = 2;
        response.ApprovedDesiredState = KernelState(snapshot);
        Assert(response.ApprovesRun(snapshot.SessionId, previousRun, 1, DateTime.UtcNow.Ticks));
        Assert(!response.ApprovesRun(snapshot.SessionId, previousRun, 2, DateTime.UtcNow.Ticks));
        Assert(!response.ApprovesRun(snapshot.SessionId, snapshot.RunId, 3, DateTime.UtcNow.Ticks));
        response.ApprovedDesiredState.RunId = previousRun;
        Assert(!response.ApprovesRun(snapshot.SessionId, previousRun, 1, DateTime.UtcNow.Ticks));
        response.ApprovedDesiredState = KernelState(snapshot); response.ApprovedDesiredState.CapturedUtcTicks = DateTime.UtcNow.AddSeconds(-4).Ticks;
        Assert(!response.ApprovesRun(snapshot.SessionId, previousRun, 1, DateTime.UtcNow.Ticks));
    }

    private static async Task LostOperatorResponse(bool wasCommitted)
    {
        var snapshot = Snapshot().Engine;
        var name = "MTTFTest.UiOperator." + Guid.NewGuid().ToString("N");
        var command = new OperatorCommand
        {
            CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId, RunId = snapshot.RunId,
            RunEpoch = 1, BaseRevision = 1, Kind = OperatorCommandKind.Start,
            PayloadSha256 = SupervisorProtocol.ComputeTextSha256("OperatorStart"), IssuedUtcTicks = DateTime.UtcNow.Ticks
        };
        var requestCount = 0;
        var server = Task.Run(async () =>
        {
            try
            {
            for (var index = 0; index < (wasCommitted ? 2 : 3); index++)
                using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    await pipe.WaitForConnectionAsync();
                    var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                    var request = SupervisorOperatorCommandRequest.ReadBodyFrom(reader, reader.ReadString());
                    requestCount++;
                    Assert(request.Command.CommandId == command.CommandId &&
                        OperatorCommandAdmission.GetFingerprint(request.Command) == OperatorCommandAdmission.GetFingerprint(command));
                    Assert(request.QueryOnly == (index == 1));
                    if (index == 0) continue; // Original response lost after either commit or before admission.
                    new SupervisorOperatorCommandResponse
                    {
                        RequestId = request.RequestId, ChallengeNonce = request.ChallengeNonce,
                        Accepted = wasCommitted || index == 2,
                        FailureCode = !wasCommitted && index == 1 ? "OperatorCommandNotFound" : string.Empty,
                        OwnerId = RecoveryProtocolV7.NewId(), IncidentId = RecoveryProtocolV7.NewId(), DesiredState = SystemTerminalState.Running
                    }.WriteTo(new BinaryWriter(pipe, Encoding.UTF8, true));
                }
            }
            catch (Exception ex) { Console.WriteLine("Operator test peer failed: " + ex); throw; }
        });
        var identity = V3EngineIdentity.Parse(new[] { SessionAgentProtocol.SessionArgument, snapshot.SessionId,
            "--engine-run", snapshot.RunId, "--engine-epoch", "1" });
        var client = new V3EngineHostClient(identity, name + ".UnusedEngine", name);
        var failed = false;
        try { await client.SubmitTransactionAsync(command, CancellationToken.None); } catch (IOException) { failed = true; }
        Assert(failed && client.HasUnresolvedCommands);
        Assert((await client.ResolvePendingAsync(CancellationToken.None)).Accepted);
        Assert(!client.HasUnresolvedCommands);
        await server;
        Assert(requestCount == (wasCommitted ? 2 : 3));
    }

    private static async Task StopWhileStartPending()
    {
        var fake = new FakeClient { PendingStart = new TaskCompletionSource<SupervisorOperatorCommandResponse>() };
        using (var session = new V3MonitorSession(fake))
        {
            await session.RefreshAsync();
            var start = session.SubmitAsync(OperatorCommandKind.Start);
            Assert(session.CommandPending && !session.CanStart && session.CanStop);
            await session.SubmitAsync(OperatorCommandKind.Start);
            Assert(fake.Commands == 1);
            await session.SubmitAsync(OperatorCommandKind.Stop);
            Assert(fake.Commands == 2);
            var stopMessage = session.OperationMessage;
            fake.PendingStart.SetResult(new SupervisorOperatorCommandResponse { Accepted = true, DesiredState = SystemTerminalState.Running });
            await start;
            Assert(session.OperationMessage == stopMessage && !session.CommandPending);
        }
    }

    private static EngineTestConfiguration Settings() => new EngineTestConfiguration
    {
        TestName = "10243-028", StoreDir = "D:\\Synthetic", TestPeriod = 15, TestTarget = 200000, Owner = "Synthetic",
        Channels = Enumerable.Range(1, 12).Select(c => new EngineRunnerConfiguration
        { Channel = c, Name = "EPB-" + c, Selected = c != 6, TargetTotalCount = 200000 + c, ForwardA = c }).ToArray(),
        Hydraulics = new[] { new EngineHydraulicSetting { Id = 1, Enabled = true, PressureThresholdBar = 70 },
            new EngineHydraulicSetting { Id = 2, Enabled = true, PressureThresholdBar = 40 } }
    };

    private static void ConfigurationWireRoundTrip()
    {
        var settings = Settings(); var snapshot = Snapshot().Engine;
        var payload = new TestConfigurationCommit { Configuration = settings, BaseConfigurationRevision = 17, BaseConfigurationSha256 = settings.ComputeSha256() };
        var command = new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId, RunId = snapshot.RunId,
            RunEpoch = 1, Kind = OperatorCommandKind.CommitConfiguration, TestConfiguration = payload, PayloadSha256 = payload.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks };
        foreach (var query in new[] { false, true })
            using (var stream = new MemoryStream())
            {
                new SupervisorOperatorCommandRequest { RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(),
                    RequesterProcessId = 100, RequesterProcessStartUtcTicks = 1, Command = command, QueryOnly = query }.WriteTo(new BinaryWriter(stream));
                stream.Position = 0; var reader = new BinaryReader(stream);
                var decoded = SupervisorOperatorCommandRequest.ReadBodyFrom(reader, reader.ReadString());
                Assert(decoded.IsStructurallyValid() && decoded.QueryOnly == query &&
                    OperatorCommandAdmission.GetFingerprint(decoded.Command) == OperatorCommandAdmission.GetFingerprint(command));
                decoded.Command.TestConfiguration.Configuration.TestTarget++;
                Assert(!decoded.Command.IsStructurallyValid());
            }
    }

    private static void ManualBatchWireRoundTrip()
    {
        var engine = Snapshot().Engine;
        foreach (var kind in new[] { OperatorCommandKind.Pause, OperatorCommandKind.Resume, OperatorCommandKind.PauseChannel,
                     OperatorCommandKind.ResumeChannel, OperatorCommandKind.RetryQualification })
        {
            var payload = new ManualBatchCommand { EngineInstanceId = engine.EngineInstanceId,
                Channel = ManualBatchCommand.IsChannelOperation(kind) ? 12 : 0,
                PauseIncidentId = !ManualBatchCommand.IsPause(kind) && kind != OperatorCommandKind.RetryQualification ? RecoveryProtocolV7.NewId() : string.Empty,
                PauseOwnerId = !ManualBatchCommand.IsPause(kind) && kind != OperatorCommandKind.RetryQualification ? RecoveryProtocolV7.NewId() : string.Empty };
            var command = new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = engine.SessionId, RunId = engine.RunId,
                RunEpoch = 1, BaseRevision = 9, Kind = kind, IssuedUtcTicks = DateTime.UtcNow.Ticks,
                ManualBatch = payload, PayloadSha256 = payload.ComputeSha256() };
            foreach (var query in new[] { false, true })
                using (var stream = new MemoryStream())
                {
                    new SupervisorOperatorCommandRequest { RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(),
                        RequesterProcessId = 100, RequesterProcessStartUtcTicks = 1, Command = command, QueryOnly = query }.WriteTo(new BinaryWriter(stream));
                    stream.Position = 0; var reader = new BinaryReader(stream);
                    var decoded = SupervisorOperatorCommandRequest.ReadBodyFrom(reader, reader.ReadString());
                    Assert(decoded.IsStructurallyValid() && decoded.QueryOnly == query &&
                        OperatorCommandAdmission.GetFingerprint(decoded.Command) == OperatorCommandAdmission.GetFingerprint(command));
                    decoded.Command.ManualBatch.EngineInstanceId = RecoveryProtocolV7.NewId();
                    Assert(!decoded.Command.IsStructurallyValid());
                }
        }
    }

    private static FakeClient ManualBatchClient()
    {
        var fake = new FakeClient { ManualControls = true };
        fake.Value.Capabilities = new[] { EngineUiContract.Monitor, EngineUiContract.ManualBatchControl };
        fake.Value.Engine.State = fake.Value.Kernel.DesiredState = SystemTerminalState.Running;
        fake.Value.Engine.OutputsEnergized = true; fake.Value.BatchPauseAvailable = true;
        return fake;
    }

    private static void ManualChannelOriginalButtons()
    {
        var fake = ManualBatchClient();
        fake.Value.Capabilities = fake.Value.Capabilities.Concat(new[] { EngineUiContract.ManualChannelControl }).ToArray();
        fake.Value.Engine.ChannelPauseMask = 4095;
        foreach (var channel in fake.Value.Channels) { channel.Running = channel.Selected = true; channel.Isolated = false; }
        var counts = fake.Value.Channels.Select(channel => channel.FormalCycles).ToArray();
        using (var session = new V3MonitorSession(fake))
        using (var form = new FrmEpbMainMonitor(session, Path.Combine(Path.GetTempPath(), "MTTFTest.ChannelUi." + RecoveryProtocolV7.NewId() + ".xml")))
        {
            session.RefreshAsync().GetAwaiter().GetResult();
            for (var channel = 1; channel <= 12; channel++)
            {
                var toggle = form.Controls.Find("SwitchEpb" + channel, true).Single();
                Assert(toggle.Enabled);
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(toggle, new object[] { EventArgs.Empty });
                Assert(fake.LastChannel == channel && fake.LastBatchKind == OperatorCommandKind.PauseChannel && !session.CanClose && session.CanPause);
                Assert(fake.Value.Channels.Where(value => value.Channel != channel).All(value => value.Running));
                Assert(!fake.Value.Channels[channel - 1].Running && session.CanOperateChannel(channel));
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(toggle, new object[] { EventArgs.Empty });
                Assert(fake.LastBatchKind == OperatorCommandKind.ResumeChannel && fake.Value.Channels.All(value => value.Running));
            }
            Assert(fake.Commands == 24 && fake.Value.Channels.Select(channel => channel.FormalCycles).SequenceEqual(counts));
            session.SubmitChannelActionAsync(4).GetAwaiter().GetResult();
            fake.Value.Engine.EngineInstanceId = RecoveryProtocolV7.NewId(); session.RefreshAsync().GetAwaiter().GetResult();
            Assert(!session.CanOperateChannel(4) && !form.Controls.Find("SwitchEpb4", true).Single().Enabled);
        }
    }

    private static void QualificationRetryOriginalButton()
    {
        var fake = ManualBatchClient();
        fake.Value.Capabilities = fake.Value.Capabilities.Concat(new[] { EngineUiContract.ManualChannelControl,
            EngineUiContract.ChannelQualificationRecovery }).ToArray();
        fake.Value.Engine.State = fake.Value.Kernel.DesiredState = SystemTerminalState.SafeIdleAlarmed;
        fake.Value.Engine.OutputsEnergized = false;
        fake.Value.Engine.IsolatedResources = new[] { "Channel:6" };
        fake.Value.Kernel.IsolatedResources = new[] { "Channel:6" };
        fake.Value.Kernel.QualificationRetryEligibleScopes = new[] { "Channel:6" };
        var isolated = fake.Value.Channels.Single(value => value.Channel == 6);
        isolated.Selected = false; isolated.Isolated = true; isolated.RemainingCycles = 178000;
        var counts = fake.Value.Channels.Select(value => value.FormalCycles).ToArray();
        using (var session = new V3MonitorSession(fake))
        using (var form = new FrmEpbMainMonitor(session, Path.Combine(Path.GetTempPath(),
                   "MTTFTest.QualificationRetryUi." + RecoveryProtocolV7.NewId() + ".xml")))
        {
            var confirms = 0;
            form.ConfirmQualificationRetry = channel => { confirms++; return false; };
            session.RefreshAsync().GetAwaiter().GetResult();
            var toggle = form.Controls.Find("SwitchEpb6", true).Single();
            Assert(toggle.Enabled && session.CanRetryQualification(6) && !session.CanRetryQualification(5));
            ClickControl(toggle);
            Assert(fake.Commands == 0 && confirms == 1 && fake.Value.Channels[5].Isolated);
            form.ConfirmQualificationRetry = channel => { confirms++; return channel == 6; };
            ClickControl(toggle);
            Assert(fake.Commands == 1 && confirms == 2 && fake.LastBatchKind == OperatorCommandKind.RetryQualification &&
                fake.LastChannel == 6 && !fake.Value.Channels[5].Isolated && fake.Value.Channels[5].Running &&
                fake.Value.Channels.Select(value => value.FormalCycles).SequenceEqual(counts));
        }
    }

    private static void ManualBatchOriginalButton()
    {
        var fake = ManualBatchClient();
        var formal = fake.Value.Channels.Select(channel => channel.FormalCycles).ToArray();
        var mechanical = fake.Value.Channels.Select(channel => channel.MechanicalCycles).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "MTTFTest.ManualUi." + RecoveryProtocolV7.NewId() + ".xml");
        using (var session = new V3MonitorSession(fake))
        using (var form = new FrmEpbMainMonitor(session, path))
        {
            session.RefreshAsync().GetAwaiter().GetResult();
            ExpectText(form, "BtnStartTest", "暂停试验"); Assert(session.CanPause && !session.CanClose && !session.CanStart);
            var button = form.Controls.Find("BtnStartTest", true).Single();
            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
            Assert(fake.Commands == 1 && fake.LastBatchKind == OperatorCommandKind.Pause && session.CanResume && !session.CanClose && !session.CanConfigure);
            ExpectText(form, "BtnStartTest", "继续试验");
            fake.Value.Engine.EngineInstanceId = RecoveryProtocolV7.NewId(); session.RefreshAsync().GetAwaiter().GetResult();
            Assert(!session.CanResume && !button.Enabled);
            fake.Value.Engine.EngineInstanceId = fake.Value.Kernel.ManualBatchEngineInstanceId; session.RefreshAsync().GetAwaiter().GetResult();
            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
            Assert(fake.Commands == 2 && fake.LastBatchKind == OperatorCommandKind.Resume && session.CanPause);
            ExpectText(form, "BtnStartTest", "暂停试验");
            Assert(session.Latest.Channels.Select(channel => channel.FormalCycles).SequenceEqual(formal) &&
                session.Latest.Channels.Select(channel => channel.MechanicalCycles).SequenceEqual(mechanical));
        }
    }

    private static async Task StopWhilePausePending()
    {
        var fake = ManualBatchClient(); fake.PendingManual = new TaskCompletionSource<SupervisorOperatorCommandResponse>();
        using (var session = new V3MonitorSession(fake))
        {
            await session.RefreshAsync();
            var pause = session.SubmitBatchActionAsync();
            Assert(!pause.IsCompleted && session.CanStop && !session.CanPause);
            await session.SubmitBatchActionAsync(); Assert(fake.Commands == 1);
            await session.SubmitAsync(OperatorCommandKind.Stop);
            Assert(fake.Commands == 2 && fake.LastBatchKind == OperatorCommandKind.Stop && session.CanClose);
            var message = session.OperationMessage;
            fake.PendingManual.SetResult(new SupervisorOperatorCommandResponse { Accepted = true, Detail = "late pause" }); await pause;
            Assert(session.OperationMessage == message && session.CanClose && !session.CanResume);
        }
    }

    private static void AlarmPanelWireRoundTrip()
    {
        var engine = Snapshot().Engine;
        foreach (var kind in new[] { OperatorCommandKind.AcknowledgeAlarms, OperatorCommandKind.SetBuzzerEnabled })
        {
            var payload = new AlarmPanelCommand { PanelInstanceId = RecoveryProtocolV7.NewId(), BaseRevision = 17, BuzzerEnabled = true };
            var command = new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = engine.SessionId, RunId = engine.RunId,
                RunEpoch = engine.RunEpoch, Kind = kind, AlarmPanel = payload, PayloadSha256 = payload.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks };
            using (var stream = new MemoryStream())
            {
                new SupervisorOperatorCommandRequest { RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(),
                    RequesterProcessId = 100, RequesterProcessStartUtcTicks = 1, Command = command }.WriteTo(new BinaryWriter(stream));
                stream.Position = 0; var reader = new BinaryReader(stream);
                var decoded = SupervisorOperatorCommandRequest.ReadBodyFrom(reader, reader.ReadString());
                Assert(decoded.IsStructurallyValid() && decoded.Command.Kind == kind && decoded.Command.AlarmPanel.BaseRevision == 17);
                decoded.Command.AlarmPanel.BaseRevision++; Assert(!decoded.Command.IsStructurallyValid());
            }
            using (var stream = new MemoryStream())
            {
                new SupervisorOperatorCommandResponse { Accepted = true, ExecutionCompleted = true, ExecutionSucceeded = false,
                    Detail = "synthetic write failed" }.WriteTo(new BinaryWriter(stream));
                stream.Position = 0; var response = SupervisorOperatorCommandResponse.ReadFrom(new BinaryReader(stream));
                Assert(response.Accepted && response.ExecutionCompleted && !response.ExecutionSucceeded && response.Detail == "synthetic write failed");
            }
        }
    }

    private static void AlarmPanelControls()
    {
        var fake = new FakeClient(); fake.Value.Capabilities = new[] { EngineUiContract.Monitor, EngineUiContract.AlarmCommands };
        fake.Value.AlarmPanel = new AlarmPanelStatus { Available = true, PanelInstanceId = RecoveryProtocolV7.NewId(),
            Revision = 17, BuzzerEnabled = true, ActiveChannels = new[] { 4, 6 } };
        fake.Value.Engine.State = SystemTerminalState.Running; fake.Value.Engine.OutputsEnergized = true;
        using (var session = new V3MonitorSession(fake))
        {
            session.RefreshAsync().GetAwaiter().GetResult(); Assert(session.CanOperateAlarmPanel && !session.CanConfigure);
            using (var form = new FrmEpbMainMonitor(session, Path.Combine(Path.GetTempPath(), "MTTFTest.PanelUi." + RecoveryProtocolV7.NewId() + ".xml")))
            {
                InvokePrivate(form, "Render");
                var buzzer = form.Controls.Find("CbBuzzerEnabled", true).Single();
                var checkedProperty = buzzer.GetType().GetProperty("Checked");
                Assert((bool)checkedProperty.GetValue(buzzer) && buzzer.Enabled && fake.Commands == 0);
                InvokePrivate(form, "BtnClearAlarms_Click", form, EventArgs.Empty);
                Assert(fake.Commands == 1 && fake.LastPanelKind == OperatorCommandKind.AcknowledgeAlarms && fake.LastPanel.BaseRevision == 17);
                checkedProperty.SetValue(buzzer, false);
                Assert(fake.Commands == 2 && fake.LastPanelKind == OperatorCommandKind.SetBuzzerEnabled && !fake.LastPanel.BuzzerEnabled);
                Assert(fake.Value.Channels[5].Isolated && fake.Value.Channels[3].FormalCycles == 21504 && fake.Value.Engine.OutputsEnergized);
                fake.Value.AlarmPanel.Available = false; session.RefreshAsync().GetAwaiter().GetResult();
                Assert(!buzzer.Enabled && !form.Controls.Find("BtnClearAlarms", true).Single().Enabled);
            }
        }
    }

    private static async Task AlarmPanelPendingQuery(bool projectSwitch = false, bool creation = false, bool reset = false, bool daq = false, bool ao = false)
    {
        var engine = Snapshot().Engine;
        var identity = V3EngineIdentity.Parse(new[] { SessionAgentProtocol.SessionArgument, engine.SessionId,
            "--engine-run", engine.RunId, "--engine-epoch", "1" });
        var pipeName = "MTTFTest.PanelUiTests." + RecoveryProtocolV7.NewId();
        var payload = new AlarmPanelCommand { PanelInstanceId = RecoveryProtocolV7.NewId(), BaseRevision = 17 };
        var command = new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = engine.SessionId, RunId = engine.RunId,
            RunEpoch = 1, Kind = OperatorCommandKind.AcknowledgeAlarms, AlarmPanel = payload,
            PayloadSha256 = payload.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks };
        if (projectSwitch)
        {
            command.Kind = OperatorCommandKind.SwitchProject; command.AlarmPanel = null;
            command.ProjectSwitch = new ProjectSwitchRequest { EngineInstanceId = engine.EngineInstanceId,
                BaseSelectionRevision = 2, BaseConfigurationRevision = 17, BaseConfigurationSha256 = new string('a', 64),
                SourceProjectFileSha256 = new string('b', 64), TargetProjectFileSha256 = new string('c', 64),
                TargetConfigurationPath = @"D:\Synthetic\Target\Config\TestConfig.xml" };
            if (creation)
            {
                command.ProjectSwitch.TargetProjectFileSha256 = string.Empty;
                var settings = Settings(); settings.TestName = "Target";
                command.ProjectSwitch.Creation = new ProjectCreationRequest { Configuration = settings };
            }
            if (reset)
            {
                command.ProjectSwitch.TargetProjectFileSha256 = command.ProjectSwitch.SourceProjectFileSha256;
                var settings = Settings(); settings.TestName = "Target";
                command.ProjectSwitch.Reset = new ProjectResetRequest { Configuration = settings };
            }
            command.PayloadSha256 = command.ProjectSwitch.ComputeSha256();
        }
        if (daq)
        {
            command.Kind = OperatorCommandKind.CommitConfiguration; command.AlarmPanel = null;
            command.TestConfiguration = new TestConfigurationCommit { DaqConfiguration = DaqSettings(),
                BaseConfigurationRevision = 23, BaseConfigurationSha256 = DaqSettings().ComputeSha256() };
            command.PayloadSha256 = command.TestConfiguration.ComputeSha256();
        }
        if (ao)
        {
            command.Kind = OperatorCommandKind.CommitConfiguration; command.AlarmPanel = null;
            var settings = AoSettings();
            command.TestConfiguration = new TestConfigurationCommit { BaseConfigurationRevision = 31, BaseConfigurationSha256 = settings.ComputeSha256(),
                AoCalibration = new AoCalibrationCommit { DeviceName = "Cylinder1", Points = settings.Devices[0].Points } };
            command.PayloadSha256 = command.TestConfiguration.ComputeSha256();
        }
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
                using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    await pipe.WaitForConnectionAsync();
                    using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                    using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                    {
                        var request = SupervisorOperatorCommandRequest.ReadBodyFrom(reader, reader.ReadString());
                        Assert(request.Command.CommandId == command.CommandId && request.QueryOnly == (index == 1) &&
                            OperatorCommandAdmission.GetFingerprint(request.Command) == OperatorCommandAdmission.GetFingerprint(command));
                        new SupervisorOperatorCommandResponse { RequestId = request.RequestId, ChallengeNonce = request.ChallengeNonce,
                            Accepted = true, ExecutionCompleted = index == 1, ExecutionSucceeded = index == 1 }.WriteTo(writer);
                    }
                }
        });
        var client = new V3EngineHostClient(identity, "unused", pipeName);
        var admitted = await client.SubmitTransactionAsync(command, CancellationToken.None);
        Assert(admitted.Accepted && !admitted.ExecutionCompleted && client.HasUnresolvedCommands);
        var complete = await client.ResolvePendingAsync(CancellationToken.None);
        Assert(complete.ExecutionCompleted && complete.ExecutionSucceeded && !client.HasUnresolvedCommands);
        await server;
    }

    private static void SettingsBindWithoutWrites()
    {
        var fake = new FakeClient(); fake.Value.TestConfiguration = Settings();
        fake.Value.ConfigurationRevision = 17; fake.Value.ConfigurationSha256 = fake.Value.TestConfiguration.ComputeSha256();
        fake.Value.Capabilities = new[] { EngineUiContract.Monitor, EngineUiContract.TestConfiguration };
        using (var session = new V3MonitorSession(fake))
        {
            session.RefreshAsync().GetAwaiter().GetResult();
            using (var form = new FrmTestSetting(session))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show();
                Application.DoEvents();
                session.RefreshAsync().GetAwaiter().GetResult();
                ExpectText(form, "TxtTestCycle", "15"); ExpectText(form, "textEditPressureValue1To6", "70");
                var grid = (DataGridView)form.Controls.Find("dgvEpbRunnerCfgControl", true).Single();
                Assert(grid.Rows.Count == 12 && (int)grid.Rows[3].Cells["TargetTotalCount"].Value == 200004);
                Assert(grid.Columns.Count == 13 && !grid.ReadOnly && fake.Commands == 0);
                Assert(!form.Controls.Find("uiCheckBoxEpb6Enabled", true).Single().Enabled);
                form.Controls.Find("TxtTestCycle", true).Single().Text = "19";
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form.Controls.Find("BtnSaveTest", true).Single(), new object[] { EventArgs.Empty });
                Assert(fake.Commands == 1 && fake.LastConfiguration.BaseConfigurationRevision == 17 &&
                    fake.LastConfiguration.Configuration.TestPeriod == 19 &&
                    fake.LastConfiguration.Configuration.Channels[3].TargetTotalCount == 200004 &&
                    fake.Value.Channels[3].FormalCycles == 21504);
                fake.Value.Engine.State = SystemTerminalState.Running; session.RefreshAsync().GetAwaiter().GetResult();
                Assert(grid.ReadOnly && !form.Controls.Find("BtnSaveTest", true).Single().Enabled);
            }
        }
        // A console harness has no GUI pump outside the actual form test.
        SynchronizationContext.SetSynchronizationContext(null);
    }

    private static FakeClient ConfigurationClient()
    {
        var fake = new FakeClient(); fake.Value.TestConfiguration = Settings();
        fake.Value.ConfigurationRevision = 17; fake.Value.ConfigurationSha256 = fake.Value.TestConfiguration.ComputeSha256();
        fake.Value.Capabilities = new[] { EngineUiContract.Monitor, EngineUiContract.TestConfiguration };
        return fake;
    }

    private sealed class ProjectUiFixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "MTTFTest.ProjectUi." + RecoveryProtocolV7.NewId());
        internal readonly string Source;
        internal readonly string Target;
        internal readonly FakeClient Client = ConfigurationClient();
        internal ProjectUiFixture()
        {
            Source = Path.Combine(Root, "Source", "Config", "TestConfig.xml");
            Target = Path.Combine(Root, "Target", "Config", "TestConfig.xml");
            foreach (var path in new[] { Source, Target })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "<ReadOnlyUiFixture><RunCount>42004</RunCount></ReadOnlyUiFixture>");
            }
            Client.Value.TestConfiguration.StoreDir = Root; Client.Value.TestConfiguration.TestName = "Source";
            Client.Value.ConfigurationSha256 = Client.Value.TestConfiguration.ComputeSha256();
            Client.Value.ProjectSelection = new EngineUiProjectSelection { Revision = 3, ConfigurationPath = Source,
                ProjectFileSha256 = SupervisorProtocol.ComputeSha256(Source).ToLowerInvariant() };
            Client.Value.Capabilities = Client.Value.Capabilities.Concat(new[] { EngineUiContract.ProjectSwitch }).ToArray();
        }
        public void Dispose() { Directory.Delete(Root, true); }
    }

    private static async Task ProjectBrowserReads()
    {
        using (var f = new ProjectUiFixture())
        {
            var sourceBefore = File.ReadAllBytes(f.Source); var targetBefore = File.ReadAllBytes(f.Target);
            var list = await OriginalProjectBrowser.ListAsync(f.Root, CancellationToken.None);
            Assert(list.SequenceEqual(new[] { f.Source, f.Target }) && f.Client.Commands == 0);
            var request = await OriginalProjectBrowser.PrepareRequestAsync(f.Client.Value, f.Target, CancellationToken.None);
            Assert(request.IsStructurallyValid() && request.BaseSelectionRevision == 3 && request.BaseConfigurationRevision == 17 &&
                request.EngineInstanceId == f.Client.Value.Engine.EngineInstanceId && request.TargetProjectFileSha256 == SupervisorProtocol.ComputeSha256(f.Target).ToLowerInvariant());
            Assert(File.ReadAllBytes(f.Source).SequenceEqual(sourceBefore) && File.ReadAllBytes(f.Target).SequenceEqual(targetBefore));
            var rejected = false;
            try { await OriginalProjectBrowser.PrepareRequestAsync(f.Client.Value, f.Source, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected);
            using (var stream = new FileStream(f.Target, FileMode.Create)) stream.SetLength(OriginalProjectBrowser.MaximumProjectBytes + 1L);
            rejected = false;
            try { await OriginalProjectBrowser.PrepareRequestAsync(f.Client.Value, f.Target, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected && f.Client.Commands == 0);
        }
    }

    private static void PumpUntil(Func<bool> finished)
    {
        var deadline = Stopwatch.StartNew();
        while (!finished() && deadline.Elapsed < TimeSpan.FromSeconds(8)) { Application.DoEvents(); Thread.Sleep(10); }
        Assert(finished());
    }

    private static void OriginalProjectSelector(bool closeWhilePending = false)
    {
        using (var f = new ProjectUiFixture())
        using (var session = new V3MonitorSession(f.Client))
        {
            session.RefreshAsync().GetAwaiter().GetResult();
            if (closeWhilePending) f.Client.PendingProject = new TaskCompletionSource<SupervisorOperatorCommandResponse>();
            var confirmations = 0;
            using (var form = new FrmTestSetting(session, name => { confirmations++; return name == "Target"; }))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                form.Show(); Application.DoEvents();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                session.RefreshAsync().GetAwaiter().GetResult();
                Assert(f.Client.Commands == 0 && form.Controls.Find("BtnFindDir", true).Single().Enabled);
                var load = form.LoadProjectChoicesAsync(f.Root); PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
                Assert(f.Client.Commands == 0 && confirmations == 0);
                var combo = form.Controls.Find("TxtTestName", true).Single();
                combo.GetType().GetProperty("SelectedIndex").SetValue(combo, 1);
                if (closeWhilePending)
                {
                    PumpUntil(() => f.Client.LastProjectSwitch != null);
                    Assert(!form.PendingProjectSelection.IsCompleted);
                    form.Close();
                    f.Client.PendingProject.SetResult(new SupervisorOperatorCommandResponse { Accepted = true, ExecutionCompleted = true, ExecutionSucceeded = true });
                    PumpUntil(() => form.PendingProjectSelection.IsCompleted);
                    form.PendingProjectSelection.GetAwaiter().GetResult();
                    Assert(form.IsDisposed && f.Client.Commands == 1 && f.Client.Value.Channels[3].FormalCycles == 21504);
                    return;
                }
                PumpUntil(() => form.PendingProjectSelection.IsCompleted);
                form.PendingProjectSelection.GetAwaiter().GetResult();
                Assert(confirmations == 1 && f.Client.Commands == 1 && f.Client.LastProjectSwitch.TargetConfigurationPath == f.Target &&
                    f.Client.LastProjectSwitch.SourceProjectFileSha256 == SupervisorProtocol.ComputeSha256(f.Source).ToLowerInvariant());
                Assert(f.Client.Value.Channels[3].FormalCycles == 21504 && f.Client.Value.Channels[5].Isolated &&
                    f.Client.Value.Engine.State == SystemTerminalState.StoppedByOperator);
                f.Client.Value.Engine.State = SystemTerminalState.Running;
                session.RefreshAsync().GetAwaiter().GetResult();
                Assert(!form.Controls.Find("BtnFindDir", true).Single().Enabled && !combo.Enabled);
            }
        }
    }

    private static void SettingsDraftIdentity()
    {
        var snapshot = ConfigurationClient().Value; var draft = new OriginalSettingsDraft(snapshot);
        draft.Commit.Configuration.TestPeriod = 19;
        Assert(snapshot.TestConfiguration.TestPeriod == 15 && draft.IsCurrent(snapshot));
        snapshot.Engine.Revision += 30; snapshot.Sequence += 20; snapshot.Engine.EngineInstanceId = RecoveryProtocolV7.NewId();
        snapshot.Channels[3].FormalCycles += 2;
        Assert(draft.IsCurrent(snapshot) && !draft.IsApplied(snapshot));
        snapshot.ConfigurationRevision++;
        Assert(!draft.IsCurrent(snapshot) && !draft.IsApplied(snapshot));
        snapshot.ConfigurationSha256 = draft.Commit.Configuration.ComputeSha256();
        Assert(draft.IsApplied(snapshot));
        snapshot.Engine.RunEpoch++;
        Assert(!draft.IsApplied(snapshot) && !draft.IsCurrent(snapshot));
    }

    private static void SettingsLifecycle(string scenario)
    {
        var fake = ConfigurationClient();
        using (var session = new V3MonitorSession(fake))
        {
            if (scenario != "attach") session.RefreshAsync().GetAwaiter().GetResult();
            using (var form = new FrmTestSetting(session))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show();
                Application.DoEvents();
                session.RefreshAsync().GetAwaiter().GetResult();
                var period = form.Controls.Find("TxtTestCycle", true).Single();
                var save = form.Controls.Find("BtnSaveTest", true).Single();
                var status = form.Controls.Find("ConfigurationContractStatus", true).Single();
                Assert(period.Text == "15" && save.Enabled && fake.Commands == 0);
                if (scenario == "attach") return;
                period.Text = "19";
                if (scenario == "conflict")
                {
                    fake.Value.TestConfiguration.Owner = "Another approved edit";
                    fake.Value.ConfigurationRevision++;
                    fake.Value.ConfigurationSha256 = fake.Value.TestConfiguration.ComputeSha256();
                    session.RefreshAsync().GetAwaiter().GetResult();
                    Assert(period.Text == "19" && !save.Enabled && status.Text.Contains("旧草稿已保留"));
                    ClickControl(save); Assert(fake.Commands == 0);
                    return;
                }
                fake.ConfigurationAdmissionOnly = true;
                fake.RejectConfiguration = scenario == "reject";
                fake.LoseConfigurationResponse = scenario == "unknown";
                ClickControl(save);
                Assert(fake.Commands == 1 && period.Text == "19" && fake.Value.TestConfiguration.TestPeriod == 15);
                if (scenario == "unknown")
                {
                    for (var i = 0; i < 3; i++) { session.RefreshAsync().GetAwaiter().GetResult(); ClickControl(save); }
                    Assert(!save.Enabled && fake.Commands == 1 && fake.HasUnresolvedCommands);
                    var reload = typeof(FrmTestSetting).GetMethod("CanReloadDraft", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert(!(bool)reload.Invoke(form, null));
                    // Simulated definitive query + a subsequent completed operator stop.
                    fake.Unresolved = false; fake.Value.Kernel.Revision++;
                    session.RefreshAsync().GetAwaiter().GetResult();
                    Assert((bool)reload.Invoke(form, null) && !save.Enabled && period.Text == "19" && fake.Commands == 1);
                    return;
                }
                if (scenario == "reject")
                {
                    Assert(save.Enabled && status.Text.Contains("操作未通过"));
                    return;
                }
                for (var i = 0; i < 3; i++) { session.RefreshAsync().GetAwaiter().GetResult(); ClickControl(save); }
                Assert(!save.Enabled && fake.Commands == 1 && status.Text.Contains("等待权威配置更新"));
                fake.Value.TestConfiguration = fake.LastConfiguration.Configuration.Clone();
                fake.Value.ConfigurationRevision++; fake.Value.ConfigurationSha256 = fake.Value.TestConfiguration.ComputeSha256();
                session.RefreshAsync().GetAwaiter().GetResult();
                Assert(save.Enabled && period.Text == "19" && fake.Value.Channels[3].FormalCycles == 21504);
            }
        }
    }

    private static void ClickControl(Control control) => typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
        .Invoke(control, new object[] { EventArgs.Empty });

    private static async Task SilentPeer()
    {
        var name = "MTTFTest.UiSilent." + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var connected = server.WaitForConnectionAsync(); var clock = Stopwatch.StartNew();
            var timedOut = false;
            try { await BoundedPipeTransport.ExchangeAsync(name, new byte[] { 1 }, 150, 1024, CancellationToken.None); }
            catch (TimeoutException) { timedOut = true; }
            await connected; Assert(timedOut && clock.ElapsedMilliseconds < 3000);
        }
    }

    private static async Task CancelPeer()
    {
        var name = "MTTFTest.UiCancel." + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var cancel = new CancellationTokenSource())
        {
            var connected = server.WaitForConnectionAsync();
            var task = BoundedPipeTransport.ExchangeAsync(name, new byte[] { 1 }, 5000, 1024, cancel.Token);
            await connected; cancel.Cancel(); var cancelled = false;
            try { await task; } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled);
        }
    }

    internal static EngineUiSnapshot Snapshot()
    {
        var now = DateTime.UtcNow.Ticks;
        var snapshot = new EngineUiSnapshot
        {
            Sequence = 1, CapturedUtcTicks = now, TestName = "10243-028", PeriodSeconds = 15, TargetCycles = 200000, SharedTargetCycles = true,
            Capabilities = new[] { EngineUiContract.Monitor }, StatusDetail = "隔离模拟数据；无硬件控制。",
            Engine = new EngineStateSnapshot { EngineInstanceId = RecoveryProtocolV7.NewId(), SessionId = RecoveryProtocolV7.NewId(),
                RunId = RecoveryProtocolV7.NewId(), RunEpoch = 1, Revision = 1, PulseSequence = 1, CapturedUtcTicks = now,
                HardwareInitialized = true, State = SystemTerminalState.StoppedByOperator },
            Channels = Enumerable.Range(1, 12).Select(n => new EngineUiChannel
            {
                Channel = n, Selected = new[] { 4, 5, 7, 8, 9, 12 }.Contains(n), CountsValid = true,
                FormalCycles = 21500 + n, MechanicalCycles = 21504 + n, RemainingCycles = 200000 - 21504 - n,
                RunTimeTicks = TimeSpan.FromHours(13).Ticks, State = n == 6 ? "报警停机" : "已停止",
                Isolated = n == 6, Current = new UiMeasurement { Valid = true, Value = n / 10.0, CapturedUtcTicks = now }
            }).ToArray(),
            PowerSupplies = Enumerable.Range(1, 4).Select(n => new EngineUiPowerSupply { Group = n, Valid = true,
                Connected = true, CapturedUtcTicks = now, Mode = "CV", Voltage = 12, Current = n / 10.0 }).ToArray(),
            Pressures = new[] { new UiMeasurement { Valid = true, Value = 0.1, CapturedUtcTicks = now }, new UiMeasurement() },
            Curves = Enumerable.Range(1, 12).Select(channel => new EngineUiCurve
            {
                Key = "A" + channel,
                UtcTicks = Enumerable.Range(0, 301).Select(n => now - TimeSpan.FromSeconds(30).Ticks + n * TimeSpan.TicksPerMillisecond * 100).ToArray(),
                Values = Enumerable.Range(0, 301).Select(n => channel / 10.0 + (n % 150 < 15 ? Math.Sin(n % 15 * Math.PI / 15) * 6 : 0)).ToArray()
            }).ToArray()
        };
        snapshot.Kernel = KernelState(snapshot.Engine);
        return snapshot;
    }

    private static EngineUiKernelState KernelState(EngineStateSnapshot engine) => new EngineUiKernelState
    {
        Available = true, SessionId = engine.SessionId, RunId = engine.RunId, RunEpoch = engine.RunEpoch,
        Revision = 1, CapturedUtcTicks = DateTime.UtcNow.Ticks, DesiredState = SystemTerminalState.StoppedByOperator
    };

    private sealed partial class FakeClient : IEngineUiClient
    {
        internal ProjectSwitchRequest LastProjectSwitch;
        internal TaskCompletionSource<SupervisorOperatorCommandResponse> PendingProject;
        public Task<SupervisorOperatorCommandResponse> SubmitProjectSwitchAsync(EngineStateSnapshot snapshot,
            ProjectSwitchRequest project, CancellationToken token)
        {
            Commands++; LastProjectSwitch = project.Clone();
            return PendingProject?.Task ?? Task.FromResult(new SupervisorOperatorCommandResponse { Accepted = true, ExecutionCompleted = true,
                ExecutionSucceeded = true, Detail = "IsolatedProjectSwitchFixture" });
        }
        internal TaskCompletionSource<EngineUiLogPage> PendingLogPage;
        public Task<EngineUiLogPage> ReadLogsAsync(EngineUiLogQuery query, EngineStateSnapshot expected, CancellationToken token) =>
            PendingLogPage?.Task ?? Task.FromResult(new EngineUiLogPage { Level = query.Level, BeforeSequence = query.BeforeSequence });
        internal bool Unresolved;
        public bool HasUnresolvedCommands => Unresolved;
        public Task<SupervisorOperatorCommandResponse> ResolvePendingAsync(CancellationToken token) =>
            Task.FromResult<SupervisorOperatorCommandResponse>(null);
        internal EngineUiSnapshot Value = Snapshot(); internal int Reads; internal int Commands;
        internal TaskCompletionSource<bool> Delay;
        internal TaskCompletionSource<SupervisorOperatorCommandResponse> PendingStart;
        internal TaskCompletionSource<SupervisorOperatorCommandResponse> PendingManual;
        internal bool ManualControls;
        internal OperatorCommandKind LastBatchKind;
        internal TestConfigurationCommit LastConfiguration;
        internal bool ConfigurationAdmissionOnly;
        internal bool RejectConfiguration;
        internal bool LoseConfigurationResponse;
        internal TaskCompletionSource<SupervisorOperatorCommandResponse> PendingConfiguration;
        internal OperatorCommandKind LastPanelKind;
        internal AlarmPanelCommand LastPanel;
        public Task<SupervisorOperatorCommandResponse> SubmitAlarmPanelAsync(EngineStateSnapshot snapshot,
            OperatorCommandKind kind, AlarmPanelCommand panel, CancellationToken token)
        {
            Commands++; LastPanelKind = kind; LastPanel = panel.Clone();
            Value.AlarmPanel.BuzzerEnabled = panel.BuzzerEnabled;
            Value.AlarmPanel.Revision++;
            return Task.FromResult(new SupervisorOperatorCommandResponse { Accepted = true, ExecutionCompleted = true, ExecutionSucceeded = true });
        }
        public Task<SupervisorOperatorCommandResponse> SubmitConfigurationAsync(EngineStateSnapshot snapshot,
            TestConfigurationCommit configuration, CancellationToken token)
        {
            Commands++;
            LastConfiguration = configuration.Clone();
            if (PendingConfiguration != null) return PendingConfiguration.Task;
            if (LoseConfigurationResponse) { Unresolved = true; throw new TimeoutException("SyntheticLostConfigurationResponse"); }
            if (RejectConfiguration) return Task.FromResult(new SupervisorOperatorCommandResponse
                { Accepted = false, FailureCode = "ConfigurationRevisionConflict" });
            if (ConfigurationAdmissionOnly) return Task.FromResult(new SupervisorOperatorCommandResponse { Accepted = true });
            if (configuration.DaqConfiguration != null)
            {
                Value.DaqConfiguration = configuration.DaqConfiguration.Clone(); Value.DaqConfigurationRevision++;
                Value.DaqConfigurationSha256 = Value.DaqConfiguration.ComputeSha256();
            }
            else if (configuration.AoCalibration != null)
            {
                var device = Value.AoConfiguration.Devices.Single(d => d.Name == configuration.AoCalibration.DeviceName);
                device.Points = configuration.AoCalibration.Points.Select(p => p.Clone()).ToArray();
                Value.AoConfigurationRevision++; Value.AoConfigurationSha256 = Value.AoConfiguration.ComputeSha256();
            }
            else
            {
                Value.TestConfiguration = configuration.Configuration.Clone(); Value.ConfigurationRevision++;
                Value.ConfigurationSha256 = Value.TestConfiguration.ComputeSha256();
            }
            return Task.FromResult(new SupervisorOperatorCommandResponse { Accepted = true });
        }
        public async Task<EngineUiSnapshot> ReadUiSnapshotAsync(CancellationToken token)
        {
            Reads++; if (Delay != null) await Delay.Task;
            Value.Sequence++;
            Value.CapturedUtcTicks = DateTime.UtcNow.Ticks;
            Value.Engine.CapturedUtcTicks = Value.CapturedUtcTicks;
            Value.Kernel.CapturedUtcTicks = Value.CapturedUtcTicks;
            foreach (var channel in Value.Channels) channel.Current.CapturedUtcTicks = Value.CapturedUtcTicks;
            foreach (var power in Value.PowerSupplies) power.CapturedUtcTicks = Value.CapturedUtcTicks;
            RefreshPressureDisplay();
            var json = new JavaScriptSerializer(); return json.Deserialize<EngineUiSnapshot>(json.Serialize(Value));
        }
        public Task<SupervisorOperatorCommandResponse> SubmitChannelAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind, int channel, CancellationToken token)
        {
            LastChannel = channel;
            if (ManualControls && PendingManual == null && kind == OperatorCommandKind.RetryQualification)
            {
                var bit = 1 << (channel - 1);
                var scope = "Channel:" + channel;
                Value.Engine.IsolatedResources = (Value.Engine.IsolatedResources ?? Array.Empty<string>())
                    .Where(value => !string.Equals(value, scope, StringComparison.OrdinalIgnoreCase)).ToArray();
                Value.Kernel.IsolatedResources = (Value.Kernel.IsolatedResources ?? Array.Empty<string>())
                    .Where(value => !string.Equals(value, scope, StringComparison.OrdinalIgnoreCase)).ToArray();
                Value.Kernel.QualificationRetryEligibleScopes = (Value.Kernel.QualificationRetryEligibleScopes ?? Array.Empty<string>())
                    .Where(value => !string.Equals(value, scope, StringComparison.OrdinalIgnoreCase)).ToArray();
                Value.Engine.ChannelPauseMask |= bit;
                Value.Engine.ChannelResumeMask &= ~bit;
                Value.Channels[channel - 1].Selected = true;
                Value.Channels[channel - 1].Isolated = false;
                Value.Channels[channel - 1].Running = true;
                Value.Engine.State = Value.Kernel.DesiredState = SystemTerminalState.Running;
                Value.Engine.OutputsEnergized = true;
            }
            else if (ManualControls && PendingManual == null)
            {
                var bit = 1 << (channel - 1); var pause = kind == OperatorCommandKind.PauseChannel;
                Value.Engine.ChannelPauseMask = pause ? Value.Engine.ChannelPauseMask & ~bit : Value.Engine.ChannelPauseMask | bit;
                Value.Engine.ChannelResumeMask = pause ? Value.Engine.ChannelResumeMask | bit : Value.Engine.ChannelResumeMask & ~bit;
                Value.Kernel.ManualPausedChannelsMask = Value.Engine.ChannelResumeMask;
                var held = Value.Engine.ChannelResumeMask != 0;
                Value.Engine.State = Value.Kernel.DesiredState = held ? Value.Engine.ChannelPauseMask == 0 ? SystemTerminalState.StoppedByOperator :
                    SystemTerminalState.RunningDegraded : SystemTerminalState.Running;
                Value.Kernel.ActiveIncidentCount = held ? 1 : 0; Value.Kernel.OperatorStage = held ? RecoveryStage.OperatorChannelsHeld : RecoveryStage.None;
                Value.Engine.RecoveryOwnerId = Value.Kernel.ManualBatchOwnerId = held ? RecoveryProtocolV7.NewId() : "";
                Value.Engine.RecoveryIncidentId = Value.Kernel.ManualBatchIncidentId = held ? RecoveryProtocolV7.NewId() : "";
                Value.Kernel.ManualBatchEngineInstanceId = Value.Engine.EngineInstanceId;
                Value.Channels[channel - 1].Running = !pause;
            }
            return SubmitAsync(snapshot, kind, token);
        }
        internal int LastChannel;
        public Task<SupervisorOperatorCommandResponse> SubmitAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind, CancellationToken token)
        {
            Commands++;
            LastBatchKind = kind;
            if (ManualBatchCommand.IsOperation(kind) && PendingManual != null) return PendingManual.Task;
            if (ManualControls)
            {
                Value.Engine.Revision++; Value.Kernel.Revision++;
                if (kind == OperatorCommandKind.Pause)
                {
                    Value.Engine.State = Value.Kernel.DesiredState = SystemTerminalState.StoppedByOperator;
                    Value.Engine.OutputsEnergized = false;
                    Value.Engine.RecoveryOwnerId = Value.Kernel.ManualBatchOwnerId = RecoveryProtocolV7.NewId();
                    Value.Engine.RecoveryIncidentId = Value.Kernel.ManualBatchIncidentId = RecoveryProtocolV7.NewId();
                    Value.Kernel.ManualBatchEngineInstanceId = Value.Engine.EngineInstanceId;
                    Value.Kernel.ActiveIncidentCount = 1; Value.Kernel.OperatorStage = RecoveryStage.OperatorPaused;
                    Value.BatchPauseAvailable = false; Value.BatchResumeAvailable = true;
                }
                else if (kind == OperatorCommandKind.Resume || kind == OperatorCommandKind.Stop)
                {
                    Value.Engine.State = kind == OperatorCommandKind.Resume ? SystemTerminalState.Running : SystemTerminalState.StoppedByOperator;
                    Value.Engine.OutputsEnergized = kind == OperatorCommandKind.Resume;
                    Value.Engine.RecoveryOwnerId = Value.Engine.RecoveryIncidentId = string.Empty;
                    Value.Kernel = KernelState(Value.Engine); Value.Kernel.DesiredState = Value.Engine.State;
                    Value.BatchPauseAvailable = kind == OperatorCommandKind.Resume; Value.BatchResumeAvailable = false;
                }
            }
            if (kind == OperatorCommandKind.Start && PendingStart != null) return PendingStart.Task;
            return Task.FromResult(new SupervisorOperatorCommandResponse { Accepted = true });
        }
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void ExpectText(Control form, string name, string expected)
    {
        var actual = form.Controls.Find(name, true).Single().Text;
        if (actual != expected) throw new Exception(name + " expected=" + expected + "; actual=" + actual);
    }
    private static void Run(string name, Action test)
    {
        if (_filter != null && name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) return;
        try { test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
    }
}
