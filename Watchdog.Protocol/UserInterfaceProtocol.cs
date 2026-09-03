using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class UserInterfaceExitReceipt
    {
        public int SchemaVersion { get; set; } = RecoveryProtocolV7.SchemaVersion;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public SystemTerminalState LastObservedState { get; set; }
        public string Reason { get; set; } = string.Empty;
        public long WrittenUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            return SchemaVersion == RecoveryProtocolV7.SchemaVersion &&
                   RecoveryProtocolV7.IsGuid(SessionId) &&
                   RecoveryProtocolV7.IsGuid(RunId) && RunEpoch > 0 &&
                   ProcessId > 0 && ProcessStartUtcTicks > 0 &&
                   (LastObservedState == SystemTerminalState.SafeIdleAlarmed ||
                    LastObservedState == SystemTerminalState.StoppedByOperator) &&
                   RecoveryProtocolV7.HasText(Reason) && WrittenUtcTicks > 0;
        }
    }

    public static class UserInterfaceExitReceiptStore
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string PathFor(string sessionId)
        {
            if (!RecoveryProtocolV7.IsGuid(sessionId))
                throw new InvalidDataException("UiExitReceiptSessionInvalid");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTest",
                "RecoveryKernel",
                "ui-exit-" + sessionId + ".v7.json");
        }

        public static void Write(UserInterfaceExitReceipt receipt)
        {
            if (receipt?.IsStructurallyValid() != true)
                throw new InvalidDataException("UiExitReceiptInvalid");
            var path = PathFor(receipt.SessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                           temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(Json.Serialize(receipt));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        public static bool TryReadExact(
            string sessionId,
            string runId,
            long runEpoch,
            int processId,
            long processStartUtcTicks,
            out UserInterfaceExitReceipt receipt)
        {
            receipt = null;
            try
            {
                var value = Json.Deserialize<UserInterfaceExitReceipt>(
                    File.ReadAllText(PathFor(sessionId), Encoding.UTF8));
                if (value?.IsStructurallyValid() != true ||
                    !string.Equals(value.SessionId, sessionId, StringComparison.Ordinal) ||
                    !string.Equals(value.RunId, runId, StringComparison.Ordinal) ||
                    value.RunEpoch != runEpoch || value.ProcessId != processId ||
                    value.ProcessStartUtcTicks != processStartUtcTicks)
                    return false;
                receipt = value;
                return true;
            }
            catch { return false; }
        }

        public static UserInterfaceExitReceipt ForCurrentProcess(
            EngineStateSnapshot snapshot,
            string reason)
        {
            using (var process = Process.GetCurrentProcess())
                return new UserInterfaceExitReceipt
                {
                    SessionId = snapshot.SessionId,
                    RunId = snapshot.RunId,
                    RunEpoch = snapshot.RunEpoch,
                    ProcessId = process.Id,
                    ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    LastObservedState = snapshot.State,
                    Reason = reason,
                    WrittenUtcTicks = DateTime.UtcNow.Ticks
                };
        }
    }
}
