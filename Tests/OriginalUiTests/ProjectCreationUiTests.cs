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
    private static async Task NewProjectBrowser()
    {
        using (var f = new ProjectUiFixture())
        using (var session = new V3MonitorSession(f.Client))
        {
            var original = File.ReadAllBytes(f.Source);
            var config = f.Client.Value.TestConfiguration.Clone(); config.TestName = "NewProject";
            var request = await OriginalProjectBrowser.PrepareCreationRequestAsync(f.Client.Value, config, CancellationToken.None);
            Assert(request.IsStructurallyValid() && request.Creation.Configuration.TestName == "NewProject" &&
                request.TargetProjectFileSha256 == string.Empty && request.BaseConfigurationRevision == 17 &&
                !Directory.Exists(Path.Combine(f.Root, "NewProject")) && File.ReadAllBytes(f.Source).SequenceEqual(original));
            config.TestName = "changed";
            Assert(request.Creation.Configuration.TestName == "NewProject");
            foreach (var existing in new[] { "Source", "Target", "Empty", "NotADirectory" })
            {
                if (existing == "Empty") Directory.CreateDirectory(Path.Combine(f.Root, existing));
                if (existing == "NotADirectory") File.WriteAllText(Path.Combine(f.Root, existing), "old work");
                config.TestName = existing;
                var rejected = false;
                try { await OriginalProjectBrowser.PrepareCreationRequestAsync(f.Client.Value, config, CancellationToken.None); }
                catch (IOException) { rejected = true; }
                Assert(rejected && f.Client.Commands == 0);
            }
            var staging = Path.Combine(f.Root, ".v3-create-" + RecoveryProtocolV7.NewId(), "Config");
            Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "TestConfig.xml"), "not-published");
            Assert((await OriginalProjectBrowser.ListAsync(f.Root, CancellationToken.None)).SequenceEqual(new[] { f.Source, f.Target }));
            await session.RefreshAsync();
            Assert(session.CanSwitchProject && !session.CanCreateProject);
            await session.SubmitProjectSwitchAsync(request); Assert(f.Client.Commands == 0);
            f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.ProjectCreation }).ToArray();
            await session.RefreshAsync(); Assert(session.CanCreateProject);
            var invalid = f.Client.Value.Capabilities.Where(c => c != EngineUiContract.ProjectSwitch).ToArray();
            f.Client.Value.Capabilities = invalid; Assert(!f.Client.Value.IsStructurallyValid());
        }
    }

    private static void NewProjectOriginalSave(string scenario)
    {
        using (var f = new ProjectUiFixture())
        using (var session = new V3MonitorSession(f.Client))
        {
            var original = File.ReadAllBytes(f.Source);
            var createdRoot = Path.Combine(f.Root, "BrandNew");
            if (scenario == "existing") Directory.CreateDirectory(createdRoot);
            if (scenario != "capability")
                f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.ProjectCreation }).ToArray();
            session.RefreshAsync().GetAwaiter().GetResult();
            var confirmations = 0;
            using (var form = new FrmTestSetting(session, _ => false, name => { confirmations++; Assert(name == "BrandNew"); return scenario != "cancel"; }))
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                form.Show(); Application.DoEvents();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                session.RefreshAsync().GetAwaiter().GetResult();
                var name = form.Controls.Find("TxtTestName", true).Single();
                var save = form.Controls.Find("BtnSaveTest", true).Single();
                Assert(f.Client.Commands == 0);
                if (scenario == "capability")
                {
                    Assert(!name.Enabled);
                    f.Client.Value.Capabilities = f.Client.Value.Capabilities.Concat(new[] { EngineUiContract.ProjectCreation }).ToArray();
                    session.RefreshAsync().GetAwaiter().GetResult(); Assert(name.Enabled);
                    f.Client.Value.Engine.State = SystemTerminalState.Running;
                    session.RefreshAsync().GetAwaiter().GetResult();
                    Assert(!name.Enabled && !save.Enabled); ClickControl(save); Assert(f.Client.Commands == 0);
                    return;
                }
                Assert(name.Enabled && save.Enabled);
                name.Text = "BrandNew";
                form.Controls.Find("TxtTestCycle", true).Single().Text = "19";
                form.Controls.Find("TxtTestMan", true).Single().Text = "NewOperator";
                f.Client.PendingProject = new TaskCompletionSource<SupervisorOperatorCommandResponse>();
                ClickControl(save);
                if (scenario == "cancel" || scenario == "existing")
                {
                    PumpUntil(() => form.PendingSave.IsCompleted); form.PendingSave.GetAwaiter().GetResult();
                    Assert(confirmations == 1 && f.Client.Commands == 0 && name.Text == "BrandNew" && save.Enabled);
                    if (scenario == "existing") Assert(form.Controls.Find("ConfigurationContractStatus", true).Single().Text.Contains("同名项目目录已存在"));
                }
                else
                {
                    PumpUntil(() => f.Client.LastProjectSwitch != null);
                    var sent = f.Client.LastProjectSwitch;
                    Assert(sent.IsStructurallyValid() && sent.Creation.Configuration.TestName == "BrandNew" &&
                        sent.Creation.Configuration.TestPeriod == 19 && sent.Creation.Configuration.Owner == "NewOperator" &&
                        !sent.Creation.Configuration.Channels.Single(c => c.Channel == 6).Selected &&
                        f.Client.LastConfiguration == null && !save.Enabled);
                    ClickControl(save); Assert(f.Client.Commands == 1 && confirmations == 1 && !form.PendingSave.IsCompleted);
                    if (scenario == "close") form.Close();
                    f.Client.PendingProject.SetResult(new SupervisorOperatorCommandResponse { Accepted = true, ExecutionCompleted = true, ExecutionSucceeded = true });
                    PumpUntil(() => form.PendingSave.IsCompleted); form.PendingSave.GetAwaiter().GetResult();
                    if (scenario == "close") Assert(form.IsDisposed);
                    Assert(f.Client.Commands == 1);
                }
                Assert(f.Client.Value.Channels[3].FormalCycles == 21504 && f.Client.Value.Channels[5].Isolated &&
                    f.Client.Value.Engine.State == SystemTerminalState.StoppedByOperator && f.Client.Value.TestConfiguration.TestName == "Source" &&
                    f.Client.Value.TestConfiguration.TestPeriod == 15 && File.ReadAllBytes(f.Source).SequenceEqual(original));
                Assert(scenario == "existing" ? Directory.GetFileSystemEntries(createdRoot).Length == 0 : !Directory.Exists(createdRoot));
            }
        }
    }
}
