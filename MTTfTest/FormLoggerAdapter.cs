using System;
using System.Collections.Concurrent;
using System.Windows.Forms;
using Config;
using DataOperation;

namespace MTEmbTest
{
    /// <summary>
    /// 面向 WinForm 的日志适配器：UI 队列保持窗体内独立，持久化统一交给进程级项目日志中心。
    /// </summary>
    public class FormLoggerAdapter : IAppLogger, IFlushableAppLogger, IAsyncFlushableAppLogger, IDisposable
    {
        private readonly Control _ui;
        private readonly int _maxInfos;
        private readonly int _maxWarns;
        private readonly int _maxErrors;
        private ConcurrentQueue<string> _logInfo;
        private ConcurrentQueue<string> _logWarn;
        private ConcurrentQueue<string> _logError;
        private readonly object _projectLogConfigGate = new object();
        private string _configuredProjectRoot;
        private static readonly object FailureNoticeGate = new object();
        private static DateTime _lastFailureNoticeUtc = DateTime.MinValue;

        public FormLoggerAdapter(
            int maxInfos,
            int maxWarns,
            int maxErrors,
            ConcurrentQueue<string> logInfo,
            ConcurrentQueue<string> logWarn,
            ConcurrentQueue<string> logError,
            Control uiForInvoke)
        {
            _maxInfos = maxInfos;
            _maxWarns = maxWarns;
            _maxErrors = maxErrors;
            _logInfo = logInfo ?? new ConcurrentQueue<string>();
            _logWarn = logWarn ?? new ConcurrentQueue<string>();
            _logError = logError ?? new ConcurrentQueue<string>();
            _ui = uiForInvoke;
        }

        public void ConfigureProjectLogDirectory(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot)) return;
            if (ProjectLogHub.EnqueueConfigure(projectRoot))
                _configuredProjectRoot = projectRoot;
        }

        public void Info(string message, string category = null)
        {
            EnsureProjectLogConfigured();
            Persist(ProjectLogLevel.Info, message, category, null);
            Dispatch(() => ClsLogProcess.AddToInfoList(
                _maxInfos,
                ref _logInfo,
                message,
                category ?? "信息"));
        }

        public void Warn(string message, string category = null)
        {
            EnsureProjectLogConfigured();
            Persist(ProjectLogLevel.Warning, message, category, null);
            ProjectLogHub.RequestFlush(true);
            Dispatch(() => ClsLogProcess.AddToWarnList(
                _maxWarns,
                ref _logWarn,
                message,
                category ?? "警告"));
        }

        public void Error(string message, string category = null, Exception ex = null)
        {
            EnsureProjectLogConfigured();
            Persist(ProjectLogLevel.Error, message, category, ex);
            ProjectLogHub.RequestFlush(true);
            var detail = ex == null ? message : $"{message} | {ex}";
            Dispatch(() => ClsErrorProcess.AddToErrorList(
                _maxErrors,
                ref _logError,
                detail,
                category ?? "错误"));
        }

        public bool Flush(bool durable = false)
        {
            EnsureProjectLogConfigured();
            return ProjectLogHub.Flush(durable);
        }

        public bool RequestFlush(bool durable = false)
        {
            EnsureProjectLogConfigured();
            return ProjectLogHub.RequestFlush(durable);
        }

        public void Dispose()
        {
            // 共享日志中心由 Program 的进程退出路径统一关闭。
        }

        private void EnsureProjectLogConfigured()
        {
            try
            {
                lock (_projectLogConfigGate)
                {
                    var projectRoot = global::ConfigLoader.CurrentProjectRootDir;
                    if (string.IsNullOrWhiteSpace(projectRoot)) return;
                    if (string.Equals(projectRoot, _configuredProjectRoot, StringComparison.OrdinalIgnoreCase))
                        return;
                    ConfigureProjectLogDirectory(projectRoot);
                }
            }
            catch
            {
                // 项目目录暂不可用时，下条日志会自动重试。
            }
        }

        private void Persist(
            ProjectLogLevel level,
            string message,
            string category,
            Exception exception)
        {
            if (ProjectLogHub.Enqueue(level, message, category, exception)) return;

            var failure = ProjectLogHub.LastFailure;
            lock (FailureNoticeGate)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastFailureNoticeUtc).TotalMinutes < 1) return;
                _lastFailureNoticeUtc = now;
            }

            var notice =
                "项目日志后台队列已满或写盘/轮转失败，控制流程继续；日志链路不会反压实时控制。" +
                (failure == null ? string.Empty : $" 原因：{failure.Message}");
            Dispatch(() => ClsLogProcess.AddToWarnList(
                _maxWarns,
                ref _logWarn,
                notice,
                "日志"));
        }

        private void Dispatch(Action write)
        {
            if (_ui != null && !_ui.IsDisposed && _ui.IsHandleCreated && _ui.InvokeRequired)
            {
                try
                {
                    _ui.BeginInvoke(write);
                    return;
                }
                catch
                {
                    // 窗体关闭/句柄销毁期间降级为直接写队列。
                }
            }

            write();
        }
    }
}
