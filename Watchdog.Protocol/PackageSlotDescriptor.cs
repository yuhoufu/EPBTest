using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class PackageSlotDescriptor
    {
        public int SchemaVersion { get; set; }
        public string SlotName { get; set; }
        public string ProductVersion { get; set; }
        public string ArchitectureGeneration { get; set; }
        public string ReleaseStatus { get; set; }
        public string RootPath { get; set; }
        public string ManifestSha256 { get; set; }
        public string ConfigSha256 { get; set; }
        public string CreatedUtc { get; set; }
        public PackageSlotFileEntry[] Files { get; set; } = Array.Empty<PackageSlotFileEntry>();
    }

    public sealed class PackageSlotFileEntry
    {
        // Names intentionally match build-identity.json.
        public string name { get; set; }
        public long bytes { get; set; }
        public string sha256 { get; set; }
    }

    public sealed class PackageSlotPointer
    {
        public int SchemaVersion { get; set; }
        public string ActiveSlot { get; set; }
        public string CurrentPath { get; set; }
        public string LastKnownGoodPath { get; set; }
        public long RevisionUtcTicks { get; set; }
    }

    public static class PackageSlotDescriptorStore
    {
        public const int SchemaVersion = 6;
        public const string DescriptorFileName = "package-slot.v6.json";
        public const string PointerFileName = "package-pointer.v6.json";
        private const string Entropy = "MTTFTest.PackageSlot.Schema6";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static bool TryValidateSlot(
            string directory,
            string expectedSlot,
            out PackageSlotDescriptor descriptor,
            out string reason)
        {
            descriptor = null;
            reason = string.Empty;
            try
            {
                var root = Path.GetFullPath(directory ?? string.Empty).TrimEnd('\\', '/');
                var envelope = ReadEnvelope(Path.Combine(root, DescriptorFileName));
                descriptor = Json.Deserialize<PackageSlotDescriptor>(Unprotect(envelope));
                if (descriptor == null || descriptor.SchemaVersion != SchemaVersion)
                    return Fail("PackageSlotSchemaMismatch", out reason);
                if (!string.Equals(Path.GetFullPath(descriptor.RootPath ?? string.Empty).TrimEnd('\\', '/'),
                        root, StringComparison.OrdinalIgnoreCase))
                    return Fail("PackageSlotRootMismatch", out reason);
                if (!string.IsNullOrWhiteSpace(expectedSlot) &&
                    !string.Equals(descriptor.SlotName, expectedSlot, StringComparison.Ordinal))
                    return Fail("PackageSlotNameMismatch", out reason);
                if (!string.Equals(descriptor.ProductVersion, "3.0.0.0", StringComparison.Ordinal))
                    return Fail("PackageSlotVersionMismatch", out reason);
                if (!string.Equals(descriptor.ArchitectureGeneration,
                        RecoveryProtocolV7.ArchitectureGeneration, StringComparison.Ordinal))
                    return Fail("PackageArchitectureGenerationMismatch", out reason);
                if (descriptor.Files == null || descriptor.Files.Length == 0)
                    return Fail("PackageSlotFileListMissing", out reason);

                var prefix = root + Path.DirectorySeparatorChar;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in descriptor.Files)
                {
                    var relative = (entry?.name ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                        relative.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
                        return Fail("PackageSlotRelativePathInvalid", out reason);
                    var path = Path.GetFullPath(Path.Combine(root, relative));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        !seen.Add(path) || !File.Exists(path))
                        return Fail("PackageSlotFileMissingOrDuplicate:" + relative, out reason);
                    var info = new FileInfo(path);
                    if (info.Length != entry.bytes ||
                        !string.Equals(Sha256File(path), entry.sha256,
                            StringComparison.OrdinalIgnoreCase))
                        return Fail("PackageSlotFileIdentityMismatch:" + relative, out reason);
                }
                var manifestPath = Path.Combine(root, "build-identity.json");
                if (!File.Exists(manifestPath) ||
                    !string.Equals(Sha256File(manifestPath), descriptor.ManifestSha256,
                        StringComparison.OrdinalIgnoreCase))
                    return Fail("PackageManifestIdentityMismatch", out reason);
                reason = "PackageSlotValidated";
                return true;
            }
            catch (Exception ex)
            {
                reason = "PackageSlotValidationFailed:" + ex.GetBaseException().Message;
                descriptor = null;
                return false;
            }
        }

        public static bool TryResolveActiveExecutable(
            string requestedExecutable,
            out string resolvedExecutable,
            out string reason)
        {
            try
            {
                resolvedExecutable = Path.GetFullPath(requestedExecutable ?? string.Empty);
                if (!File.Exists(resolvedExecutable))
                    return Fail("ExecutableMissing", out reason);
                reason = "OperatorManagedExecutable";
                return true;
            }
            catch (Exception ex)
            {
                resolvedExecutable = string.Empty;
                return Fail("ExecutablePathInvalid:" + ex.GetBaseException().Message, out reason);
            }
        }

        public static bool TryActivateLastKnownGood(
            string currentExecutable,
            out string lastKnownGoodExecutable,
            out string reason)
        {
            lastKnownGoodExecutable = string.Empty;
            reason = string.Empty;
            try
            {
                var currentDirectory = Path.GetDirectoryName(Path.GetFullPath(currentExecutable ?? string.Empty));
                var installRoot = Directory.GetParent(currentDirectory ?? string.Empty)?.FullName;
                if (string.IsNullOrWhiteSpace(installRoot))
                    return Fail("InstallRootMissing", out reason);
                var lastKnownGoodPath = Path.Combine(installRoot, "LastKnownGood");
                if (!TryValidateV3BuildIdentity(currentDirectory, out var currentReason))
                    return Fail("CurrentV3IdentityInvalid:" + currentReason, out reason);
                if (!TryValidateV3BuildIdentity(lastKnownGoodPath, out var lkgReason))
                    return Fail("LastKnownGoodV3IdentityInvalid:" + lkgReason, out reason);
                var executable = Path.Combine(
                    lastKnownGoodPath,
                    Path.GetFileName(currentExecutable));
                if (!File.Exists(executable)) return Fail("LastKnownGoodExecutableMissing", out reason);
                lastKnownGoodExecutable = executable;
                reason = "OperatorManagedLastKnownGood";
                return true;
            }
            catch (Exception ex)
            {
                return Fail("LastKnownGoodActivationFailed:" + ex.GetBaseException().Message, out reason);
            }
        }

        private static bool TryValidateV3BuildIdentity(
            string directory,
            out string reason)
        {
            reason = string.Empty;
            try
            {
                var path = Path.Combine(
                    Path.GetFullPath(directory ?? string.Empty),
                    "build-identity.json");
                if (!File.Exists(path)) return Fail("BuildIdentityMissing", out reason);
                var identity = Json.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (identity == null ||
                    !identity.TryGetValue("productVersion", out var product) ||
                    !string.Equals(Convert.ToString(product), "V3.0.0.0",
                        StringComparison.Ordinal) ||
                    !identity.TryGetValue("recoveryArchitectureGeneration",
                        out var generation) ||
                    !string.Equals(Convert.ToString(generation),
                        RecoveryProtocolV7.ArchitectureGeneration,
                        StringComparison.Ordinal) ||
                    !identity.TryGetValue("watchdogSchema", out var schema) ||
                    Convert.ToInt32(schema) != RecoveryProtocolV7.SchemaVersion ||
                    !identity.TryGetValue("packageSlotSchema", out var packageSchema) ||
                    Convert.ToInt32(packageSchema) != SchemaVersion)
                    return Fail("BuildIdentityGenerationMismatch", out reason);
                foreach (var name in new[]
                {
                    "MTTFTest.exe",
                    "MTTFTest.EngineHost.exe",
                    "MTTFTest.Recovery.Kernel.dll",
                    "MTTFTest.Watchdog.exe",
                    "MTTFTest.Watchdog.Protocol.dll",
                    "MTTFTest.Watchdog.Client.dll",
                    "MTTFTest.SafetyAgent.exe",
                    "MTTFTest.SafetyHardware.dll",
                    "MTTFTest.SessionAgent.exe",
                    "Controller.dll"
                })
                    if (!File.Exists(Path.Combine(directory, name)) ||
                        !string.Equals(
                            FileVersionInfo.GetVersionInfo(
                                Path.Combine(directory, name)).FileVersion,
                            "3.0.0.0", StringComparison.Ordinal))
                        return Fail("V3ComponentMissingOrMismatched:" + name, out reason);
                reason = "V3BuildIdentityValidated";
                return true;
            }
            catch (Exception ex)
            {
                return Fail("BuildIdentityReadFailed:" + ex.GetBaseException().Message,
                    out reason);
            }
        }

        private static PackageSlotEnvelope ReadEnvelope(string path)
        {
            var envelope = Json.Deserialize<PackageSlotEnvelope>(File.ReadAllText(path, Encoding.UTF8));
            if (envelope == null || envelope.SchemaVersion != SchemaVersion ||
                !string.Equals(envelope.Protection, "DPAPI-LocalMachine", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(envelope.ProtectedPayloadBase64))
                throw new InvalidDataException("PackageSlotEnvelopeInvalid");
            return envelope;
        }

        private static string Unprotect(PackageSlotEnvelope envelope)
        {
            var clear = ProtectedData.Unprotect(
                Convert.FromBase64String(envelope.ProtectedPayloadBase64),
                Encoding.UTF8.GetBytes(Entropy),
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(clear);
        }

        private static void WriteEnvelopeAtomic<T>(string path, T payload)
        {
            var clear = Encoding.UTF8.GetBytes(Json.Serialize(payload));
            var envelope = new PackageSlotEnvelope
            {
                SchemaVersion = SchemaVersion,
                Protection = "DPAPI-LocalMachine",
                ProtectedPayloadBase64 = Convert.ToBase64String(ProtectedData.Protect(
                    clear, Encoding.UTF8.GetBytes(Entropy), DataProtectionScope.LocalMachine))
            };
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, Json.Serialize(envelope), new UTF8Encoding(false));
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static bool Fail(string value, out string reason)
        {
            reason = value;
            return false;
        }

        private sealed class PackageSlotEnvelope
        {
            public int SchemaVersion { get; set; }
            public string Protection { get; set; }
            public string ProtectedPayloadBase64 { get; set; }
        }
    }
}
