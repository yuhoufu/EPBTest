using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
    /// <summary>
    /// 日志管理：信息与警告（错误保留在 ClsErrorProcess）。
    /// </summary>
    public class ClsLogProcess
    {
        #region 添加到信息日志

        public static string AddToInfoList(int MaxLens, ref ConcurrentQueue<string> LogInformation, string InforText,
            string InforType)
        {
            try
            {
                LogInformation.Enqueue($"{DateTime.Now:yyyy-MM-dd:HH:mm:ss.fff>  ,}{InforType}  ,{InforText}");
                if (LogInformation.Count > MaxLens)
                {
                    string _;
                    LogInformation.TryDequeue(out _);
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        #endregion

        #region 浏览信息日志

        public static string ViewLogData(ref ConcurrentQueue<string> LogInformation, string FileName)
        {
            try
            {
                string[] WriteText = LogInformation.ToArray();
                using (var writer = new StreamWriter(FileName))
                {
                    for (int i = WriteText.Length - 1; i >= 0; i--)
                    {
                        if (!string.IsNullOrEmpty(WriteText[i]))
                        {
                            writer.Write(i.ToString("D7") + "\t");
                            writer.WriteLine(WriteText[i]);
                        }
                    }
                }

                Process.Start("notepad.exe", FileName);
                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        #endregion

        #region 添加到警告日志（新增）

        /// <summary>
        /// 追加警告日志到队列，超长自动出队。
        /// </summary>
        public static string AddToWarnList(int MaxLens, ref ConcurrentQueue<string> LogWarning, string WarnText,
            string WarnType)
        {
            try
            {
                LogWarning.Enqueue($"{DateTime.Now:yyyy-MM-dd:HH:mm:ss.fff>  ,}{WarnType}  ,{WarnText}");
                if (LogWarning.Count > MaxLens)
                {
                    string _;
                    LogWarning.TryDequeue(out _);
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        #endregion

        #region 浏览警告日志（新增）

        /// <summary>
        /// 将当前警告日志写入文件并用记事本打开查看。
        /// </summary>
        public static string ViewWarnData(ref ConcurrentQueue<string> LogWarning, string FileName)
        {
            try
            {
                string[] WriteText = LogWarning.ToArray();
                using (var writer = new StreamWriter(FileName))
                {
                    for (int i = WriteText.Length - 1; i >= 0; i--)
                    {
                        if (!string.IsNullOrEmpty(WriteText[i]))
                        {
                            writer.Write(i.ToString("D7") + "\t");
                            writer.WriteLine(WriteText[i]);
                        }
                    }
                }

                Process.Start("notepad.exe", FileName);
                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        #endregion
    }
}