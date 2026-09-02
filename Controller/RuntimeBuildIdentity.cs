using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Controller
{
    public sealed class RuntimeBuildIdentity
    {
        public string ProductVersion { get; private set; }
        public string AssemblyVersion { get; private set; }
        public string ExecutablePath { get; private set; }
        public string ExecutableSha256 { get; private set; }
        public string ConfigSha256 { get; private set; }
        public string ReleaseConfigSha256 { get; private set; }
        public int ProcessBitness { get; private set; }
        public int ProcessId { get; private set; }
        public string GitCommit { get; private set; }
        public string GitDirty { get; private set; }
        public string BuildUtc { get; private set; }
        public bool ReleasePackageVerified { get; private set; }
        public string ReleasePackageCode { get; private set; }
        public string ReleasePackageDetail { get; private set; }
        public int ReleasePackageFileCount { get; private set; }
        public DateTime CapturedUtc { get; private set; }

        public static RuntimeBuildIdentity Capture()
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var executable = TryGetExecutablePath(assembly);
            var gitCommit = ReadBuildMetadata(assembly, "GitCommit", "EPB_GIT_COMMIT");
            var gitDirty = ReadBuildMetadata(assembly, "GitDirty", "EPB_GIT_DIRTY");
            var buildUtc = ReadBuildMetadata(assembly, "BuildUtc", "EPB_BUILD_UTC");
            var releaseConfigSha256 = ReadBuildMetadata(
                assembly,
                "ConfigSha256",
                "EPB_CONFIG_SHA256");
            if (gitCommit == "unknown" || gitDirty == "unknown")
                TryReadGitIdentity(executable, ref gitCommit, ref gitDirty);
            var identity = new RuntimeBuildIdentity
            {
                // 现场补丁版本使用第四段 Revision；截成三段会把 2.12.0.3—.6
                // 全部记录为同一个 V2.12.0，事故证据无法对应实际二进制。
                ProductVersion = FormatProductVersion(version),
                AssemblyVersion = version?.ToString() ?? "unknown",
                ExecutablePath = string.IsNullOrWhiteSpace(executable) ? "unknown" : executable,
                ExecutableSha256 = ComputeFileSha256(executable),
                ConfigSha256 = ComputeFileSha256(
                    string.IsNullOrWhiteSpace(executable) ? null : executable + ".config"),
                ReleaseConfigSha256 = releaseConfigSha256,
                ProcessBitness = IntPtr.Size * 8,
                ProcessId = TryGetProcessId(),
                GitCommit = gitCommit,
                GitDirty = gitDirty,
                BuildUtc = buildUtc,
                CapturedUtc = DateTime.UtcNow
            };
            // 运行版本和交付包由操作人员负责。这里仅记录构建身份，不能再用
            // manifest、配置哈希或目录文件集合阻止启动、恢复和正常退出。
            identity.ReleasePackageVerified = true;
            identity.ReleasePackageCode = "OperatorManaged";
            identity.ReleasePackageDetail = "版本与交付包由操作人员管理；运行时不执行包身份门禁。";
            identity.ReleasePackageFileCount = 0;
            return identity;
        }

        public void WriteJson(string path)
        {
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }

        public bool TryWriteProjectJson(
            string projectConfigDirectory,
            string projectName,
            string projectRoot,
            out string path,
            out string error)
        {
            path = string.Empty;
            error = string.Empty;
            string temporaryPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(projectConfigDirectory))
                    throw new ArgumentException("项目 Config 目录不能为空。", nameof(projectConfigDirectory));
                if (string.IsNullOrWhiteSpace(projectName))
                    throw new ArgumentException("项目名称不能为空。", nameof(projectName));

                var configDirectory = Path.GetFullPath(projectConfigDirectory);
                Directory.CreateDirectory(configDirectory);
                path = Path.Combine(configDirectory, "runtime-build-identity.json");
                temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.Read,
                           4096,
                           FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(ToProjectJson(projectName, projectRoot));
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null, true);
                else
                    File.Move(temporaryPath, path);
                temporaryPath = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryPath))
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        public string ToProjectJson(string projectName, string projectRoot)
        {
            return "{\n" +
                   "  \"schemaVersion\": 1,\n" +
                   $"  \"projectName\": \"{Escape(projectName)}\",\n" +
                   $"  \"projectRoot\": \"{Escape(projectRoot)}\",\n" +
                   $"  \"productVersion\": \"{Escape(ProductVersion)}\",\n" +
                   $"  \"assemblyVersion\": \"{Escape(AssemblyVersion)}\",\n" +
                   $"  \"executablePath\": \"{Escape(ExecutablePath)}\",\n" +
                   $"  \"executableSha256\": \"{Escape(ExecutableSha256)}\",\n" +
                   $"  \"configSha256\": \"{Escape(ConfigSha256)}\",\n" +
                   $"  \"releaseConfigSha256\": \"{Escape(ReleaseConfigSha256)}\",\n" +
                   $"  \"processBitness\": {ProcessBitness},\n" +
                   $"  \"processId\": {ProcessId},\n" +
                   $"  \"gitCommit\": \"{Escape(GitCommit)}\",\n" +
                   $"  \"gitDirty\": \"{Escape(GitDirty)}\",\n" +
                   $"  \"buildUtc\": \"{Escape(BuildUtc)}\",\n" +
                   $"  \"releasePackageVerified\": {ReleasePackageVerified.ToString().ToLowerInvariant()},\n" +
                   $"  \"releasePackageCode\": \"{Escape(ReleasePackageCode)}\",\n" +
                   $"  \"releasePackageDetail\": \"{Escape(ReleasePackageDetail)}\",\n" +
                   $"  \"releasePackageFileCount\": {ReleasePackageFileCount},\n" +
                   $"  \"capturedUtc\": \"{CapturedUtc:O}\"\n" +
                   "}\n";
        }

        public string ToJson()
        {
            return "{\n" +
                   $"  \"productVersion\": \"{Escape(ProductVersion)}\",\n" +
                   $"  \"assemblyVersion\": \"{Escape(AssemblyVersion)}\",\n" +
                   $"  \"executablePath\": \"{Escape(ExecutablePath)}\",\n" +
                   $"  \"executableSha256\": \"{Escape(ExecutableSha256)}\",\n" +
                   $"  \"configSha256\": \"{Escape(ConfigSha256)}\",\n" +
                   $"  \"releaseConfigSha256\": \"{Escape(ReleaseConfigSha256)}\",\n" +
                   $"  \"processBitness\": {ProcessBitness},\n" +
                   $"  \"processId\": {ProcessId},\n" +
                   $"  \"gitCommit\": \"{Escape(GitCommit)}\",\n" +
                   $"  \"gitDirty\": \"{Escape(GitDirty)}\",\n" +
                   $"  \"buildUtc\": \"{Escape(BuildUtc)}\",\n" +
                   $"  \"releasePackageVerified\": {ReleasePackageVerified.ToString().ToLowerInvariant()},\n" +
                   $"  \"releasePackageCode\": \"{Escape(ReleasePackageCode)}\",\n" +
                   $"  \"releasePackageDetail\": \"{Escape(ReleasePackageDetail)}\",\n" +
                   $"  \"releasePackageFileCount\": {ReleasePackageFileCount},\n" +
                   $"  \"capturedUtc\": \"{CapturedUtc:O}\"\n" +
                   "}\n";
        }

        public string ToStartupLogLine()
        {
            return $"ProductVersion={ProductVersion} AssemblyVersion={AssemblyVersion} " +
                   $"PID={ProcessId} Bitness={ProcessBitness} " +
                   $"ExecutablePath=\"{ExecutablePath}\" ExeSha256={ExecutableSha256} " +
                   $"ConfigSha256={ConfigSha256} ReleaseConfigSha256={ReleaseConfigSha256} " +
                   $"GitCommit={GitCommit} GitDirty={GitDirty} BuildUtc={BuildUtc} " +
                   $"PackageVerified={ReleasePackageVerified} PackageCode={ReleasePackageCode} " +
                   $"PackageFiles={ReleasePackageFileCount}";
        }

        public string ToDisplayText()
        {
            return $"产品版本：{ProductVersion}\r\n" +
                   $"程序集版本：{AssemblyVersion}\r\n" +
                   $"进程：PID {ProcessId} / {ProcessBitness} 位\r\n" +
                   $"EXE 路径：{ExecutablePath}\r\n" +
                   $"EXE SHA-256：{ExecutableSha256}\r\n" +
                   $"配置 SHA-256：{ReleaseConfigSha256}\r\n" +
                   $"Git SHA：{GitCommit}\r\n" +
                   $"Git Dirty：{GitDirty}\r\n" +
                   $"构建时间：{BuildUtc}\r\n" +
                   $"发布包校验：{ReleasePackageVerified} / {ReleasePackageCode}\r\n" +
                   $"递归校验文件：{ReleasePackageFileCount}\r\n" +
                   $"校验详情：{ReleasePackageDetail}";
        }

        internal static string FormatProductVersion(Version version)
        {
            return version == null ? "V0.0.0.0" : $"V{version.ToString(4)}";
        }

        private static int TryGetProcessId()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    return process.Id;
            }
            catch { return 0; }
        }

        private static string TryGetExecutablePath(Assembly assembly)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName))
                        return process.MainModule.FileName;
            }
            catch { }
            try { return assembly.Location; }
            catch { return null; }
        }

        public static string ComputeFileSha256(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "unknown";
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            }
            catch { return "unknown"; }
        }

        private static string ReadBuildMetadata(Assembly assembly, string key, string environmentName)
        {
            try
            {
                var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(attribute =>
                        string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
            var environment = Environment.GetEnvironmentVariable(environmentName);
            return string.IsNullOrWhiteSpace(environment) ? "unknown" : environment;
        }

        private static void TryReadGitIdentity(
            string executable,
            ref string commit,
            ref string dirty)
        {
            try
            {
                var root = FindGitRoot(Path.GetDirectoryName(executable));
                if (root == null) root = FindGitRoot(Environment.CurrentDirectory);
                if (root == null) return;
                if (commit == "unknown")
                {
                    var value = RunGit(root, "rev-parse HEAD");
                    if (!string.IsNullOrWhiteSpace(value)) commit = value.Trim();
                }
                if (dirty == "unknown")
                {
                    var value = RunGit(root, "status --porcelain");
                    if (value != null) dirty = string.IsNullOrWhiteSpace(value) ? "false" : "true";
                }
            }
            catch { }
        }

        private static string FindGitRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory)) return null;
            var directory = new DirectoryInfo(startDirectory);
            for (var depth = 0; directory != null && depth < 12; depth++, directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                    return directory.FullName;
            return null;
        }

        private static string RunGit(string workingDirectory, string arguments)
        {
            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            })
            {
                if (!process.Start()) return null;
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(2000))
                {
                    try { process.Kill(); } catch { }
                    return null;
                }
                return process.ExitCode == 0 ? output : null;
            }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
