using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest;

namespace MtEmbTest
{
    static class Program
    {

        private static void TryWriteFatalLog(string source, Exception ex)
        {
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                var file = Path.Combine(dir, "FatalLog.txt");
                File.AppendAllText(
                    file,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
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
            Application.Run(new Main_Frm());
            //Application.Run(new FrmEpbMainMonitor());
        }
    }
}
