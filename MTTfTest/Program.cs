using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using MTEmbTest;

namespace MtEmbTest
{
    static class Program
    {

        private static void TryWriteFatalLog(string source, Exception ex)
        {
            try
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    $"{source}: {ex}",
                    "未处理异常");
                ProjectLogHub.Flush(true);
            }
            catch
            {
                // best-effort only
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (FirstRunBootstrap.TryRunElevatedWorker(args)) return;
            if (!LaunchCapabilityGate.ValidateOrReject(args)) return;
            if (!FirstRunBootstrap.PrepareOrExit(args)) return;
            try
            {
                var identity = V3EngineIdentity.Parse(args);
                if (Environment.OSVersion.Version.Major >= 6)
                    SetProcessDPIAware();
                Application.SetUnhandledExceptionMode(
                    UnhandledExceptionMode.CatchException);
                Application.ThreadException += (sender, eventArgs) =>
                {
                    TryWriteFatalLog("V3UiThreadException", eventArgs.Exception);
                    // No typed-exit receipt is written. Supervisor treats this
                    // as an abnormal UI disappearance and restarts UI only.
                    Environment.Exit(10);
                };
                AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
                    TryWriteFatalLog(
                        "V3UiUnhandledException",
                        eventArgs.ExceptionObject as Exception ??
                        new Exception(eventArgs.ExceptionObject?.ToString() ?? "<null>"));
                TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
                {
                    TryWriteFatalLog("V3UiUnobservedTaskException", eventArgs.Exception);
                    eventArgs.SetObserved();
                };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new V3EngineClientForm(identity));
            }
            catch (Exception ex)
            {
                TryWriteFatalLog("V3UiBootstrap", ex);
                MessageBox.Show(
                    "V3 界面无法连接由 Supervisor 管理的 EngineHost。\r\n" +
                    ex.GetBaseException().Message,
                    "启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                ProjectLogHub.Flush(true);
                ProjectLogHub.Shutdown();
            }
        }
    }
}
