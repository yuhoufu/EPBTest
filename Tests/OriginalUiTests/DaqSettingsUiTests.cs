using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest;
using MtEmbTest;
using MTTFTest.Watchdog.Protocol;

internal static partial class Program
{
    private static EngineDaqConfiguration DaqSettings() => new EngineDaqConfiguration
    {
        Channels = Enumerable.Range(1, 15).Select(i => new EngineDaqChannelConfiguration
        {
            Sequence = i, PhysicalChannel = "Dev" + (i <= 8 ? 1 : 2) + "/ai" + ((i - 1) % 8),
            ParameterName = i <= 12 ? "EPB" + i + "_current" : i < 15 ? "Pressure_" + (i - 12) : "Force",
            Unit = i <= 12 ? "A" : i < 15 ? "bar" : "N", Slope = i + 0.5, Intercept = -i / 100.0,
            ParameterType = i <= 12 ? "电流" : i < 15 ? "管路压力" : "夹紧力", Enabled = i != 6, ZeroOffset = i / 1000.0
        }).ToArray()
    };

    private static FakeClient DaqClient()
    {
        var fake = ConfigurationClient(); fake.Value.DaqConfiguration = DaqSettings(); fake.Value.DaqConfigurationRevision = 23;
        fake.Value.DaqConfigurationSha256 = fake.Value.DaqConfiguration.ComputeSha256();
        fake.Value.Capabilities = fake.Value.Capabilities.Concat(new[] { EngineUiContract.DaqConfiguration }).ToArray(); return fake;
    }

    private static void DaqDraftIdentity()
    {
        var snapshot = DaqClient().Value; var draft = new OriginalSettingsDraft(snapshot, daq: true);
        draft.Commit.DaqConfiguration.Channels[3].Slope = 11.5;
        Assert(snapshot.DaqConfiguration.Channels[3].Slope == 4.5 && draft.IsCurrent(snapshot));
        snapshot.ConfigurationRevision++; snapshot.Engine.Revision++; snapshot.Engine.EngineInstanceId = RecoveryProtocolV7.NewId();
        Assert(draft.IsCurrent(snapshot));
        snapshot.DaqConfigurationRevision++; Assert(!draft.IsCurrent(snapshot) && !draft.IsApplied(snapshot));
        snapshot.DaqConfigurationSha256 = draft.Commit.DaqConfiguration.ComputeSha256(); Assert(draft.IsApplied(snapshot));
        snapshot.Engine.RunEpoch++; Assert(!draft.IsCurrent(snapshot) && !draft.IsApplied(snapshot));
        var clone = draft.Commit.Clone(); clone.DaqConfiguration.Channels[0].Slope++;
        Assert(clone.ComputeSha256() != draft.Commit.ComputeSha256());
        clone.Configuration = Settings(); Assert(!clone.IsStructurallyValid());
    }

    private static void DaqOriginalGrid(string scenario)
    {
        var fake = DaqClient();
        using (var session = new V3MonitorSession(fake))
        {
            session.RefreshAsync().GetAwaiter().GetResult();
            using (var form = new FrmTestSetting(session))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                form.Show(); Application.DoEvents();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                session.RefreshAsync().GetAwaiter().GetResult();
                var grid = (DataGridView)form.Controls.Find("dgvDaqAI", true).Single();
                var save = form.Controls.Find("BtnSaveDaqAI", true).Single();
                var testSave = form.Controls.Find("BtnSaveTest", true).Single();
                var period = form.Controls.Find("TxtTestCycle", true).Single();
                Assert(fake.Commands == 0 && save.Enabled && grid.Rows.Count == 15 && grid.Columns.Count == 9);
                Assert(grid.Columns[0].ReadOnly && grid.Columns[1].ReadOnly && grid.Columns[8].ReadOnly && !grid.Columns[8].Visible &&
                    grid.Columns[6] is DataGridViewComboBoxColumn && grid.Columns[7] is DataGridViewCheckBoxColumn);
                Assert((double)grid.Rows[3].Cells["Slope"].Value == 4.5 && (string)grid.Rows[12].Cells["ParameterType"].Value == "管路压力");
                grid.Rows[3].Cells["Slope"].Value = 11.5; grid.EndEdit(); period.Text = "19";
                if (scenario == "gates")
                {
                    fake.Value.Engine.State = SystemTerminalState.Running; session.RefreshAsync().GetAwaiter().GetResult();
                    Assert(!save.Enabled && grid.ReadOnly); ClickControl(save); Assert(fake.Commands == 0);
                    fake.Value.Engine.State = SystemTerminalState.StoppedByOperator;
                    fake.Value.Capabilities = fake.Value.Capabilities.Where(c => c != EngineUiContract.DaqConfiguration).ToArray();
                    session.RefreshAsync().GetAwaiter().GetResult(); Assert(!save.Enabled); ClickControl(save); Assert(fake.Commands == 0);
                    fake.Value.Capabilities = fake.Value.Capabilities.Concat(new[] { EngineUiContract.DaqConfiguration }).ToArray();
                    fake.Value.DaqConfigurationRevision++; session.RefreshAsync().GetAwaiter().GetResult();
                    Assert(!save.Enabled && (double)grid.Rows[3].Cells["Slope"].Value == 11.5); ClickControl(save); Assert(fake.Commands == 0); return;
                }
                if (scenario == "independent")
                {
                    ClickControl(testSave); PumpUntil(() => form.PendingSave.IsCompleted); form.PendingSave.GetAwaiter().GetResult();
                    Assert(fake.Commands == 1 && (double)grid.Rows[3].Cells["Slope"].Value == 11.5 && save.Enabled);
                }
                var beforeCommands = fake.Commands;
                if (scenario == "close") fake.PendingConfiguration = new TaskCompletionSource<SupervisorOperatorCommandResponse>();
                fake.RejectConfiguration = scenario == "reject"; fake.LoseConfigurationResponse = scenario == "unknown";
                ClickControl(save);
                if (scenario == "close")
                {
                    Assert(!save.Enabled && !testSave.Enabled && !form.PendingDaqSave.IsCompleted);
                    ClickControl(save); ClickControl(testSave); Assert(fake.Commands == beforeCommands + 1);
                    form.Close(); fake.PendingConfiguration.SetResult(new SupervisorOperatorCommandResponse { Accepted = true });
                }
                PumpUntil(() => form.PendingDaqSave.IsCompleted); form.PendingDaqSave.GetAwaiter().GetResult();
                Assert(fake.Commands == beforeCommands + 1 && fake.LastConfiguration.Configuration == null &&
                    fake.LastConfiguration.DaqConfiguration.Channels[3].Slope == 11.5 && fake.LastConfiguration.BaseConfigurationRevision == 23);
                if (scenario == "close") Assert(form.IsDisposed);
                else if (scenario == "reject") Assert(save.Enabled && (double)grid.Rows[3].Cells["Slope"].Value == 11.5);
                else if (scenario == "unknown") { Assert(!save.Enabled); ClickControl(save); Assert(fake.Commands == beforeCommands + 1); }
                else Assert(save.Enabled && fake.Value.DaqConfigurationRevision == 24 && period.Text == "19" &&
                    fake.Value.TestConfiguration.TestPeriod == (scenario == "independent" ? 19 : 15));
                Assert(fake.Value.Channels[3].FormalCycles == 21504 && fake.Value.Channels[5].Isolated &&
                    fake.Value.Engine.State == SystemTerminalState.StoppedByOperator);
            }
        }
    }
}
