using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class ProductionPipeClientTests
    {
        internal static int RunAll()
        {
            CheckReply("ProductionPipeClientReadsRealNamedPipe", (pipe, request) =>
                WriteResponse(pipe, request.RequestId), null);
            CheckReply("ProductionPipeClientRejectsForeignResponse", (pipe, request) =>
                WriteResponse(pipe, RecoveryProtocolV7.NewId()), typeof(InvalidDataException));
            CheckReply("ProductionPipeClientRejectsTruncatedFrame", (pipe, request) =>
            {
                var header = BitConverter.GetBytes(200);
                pipe.Write(header, 0, header.Length);
            }, typeof(EndOfStreamException));
            CheckReply("ProductionPipeClientBoundsConnectedSilentPeer", (pipe, request) =>
                Task.Delay(1500).GetAwaiter().GetResult(), typeof(TimeoutException), 400);
            return 4;
        }

        private static void CheckReply(string name,
            Action<NamedPipeServerStream, EngineHostRequest> respond,
            Type expectedException, int timeout = 3000)
        {
            var pipeName = "MTTFTest.ProductionPipeTest." + RecoveryProtocolV7.NewId();
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                       1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var peer = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using (var reader = new BinaryReader(server, Encoding.UTF8, true))
                    {
                        var payload = reader.ReadBytes(reader.ReadInt32());
                        var request = new JavaScriptSerializer().Deserialize<EngineHostRequest>(
                            Encoding.UTF8.GetString(payload));
                        respond(server, request);
                    }
                    server.Disconnect();
                });
                Exception failure = null;
                var elapsed = Stopwatch.StartNew();
                try
                {
                    var response = EngineHostPipeClient.Send(new EngineHostRequest
                    {
                        RequestId = RecoveryProtocolV7.NewId(),
                        Kind = EngineHostRequestKind.Ping
                    }, timeout, pipeName);
                    if (!response.Accepted) throw new Exception("UnexpectedRejectedResponse");
                }
                catch (Exception ex) { failure = ex; }
                if (expectedException == null && failure != null) throw failure;
                if (expectedException != null &&
                    (failure == null || !expectedException.IsInstanceOfType(failure)))
                    throw new Exception(name + ":UnexpectedException", failure);
                if (elapsed.ElapsedMilliseconds > timeout + 1500)
                    throw new Exception(name + ":DeadlineNotBounded");
                if (!peer.Wait(4000)) throw new Exception(name + ":PeerDidNotExit");
                Console.WriteLine("PASS " + name);
            }
        }

        private static void WriteResponse(NamedPipeServerStream pipe, string requestId)
        {
            var payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(
                new EngineHostResponse { RequestId = requestId, Accepted = true }));
            var header = BitConverter.GetBytes(payload.Length);
            pipe.Write(header, 0, header.Length);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
        }
    }
}
