using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using Config;
using Controller;
using DataOperation;

namespace AdaptiveControlTests
{
    internal static class HistoricalStorageBudgetTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("HistoricalMinimumFreeBytes严格解析", StrictMinimumFreeBytesParsing, ref passed);
            Run("Historical低空间边界与未知空间安全", LowSpaceBoundaryAndUnknownSpace, ref passed);
            Run("Historical正常写入字节计量", NormalWriteByteMeasurement, ref passed);
            return passed;
        }

        private static void StrictMinimumFreeBytesParsing()
        {
            var warnings = new List<string>();
            var invalid = new NameValueCollection
            {
                ["HistoricalMinimumFreeBytes"] = "0"
            };
            var policy = ProgramStoragePolicy.Load(warnings.Add, invalid);
            Assert(policy.HistoricalMinimumFreeBytes == 10L * 1024L * 1024L * 1024L,
                "HistoricalMinimumFreeBytes=0 未回退默认10GiB。");
            Assert(warnings.Exists(x => x.IndexOf("HistoricalMinimumFreeBytes", StringComparison.Ordinal) >= 0),
                "HistoricalMinimumFreeBytes非法配置未告警。");

            var valid = ProgramStoragePolicy.Load(null, new NameValueCollection
            {
                ["HistoricalMinimumFreeBytes"] = "123456789"
            });
            Assert(valid.HistoricalMinimumFreeBytes == 123456789,
                "HistoricalMinimumFreeBytes有效值未解析。");
        }

        private static void LowSpaceBoundaryAndUnknownSpace()
        {
            Assert(HistoricalStorageBudget.IsBelowMinimum(99, 100),
                "低于最低剩余空间时未跳过历史副本。");
            Assert(!HistoricalStorageBudget.IsBelowMinimum(100, 100),
                "刚好达到最低剩余空间时错误跳过历史副本。");
            Assert(!HistoricalStorageBudget.IsBelowMinimum(-1, 100),
                "剩余空间未知时不应按低空间误跳过历史副本。");

            const string uncPath = @"\\server\share\HistoricalSnapshots";
            string normalizedPath = null;
            Assert(HistoricalStorageBudget.TryGetAvailableFreeBytes(
                       uncPath,
                       path =>
                       {
                           normalizedPath = path;
                           return 123L;
                       },
                       out var injectedFree) && injectedFree == 123L,
                "UNC剩余空间探针注入未返回可用字节。");
            Assert(string.Equals(normalizedPath, Path.GetFullPath(uncPath),
                       StringComparison.OrdinalIgnoreCase),
                "UNC目标路径未先规范化后查询。");

            var root = Path.Combine(Path.GetTempPath(), "epb-historical-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Assert(HistoricalStorageBudget.TryGetAvailableFreeBytes(root, out var free) && free >= 0,
                    "正常目标卷无法查询剩余空间。");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void NormalWriteByteMeasurement()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-historical-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var first = Path.Combine(root, "a.bin");
                var nested = Path.Combine(root, "nested");
                Directory.CreateDirectory(nested);
                File.WriteAllBytes(first, new byte[17]);
                File.WriteAllBytes(Path.Combine(nested, "b.bin"), new byte[29]);
                Assert(HistoricalStorageBudget.MeasureDirectoryBytes(root) == 46,
                    "Historical写入字节计量未覆盖嵌套文件。");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
