using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IO.NI
{
    public interface IAppLogger
    {
        void Info(string message, string category = null);
        void Error(string message, string category = null, Exception ex = null);
    }

    /// <summary>
    /// 空实现，用于单元测试 / 未注入日志的场景，避免到处判空。
    /// </summary>
    public sealed class NullLogger : IAppLogger
    {
        public static readonly IAppLogger Instance = new NullLogger();

        private NullLogger()
        {
        }

        public void Info(string message, string category = null)
        {
        }

        public void Error(string message, string category = null, Exception ex = null)
        {
        }
    }
}