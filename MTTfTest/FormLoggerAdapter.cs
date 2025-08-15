using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows.Forms;
using DataOperation;
using IO.NI;

namespace MTEmbTest
{

    public class FormLoggerAdapter : IAppLogger
    {
        private readonly Control _ui;   // 用于 Invoke/BeginInvoke 回 UI 线程
        private readonly int _maxInfos, _maxErrors;
        private ConcurrentQueue<string> _logInfo;
        private ConcurrentQueue<string> _logError;

        public FormLoggerAdapter(int maxInfos, int maxErrors,
            ConcurrentQueue<string> logInfo, ConcurrentQueue<string> logError,
            Control uiForInvoke)
        {
            _maxInfos = maxInfos; _maxErrors = maxErrors;
            _logInfo = logInfo; _logError = logError;
            _ui = uiForInvoke;
        }

        public void Info(string message, string category = null)
        {
            void write() => ClsLogProcess.AddToInfoList(_maxInfos, ref _logInfo, message, category ?? "DO操作");
            if (_ui != null && _ui.InvokeRequired) _ui.BeginInvoke((Action)write); else write();
        }

        public void Error(string message, string category = null, Exception ex = null)
        {
            string msg = ex == null ? message : $"{message} | {ex}";
            void write() => ClsErrorProcess.AddToErrorList(_maxErrors, ref _logError, msg, category ?? "DO操作");
            if (_ui != null && _ui.InvokeRequired) _ui.BeginInvoke((Action)write); else write();
        }
    }

}