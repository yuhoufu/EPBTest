using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest;
using MtEmbTest;
using MTTFTest.Watchdog.Protocol;

internal static partial class Program
{
    private static void ResetOriginalButton(string scenario)
    {
        using (var f = new ProjectUiFixture())
        using (var session = new V3MonitorSession(f.Client))
        {
            var before = File.ReadAllBytes(f.Source);
            f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.ProjectCreation }).ToArray();
            if (scenario != "gates") f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.ProjectReset }).ToArray();
            session.RefreshAsync().GetAwaiter().GetResult();
            var confirmations = 0;
            using (var form = new FrmTestSetting(session, _ => false, _ => false,
                name => { confirmations++; Assert(name == "Source"); return scenario != "cancel"; }))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                form.Show(); Application.DoEvents();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                session.RefreshAsync().GetAwaiter().GetResult();
                var reset = form.Controls.Find("uiButtonResetEpbRecord", true).Single();
                var save = form.Controls.Find("BtnSaveTest", true).Single();
                Assert(f.Client.Commands == 0);
                if (scenario == "gates")
                {
                    Assert(!reset.Enabled); ClickControl(reset); Assert(f.Client.Commands == 0);
                    f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.ProjectReset }).ToArray();
                    session.RefreshAsync().GetAwaiter().GetResult(); Assert(reset.Enabled);
                    f.Client.Value.Engine.State = SystemTerminalState.Running;
                    session.RefreshAsync().GetAwaiter().GetResult(); Assert(!reset.Enabled); ClickControl(reset); Assert(f.Client.Commands == 0);
                    f.Client.Value.Engine.State = SystemTerminalState.StoppedByOperator; f.Client.Value.ConfigurationRevision++;
                    session.RefreshAsync().GetAwaiter().GetResult(); Assert(!reset.Enabled); ClickControl(reset); Assert(f.Client.Commands == 0);
                    return;
                }
                Assert(reset.Enabled && save.Enabled);
                form.Controls.Find("TxtTestName", true).Single().Text = "UnsavedOtherProject";
                form.Controls.Find("TxtTestCycle", true).Single().Text = "19";
                f.Client.PendingProject = new TaskCompletionSource<SupervisorOperatorCommandResponse>();
                ClickControl(reset);
                if (scenario == "cancel")
                {
                    Assert(form.PendingSave.IsCompleted && confirmations == 1 && f.Client.Commands == 0 && reset.Enabled);
                }
                else
                {
                    PumpUntil(() => f.Client.LastProjectSwitch != null);
                    var request = f.Client.LastProjectSwitch;
                    Assert(request.IsStructurallyValid() && request.Reset != null && request.Creation == null &&
                        request.TargetConfigurationPath == f.Source && request.TargetProjectFileSha256 == request.SourceProjectFileSha256 &&
                        request.Reset.Configuration.TestName == "Source" && request.Reset.Configuration.TestPeriod == 19 &&
                        !request.Reset.Configuration.Channels.Single(c => c.Channel == 6).Selected && !reset.Enabled && !save.Enabled);
                    ClickControl(reset); ClickControl(save);
                    Assert(f.Client.Commands == 1 && confirmations == 1 && !form.PendingSave.IsCompleted);
                    if (scenario == "close") form.Close();
                    f.Client.PendingProject.SetResult(new SupervisorOperatorCommandResponse { Accepted = scenario != "reject", ExecutionCompleted = true,
                        ExecutionSucceeded = scenario != "reject", FailureCode = scenario == "reject" ? "ConfigurationRevisionConflict" : string.Empty });
                    PumpUntil(() => form.PendingSave.IsCompleted); form.PendingSave.GetAwaiter().GetResult();
                    if (scenario == "close") Assert(form.IsDisposed);
                    else if (scenario == "reject") Assert(reset.Enabled && form.Controls.Find("TxtTestCycle", true).Single().Text == "19");
                    else
                    {
                        var archive = Path.Combine(f.Root, ".v3-reset-archive-" + RecoveryProtocolV7.NewId());
                        f.Client.Value.ProjectSelection.LastResetArchivePath = archive;
                        session.RefreshAsync().GetAwaiter().GetResult();
                        Assert(form.Controls.Find("ConfigurationContractStatus", true).Single().Text.Contains(archive));
                    }
                }
                Assert(f.Client.Value.TestConfiguration.TestName == "Source" && f.Client.Value.TestConfiguration.TestPeriod == 15 &&
                    f.Client.Value.Channels[3].FormalCycles == 21504 && f.Client.Value.Channels[5].Isolated &&
                    f.Client.Value.Engine.State == SystemTerminalState.StoppedByOperator && File.ReadAllBytes(f.Source).SequenceEqual(before) &&
                    !Directory.Exists(Path.Combine(f.Root, "UnsavedOtherProject")));
            }
        }
    }
}
