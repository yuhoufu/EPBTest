using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows.Forms;
using DataOperation;
using IO.NI;
namespace MTEmbTest
{
    /// <summary>
    /// 面向 WinForm 的日志适配器：把 IAppLogger 的 Info/Warn/Error
    /// 分别写入 信息 / 警告 / 错误 队列；线程安全（UI Invoke）。
    /// </summary>
    public class FormLoggerAdapter : Config.IAppLogger
    {
        private readonly Control _ui;           // 用于回主线程
        private readonly int _maxInfos, _maxWarns, _maxErrors;
        private ConcurrentQueue<string> _logInfo;
        private ConcurrentQueue<string> _logWarn;
        private ConcurrentQueue<string> _logError;

        /// <summary>
        /// 构造适配器。
        /// </summary>
        /// <param name="maxInfos">信息队列最大长度</param>
        /// <param name="maxWarns">警告队列最大长度</param>
        /// <param name="maxErrors">错误队列最大长度</param>
        /// <param name="logInfo">信息队列引用</param>
        /// <param name="logWarn">警告队列引用</param>
        /// <param name="logError">错误队列引用</param>
        /// <param name="uiForInvoke">UI 控件（用于跨线程回调），可为 null</param>
        public FormLoggerAdapter(
            int maxInfos, int maxWarns, int maxErrors,
            ConcurrentQueue<string> logInfo, ConcurrentQueue<string> logWarn, ConcurrentQueue<string> logError,
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

        public void Info(string message, string category = null)
        {
            void write() => ClsLogProcess.AddToInfoList(_maxInfos, ref _logInfo, message, category ?? "信息");
            if (_ui != null && _ui.InvokeRequired) _ui.BeginInvoke((Action)write); else write();
        }

        public void Warn(string message, string category = null)
        {
            void write() => ClsLogProcess.AddToWarnList(_maxWarns, ref _logWarn, message, category ?? "警告");
            if (_ui != null && _ui.InvokeRequired) _ui.BeginInvoke((Action)write); else write();
        }

        public void Error(string message, string category = null, Exception ex = null)
        {
            string msg = ex == null ? message : $"{message} | {ex}";
            void write() => ClsErrorProcess.AddToErrorList(_maxErrors, ref _logError, msg, category ?? "错误");
            if (_ui != null && _ui.InvokeRequired) _ui.BeginInvoke((Action)write); else write();
        }
    }
}