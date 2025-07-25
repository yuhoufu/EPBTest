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
    public class ClsLogProcess
    {
        #region 添加到日志
        public static string AddToInfoList(int MaxLens, ref ConcurrentQueue<string> LogInformation, string InforText, string InforType)
        {
            try
            {
                LogInformation.Enqueue(DateTime.Now.ToString("yyyy-MM-dd:HH:mm:ss.fff>  ,") + InforType + "  ," + InforText);
                if (LogInformation.Count > MaxLens)
                {
                    string RemoveString;
                    LogInformation.TryDequeue(out RemoveString);
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
        #endregion

        #region 浏览日志列表
        public static string ViewLogData(ref ConcurrentQueue<string> LogInformation, string FileName)
        {
            try
            {
                string[] WriteText = LogInformation.ToArray();
                int Counter = WriteText.Length;
                string OutFile = FileName;
                StreamWriter writer = new StreamWriter(OutFile);
                for (int i = Counter - 1; i >= 0; i--)
                {
                    if (!string.IsNullOrEmpty(WriteText[i]))
                    {

                        writer.Write(i.ToString("D7") + "\t");
                        writer.WriteLine(WriteText[i]);
                    }
                }

                writer.Close();
                WriteText = null;
                Process.Start("notepad.exe", OutFile);

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
