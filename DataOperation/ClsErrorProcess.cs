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
   public class ClsErrorProcess
    {
        #region 添加到错误列表
        public static string AddToErrorList(int MaxLens, ref ConcurrentQueue<string> LogError, string ErrorText, string ErrType)
        {
            try
            {
                LogError.Enqueue(DateTime.Now.ToString("yyyy-MM-dd:HH:mm:ss.fff>  ,") + ErrType + "  ," + ErrorText);
                if (LogError.Count > MaxLens)
                {
                    string RemoveString;
                    LogError.TryDequeue(out RemoveString);
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
        #endregion

        #region 浏览错误列表
        public static string ViewErrorData(ref ConcurrentQueue<string> LogError, string FileName)
        {
            try
            {
                string[] WriteText = LogError.ToArray();
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
