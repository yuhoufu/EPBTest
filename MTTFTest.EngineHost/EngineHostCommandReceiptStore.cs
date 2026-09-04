using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal static class EngineHostCommandReceiptStore
    {
        internal static bool TryRead(
            string idempotencyKey,
            out RecoveryCommandReceipt receipt,
            string rootDirectory = null)
        {
            receipt = null;
            try
            {
                var path = PathFor(idempotencyKey, rootDirectory);
                if (!File.Exists(path)) return false;
                var value = new JavaScriptSerializer().Deserialize<RecoveryCommandReceipt>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (value == null ||
                    value.SchemaVersion != EngineHostProtocol.SchemaVersion ||
                    !RecoveryProtocolV7.IsGuid(value.CommandId) ||
                    !string.Equals(value.IdempotencyKey, idempotencyKey,
                        StringComparison.Ordinal) ||
                    value.CompletedUtcTicks <= 0)
                    return false;
                receipt = value;
                return true;
            }
            catch { return false; }
        }

        internal static void Write(
            RecoveryCommandReceipt receipt,
            string rootDirectory = null)
        {
            if (receipt == null ||
                receipt.SchemaVersion != EngineHostProtocol.SchemaVersion ||
                !RecoveryProtocolV7.IsGuid(receipt.CommandId) ||
                !IsSha256(receipt.IdempotencyKey) || receipt.CompletedUtcTicks <= 0)
                throw new InvalidDataException("EngineCommandReceiptInvalid");
            var path = PathFor(receipt.IdempotencyKey, rootDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                           temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(new JavaScriptSerializer().Serialize(receipt));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                if (!TryRead(receipt.IdempotencyKey, out var verified, rootDirectory) ||
                    verified.CommandId != receipt.CommandId ||
                    verified.CompletedUtcTicks != receipt.CompletedUtcTicks)
                    throw new IOException("EngineCommandReceiptReadbackFailed");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static string PathFor(
            string idempotencyKey,
            string rootDirectory)
        {
            if (!IsSha256(idempotencyKey))
                throw new InvalidDataException("EngineCommandIdempotencyKeyInvalid");
            var root = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "MTTFTest",
                    "RecoveryKernel",
                    "engine-receipts")
                : Path.GetFullPath(rootDirectory);
            return Path.Combine(
                root,
                "command-" + idempotencyKey.ToLowerInvariant() + ".v7.json");
        }

        private static bool IsSha256(string value)
        {
            return value?.Length == 64 && value.All(character =>
                character >= '0' && character <= '9' ||
                character >= 'a' && character <= 'f' ||
                character >= 'A' && character <= 'F');
        }
    }
}
