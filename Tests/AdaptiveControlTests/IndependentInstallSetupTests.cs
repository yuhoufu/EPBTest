using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using Config;
using MtEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class IndependentInstallSetupTests
    {
        internal static int RunAll()
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                throw new InvalidOperationException("EPB_TEST_ARTIFACT_ROOT required"), "setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repo = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "TfTest.sln"))) repo = repo.Parent;
            if (repo == null) throw new InvalidOperationException("Source template required");
            var template = Path.Combine(repo.FullName, "MTTfTest", "Config", "TestConfig.xml");
            var original = SupervisorProtocol.ComputeSha256(template);
            var project = IndependentInstallSetup.CreateProject(root, "新项目 空格", template);
            IndependentInstallSetup.ValidateProject(project);
            var config = ConfigLoader.LoadTest(Path.Combine(project, "Config", "TestConfig.xml"), null);
            Check(config.EpbRecords.All(r => !r.Enabled && r.RunCount == 0), "New project has no enabled calipers or historical cycles");
            var database = Path.Combine(project, "index.db");
            Check(new FileInfo(database).Length > 0, "New SQLite file has a persisted header");
            var digest = SupervisorProtocol.ComputeSha256(database);
            IndependentInstallSetup.ValidateProject(project);
            Check(digest == SupervisorProtocol.ComputeSha256(database), "Opening existing project does not change database");
            Reject(() => IndependentInstallSetup.CreateProject(root, "新项目 空格", template), "Existing project cannot be replaced");
            Check(digest == SupervisorProtocol.ComputeSha256(database), "Duplicate create preserves database");
            File.Move(database, database + ".preserved");
            Reject(() => IndependentInstallSetup.ValidateProject(project), "Missing historical database is rejected");
            Check(!File.Exists(database), "Missing database is not invented");
            Check(original == SupervisorProtocol.ComputeSha256(template), "Template preserved");
            var marker = Path.Combine(root, "setup.json");
            File.WriteAllText(marker, "{\"schemaVersion\":1,\"version\":\"4.1.0.3\",\"stage\":\"AwaitingProject\",\"interactiveUserSid\":\"S-1-5-21-1-2-3-1001\",\"projectDirectory\":\"\"}", new UTF8Encoding(false));
            var state = BoundedJson.Read<IndependentInstallSetup.SetupState>(marker);
            Check(state.SchemaVersion == 1 && state.Stage == "AwaitingProject" && state.InteractiveUserSid.EndsWith("1001"), "PowerShell marker is readable by startup gate");
            BoundedJson.Write(marker, state);
            Check(BoundedJson.Read<IndependentInstallSetup.SetupState>(marker).Version == "4.1.0.3", "Setup result round trip");
            Exception uiFailure = null;
            var ui = new Thread(() =>
            {
                try
                {
                    var type = typeof(IndependentInstallSetup).GetNestedType("ProjectForm", BindingFlags.NonPublic);
                    using (var form = (Form)Activator.CreateInstance(type, true))
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        Check(form.Controls.OfType<Button>().Count() == 3, "Project screen has open/create/finish actions");
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-2000, -2000);
                        form.ShowInTaskbar = false;
                        form.Show();
                        Application.DoEvents();
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(root, "first-project.png"));
                        form.Hide();
                    }
                }
                catch (Exception error) { uiFailure = error; }
            });
            ui.SetApartmentState(ApartmentState.STA); ui.Start();
            if (!ui.Join(15000)) throw new TimeoutException("Project setup UI construction timed out");
            if (uiFailure != null) throw new Exception("Project setup UI failed", uiFailure);
            Console.WriteLine("PASS independent installation project setup 1/1 (no hardware/services)");
            return 1;
        }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Reject(Action action, string message)
        {
            try { action(); } catch (IOException) { return; } catch (InvalidDataException) { return; } catch (InvalidOperationException) { return; }
            throw new Exception(message);
        }
    }
}
