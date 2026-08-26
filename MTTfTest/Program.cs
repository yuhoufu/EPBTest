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
            var watchdogRecoveryIntent = WatchdogRecoveryIntent.Parse(args);
            var recoveryIntent = watchdogRecoveryIntent == null
                ? RecoveryProcessBootstrap.Parse(args)
                : null;
            UnattendedRecoveryCoordinator.SetRecoveryProcessMode(
                recoveryIntent != null || watchdogRecoveryIntent != null);
            using var recoveryHandoff = RecoveryProcessBootstrap.AttachHandoff(recoveryIntent);
            if (recoveryIntent != null && recoveryHandoff == null)
            {
                TryWriteFatalLog(
                    "RecoveryHandoff",
                    new InvalidOperationException("恢复子进程未取得父进程创建的跨进程撤权门，拒绝续测。"));
                return;
            }
            RecoveryProcessBootstrap.WaitForParent(recoveryIntent);
            if (recoveryHandoff?.IsRevoked == true)
            {
                TryWriteFatalLog(
                    "RecoveryHandoff",
                    new OperationCanceledException("父进程退出前运行授权已撤销，恢复子进程保持全断能退出。"));
                return;
            }
            using var singleInstance = new Mutex(false, RecoveryProcessBootstrap.MutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = singleInstance.WaitOne(recoveryIntent == null ? 0 : 5000, false);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }
                if (!ownsMutex) return;

            if (Environment.OSVersion.Version.Major >= 6)
                SetProcessDPIAware();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                TryWriteFatalLog("Application.ThreadException", e.Exception);
                UnattendedRecoveryCoordinator.RequestFatalRestart(
                    "Application.ThreadException",
                    e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                TryWriteFatalLog(
                    "AppDomain.CurrentDomain.UnhandledException",
                    e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "<null>"));
                UnattendedRecoveryCoordinator.RequestFatalRestart(
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
            Main_Frm mainForm = null;
            try
            {
                mainForm = new Main_Frm(recoveryIntent, watchdogRecoveryIntent);
                Application.Run(mainForm);
            }
            finally
            {
                if (mainForm != null)
                {
                    try
                    {
                        mainForm.ShutdownWatchdogForApplicationExitAndReleaseUiAsync(
                                "ApplicationExit")
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        TryWriteFatalLog("MainOwnedWatchdogShutdown", ex);
                    }
                }
                ProjectLogHub.Flush(true);
                ProjectLogHub.Shutdown();
            }
            //Application.Run(new FrmEpbMainMonitor());
            }
            finally
            {
                if (ownsMutex)
                {
                    try { singleInstance.ReleaseMutex(); } catch { }
                }
            }
        }
    }
}
