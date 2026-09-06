using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Config
{
    /// <summary>
    /// Resolves the machine-writable runtime configuration directory. Installed
    /// binaries keep their packaged Config directory read-only and use ProgramData;
    /// repository/developer runs continue to use the adjacent Config directory.
    /// </summary>
    public static class RuntimeConfigPaths
    {
        public const string ProductDirectoryName = "MTTFTest";
        public const string ConfigDirectoryName = "Config";
        public const string FormalModeMarkerName = "MTTFTest.UnattendedMode.required";
        public const string ConfiguredMarkerName = "MTTFTest.FirstRun.configured";

        private static readonly string[] RequiredRuntimeFiles =
        {
            "AIConfig.xml",
            "AlarmConfig.xml",
            "AOConfig.xml",
            "DOConfig.xml",
            "PowerSupplyConfig.xml",
            "TestConfig.xml",
            "UnattendedAlarmConfig.xml",
            "UIConfig.xml"
        };

        public static string ApplicationDirectory => Path.GetFullPath(
            AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory);

        public static string TemplateDirectory => Path.Combine(
            ApplicationDirectory,
            ConfigDirectoryName);

        public static string Directory => ResolveDirectory(
            ApplicationDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            IsInstalledApplicationDirectory(ApplicationDirectory));

        public static IReadOnlyList<string> RequiredFiles => RequiredRuntimeFiles;

        public static string GetPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("配置文件名不能为空。", nameof(fileName));
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
                throw new ArgumentException("配置文件名不得包含目录。", nameof(fileName));
            return Path.Combine(Directory, fileName);
        }

        public static string ResolveDirectory(
            string applicationDirectory,
            string commonApplicationDataDirectory,
            bool installed)
        {
            var application = Path.GetFullPath(
                string.IsNullOrWhiteSpace(applicationDirectory)
                    ? Environment.CurrentDirectory
                    : applicationDirectory);
            if (!installed)
                return Path.Combine(application, ConfigDirectoryName);

            var common = Path.GetFullPath(
                string.IsNullOrWhiteSpace(commonApplicationDataDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                    : commonApplicationDataDirectory);
            return Path.Combine(common, ProductDirectoryName, ConfigDirectoryName);
        }

        public static bool IsInstalledApplicationDirectory(string applicationDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectory)) return false;
            var directory = Path.GetFullPath(applicationDirectory).TrimEnd('\\', '/');
            if (!File.Exists(Path.Combine(directory, FormalModeMarkerName)) &&
                !File.Exists(Path.Combine(directory, ConfiguredMarkerName)))
                return false;

            return CandidateInstallDirectories().Any(candidate =>
                PathsEqual(directory, candidate));
        }

        public static bool Validate(bool verifyWritable, out string error)
        {
            return ValidateDirectory(Directory, RequiredRuntimeFiles, verifyWritable, out error);
        }

        public static bool ValidateDirectory(
            string directory,
            IEnumerable<string> requiredFiles,
            bool verifyWritable,
            out string error)
        {
            error = string.Empty;
            string probe = null;
            try
            {
                var resolved = Path.GetFullPath(directory ?? string.Empty);
                if (!System.IO.Directory.Exists(resolved))
                    throw new DirectoryNotFoundException("运行配置目录不存在：" + resolved);
                var missing = (requiredFiles ?? Enumerable.Empty<string>())
                    .Where(name => !File.Exists(Path.Combine(resolved, name)))
                    .ToArray();
                if (missing.Length > 0)
                    throw new FileNotFoundException(
                        "运行配置缺少文件：" + string.Join(",", missing));
                if (verifyWritable)
                {
                    probe = Path.Combine(
                        resolved,
                        ".runtime-config-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                    probe = null;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return false;
            }
            finally
            {
                try { if (!string.IsNullOrWhiteSpace(probe) && File.Exists(probe)) File.Delete(probe); }
                catch { }
            }
        }

        private static IEnumerable<string> CandidateInstallDirectories()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            return roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.Combine(root, ProductDirectoryName, "Current"))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left ?? string.Empty).TrimEnd('\\', '/'),
                Path.GetFullPath(right ?? string.Empty).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
