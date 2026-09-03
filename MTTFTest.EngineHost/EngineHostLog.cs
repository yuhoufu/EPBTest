using System;
using System.IO;
using System.Text;
using Config;

namespace MTTFTest.EngineHost
{
    internal sealed class EngineHostLog : IAppLogger
    {
        private static readonly object Gate = new object();
        private static readonly string PathName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MTTFTest",
            "EngineHost",
            "engine-host.log");

        public void Info(string message, string category = null) =>
            Write("INFO", category, message, null);

        public void Warn(string message, string category = null) =>
            Write("WARN", category, message, null);

        public void Error(string message, string category = null, Exception ex = null) =>
            Write("ERROR", category, message, ex);

        internal static void Error(string message, Exception ex) =>
            Write("ERROR", "EngineHost", message, ex);

        internal static void Info(string message) =>
            Write("INFO", "EngineHost", message, null);

        private static void Write(
            string level,
            string category,
            string message,
            Exception exception)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName));
                    File.AppendAllText(
                        PathName,
                        DateTime.UtcNow.ToString("O") + "\t" + level + "\t" +
                        (category ?? "EngineHost") + "\t" + (message ?? string.Empty) +
                        (exception == null ? string.Empty : "\t" + exception) +
                        Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
