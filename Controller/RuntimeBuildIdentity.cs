using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Controller
{
    internal sealed class RuntimeBuildIdentity
    {
        public string ProductVersion { get; private set; }
        public string AssemblyVersion { get; private set; }
        public string ExecutablePath { get; private set; }
        public string ExecutableSha256 { get; private set; }
        public string ConfigSha256 { get; private set; }
        public int ProcessBitness { get; private set; }
        public string GitCommit { get; private set; }
        public string GitDirty { get; private set; }
        public DateTime CapturedUtc { get; private set; }

        public static RuntimeBuildIdentity Capture()
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var executable = TryGetExecutablePath(assembly);
            var gitCommit = ReadBuildMetadata(assembly, "GitCommit", "EPB_GIT_COMMIT");
            var gitDirty = ReadBuildMetadata(assembly, "GitDirty", "EPB_GIT_DIRTY");
            if (gitCommit == "unknown" || gitDirty == "unknown")
                TryReadGitIdentity(executable, ref gitCommit, ref gitDirty);
            return new RuntimeBuildIdentity
            {
                ProductVersion = $"V{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0}",
                AssemblyVersion = version?.ToString() ?? "unknown",
                ExecutablePath = string.IsNullOrWhiteSpace(executable) ? "unknown" : executable,
                ExecutableSha256 = TryComputeSha256(executable),
                ConfigSha256 = TryComputeSha256(
                    string.IsNullOrWhiteSpace(executable) ? null : executable + ".config"),
                ProcessBitness = IntPtr.Size * 8,
                GitCommit = gitCommit,
                GitDirty = gitDirty,
                CapturedUtc = DateTime.UtcNow
            };
        }

        public void WriteJson(string path)
        {
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }

        public string ToJson()
        {
            return "{\n" +
                   $"  \"productVersion\": \"{Escape(ProductVersion)}\",\n" +
                   $"  \"assemblyVersion\": \"{Escape(AssemblyVersion)}\",\n" +
                   $"  \"executablePath\": \"{Escape(ExecutablePath)}\",\n" +
                   $"  \"executableSha256\": \"{Escape(ExecutableSha256)}\",\n" +
                   $"  \"configSha256\": \"{Escape(ConfigSha256)}\",\n" +
                   $"  \"processBitness\": {ProcessBitness},\n" +
                   $"  \"gitCommit\": \"{Escape(GitCommit)}\",\n" +
                   $"  \"gitDirty\": \"{Escape(GitDirty)}\",\n" +
                   $"  \"capturedUtc\": \"{CapturedUtc:O}\"\n" +
                   "}\n";
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

        private static string TryComputeSha256(string path)
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
