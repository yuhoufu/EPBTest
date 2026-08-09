using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Controller
{
    public sealed class ReleasePackageVerification
    {
        public bool Verified { get; internal set; }
        public string Code { get; internal set; } = "NotChecked";
        public string Detail { get; internal set; } = string.Empty;
        public int VerifiedFileCount { get; internal set; }

        public override string ToString()
            => $"Verified={Verified} Code={Code} Files={VerifiedFileCount} Detail={Detail}";
    }

    /// <summary>
    /// 对程序实际运行目录执行独立发布包校验。它故意不信任程序集版本号：
    /// VS 可以在保留旧 build-identity.json 的同时覆盖 EXE/DLL，只有逐文件哈希
    /// 和构建身份同时匹配，才允许开始新的正式试验。
    /// </summary>
    public static class ReleasePackageVerifier
    {
        private static readonly object CacheGate = new();
        private static string _cachedRoot;
        private static ReleasePackageVerification _cached;

        public static ReleasePackageVerification VerifyCurrent(
            RuntimeBuildIdentity identity,
            bool refresh = false)
        {
            if (identity == null)
                return Failed("IdentityMissing", "运行构建身份为空。");
            var root = Path.GetDirectoryName(identity.ExecutablePath ?? string.Empty);
            if (refresh)
                return VerifyDirectory(
                    root,
                    identity.ProductVersion,
                    identity.GitCommit,
                    identity.GitDirty,
                    identity.BuildUtc,
                    identity.ReleaseConfigSha256);

            lock (CacheGate)
            {
                if (_cached != null && string.Equals(
                        _cachedRoot,
                        root,
                        StringComparison.OrdinalIgnoreCase))
                    return _cached;
                _cachedRoot = root;
                _cached = VerifyDirectory(
                    root,
                    identity.ProductVersion,
                    identity.GitCommit,
                    identity.GitDirty,
                    identity.BuildUtc,
                    identity.ReleaseConfigSha256);
                return _cached;
            }
        }

        public static ReleasePackageVerification VerifyDirectory(
            string rootDirectory,
            string expectedProductVersion,
            string expectedGitCommit,
            string expectedGitDirty,
            string expectedBuildUtc,
            string expectedConfigSha256)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                    return Failed("PackageDirectoryMissing", rootDirectory ?? "null");
                var root = Path.GetFullPath(rootDirectory).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var identityPath = Path.Combine(root, "build-identity.json");
                var checksumPath = Path.Combine(root, "SHA256SUMS.txt");
                if (!File.Exists(identityPath) || !File.Exists(checksumPath))
                    return Failed(
                        "PackageManifestMissing",
                        $"Identity={File.Exists(identityPath)} Checksums={File.Exists(checksumPath)}");

                var identityJson = File.ReadAllText(identityPath);
                if (!TryReadJsonString(identityJson, "productVersion", out var productVersion) ||
                    !TryReadJsonString(identityJson, "releaseStatus", out var releaseStatus) ||
                    !TryReadJsonBoolean(identityJson, "deploymentApproved", out var approved) ||
                    !TryReadJsonString(identityJson, "gitCommit", out var gitCommit) ||
                    !TryReadJsonBoolean(identityJson, "gitDirty", out var gitDirty) ||
                    !TryReadJsonString(identityJson, "buildUtc", out var buildUtc) ||
                    !TryReadJsonString(identityJson, "configSha256", out var configSha256))
                    return Failed("PackageIdentityInvalid", "build-identity.json 缺少正式身份字段。");

                if (!approved || gitDirty ||
                    !string.Equals(releaseStatus, "FORMAL_RELEASE_CANDIDATE", StringComparison.Ordinal))
                    return Failed(
                        "PackageNotApproved",
                        $"Status={releaseStatus} Approved={approved} GitDirty={gitDirty}");
                if (!EqualsOrdinal(productVersion, expectedProductVersion) ||
                    !EqualsOrdinalIgnoreCase(gitCommit, expectedGitCommit) ||
                    !EqualsOrdinalIgnoreCase(buildUtc, expectedBuildUtc) ||
                    !EqualsOrdinalIgnoreCase(configSha256, expectedConfigSha256) ||
                    !string.Equals(expectedGitDirty, "false", StringComparison.OrdinalIgnoreCase))
                    return Failed(
                        "PackageIdentityMismatch",
                        $"Version={productVersion}/{expectedProductVersion} " +
                        $"Commit={gitCommit}/{expectedGitCommit} Dirty={expectedGitDirty} " +
                        $"BuildUtc={buildUtc}/{expectedBuildUtc} Config={configSha256}/{expectedConfigSha256}");

                var expectedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(checksumPath))
                {
                    var match = Regex.Match(line, "^([0-9a-fA-F]{64})  (.+)$");
                    if (!match.Success)
                        return Failed("ChecksumFormatInvalid", line);
                    var relative = NormalizeRelativePath(match.Groups[2].Value);
                    if (relative == null)
                        return Failed("ChecksumPathUnsafe", match.Groups[2].Value);
                    if (expectedFiles.ContainsKey(relative))
                        return Failed("ChecksumPathDuplicate", relative);
                    expectedFiles.Add(relative, match.Groups[1].Value.ToLowerInvariant());
                }

                var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path => new
                    {
                        FullPath = path,
                        Relative = path.Substring(root.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Replace('\\', '/')
                    })
                    .Where(item => !string.Equals(
                        item.Relative,
                        "SHA256SUMS.txt",
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (actualFiles.Length != expectedFiles.Count)
                    return Failed(
                        "PackageFileSetMismatch",
                        $"Manifest={expectedFiles.Count} Actual={actualFiles.Length}");

                var verified = 0;
                foreach (var file in actualFiles)
                {
                    if (!expectedFiles.TryGetValue(file.Relative, out var expectedHash))
                        return Failed("UnexpectedPackageFile", file.Relative);
                    var actualHash = ComputeSha256(file.FullPath);
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        return Failed(
                            "PackageFileHashMismatch",
                            $"{file.Relative} Expected={expectedHash} Actual={actualHash}");
                    verified++;
                }

                return new ReleasePackageVerification
                {
                    Verified = true,
                    Code = "Verified",
                    Detail = "正式发布身份与递归文件哈希一致。",
                    VerifiedFileCount = verified
                };
            }
            catch (Exception ex)
            {
                return Failed("PackageVerificationException", ex.GetBaseException().Message);
            }
        }

        private static string NormalizeRelativePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return null;
            var normalized = value.Replace('\\', '/').TrimStart('/');
            if (normalized.Split('/').Any(part => part == ".." || part.Length == 0)) return null;
            return normalized;
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static bool TryReadJsonString(string json, string name, out string value)
        {
            var match = Regex.Match(
                json ?? string.Empty,
                "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"",
                RegexOptions.CultureInvariant);
            value = match.Success ? match.Groups[1].Value : null;
            return match.Success;
        }

        private static bool TryReadJsonBoolean(string json, string name, out bool value)
        {
            var match = Regex.Match(
                json ?? string.Empty,
                "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(true|false)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            value = match.Success && string.Equals(
                match.Groups[1].Value,
                "true",
                StringComparison.OrdinalIgnoreCase);
            return match.Success;
        }

        private static bool EqualsOrdinal(string left, string right)
            => !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.Ordinal);

        private static bool EqualsOrdinalIgnoreCase(string left, string right)
            => !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private static ReleasePackageVerification Failed(string code, string detail)
            => new()
            {
                Verified = false,
                Code = code,
                Detail = detail ?? string.Empty,
                VerifiedFileCount = 0
            };
    }
}
