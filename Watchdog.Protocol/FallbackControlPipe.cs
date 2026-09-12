using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class FallbackCommand
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public long Revision { get; set; }
        public string Action { get; set; }
        public string CommandId { get; set; }
        public string Requester { get; set; }
        public long FenceGeneration { get; set; }
    }
    public sealed class FallbackCommandResult
    {
        public string Status { get; set; }
        public string Detail { get; set; }
        public FallbackLedger Ledger { get; set; }
    }
    public static class FallbackControlPipe
    {
        public static string Name(string session) => "MTTF-Fallback-v1-" + session;
        public static FallbackCommandResult Send(FallbackCommand command) =>
            DeadlinePipeExchange.Execute(Name(command.SessionId), 3000,
                writer => Write(writer, command), reader => Read<FallbackCommandResult>(reader));

        public static async Task ServeAsync(string session, Func<FallbackCommand, FallbackCommandResult> execute,
            CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                var security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                using (var pipe = new NamedPipeServerStream(Name(session), PipeDirection.InOut, 1,
                           PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                using (cancellation.Register(() => pipe.Dispose()))
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                        DeadlinePipeExchange.OnConnected(pipe, 3000, (reader, writer) =>
                        {
                            var request = Read<FallbackCommand>(reader);
                            FallbackCommandResult result;
                            try { result = execute(request); }
                            catch (Exception error) { result = new FallbackCommandResult { Status = error is IOException || error is TimeoutException ? "Unknown" : "Rejected", Detail = error.Message }; }
                            Write(writer, result); return true;
                        }, cancellation);
                    }
                    catch (Exception) when (!cancellation.IsCancellationRequested) { /* malformed/expired client; bounded connection closed */ }
                    catch (Exception) when (cancellation.IsCancellationRequested) { return; }
                }
            }
        }
        private static T Read<T>(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length <= 0 || length > BoundedJson.MaximumBytes) throw new InvalidDataException("FallbackFrameLength");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new JavaScriptSerializer { MaxJsonLength = BoundedJson.MaximumBytes, RecursionLimit = 12 }
                .Deserialize<T>(new UTF8Encoding(false, true).GetString(bytes));
        }
        private static void Write<T>(BinaryWriter writer, T value)
        {
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
            if (bytes.Length > BoundedJson.MaximumBytes) throw new InvalidDataException("FallbackFrameLength");
            writer.Write(bytes.Length); writer.Write(bytes); writer.Flush();
        }
    }
}
