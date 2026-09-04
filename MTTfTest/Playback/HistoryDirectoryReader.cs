using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Xml;

namespace MTEmbTest.Playback
{
    internal sealed class HistoryProjectMetadata
    {
        internal string TestName, TestTarget;
    }

    internal static class HistoryDirectoryReader
    {
        internal const int MaximumFiles = 10000;
        internal const int MaximumConfigurationBytes = 1024 * 1024;
        internal static string[] List(string directory, string pattern, CancellationToken token)
        {
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var file = new FileInfo(path);
                if (file.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (files.Count == MaximumFiles) throw new IOException("匹配历史文件超过 10000 个，请选择更具体的历史目录。");
                files.Add(file);
            }
            return files.OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => f.Name).ToArray();
        }

        internal static HistoryProjectMetadata LoadTestConfiguration(string directory, CancellationToken token)
        {
            var rootPath = Path.Combine(directory, "TestConfig.xml");
            var configPath = Path.Combine(directory, "Config", "TestConfig.xml");
            var candidates = new[] { rootPath, configPath }.Where(File.Exists).ToArray();
            if (candidates.Length == 0) return null;
            if (candidates.Length == 2 && !HashesMatch(candidates[0], candidates[1], token))
                throw new InvalidDataException("历史目录中两份 TestConfig.xml 不一致，无法确定权威试验信息。");

            token.ThrowIfCancellationRequested();
            using (var stream = OpenBounded(candidates[0]))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumConfigurationBytes
            }))
            {
                var metadata = new HistoryProjectMetadata();
                var document = new XmlDocument { XmlResolver = null };
                document.Load(reader); token.ThrowIfCancellationRequested();
                foreach (XmlNode node in document.GetElementsByTagName("TestName"))
                    AssignUnique(ref metadata.TestName, node.InnerText, "TestName");
                foreach (XmlNode node in document.GetElementsByTagName("TestTarget"))
                    AssignUnique(ref metadata.TestTarget, node.InnerText, "TestTarget");
                return metadata;
            }
        }

        private static void AssignUnique(ref string current, string value, string name)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length > 256) throw new InvalidDataException("历史 " + name + " 超过 256 字符。");
            if (current != null && !string.Equals(current, value, StringComparison.Ordinal))
                throw new InvalidDataException("历史 " + name + " 存在冲突值。");
            current = value;
        }

        private static bool HashesMatch(string first, string second, CancellationToken token)
        {
            using (var hash = SHA256.Create())
            {
                var firstHash = HashFile(first, hash, token);
                var secondHash = HashFile(second, hash, token);
                return firstHash.SequenceEqual(secondHash);
            }
        }

        private static byte[] HashFile(string path, HashAlgorithm hash, CancellationToken token)
        {
            using (var stream = OpenBounded(path))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var result = hash.Hash;
                hash.Initialize();
                return result;
            }
        }

        private static FileStream OpenBounded(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
            if (stream.Length == 0 || stream.Length > MaximumConfigurationBytes)
            {
                stream.Dispose();
                throw new InvalidDataException("历史 TestConfig.xml 为空或超过 1 MiB 上限。");
            }
            return stream;
        }
    }
}
