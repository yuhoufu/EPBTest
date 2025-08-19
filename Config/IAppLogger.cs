using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Config
{
    /// <summary>
    /// 统一日志接口：信息 / 警告 / 错误。
    /// 与原有接口保持兼容，新增 Warn。
    /// </summary>
    public interface IAppLogger
    {
        /// <summary>记录信息日志。</summary>
        /// <param name="message">内容</param>
        /// <param name="category">分类（如：DO操作/液压/EPB/配置等）</param>
        void Info(string message, string category = null);

        /// <summary>记录警告日志。</summary>
        /// <param name="message">内容</param>
        /// <param name="category">分类</param>
        void Warn(string message, string category = null);

        /// <summary>记录错误日志。</summary>
        /// <param name="message">内容</param>
        /// <param name="category">分类</param>
        /// <param name="ex">异常对象（可空）</param>
        void Error(string message, string category = null, Exception ex = null);
    }

    /// <summary>
    /// 空实现：便于未注入时调用安全。
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

        public void Warn(string message, string category = null)
        {
        }

        public void Error(string message, string category = null, Exception ex = null)
        {
        }
    }
}