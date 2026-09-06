using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MTTFTest.Watchdog
{
    internal static class MiniDumpCapture
    {
        [Flags]
        private enum MiniDumpType : uint
        {
            Normal = 0x00000000,
            WithDataSegs = 0x00000001,
            WithFullMemory = 0x00000002,
            WithHandleData = 0x00000004,
            WithThreadInfo = 0x00001000
        }

        [DllImport("Dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(
            IntPtr processHandle,
            int processId,
            IntPtr fileHandle,
            MiniDumpType dumpType,
            IntPtr exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam);

        internal static async Task TryCaptureAsync(
            Process process,
            string journalDirectory,
            string sessionId,
            TimeSpan timeout,
            Action<string> report,
            bool fullMemory = false)
        {
            if (process == null) return;
            var directory = Path.Combine(
                Path.GetFullPath(journalDirectory),
                "dumps",
                Sanitize(sessionId));
            Directory.CreateDirectory(directory);
            Prune(directory);
            var path = Path.Combine(
                directory,
                $"MTTFTest_{process.Id}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.dmp");
            var capture = Task.Run(() =>
            {
                using (var stream = new FileStream(
                           path,
                           FileMode.CreateNew,
                           FileAccess.ReadWrite,
                           FileShare.Read))
                {
                    var dumpType = MiniDumpType.WithDataSegs |
                                   MiniDumpType.WithHandleData |
                                   MiniDumpType.WithThreadInfo;
                    if (fullMemory) dumpType |= MiniDumpType.WithFullMemory;
                    var ok = MiniDumpWriteDump(
                        process.Handle,
                        process.Id,
                        stream.SafeFileHandle.DangerousGetHandle(),
                        dumpType,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    if (!ok)
                        throw new InvalidOperationException(
                            "MiniDumpWriteDump failed. Win32Error=" + Marshal.GetLastWin32Error());
                }
            });
            try
            {
                if (await Task.WhenAny(capture, Task.Delay(timeout)).ConfigureAwait(false) != capture)
                {
                    report?.Invoke("Timeout=" + timeout.TotalSeconds + "s;Path=" + path);
                    return;
                }
                await capture.ConfigureAwait(false);
                report?.Invoke("Captured=" + path);
                Prune(directory);
            }
            catch (Exception ex)
            {
                report?.Invoke("Failed=" + ex.GetBaseException().Message);
            }
        }

        private static void Prune(string directory)
        {
            try
            {
                const long maximumBytes = 256L * 1024 * 1024;
                var files = new DirectoryInfo(directory).GetFiles("*.dmp")
                    .OrderByDescending(item => item.CreationTimeUtc)
                    .ToList();
                long retained = 0;
                for (var index = 0; index < files.Count; index++)
                {
                    var keep = index < 3 && retained + files[index].Length <= maximumBytes;
                    if (keep) retained += files[index].Length;
                    else
                    {
                        try { files[index].Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        private static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((value ?? "unknown")
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray());
        }
    }
}
