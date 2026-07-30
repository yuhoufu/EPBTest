using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        static void Main()
        {
            if (Environment.OSVersion.Version.Major >= 6)
                SetProcessDPIAware();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                TryWriteFatalLog("Application.ThreadException", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                TryWriteFatalLog(
                    "AppDomain.CurrentDomain.UnhandledException",
                    e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "<null>"));
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                TryWriteFatalLog("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new Main_Frm());
            }
            finally
            {
                ProjectLogHub.Flush(true);
                ProjectLogHub.Shutdown();
            }
            //Application.Run(new FrmEpbMainMonitor());
        }
    }
}
