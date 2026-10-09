using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows.Forms;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    // Setup runs before Main_Frm, FirstRunBootstrap and all control/hardware code.
    internal static class IndependentInstallSetup
    {
        internal sealed class SetupState
        {
            public SetupState() { }
            public int SchemaVersion { get; set; }
            public string Version { get; set; }
            public string Stage { get; set; }
            public string InteractiveUserSid { get; set; }
            public string ProjectDirectory { get; set; }
        }

        internal static bool PrepareOrExit(string[] args)
        {
            var executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MTTFTest.exe");
            var root = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
            var marker = Path.Combine(root, "install-setup.json");
            var projectUi = args.Contains("--independent-setup-project");
            if (!File.Exists(marker))
            {
                if (!projectUi) return true;
                Environment.ExitCode = 66;
                return false;
            }
            try
            {
                IndependentProtectedFiles.RequireTrustedFile(marker);
                var state = BoundedJson.Read<SetupState>(marker);
                if (state == null || state.SchemaVersion != 1 ||
                    (state.Stage != "Ready" && state.Version != FileVersionInfo.GetVersionInfo(executable).FileVersion) ||
                    !new[] { "AwaitingProject", "Binding", "Ready" }.Contains(state.Stage))
                    throw new InvalidDataException("安装项目设置状态无效，请使用原安装包修复。");
                if (state.Stage == "Ready")
                {
                    if (IndependentInstallationBinding.ResolveForStartup(executable) == null)
                        throw new InvalidDataException("恢复注册缺失，不能以未绑定方式运行。");
                    string configError;
                    if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RuntimeConfigPaths.ConfiguredMarkerName)) ||
                        !RuntimeConfigPaths.Validate(true, out configError))
                        throw new InvalidDataException("已安装运行配置不完整，请使用原构建修复入口；不会转用旧版首次运行安装器。");
                    if (projectUi) throw new InvalidOperationException("项目已经绑定，不能重复执行首次设置。");
                    return true;
                }
                using (var user = WindowsIdentity.GetCurrent())
                    if (user.User.Value != state.InteractiveUserSid)
                        throw new InvalidOperationException("请用安装时的试验账户启动程序，不能用其他管理员账户代替。");
                if (projectUi)
                {
                    if (args.Length != 1 || state.Stage != "AwaitingProject")
                        throw new InvalidOperationException("首次项目设置入口状态无效。");
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new ProjectForm())
                    {
                        if (form.ShowDialog() != DialogResult.OK) { Environment.ExitCode = 2; return false; }
                        state.ProjectDirectory = form.ProjectDirectory;
                        BoundedJson.Write(Path.Combine(root, "setup-project.json"), state);
                    }
                    Environment.ExitCode = 0;
                    return false;
                }
                if (args.Length != 0) throw new InvalidOperationException("项目尚未绑定，不能从恢复或其他启动入口运行。");
                var script = Path.Combine(root, "Tools", "Complete-IndependentSetup.ps1");
                IndependentProtectedFiles.RequireTrustedFile(script);
                using (var current = Process.GetCurrentProcess())
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) + " -Mode Launch -InstallRoot " + Quote(root) +
                        " -ParentProcessId " + current.Id + " -ParentStartUtcTicks " + current.StartTime.ToUniversalTime().Ticks,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                })) { }
            }
            catch (Exception error)
            {
                Environment.ExitCode = 66;
                MessageBox.Show(error.GetBaseException().Message, "EPB 项目设置未完成", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }

        private static string Quote(string value)
        {
            if (value.Contains("\"")) throw new ArgumentException("路径包含引号。");
            return "\"" + value + "\"";
        }

        internal static string CreateProject(string store, string name, string template)
        {
            var source = ConfigLoader.LoadTest(template, null);
            ConfigLoader.CreateNewProjectTestConfig(source, template, store, name);
            var project = ConfigLoader.GetProjectRootDir(store, name);
            // Only a project just created above gets a new empty database. Never
            // replace a missing historical database or fabricate completed cycles.
            var database = Path.Combine(project, "index.db");
            using (new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var builder = new SQLiteConnectionStringBuilder { DataSource = database, Pooling = false };
            using (var connection = new SQLiteConnection(builder.ConnectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version=0;";
                    command.ExecuteNonQuery();
                }
            }
            return project;
        }

        internal static void ValidateProject(string project)
        {
            if (!Path.IsPathRooted(project ?? string.Empty) || project.StartsWith(@"\\") || project.Contains("\""))
                throw new InvalidDataException("请选择本机项目目录。");
            if (!File.Exists(Path.Combine(project, "index.db")))
                throw new InvalidDataException("项目缺少 index.db；不会自动重建历史数据库，请选择完整项目或新建项目。");
            var config = ConfigLoader.LoadTest(Path.Combine(project, "Config", "TestConfig.xml"), null);
            if (config.Hydraulics == null || !config.Hydraulics.Any(h => h.Enabled))
                throw new InvalidDataException("项目没有启用的液压配置，不能完成恢复绑定。");
        }

        private sealed class ProjectForm : Form
        {
            internal string ProjectDirectory { get; private set; }
            private readonly Label _selected;

            internal ProjectForm()
            {
                Text = "EPB 首次项目设置 — 尚未启用恢复";
                ClientSize = new Size(640, 250);
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                var info = new Label { Left = 20, Top = 18, Width = 600, Height = 45,
                    Text = "程序已安装。请创建新项目或打开已有项目，完成后自动绑定恢复。\r\n本页面不连接硬件、不启动试验。" };
                var open = new Button { Left = 20, Top = 75, Width = 180, Text = "打开已有项目" };
                var create = new Button { Left = 215, Top = 75, Width = 180, Text = "创建新项目" };
                _selected = new Label { Left = 20, Top = 120, Width = 600, Height = 65, Text = "尚未选择项目" };
                var finish = new Button { Left = 410, Top = 200, Width = 210, Text = "完成设置并打开程序" };
                Controls.AddRange(new Control[] { info, open, create, _selected, finish });
                open.Click += (s, e) => Run(() =>
                {
                    using (var chooser = new FolderBrowserDialog { Description = "选择包含 Config 和 index.db 的项目目录", ShowNewFolderButton = false })
                        if (chooser.ShowDialog(this) == DialogResult.OK) SelectProject(chooser.SelectedPath);
                });
                create.Click += (s, e) => Run(() =>
                {
                    using (var chooser = new SaveFileDialog { Title = "选择存储位置并输入新项目名称", FileName = "EPB-" + DateTime.Now.ToString("yyyyMMdd"),
                        AddExtension = false, CheckFileExists = false, OverwritePrompt = true, Filter = "项目名称|*.*" })
                        if (chooser.ShowDialog(this) == DialogResult.OK)
                        {
                            var path = Path.GetFullPath(chooser.FileName);
                            SelectProject(CreateProject(Path.GetDirectoryName(path), Path.GetFileName(path), RuntimeConfigPaths.GetPath("TestConfig.xml")));
                        }
                });
                finish.Click += (s, e) => Run(() =>
                {
                    ValidateProject(ProjectDirectory);
                    string error;
                    if (!LastProjectSelectionStore.TrySave(Path.GetDirectoryName(ProjectDirectory), Path.GetFileName(ProjectDirectory), out error))
                        throw new IOException(error);
                    DialogResult = DialogResult.OK;
                    Close();
                });
            }

            private void SelectProject(string path)
            {
                ValidateProject(path);
                ProjectDirectory = Path.GetFullPath(path).TrimEnd('\\');
                _selected.Text = "将使用项目：\r\n" + ProjectDirectory;
            }

            private void Run(Action action)
            {
                try { action(); }
                catch (Exception error) { MessageBox.Show(this, error.GetBaseException().Message, "项目设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
    }
}
