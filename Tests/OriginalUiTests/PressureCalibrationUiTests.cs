using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest;
using MtEmbTest;
using MTTFTest.Watchdog.Protocol;

internal static partial class Program
{
    private static void RunPressurePageTests(string repo)
    {
        Run("Pressure page restores all eight original buttons and real command bindings", PressureOriginalButtons);
        Run("Pressure page keeps nonzero history and other cylinder draft after save", PressureSaveDrafts);
        Run("Pressure page waits for safety proof before save or normal close", PressureSafetyClosure);
        Run("Pressure page stop preempts pending output without late reenergization", PressurePendingOutput);
        Run("Pressure page rejects missing capability invalid samples and stale configuration", PressureUiGates);
        Run("Pressure page confirmation cancels without commands or local clearing", PressureCancel);
        Run("Pressure page tab and cylinder changes end the same maintenance session", PressureNavigation);
        Run("Pressure page heartbeat stops for frozen UI and binds the exact lease", PressureHeartbeat);
        Run("Pressure page disposal and rejected heartbeat request one bounded end", PressureDisposalAndHeartbeatDenial);
        Run("Pressure page save rejection or lost reply keeps draft and original transaction", PressureSaveFailures);
        Run("Pressure UI wire preserves all four typed commands and original result IDs", () => PressureUiCommandsWire().GetAwaiter().GetResult());
        Run("Pressure UI heartbeat validates actual peer identity and bounded responses", () => PressureUiHeartbeatWire().GetAwaiter().GetResult());
        Run("Pressure page original layout diagnostic render at 1440x900", () => PressurePageRender(repo));
    }

    private sealed class PressureUiFixture : IDisposable
    {
        internal readonly FakeClient Client = ConfigurationClient();
        internal readonly V3MonitorSession Session;
        internal readonly FrmTestSetting Form;
        internal bool Confirm = true;
        internal readonly List<string> Confirmations = new List<string>();
        internal PressureUiFixture()
        {
            Client.PressureMode = true; Client.Value.AoConfiguration = AoSettings(); Client.Value.AoConfigurationRevision = 31;
            Client.Value.AoConfigurationSha256 = Client.Value.AoConfiguration.ComputeSha256();
            Client.Value.Capabilities = Client.Value.Capabilities.Concat(new[] { EngineUiContract.PressureMaintenance, EngineUiContract.AoCalibration }).ToArray();
            Session = new V3MonitorSession(Client); Session.RefreshAsync().GetAwaiter().GetResult();
            Form = new FrmTestSetting(Session, confirmPressureAction: message => { Confirmations.Add(message); return Confirm; });
            Form.ShowInTaskbar = false; Form.StartPosition = FormStartPosition.Manual; Form.Location = new Point(-20000, -20000);
            Form.Show(); Application.DoEvents();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ReadField<TabControl>(Form, "TabSetting").SelectedTab = ReadField<TabPage>(Form, "tabPagePressureCalibration");
            Tick();
        }
        internal Control Button(string name) => ReadField<Control>(Form, "BtnPressureCalibration" + name);
        internal DataGridView Grid => ReadField<DataGridView>(Form, "DgvPressureCalibration");
        internal PressureMaintenanceUiSession Maintenance => ReadField<PressureMaintenanceUiSession>(Form, "_pressureMaintenanceUi");
        internal void SelectCylinder(int index)
        {
            var combo = ReadField<Control>(Form, "CmbPressureCalibrationCylinder");
            combo.GetType().GetProperty("SelectedIndex").SetValue(combo, index);
        }
        internal void Tick()
        {
            Session.RefreshAsync().GetAwaiter().GetResult();
            InvokePrivate(Form, "PressureCalibrationTimerTick", null, EventArgs.Empty);
            Application.DoEvents();
            Assert(Session.IsConnected);
        }
        internal void Click(string name)
        {
            Console.WriteLine("  Pressure control: " + name);
            Assert(Button(name).Enabled); ClickControl(Button(name));
            PumpUntil(() => Form.PendingPressureAction.IsCompleted); Form.PendingPressureAction.GetAwaiter().GetResult(); Tick();
        }
        internal void Start() { Click("Start"); Assert(Maintenance.Ready && !Session.CanClose); }
        internal void Output(double pressure)
        {
            ReadField<Control>(Form, "TxtPressureCalibrationCommand").Text = pressure.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Click("Output"); Assert(Maintenance.Display?.OutputActive == true);
        }
        public void Dispose()
        {
            var renew = Maintenance.RenewalTask;
            Form.Dispose(); Session.Dispose();
            Assert(renew.Wait(3000)); SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private static void PressureOriginalButtons()
    {
        using (var f = new PressureUiFixture())
        {
            foreach (var name in new[] { "Start", "Output", "Stop", "UseLive", "Record", "Delete", "Clear", "Save" }) Assert(f.Button(name).Visible);
            Assert(f.Client.Commands == 0 && f.Grid.Rows.Count == 2 && !f.Button("Output").Enabled);
            f.Start(); f.Output(40);
            ReadField<Control>(f.Form, "TxtPressureCalibrationMeasured").Text = "39.25";
            f.Click("Record");
            Assert(f.Grid.Rows.Count == 3 && (double)f.Grid.CurrentRow.Cells[2].Value == 39.25);
            f.Client.PressureValue = 41.5; f.Tick(); f.Click("UseLive"); f.Click("Record");
            Assert(f.Grid.Rows.Count == 3 && (double)f.Grid.CurrentRow.Cells[2].Value == 41.5);
            Assert((double)f.Grid.CurrentRow.Cells[1].Value == 40 && (double)f.Grid.CurrentRow.Cells[3].Value == 2);
            f.Click("Stop"); Assert(!f.Maintenance.MayBeEnergized && f.Maintenance.Requested && !f.Session.CanClose);
            f.Click("Delete"); Assert(f.Grid.Rows.Count == 2);
            f.Click("Clear"); Assert(f.Grid.Rows.Count == 0 && !f.Button("Save").Enabled);
            Assert(f.Client.MaintenanceCommands.Select(c => c.Kind).SequenceEqual(new[] { OperatorCommandKind.BeginPressureMaintenance,
                OperatorCommandKind.SetMaintenancePressure, OperatorCommandKind.StopMaintenanceOutput }));
            Assert(f.Client.Value.Channels[3].FormalCycles == 21504 && f.Client.Value.Channels[5].Isolated);
        }
    }

    private static void PressureSaveDrafts()
    {
        using (var f = new PressureUiFixture())
        {
            f.SelectCylinder(1); f.Tick(); f.Click("Clear"); f.SelectCylinder(0); f.Tick();
            f.Start(); f.Output(40); f.Click("Record"); f.Click("Stop"); f.Click("Save");
            Assert(f.Client.LastConfiguration.AoCalibration.DeviceName == "Cylinder1" && f.Client.LastConfiguration.BaseConfigurationRevision == 31);
            Assert(f.Client.Value.AoConfigurationRevision == 32 && f.Client.Value.ConfigurationRevision == 17 && f.Session.CanClose);
            Assert(f.Client.MaintenanceCommands.Last().Kind == OperatorCommandKind.EndPressureMaintenance);
            Assert(f.Client.Value.AoConfiguration.Devices.Single(d => d.Name == "Cylinder2").Points.Length == 2);
            f.SelectCylinder(1); f.Tick(); Assert(f.Grid.Rows.Count == 0);
            Assert(f.Client.Value.Channels[3].FormalCycles == 21504 && f.Confirmations.Any(s => s.Contains("新旧点混合")));
        }
    }

    private static void PressureSafetyClosure()
    {
        foreach (var close in new[] { false, true }) using (var f = new PressureUiFixture())
        {
            f.Start(); f.Client.HoldMaintenanceProof = true;
            if (close) f.Form.Close(); else ClickControl(f.Button("Save"));
            PumpUntil(() => f.Client.MaintenanceCommands.Any(c => c.Kind == OperatorCommandKind.EndPressureMaintenance));
            Assert(!f.Form.IsDisposed && !f.Session.CanClose && f.Client.LastConfiguration == null && f.Maintenance.Ending);
            Assert(!f.Form.PendingPressureAction.IsCompleted);
            f.Client.CompleteMaintenanceProof();
            PumpUntil(() => f.Form.PendingPressureAction.IsCompleted);
            if (close) Assert(f.Form.IsDisposed);
            else { f.Tick(); Assert(f.Client.LastConfiguration?.AoCalibration != null && f.Session.CanClose && !f.Maintenance.Requested); }
        }
    }

    private static void PressurePendingOutput()
    {
        using (var f = new PressureUiFixture())
        {
            f.Start(); f.Client.PendingPressureOutput = new TaskCompletionSource<SupervisorOperatorCommandResponse>();
            ClickControl(f.Button("Output")); var original = f.Form.PendingPressureAction;
            Assert(!original.IsCompleted); f.Tick(); Assert(f.Button("Stop").Enabled && !f.Button("Output").Enabled);
            f.Click("Stop");
            f.Client.PendingPressureOutput.SetResult(new SupervisorOperatorCommandResponse { Accepted = true, ExecutionCompleted = true, ExecutionSucceeded = true });
            PumpUntil(() => original.IsCompleted); f.Tick();
            Assert(f.Maintenance.Ready && !f.Client.Value.Engine.OutputsEnergized && !f.Maintenance.Display.OutputActive);
            Assert(f.Client.MaintenanceCommands.Count == 3);
        }
    }

    private static void PressureUiGates()
    {
        using (var f = new PressureUiFixture())
        {
            f.Client.Value.Capabilities = f.Client.Value.Capabilities.Where(c => c != EngineUiContract.AoCalibration).ToArray(); f.Tick();
            Assert(!f.Button("Start").Enabled); ClickControl(f.Button("Start")); Assert(f.Client.Commands == 0);
            f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.AoCalibration }).ToArray(); f.Tick(); f.Start();
            f.Client.PressureSampleInvalid = true; f.Tick(); Assert(!f.Button("Output").Enabled);
            Assert(ReadField<Control>(f.Form, "LblPressureCalibrationLive").Text.Contains("--"));
            f.Client.PressureSampleInvalid = false; f.Tick(); f.Output(40);
            f.Client.PressureSampleStale = true; f.Tick(); Assert(!f.Button("Record").Enabled); ClickControl(f.Button("Record"));
            PumpUntil(() => f.Form.PendingPressureAction.IsCompleted); Assert(f.Grid.Rows.Count == 2);
            f.Client.PressureSampleStale = false; f.Click("Stop");
            f.Output(40);
            f.Client.Value.Capabilities = f.Client.Value.Capabilities.Where(c => c != EngineUiContract.PressureMaintenance).ToArray(); f.Tick();
            Assert(!f.Button("Record").Enabled && f.Button("Stop").Enabled); f.Click("Stop");
            f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.PressureMaintenance }).ToArray(); f.Tick();
            f.Client.Value.AoConfigurationRevision++; f.Tick(); Assert(!f.Button("Output").Enabled && !f.Button("Save").Enabled && f.Grid.Rows.Count == 2);
        }
    }

    private static void PressureCancel()
    {
        using (var f = new PressureUiFixture())
        {
            f.Confirm = false; f.Click("Start"); f.Click("Clear"); f.Click("Save");
            Assert(f.Client.Commands == 0 && f.Grid.Rows.Count == 2 && !f.Maintenance.Requested);
        }
    }

    private static void PressureNavigation()
    {
        foreach (var tab in new[] { false, true }) using (var f = new PressureUiFixture())
        {
            f.Start(); var incident = f.Maintenance.Lease.IncidentId;
            if (tab) ReadField<TabControl>(f.Form, "TabSetting").SelectedIndex = 0;
            else f.SelectCylinder(1);
            PumpUntil(() => f.Form.PendingPressureAction.IsCompleted); f.Tick();
            Assert(f.Session.CanClose && !f.Maintenance.Requested && f.Client.MaintenanceCommands.Last().Payload.IncidentId == incident);
            if (!tab) { f.Start(); Assert(f.Maintenance.Lease.HydraulicId == 2 && f.Maintenance.Lease.IncidentId != incident); }
        }
    }

    private static void PressureHeartbeat()
    {
        using (var f = new PressureUiFixture())
        {
            f.Start(); PumpUntil(() => Volatile.Read(ref f.Client.MaintenanceHeartbeats) > 0);
            Assert(f.Maintenance.Failure.Length == 0 && f.Client.LastHeartbeat.Binds(f.Maintenance.Lease));
            // Do not pump WinForms: the independent worker must not renew forever.
            Assert(f.Maintenance.RenewalTask.Wait(5000));
            Assert(f.Maintenance.Failure.Contains("失联"));
            Assert(f.Client.MaintenanceCommands.Last().Kind == OperatorCommandKind.EndPressureMaintenance);
            f.Tick(); PumpUntil(() => f.Form.PendingPressureAction.IsCompleted); f.Tick();
            Assert(f.Session.CanClose && !f.Maintenance.Requested && f.Client.MaintenanceCommands.Last().Kind == OperatorCommandKind.EndPressureMaintenance);
        }
    }

    private static void PressurePageRender(string repo)
    {
        using (var f = new PressureUiFixture())
        {
            f.Form.WindowState = FormWindowState.Normal; f.Form.Size = new Size(1440, 900); f.Tick();
            var page = ReadField<TabPage>(f.Form, "tabPagePressureCalibration");
            foreach (var name in new[] { "Start", "Output", "Stop", "UseLive", "Record", "Delete", "Clear", "Save" })
            {
                var button = f.Button(name); var bounds = page.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                Assert(button.Visible && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= page.ClientSize.Width &&
                    bounds.Bottom <= page.ClientSize.Height && button.Height >= button.Font.Height);
            }
            var directory = Path.Combine(repo, "docs", "02_Issues", "截图"); Directory.CreateDirectory(directory);
            using (var bitmap = new Bitmap(f.Form.Width, f.Form.Height))
            {
                f.Form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, f.Form.Size));
                bitmap.Save(Path.Combine(directory, "2026-09-05_R26_原压力校正页_隔离模拟_1440x900.png"));
            }
            Assert(f.Client.Commands == 0 && f.Client.Value.Channels[3].FormalCycles == 21504);
        }
    }

    private static void PressureDisposalAndHeartbeatDenial()
    {
        foreach (var dispose in new[] { true, false }) using (var f = new PressureUiFixture())
        {
            f.Start();
            if (dispose) { f.Form.Dispose(); f.Form.Dispose(); }
            else { f.Client.RejectMaintenanceHeartbeat = true; PumpUntil(() => f.Maintenance.RenewalTask.IsCompleted); }
            Assert(f.Maintenance.RenewalTask.Wait(3000));
            Assert(f.Client.MaintenanceCommands.Count == 2 && f.Client.MaintenanceCommands.Last().Kind == OperatorCommandKind.EndPressureMaintenance);
            Assert(!f.Client.Value.Engine.OutputsEnergized && f.Client.Value.Channels[3].FormalCycles == 21504);
        }
    }

    private static void PressureSaveFailures()
    {
        foreach (var unknown in new[] { false, true }) using (var f = new PressureUiFixture())
        {
            f.Start(); f.Client.RejectConfiguration = !unknown; f.Client.LoseConfigurationResponse = unknown;
            f.Click("Save");
            Assert(f.Client.LastConfiguration.AoCalibration != null && f.Client.Value.AoConfigurationRevision == 31 && f.Grid.Rows.Count == 2);
            if (unknown)
            {
                var before = f.Client.Commands; Assert(!f.Button("Save").Enabled);
                ClickControl(f.Button("Save")); PumpUntil(() => f.Form.PendingPressureAction.IsCompleted);
                Assert(f.Client.Commands == before);
            }
            else Assert(f.Button("Save").Enabled);
        }
    }

    private static async Task PressureUiCommandsWire()
    {
        foreach (var kind in new[] { OperatorCommandKind.BeginPressureMaintenance, OperatorCommandKind.SetMaintenancePressure,
            OperatorCommandKind.StopMaintenanceOutput, OperatorCommandKind.EndPressureMaintenance })
        {
            var engine = Snapshot().Engine;
            var identity = V3EngineIdentity.Parse(new[] { SessionAgentProtocol.SessionArgument, engine.SessionId, "--engine-run", engine.RunId, "--engine-epoch", "1" });
            var pipeName = "MTTFTest.PressureUiCommands." + RecoveryProtocolV7.NewId();
            var client = new V3EngineHostClient(identity, "unused", pipeName);
            var payload = new PressureMaintenanceCommand { EngineInstanceId = engine.EngineInstanceId, UiProcessId = client.UiProcessId,
                UiProcessStartUtcTicks = client.UiProcessStartUtcTicks, HydraulicId = 2, PressureBar = kind == OperatorCommandKind.SetMaintenancePressure ? 47.25 : 0,
                IncidentId = kind == OperatorCommandKind.BeginPressureMaintenance ? "" : RecoveryProtocolV7.NewId(),
                OwnerId = kind == OperatorCommandKind.BeginPressureMaintenance ? "" : RecoveryProtocolV7.NewId() };
            using (var stop = new CancellationTokenSource(8000))
            {
                var server = Task.Run(async () =>
                {
                    using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        await pipe.WaitForConnectionAsync(stop.Token);
                        using (var reader = new BinaryReader(pipe, Encoding.UTF8, true)) using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                        {
                            Assert(reader.ReadString() == SupervisorUiAttachmentRequest.Magic);
                            var request = SupervisorUiAttachmentRequest.ReadBodyFrom(reader);
                            Assert(request.RequesterProcessId == PipePeerIdentity.ClientProcessId(pipe));
                            new SupervisorUiAttachmentResponse { RequestId = request.RequestId, ChallengeNonce = request.ChallengeNonce,
                                Accepted = true, Engine = engine, EngineProcessId = client.UiProcessId,
                                EngineProcessStartUtcTicks = client.UiProcessStartUtcTicks, ApprovedDesiredState = KernelState(engine) }.WriteTo(writer);
                        }
                    }
                    string fingerprint = null, commandId = null;
                    for (var index = 0; index < 2; index++) using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        await pipe.WaitForConnectionAsync(stop.Token);
                        using (var reader = new BinaryReader(pipe, Encoding.UTF8, true)) using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                        {
                            var request = SupervisorOperatorCommandRequest.ReadBodyFrom(reader, reader.ReadString()); var command = request.Command;
                            Assert(command.IsStructurallyValid() && command.Kind == kind && command.PressureMaintenance.ComputeSha256() == payload.ComputeSha256());
                            if (index == 0) { commandId = command.CommandId; fingerprint = OperatorCommandAdmission.GetFingerprint(command); }
                            Assert(command.CommandId == commandId && OperatorCommandAdmission.GetFingerprint(command) == fingerprint && request.QueryOnly == (index == 1));
                            new SupervisorOperatorCommandResponse { RequestId = request.RequestId, ChallengeNonce = request.ChallengeNonce,
                                Accepted = true, ExecutionCompleted = index == 1, ExecutionSucceeded = index == 1 }.WriteTo(writer);
                        }
                    }
                });
                await (Task)InvokePrivate(client, "AttachAsync", stop.Token);
                var result = await client.SubmitMaintenanceAsync(engine, kind, payload, stop.Token);
                Assert(result.Accepted && !result.ExecutionCompleted && client.HasUnresolvedCommands);
                result = await client.ResolvePendingAsync(stop.Token);
                Assert(result.ExecutionCompleted && result.ExecutionSucceeded && !client.HasUnresolvedCommands);
                await server;
            }
        }
    }

    private static async Task PressureUiHeartbeatWire()
    {
        foreach (var scenario in new[] { "ok", "wrong-reply", "wrong-peer", "silent", "cancel" })
        {
            Console.WriteLine("  Pressure heartbeat wire: " + scenario);
            var engine = Snapshot().Engine;
            var identity = V3EngineIdentity.Parse(new[] { SessionAgentProtocol.SessionArgument, engine.SessionId, "--engine-run", engine.RunId, "--engine-epoch", "1" });
            var client = new V3EngineHostClient(identity); var now = DateTime.UtcNow.Ticks;
            var heartbeat = new PressureMaintenanceHeartbeat { SessionId = engine.SessionId, RunId = engine.RunId, RunEpoch = engine.RunEpoch,
                EngineInstanceId = engine.EngineInstanceId, IncidentId = RecoveryProtocolV7.NewId(), OwnerId = RecoveryProtocolV7.NewId(),
                UiProcessId = client.UiProcessId, UiProcessStartUtcTicks = client.UiProcessStartUtcTicks, Sequence = 1, IssuedUtcTicks = now };
            var lease = new PressureMaintenanceLease { SessionId = heartbeat.SessionId, RunId = heartbeat.RunId, RunEpoch = heartbeat.RunEpoch,
                EngineInstanceId = heartbeat.EngineInstanceId, IncidentId = heartbeat.IncidentId, OwnerId = heartbeat.OwnerId,
                UiProcessId = heartbeat.UiProcessId, UiProcessStartUtcTicks = heartbeat.UiProcessStartUtcTicks, HydraulicId = 1, Revision = 2,
                CreatedUtcTicks = now - TimeSpan.FromSeconds(1).Ticks, LastHeartbeatUtcTicks = now, LastHeartbeatSequence = 1,
                ExpiresUtcTicks = now + TimeSpan.FromSeconds(10).Ticks, AbsoluteDeadlineUtcTicks = now + TimeSpan.FromMinutes(20).Ticks };
            var pipeName = "MTTFTest.PressureUiHeartbeat." + RecoveryProtocolV7.NewId();
            using (var stop = new CancellationTokenSource(7000)) using (var cancel = new CancellationTokenSource())
            using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var connected = pipe.WaitForConnectionAsync(stop.Token);
                var server = Task.Run(async () =>
                {
                    await connected;
                    if (scenario == "wrong-peer") { Assert(await pipe.ReadAsync(new byte[1], 0, 1, stop.Token) == 0); return; }
                    var request = PressureMaintenanceTransport.Decode<PressureMaintenanceHeartbeatRequest>(await PressureMaintenanceTransport.ReadFrameAsync(pipe, stop.Token));
                    Assert(request.IsStructurallyValid() && request.Heartbeat.Binds(lease));
                    if (scenario == "silent" || scenario == "cancel")
                    { if (scenario == "cancel") cancel.Cancel(); Assert(await pipe.ReadAsync(new byte[1], 0, 1, stop.Token) == 0); return; }
                    await PressureMaintenanceTransport.WriteFrameAsync(pipe, new PressureMaintenanceHeartbeatResponse { RequestId = request.RequestId,
                        ChallengeNonce = scenario == "wrong-reply" ? RecoveryProtocolV7.NewId() : request.ChallengeNonce, Accepted = true, Lease = lease }, stop.Token);
                });
                var clock = Stopwatch.StartNew(); var rejected = false; var validated = false;
                try
                {
                    var reply = await client.RenewMaintenanceAsync(heartbeat, cancel.Token, pipeName, peer =>
                    {
                        Assert(PipePeerIdentity.ServerProcessId(peer) == client.UiProcessId); validated = true;
                        if (scenario == "wrong-peer") throw new InvalidDataException("isolated peer rejected before payload");
                    });
                    Assert(scenario == "ok" && reply.Accepted);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is TimeoutException || ex is OperationCanceledException) { rejected = true; }
                Assert(validated && rejected == (scenario != "ok") && clock.Elapsed < TimeSpan.FromSeconds(5));
                await server;
            }
        }
    }

    private sealed partial class FakeClient : IEnginePressureMaintenanceUiClient
    {
        internal sealed class MaintenanceCall { internal OperatorCommandKind Kind; internal PressureMaintenanceCommand Payload; }
        internal readonly List<MaintenanceCall> MaintenanceCommands = new List<MaintenanceCall>();
        internal bool PressureMode, HoldMaintenanceProof, PressureSampleInvalid, PressureSampleStale, RejectMaintenanceHeartbeat;
        internal double PressureValue = 40.25;
        internal int MaintenanceHeartbeats;
        internal PressureMaintenanceHeartbeat LastHeartbeat;
        internal TaskCompletionSource<SupervisorOperatorCommandResponse> PendingPressureOutput;
        public int UiProcessId { get { using (var process = Process.GetCurrentProcess()) return process.Id; } }
        public long UiProcessStartUtcTicks { get { using (var process = Process.GetCurrentProcess()) return process.StartTime.ToUniversalTime().Ticks; } }
        public Task<SupervisorOperatorCommandResponse> SubmitMaintenanceAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind,
            PressureMaintenanceCommand payload, CancellationToken token)
        {
            Assert(PressureMode && payload.IsStructurallyValid(kind)); Commands++;
            MaintenanceCommands.Add(new MaintenanceCall { Kind = kind, Payload = payload.Clone() });
            if (kind == OperatorCommandKind.BeginPressureMaintenance)
            {
                var now = DateTime.UtcNow.Ticks;
                var lease = new PressureMaintenanceLease { SessionId = snapshot.SessionId, RunId = snapshot.RunId, RunEpoch = snapshot.RunEpoch,
                    EngineInstanceId = snapshot.EngineInstanceId, IncidentId = RecoveryProtocolV7.NewId(), OwnerId = RecoveryProtocolV7.NewId(),
                    UiProcessId = payload.UiProcessId, UiProcessStartUtcTicks = payload.UiProcessStartUtcTicks, HydraulicId = payload.HydraulicId,
                    Revision = 1, CreatedUtcTicks = now, LastHeartbeatUtcTicks = now, ExpiresUtcTicks = now + TimeSpan.FromSeconds(10).Ticks,
                    AbsoluteDeadlineUtcTicks = now + TimeSpan.FromMinutes(30).Ticks };
                Value.Kernel.PressureMaintenance = lease; Value.Kernel.ActiveIncidentCount = 1;
                Value.Kernel.PressureMaintenanceStage = RecoveryStage.PressureMaintenanceReady;
                Value.Engine.RecoveryIncidentId = lease.IncidentId; Value.Engine.RecoveryOwnerId = lease.OwnerId;
                Value.PressureMaintenance = new EngineUiPressureMaintenance { Identity = new RecoveryIdentity { SessionId = lease.SessionId, RunId = lease.RunId,
                    RunEpoch = lease.RunEpoch, IncidentId = lease.IncidentId, ResourceScope = "System", Generation = lease.Generation, Revision = 1 },
                    EngineInstanceId = lease.EngineInstanceId, OwnerId = lease.OwnerId, HydraulicId = lease.HydraulicId,
                    CapturedUtcTicks = now, LeaseRevision = 1, Prepared = true, AuthorityValid = true, PressureSampleMaximumAgeMilliseconds = 100,
                    Pressure = new UiMeasurement { Valid = true, CapturedUtcTicks = now, Value = 0 } };
            }
            else
            {
                Assert(payload.Binds(Value.Kernel.PressureMaintenance, kind));
                if (kind == OperatorCommandKind.SetMaintenancePressure && PendingPressureOutput != null)
                { Value.Kernel.CommandPending = true; Value.Kernel.PressureMaintenanceStage = RecoveryStage.PressureMaintenanceExecuting; return PendingPressureOutput.Task; }
                var output = kind == OperatorCommandKind.SetMaintenancePressure;
                Value.Engine.OutputsEnergized = Value.PressureMaintenance.OutputActive = Value.PressureMaintenance.MayBeEnergized = output;
                Value.PressureMaintenance.OutputGeneration++; Value.PressureMaintenance.OutputCommandId = output ? RecoveryProtocolV7.NewId() : "";
                Value.PressureMaintenance.CommandPressureBar = output ? payload.PressureBar : 0; Value.PressureMaintenance.Voltage = output ? payload.PressureBar / 20 : 0;
                Value.Kernel.CommandPending = false; Value.Kernel.PressureMaintenanceStage = RecoveryStage.PressureMaintenanceReady;
                if (kind == OperatorCommandKind.EndPressureMaintenance)
                {
                    Value.Kernel.PressureMaintenance.Revoked = true;
                    Value.Kernel.PressureMaintenanceStage = RecoveryStage.AwaitingSafetyProof;
                    Value.Kernel.CommandPending = true; Value.PressureMaintenance.AuthorityValid = false;
                    if (HoldMaintenanceProof) return Task.FromResult(new SupervisorOperatorCommandResponse { Accepted = true });
                    CompleteMaintenanceProof();
                }
            }
            return Task.FromResult(new SupervisorOperatorCommandResponse { Accepted = true, ExecutionCompleted = true, ExecutionSucceeded = true });
        }
        internal void CompleteMaintenanceProof()
        {
            Value.Engine.OutputsEnergized = false; Value.Engine.RecoveryOwnerId = Value.Engine.RecoveryIncidentId = "";
            Value.Kernel = KernelState(Value.Engine); Value.PressureMaintenance = null;
        }
        private void RefreshPressureDisplay()
        {
            if (!PressureMode || Value.PressureMaintenance == null) return;
            var display = Value.PressureMaintenance; display.CapturedUtcTicks = Value.CapturedUtcTicks;
            display.Pressure = new UiMeasurement { Valid = !PressureSampleInvalid, Value = display.OutputActive ? PressureValue : 0,
                CapturedUtcTicks = Value.CapturedUtcTicks - (PressureSampleStale ? TimeSpan.FromMilliseconds(500).Ticks : 0) };
        }
        public Task<PressureMaintenanceHeartbeatResponse> RenewMaintenanceAsync(PressureMaintenanceHeartbeat heartbeat, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (RejectMaintenanceHeartbeat) return Task.FromResult(new PressureMaintenanceHeartbeatResponse { Detail = "isolated renewal denied" });
            var lease = Value.Kernel.PressureMaintenance.Clone(); Assert(heartbeat.Binds(lease)); LastHeartbeat = heartbeat;
            lease.LastHeartbeatSequence = heartbeat.Sequence; lease.LastHeartbeatUtcTicks = heartbeat.IssuedUtcTicks;
            lease.ExpiresUtcTicks = heartbeat.IssuedUtcTicks + TimeSpan.FromSeconds(10).Ticks; lease.Revision += heartbeat.Sequence;
            Interlocked.Increment(ref MaintenanceHeartbeats);
            return Task.FromResult(new PressureMaintenanceHeartbeatResponse { Accepted = true, Lease = lease });
        }
    }
}
